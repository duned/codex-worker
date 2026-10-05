using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class ManagedCheckoutTests
{
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
