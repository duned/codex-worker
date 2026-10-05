using System.Net;
using System.Net.Http.Json;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class ManagedCheckoutTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task FirstColdAssignmentPreservesLeaseGenerationInHistoryAndEveryLifecycleReport(long generation)
    {
        using var fixture = await Fixture.CreateAsync();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Root, "history.db"));
        var settings = new WorkerServerSettings
        {
            Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(fixture.Root, "identity")
        };
        var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        var now = DateTimeOffset.UtcNow;
        var project = new ServerProjectContract("central", "Central", "owner/repo", "main", "", [], 4, now, now);
        var synchronizer = new ManagedConfigurationSynchronizer(Path.Combine(fixture.Root, "snapshot.json"),
            new ManagedProjectRuntimeSettings { CheckoutDirectory = Path.Combine(fixture.Root, "derived") });
        var configuration = Assert.Single(synchronizer.Apply(new(1,
            ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]))).Configuration;
        Assert.Equal("not-materialized", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics).Projects).State);
        Assert.False(Directory.Exists(configuration.Project.Directory));
        var assignment = new WorkerAssignmentContract("assignment", "execution", project, new("github-issue", "17"),
            workerId, new Dictionary<string, string>(), new("execution", workerId, generation, now, now.AddMinutes(15), "Active", 60));
        var reports = new List<WorkerExecutionReportContract>();
        var entries = new List<ExecutionHistoryEntry>();
        using var handler = new ReportHandler(async (request, ct) =>
        {
            var report = await (request.Content ?? throw new InvalidDataException("Missing report"))
                .ReadFromJsonAsync<WorkerExecutionReportContract>(ct);
            reports.Add(Assert.IsType<WorkerExecutionReportContract>(report));
            entries.Add(Assert.Single(await history.ReadAllAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var repository = new GitRepository(new ProcessRunner(), configuration.Project.Directory,
            configuration.Project.Repository, configuration.Git, configuration.Worker, Path.Combine(fixture.Root, "worktrees"));
        // Follow the host's cold-assignment preparation before claiming the assigned Issue.
        synchronizer.RecordProjectState(project, "materializing");
        await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        var instructions = Path.Combine(fixture.Root, "AGENTS.md");
        await File.WriteAllTextAsync(instructions, "test instructions");
        configuration.Codex.InstructionsFile = instructions;
        using var journal = new StringWriter();
        var operationalMessages = new List<string>();
        var output = new WorkerConsole(journal, interactive: false);
        using var telegram = new TelegramNotifier(false, output);
        var worker = new Worker(configuration, new AssignedIssueClient(), repository, new NoChangeCodex(),
            new PassingValidation(), telegram, output, history, serverSettings: settings,
            registrationClient: new WorkerRegistrationClient(client), operationalLog: message =>
            {
                operationalMessages.Add(message);
                journal.WriteLine(message);
            });
        await worker.PrepareForHostAsync(CancellationToken.None);
        synchronizer.RecordProjectState(project, "ready");

        var execution = await worker.ClaimAssignedAsync(assignment, CancellationToken.None);
        Assert.NotNull(execution);
        Assert.Equal(IssueOutcomeKind.Succeeded, Assert.IsType<IssueProcessingResult>(await execution).Kind);

        var completedEntry = Assert.Single(await history.ReadAllAsync());
        var completed = Assert.Single(operationalMessages, message => message.StartsWith("Managed ·", StringComparison.Ordinal));
        Assert.Equal($"Managed · execution {ExecutionFormatting.Display(completedEntry.ExecutionId)} · Server report completed · Completed", completed);
        Assert.Single(journal.ToString().Split(Environment.NewLine), line => line == completed);
        Assert.DoesNotContain(" · stage ", journal.ToString(), StringComparison.Ordinal);
        Assert.Contains("Scheduler · Central / #17 claimed", journal.ToString(), StringComparison.Ordinal);
        var lines = journal.ToString().Split(Environment.NewLine);
        var started = Array.FindIndex(lines, line => line.StartsWith("▶ Issue ·", StringComparison.Ordinal));
        Assert.True(started >= 0);
        Assert.Equal("↳ Codex · model CLI default · effort medium", lines[started + 1]);

        Assert.Equal("ready", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics).Projects).State);
        Assert.Equal("Claiming", reports[0].Stage);
        Assert.Equal("Claimed", entries[0].State);
        Assert.Contains(reports, report => report.Stage == "Preparing");
        Assert.Contains(reports, report => report.Stage == "Codex");
        Assert.Contains(reports, report => report.Stage == "Validation");
        Assert.Contains(reports, report => report.Stage == "Integration");
        Assert.Equal("Completed", reports[^1].State);
        Assert.All(reports, report => Assert.Equal(generation, report.Generation));
        Assert.All(entries, entry =>
        {
            Assert.Equal(generation, entry.OwnershipGeneration);
            Assert.Equal(assignment.ServerExecutionId, entry.ServerExecutionId);
            Assert.Equal(assignment.AssignmentId, entry.AssignmentId);
            Assert.Equal(1, entry.AttemptNumber);
            Assert.Null(entry.RetryOfExecutionId);
            Assert.False(entry.Resumed);
        });
        Assert.Equal(generation, Assert.Single(await history.ReadAllAsync()).OwnershipGeneration);
    }

    private sealed class ReportHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class AssignedIssueClient : IGitHubClient
    {
        public Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubIssue?>(new(issueNumber, "Example task", "Task body", DateTimeOffset.UnixEpoch));
        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken) => Task.FromResult<GitHubIssue?>(null);
        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct) => Task.CompletedTask;
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct) => Task.CompletedTask;
        public Task CloseAsync(int issueNumber, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoChangeCodex : ICodexExecutor
    {
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct) =>
            Task.FromResult(new CodexOutcome("success", "No changes needed", [], false, null));
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) => throw new InvalidOperationException("Unexpected repair");
    }

    private sealed class PassingValidation : IValidationRunner
    {
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct) =>
            Task.FromResult(ValidationResult.Success);
    }

    [Fact]
    public async Task ServerCatalogAuthorizesFirstMaterializationAndRevisionRetainsCheckoutAcrossRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        var store = new CodexServer.SqliteRegistryStore(Path.Combine(fixture.Root, "registry.db"));
        await store.InitializeAsync();
        var central = await store.CreateProjectAsync(new("Central", "owner/repo", "main", "", []));
        var incompatible = await store.CreateProjectAsync(new("Incompatible", "other/repo", "main", "",
            [new("runtime", "unavailable-runtime")]));
        var runtime = new ManagedProjectRuntimeSettings { CheckoutDirectory = Path.Combine(fixture.Root, "derived") };
        var cache = Path.Combine(fixture.Root, "snapshot.json");
        ServerProjectContract Contract(CodexServer.CentralProject project) => new(project.Id, project.Name,
            project.Repository, project.DefaultBranch, project.Description,
            project.Requirements.Select(item => new ServerProjectRequirementContract(item.Type, item.Name, item.Version, item.Scope)).ToArray(),
            project.Revision, project.CreatedAtUtc, project.UpdatedAtUtc);
        ServerManagedConfigurationContract Snapshot(params CodexServer.CentralProject[] projects)
        {
            var contracts = projects.Select(Contract).ToArray();
            return new(1, ManagedConfigurationSynchronizer.CalculateVersion(contracts), contracts);
        }
        var synchronizer = new ManagedConfigurationSynchronizer(cache, runtime);
        var configurations = synchronizer.Apply(Snapshot(central, incompatible));
        var configuration = configurations.Single(item => item.Configuration.Project.Repository == central.Repository).Configuration;
        Assert.False(Directory.Exists(runtime.CheckoutDirectory));
        Assert.Empty(ProjectConfigurationDiscovery.LoadForWorker(new GlobalWorkerConfiguration
        {
            Projects = new() { Ownership = "managed", Directory = Path.Combine(fixture.Root, "absent-yaml") }
        }));
        var workerId = Guid.NewGuid().ToString("N");
        CodexServer.WorkerCapability[] capabilities = [new("tool", "git"), new("agent-provider", "codex"),
            new("authentication", "github-api", Scope: central.Repository),
            new("authentication", "git-repository", Scope: central.Repository),
            new("authentication", "github-api", Scope: incompatible.Repository),
            new("authentication", "git-repository", Scope: incompatible.Repository)];
        await store.RegisterWorkerAsync(new(2, workerId, "Worker", "1", "linux", 1, capabilities));
        await store.HeartbeatWorkerAsync(new(2, workerId, "1", "running", 0, 1, capabilities, [],
            "synchronized", synchronizer.Status.AppliedVersion, ManagedDiagnostics: synchronizer.Status.Diagnostics));
        var worker = Assert.IsType<CodexServer.WorkerRegistrationResponse>(await store.GetWorkerAsync(workerId));
        var diagnostics = CodexServer.WorkerDiagnosticsDerivation.Derive(worker, [central, incompatible], [],
            Snapshot(central, incompatible).Version);
        Assert.True(diagnostics.Projects.Single(item => item.ProjectId == central.Id).IsEligible);
        Assert.False(diagnostics.Projects.Single(item => item.ProjectId == incompatible.Id).IsEligible);
        Assert.Contains(diagnostics.Projects.Single(item => item.ProjectId == incompatible.Id).MissingRequirements,
            requirement => requirement.Contains("unavailable-runtime", StringComparison.Ordinal));
        Assert.All(diagnostics.Projects, item => Assert.Equal("not-materialized", item.MaterializationState));
        var pending = await store.EnqueueExecutionAsync(new(incompatible.Id, new("github-issue", "18")));
        await store.EnqueueExecutionAsync(new(central.Id, new("github-issue", "17")));
        var assignment = await store.RequestAssignmentAsync(new(workerId, true, 1,
            new Dictionary<string, int> { [central.Id] = 1, [incompatible.Id] = 1 }));
        Assert.True(assignment.HasWork);
        Assert.Equal(central.Id, assignment.Assignment?.Project.Id);
        Assert.False(Directory.Exists(runtime.CheckoutDirectory));
        using (var repository = new GitRepository(new ProcessRunner(), configuration.Project.Directory,
            configuration.Project.Repository, configuration.Git, configuration.Worker))
        {
            await repository.ValidateManagedRemoteReadAsync(CancellationToken.None);
            Assert.False(Directory.Exists(runtime.CheckoutDirectory));
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        }
        var marker = Path.Combine(configuration.Project.Directory, ".git", "reuse-marker");
        await File.WriteAllTextAsync(marker, "preserved");
        var updated = Assert.IsType<CodexServer.CentralProject>(await store.UpdateProjectAsync(central.Id,
            new(central.Name, central.Repository, central.DefaultBranch, "Server revision", []), central.Revision));
        var restarted = new ManagedConfigurationSynchronizer(cache, runtime);
        Assert.Equal(configuration.Project.Directory, restarted.LoadLastValid()
            .Single(item => item.Configuration.Project.Repository == central.Repository).Configuration.Project.Directory);
        var authoritative = restarted.Apply(Snapshot(updated, incompatible))
            .Single(item => item.Configuration.Project.Repository == central.Repository).Configuration;
        Assert.Equal(updated.Revision, restarted.AppliedProjects.Single(item => item.Id == central.Id).Revision);
        using (var repository = new GitRepository(new ProcessRunner(), authoritative.Project.Directory,
            authoritative.Project.Repository, authoritative.Git, authoritative.Worker))
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        Assert.Equal("preserved", await File.ReadAllTextAsync(marker));
        Assert.Equal(configuration.Project.Directory, authoritative.Project.Directory);
        Assert.Equal("Queued", (await store.GetExecutionAsync(pending.Id))?.State);
        Assert.Single(Directory.GetDirectories(runtime.CheckoutDirectory));
    }

    [Fact]
    public async Task CatalogProbeDoesNotCloneAndFirstAssignmentPublishesCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        using var repository = fixture.Repository();
        await repository.ValidateManagedRemoteReadAsync(CancellationToken.None);
        Assert.False(Directory.Exists(fixture.Checkout));

        await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);

        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
        Assert.True(GitRepository.OriginMatchesRepository(await fixture.Git(fixture.Checkout,
            "config", "--get", "remote.origin.url"), "owner/repo"));
        Assert.Empty(Directory.GetFiles(fixture.Checkout, "*.yml"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrivateBootstrapUsesManagedCredentialsBeforeCloneAndForReadWrite(bool authorized)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await Fixture.CreateAsync();
        await fixture.RequireManagedCredentialsAsync(authorized);
        using var repository = fixture.Repository();
        if (!authorized)
        {
            var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
                repository.ValidateManagedRemoteReadAsync(CancellationToken.None));
            Assert.DoesNotContain("credential-sentinel", failure.ToString(), StringComparison.Ordinal);
            await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
                repository.MaterializeManagedCheckoutAsync(CancellationToken.None));
            Assert.False(Directory.Exists(fixture.Checkout));
            return;
        }
        await repository.ValidateManagedRemoteReadAsync(CancellationToken.None);
        Assert.False(Directory.Exists(fixture.Checkout));
        await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        await repository.ValidateRemoteAuthenticationAsync(CancellationToken.None);
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
        Assert.Equal("https://github.com/owner/repo.git", await fixture.Git(fixture.Checkout,
            "config", "--get", "remote.origin.url"));
        Assert.Empty(Directory.GetDirectories(fixture.Root, "checkout.clone-*"));
        var configuration = await fixture.Git(fixture.Checkout, "config", "--local", "--list");
        Assert.DoesNotContain("credential-sentinel", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("credential.helper", configuration, StringComparison.Ordinal);
        var calls = await File.ReadAllLinesAsync(Path.Combine(fixture.Root, "authenticated-calls"));
        Assert.Contains("clone", calls);
        Assert.Contains("ls-remote", calls);
        Assert.Contains("push --dry-run --porcelain", calls);
        Assert.Contains("pull", calls);
    }

    [Fact]
    public async Task CatalogAccessFailureDoesNotExposeProcessDiagnostics()
    {
        using var fixture = await Fixture.CreateAsync();
        Directory.Move(Path.Combine(fixture.Root, "origin.git"), Path.Combine(fixture.Root, "unavailable.git"));
        using var repository = fixture.Repository();

        var error = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            repository.ValidateManagedRemoteReadAsync(CancellationToken.None));

        Assert.Equal("Git repository read authentication is unavailable for 'owner/repo'.", error.Message);
        Assert.Null(error.InnerException);
        Assert.False(Directory.Exists(fixture.Checkout));
    }

    [Fact]
    public async Task ReuseAndRestartFastForwardWithoutReplacingLocalResources()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var repository = fixture.Repository())
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        await fixture.Git(fixture.Checkout, "branch", "preserved-recovery");
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "task.txt"), "updated");
        await fixture.Git(fixture.Source, "commit", "-am", "update");
        await fixture.Git(fixture.Source, "push", "origin", "main");

        using var restarted = fixture.Repository();
        await restarted.MaterializeManagedCheckoutAsync(CancellationToken.None);
        await restarted.MaterializeManagedCheckoutAsync(CancellationToken.None);

        Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
        Assert.Contains("preserved-recovery", await fixture.Git(fixture.Checkout, "branch", "--list"));
    }

    [Fact]
    public async Task RepositoryMismatchPreservesCheckoutWithoutUpdatingIt()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var repository = fixture.Repository())
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        await fixture.Git(fixture.Checkout, "remote", "set-url", "origin", "https://github.com/other/project.git");
        var head = await fixture.Git(fixture.Checkout, "rev-parse", "HEAD");

        using var restarted = fixture.Repository();
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => restarted.MaterializeManagedCheckoutAsync(CancellationToken.None));

        Assert.Equal(head, await fixture.Git(fixture.Checkout, "rev-parse", "HEAD"));
        Assert.Equal("https://github.com/other/project.git", await fixture.Git(fixture.Checkout, "config", "--get", "remote.origin.url"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsafeExistingCheckoutIsPreserved(bool dirty)
    {
        using var fixture = await Fixture.CreateAsync();
        using (var repository = fixture.Repository())
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        if (dirty) await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "task.txt"), "preserved edit");
        else await fixture.Git(fixture.Checkout, "switch", "-c", "preserved-branch");

        using var restarted = fixture.Repository();
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => restarted.MaterializeManagedCheckoutAsync(CancellationToken.None));

        if (dirty) Assert.Equal("preserved edit", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
        else Assert.Equal("preserved-branch", await fixture.Git(fixture.Checkout, "branch", "--show-current"));
    }

    [Fact]
    public async Task InterruptedCloneIsNotPublishedAndRestartUsesFreshStaging()
    {
        using var fixture = await Fixture.CreateAsync();
        // A completed clone whose validation failed is equivalent to a process
        // stopping before atomic publication: it must never become authoritative.
        using (var invalid = fixture.Repository(new GitSettings { FeaturePrefix = "invalid.." }))
            await Assert.ThrowsAsync<WorkerInfrastructureException>(() => invalid.MaterializeManagedCheckoutAsync(CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Checkout));
        var abandoned = Assert.Single(Directory.GetDirectories(fixture.Root, "checkout.clone-*"));

        using var restarted = fixture.Repository();
        await restarted.MaterializeManagedCheckoutAsync(CancellationToken.None);

        Assert.True(Directory.Exists(abandoned));
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
    }

    [Fact]
    public async Task MultipleAssignmentsShareRepositoryGateAndOneCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        using var repository = fixture.Repository();
        using var gate = new SemaphoreSlim(1, 1);
        async Task PrepareAsync()
        {
            await gate.WaitAsync();
            try { await repository.MaterializeManagedCheckoutAsync(CancellationToken.None); }
            finally { gate.Release(); }
        }
        await Task.WhenAll(PrepareAsync(), PrepareAsync());
        Assert.Empty(Directory.GetDirectories(fixture.Root, "checkout.clone-*"));
        Assert.Equal("main", await fixture.Git(fixture.Checkout, "branch", "--show-current"));
    }

    [Fact]
    public async Task CancellationDoesNotPublishCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        using var repository = fixture.Repository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.MaterializeManagedCheckoutAsync(cancellation.Token));
        Assert.False(Directory.Exists(fixture.Checkout));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousHome = Environment.GetEnvironmentVariable("HOME");
        private readonly string? _previousPath = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _previousToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        private readonly string? _previousGhConfig = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
        private readonly string? _previousXdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string? _previousGitConfiguration = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cw-managed-checkout", Guid.NewGuid().ToString("N"));
        public string Checkout => Path.Combine(Root, "checkout");
        public string Source => Path.Combine(Root, "source");
        public GitRepository Repository(GitSettings? settings = null) => new(new ProcessRunner(), Checkout,
            "owner/repo", settings ?? new GitSettings(), new WorkerSettings(), Path.Combine(Root, "worktrees"));

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture.Source);
                var bare = Path.Combine(fixture.Root, "origin.git");
                await fixture.Git(fixture.Root, "init", "--bare", "--initial-branch=main", bare);
                await fixture.Git(fixture.Source, "init", "-b", "main");
                await fixture.Git(fixture.Source, "config", "user.name", "Worker Tests");
                await fixture.Git(fixture.Source, "config", "user.email", "worker@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(fixture.Source, "task.txt"), "base");
                await fixture.Git(fixture.Source, "add", "task.txt");
                await fixture.Git(fixture.Source, "commit", "-m", "base");
                await fixture.Git(fixture.Source, "remote", "add", "origin", bare);
                await fixture.Git(fixture.Source, "push", "origin", "main");
                var configuration = Path.Combine(fixture.Root, "gitconfig");
                await fixture.Git(fixture.Root, "config", "--file", configuration,
                    $"url.file://{bare}.insteadOf", "https://github.com/owner/repo.git");
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", configuration);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public async Task RequireManagedCredentialsAsync(bool authorized)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            Environment.SetEnvironmentVariable("HOME", Root);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", Path.Combine(Root, "operator"));
            Environment.SetEnvironmentVariable("GH_TOKEN", "operator-token-sentinel");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Root, "xdg"));
            var setup = new CodexProvisioning.NodeGitHubSetup(unrelatedAuthentication: () => false);
            var prepared = await setup.ExecuteAsync(new("server", "github-cli", CodexProvisioning.ProvisioningCommandAction.PrepareAuthentication),
                CancellationToken.None);
            Assert.Equal(CodexProvisioning.ProvisioningCommandStatus.Succeeded, prepared.Status);
            var managed = Path.Combine(CodexProvisioning.NodeGitHubSetup.DefaultRoot, "github");
            var bin = Path.Combine(Root, "bin");
            Directory.CreateDirectory(bin);
            // The transport is local and deterministic, but every network operation must
            // first obtain a credential through the real Git helper protocol.
            await File.WriteAllTextAsync(Path.Combine(bin, "gh"), $"""
                #!/bin/sh
                [ "$GH_CONFIG_DIR" = '{managed}' ] || exit 1
                [ -z "$GH_TOKEN$GITHUB_TOKEN$GH_ENTERPRISE_TOKEN$GITHUB_ENTERPRISE_TOKEN" ] || exit 1
                [ "$*" = 'auth git-credential get' ] || exit 1
                cat >/dev/null
                { (authorized ? "printf 'username=worker\npassword=credential-sentinel\n'" : "exit 1") }
                """);
            await File.WriteAllTextAsync(Path.Combine(bin, "git"), $$"""
                #!/bin/sh
                case "$1" in
                  ls-remote|clone|push|pull|fetch)
                    printf 'protocol=https\nhost=github.com\n\n' | /usr/bin/git credential fill >/dev/null 2>&1 || { echo credential-sentinel >&2; exit 128; }
                    case "$1" in
                      push) echo 'push --dry-run --porcelain' >>'{{Root}}/authenticated-calls';;
                      *) echo "$1" >>'{{Root}}/authenticated-calls';;
                    esac
                    ;;
                esac
                exec /usr/bin/git "$@"
                """);
            File.SetUnixFileMode(Path.Combine(bin, "gh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(bin, "git"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + _previousPath);
        }

        public async Task<string> Git(string directory, params string[] arguments)
        {
            var result = await new ProcessRunner().RunAsync("git", arguments, directory, TimeSpan.FromSeconds(15));
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput.Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", _previousGitConfiguration);
            Environment.SetEnvironmentVariable("HOME", _previousHome);
            Environment.SetEnvironmentVariable("PATH", _previousPath);
            Environment.SetEnvironmentVariable("GH_TOKEN", _previousToken);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", _previousGhConfig);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousXdgConfig);
            Directory.Delete(Root, recursive: true);
        }
    }
}
