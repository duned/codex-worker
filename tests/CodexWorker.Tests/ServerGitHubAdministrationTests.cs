namespace CodexWorker.Tests;

using CodexServer;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

public sealed class ServerGitHubAdministrationTests
{
    [Fact]
    public async Task ReadServiceBoundsQueriesReturnsLabelsAndBlockedByAndScopesAccessDiagnostics()
    {
        var commands = new List<IReadOnlyList<string>>();
        Task<GitHubReadCommandResult> Run(IReadOnlyList<string> arguments, CancellationToken _)
        {
            commands.Add(arguments);
            if (arguments.SequenceEqual(["auth", "status", "--hostname", "github.com"]))
                return Task.FromResult(new GitHubReadCommandResult(0, "", ""));
            if (arguments.SequenceEqual(["api", "repos/team/project", "--jq", ".full_name"]))
                return Task.FromResult(new GitHubReadCommandResult(0, "team/project\n", ""));
            if (arguments[0] == "issue" && arguments[1] == "list")
                return Task.FromResult(new GitHubReadCommandResult(0, """
                    [{"number":7,"title":"Build it","body":"Details","state":"OPEN","createdAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-02T00:00:00Z","url":"https://github.com/team/project/issues/7","labels":[{"name":"ready"}]}]
                    """, ""));
            if (arguments[0] == "issue" && arguments[1] == "view")
                return Task.FromResult(new GitHubReadCommandResult(0, """
                    {"number":7,"title":"Build it","body":"Details","state":"OPEN","createdAt":"2026-09-01T00:00:00Z","updatedAt":"2026-09-02T00:00:00Z","url":"https://github.com/team/project/issues/7","labels":[{"name":"ready"}]}
                    """, ""));
            if (arguments[0] == "api" && arguments.Contains("repos/team/project/issues/7/dependencies/blocked_by"))
                return Task.FromResult(new GitHubReadCommandResult(0, "[{\"number\":3,\"title\":\"Prerequisite\",\"state\":\"open\",\"html_url\":\"https://github.com/team/project/issues/3\"}]\n", ""));
            return Task.FromResult(new GitHubReadCommandResult(1, "", "private-token=must-not-leak"));
        }

        var service = new ServerGitHubReadService(Run, new TestTimeProvider(DateTimeOffset.Parse("2026-09-10T00:00:00Z")));
        var project = Project(issueReadyLabel: "ready");
        var access = await service.CheckAccessAsync(project);
        var issues = await service.ListIssuesAsync(project, new GitHubIssueQuery("open", 2, "ready"));
        var detail = await service.GetIssueAsync(project, 7);

        Assert.True(access.CliAuthenticated);
        Assert.True(access.RepositoryReadable);
        Assert.Contains("does not establish Issue write", access.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T00:00:00Z"), access.CheckedAtUtc);
        var issue = Assert.Single(issues);
        Assert.False(issue.IsEligible);
        Assert.Equal("ready", Assert.Single(issue.Labels));
        Assert.Equal(3, Assert.Single(issue.BlockedBy).Number);
        Assert.Contains("#3", Assert.Single(issue.EligibilityReasons), StringComparison.Ordinal);
        Assert.NotNull(detail);
        Assert.False(detail.IsEligible);
        Assert.Equal(3, Assert.Single(detail.BlockedBy).Number);
        var query = Assert.Single(commands, command => command[0] == "issue" && command[1] == "list");
        Assert.Contains("--limit", query);
        Assert.Equal("2", query[Array.IndexOf(query.ToArray(), "--limit") + 1]);
        Assert.Equal("ready", query[Array.IndexOf(query.ToArray(), "--label") + 1]);
        Assert.DoesNotContain(commands, command => command.Contains("permissions", StringComparer.Ordinal));
    }

    [Fact]
    public async Task AccessDiagnosticsDoNotReturnRawGhAuthenticationErrors()
    {
        var service = new ServerGitHubReadService((_, _) => Task.FromResult(
            new GitHubReadCommandResult(1, "", "gh: password=private-token")));
        var access = await service.CheckAccessAsync(Project());

        Assert.False(access.CliAuthenticated);
        Assert.False(access.RepositoryReadable);
        Assert.Contains("authentication is unavailable", access.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", access.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignmentRechecksDependenciesReleasesBlockedReservationAndOnlyReturnsEligibleWork()
    {
        using var temporary = new TemporaryDirectory();
        var registry = new SqliteRegistryStore(Path.Combine(temporary.Path, "github-assignment.db"));
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(ProjectDefinition("Queue project", issueReadyLabel: "ready"));
        var blocked = await registry.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "10")));
        var eligible = await registry.EnqueueExecutionAsync(new(project.Id, new WorkReference("github-issue", "11")));
        var workerId = Guid.NewGuid().ToString("N");
        CodexServer.WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, capabilities));
        await registry.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var read = new FakeServerGitHubReadService();
        read.Add(project.Repository, Issue(10, labels: ["ready"], blockers: [BlockingIssue(40, "open")]));
        read.Add(project.Repository, Issue(11, labels: ["ready"]));
        var service = new ServerGitHubAdministrationService(registry, read);

        var result = await service.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }));

        Assert.True(result.HasWork);
        Assert.Equal(eligible.Id, result.Assignment!.ServerExecutionId);
        var blockedAfter = await registry.GetExecutionAsync(blocked.Id);
        Assert.Equal("Queued", blockedAfter!.State);
        Assert.Equal("blocked", blockedAfter.ManagedEligibilityState);
        Assert.Contains("#40", Assert.Single(blockedAfter.ManagedEligibilityReasons!), StringComparison.Ordinal);
        Assert.Equal("Assigned", (await registry.GetExecutionAsync(eligible.Id))!.State);
        read.Add(project.Repository, Issue(10, labels: ["ready"]));
        var refreshed = Assert.Single((await service.RefreshQueuedEligibilityAsync(project.Id, 10))!);
        Assert.Equal("eligible", refreshed.ManagedEligibilityState);
        Assert.Empty(refreshed.ManagedEligibilityReasons!);
    }

    [Fact]
    public async Task AssignmentReadFailureReleasesReservationAndDoesNotReturnWork()
    {
        using var temporary = new TemporaryDirectory();
        var registry = new SqliteRegistryStore(Path.Combine(temporary.Path, "github-read-failure.db"));
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(ProjectDefinition("Read failure", issueReadyLabel: "ready"));
        var execution = await registry.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "8")));
        var workerId = Guid.NewGuid().ToString("N");
        CodexServer.WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, capabilities));
        await registry.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var failure = new GitHubReadUnavailableException(project.Repository, "Scoped read failure", "read-failed");
        var service = new ServerGitHubAdministrationService(registry, new FakeServerGitHubReadService(failure));

        await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId,
            true, 1, new Dictionary<string, int> { [project.Id] = 1 })));

        var queued = await registry.GetExecutionAsync(execution.Id);
        Assert.Equal("Queued", queued!.State);
        Assert.Equal("unavailable", queued.ManagedEligibilityState);
        Assert.Contains("could not be checked", Assert.Single(queued.ManagedEligibilityReasons!), StringComparison.Ordinal);
        Assert.Equal("Released", queued.Lease!.State);
    }

    [Fact]
    public async Task GitHubApiRequiresManagementAuthBoundsIssueQueriesAndOnlyExplicitlyEnqueuesEligibleIssues()
    {
        using var temporary = new TemporaryDirectory();
        const string managementToken = "github-admin-test-token";
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", managementToken);
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            var github = new FakeServerGitHubReadService();
            github.Add("team/project", Issue(21, labels: ["ready"], blockers: [BlockingIssue(22, "open")]));
            github.Add("team/project", Issue(22, labels: ["ready"]));
            github.Add("team/project", Issue(23, labels: ["ready", "blocked"]));
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "github-api.db")), githubReadService: github);
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var project = await registry.CreateProjectAsync(ProjectDefinition("API project", "team/project", "ready", "blocked"));
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/projects/{project.Id}/github/issues")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/projects/{project.Id}/github/issues?limit=101")).StatusCode);
            using var detailResponse = await client.GetAsync($"/api/v1/projects/{project.Id}/github/issues/23");
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            var detail = await detailResponse.Content.ReadFromJsonAsync<ManagedGitHubIssue>();
            Assert.NotNull(detail);
            Assert.False(detail.IsEligible);
            Assert.Contains("blocked label", Assert.Single(detail.EligibilityReasons), StringComparison.Ordinal);
            using var listResponse = await client.GetAsync($"/api/v1/projects/{project.Id}/github/issues?state=open&limit=2");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var issues = await listResponse.Content.ReadFromJsonAsync<ManagedGitHubIssue[]>();
            Assert.NotNull(issues);
            Assert.Equal(2, issues.Length);
            Assert.False(issues[0].IsEligible);
            Assert.Empty(await registry.GetExecutionsAsync());
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PostAsync($"/api/v1/projects/{project.Id}/github/issues/21/enqueue", null)).StatusCode);
            using var enqueued = await client.PostAsync($"/api/v1/projects/{project.Id}/github/issues/22/enqueue", null);
            Assert.Equal(HttpStatusCode.Created, enqueued.StatusCode);
            var execution = await enqueued.Content.ReadFromJsonAsync<ExecutionRequest>();
            Assert.NotNull(execution);
            Assert.Equal("github-issue", execution.WorkReference.Type);
            Assert.Equal("22", execution.WorkReference.Id);
            Assert.Equal($"https://github.com/{project.Repository}/issues/22", execution.WorkReference.Url);
            Assert.Equal("eligible", execution.ManagedEligibilityState);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Fact]
    public async Task LocalCliListsIssuesAndEnqueuesOnlyOnExplicitCommand()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "github-cli.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(ProjectDefinition("CLI project", issueReadyLabel: "ready"));
        var read = new FakeServerGitHubReadService();
        read.Add(project.Repository, Issue(31, labels: ["ready"]));
        var factory = new StubGitHubAdministrationFactory(new ServerGitHubAdministrationService(registry, read));
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new ServerAdministrationCli(new ServerConfigurationAdministrationService(), factory, output, error);

        var configuration = new[] { $"--Server:DataDirectory={temporary.Path}", $"--Server:DatabasePath={database}" };
        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["github", "issues", project.Id, "--json", .. configuration]));
        Assert.Empty(await registry.GetExecutionsAsync());
        output.GetStringBuilder().Clear();
        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["github", "enqueue", project.Id, "31", .. configuration]));

        Assert.Single(await registry.GetExecutionsAsync());
        Assert.Contains("Execution:", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    private static CentralProject Project(string name = "GitHub project", string repository = "team/project",
        string? issueReadyLabel = null, string? issueBlockedLabel = null) =>
        new("project-id", name, repository, "main", "", [], 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            IssueReadyLabel: issueReadyLabel, IssueBlockedLabel: issueBlockedLabel);

    private static CentralProjectDefinition ProjectDefinition(string name = "GitHub project", string repository = "team/project",
        string? issueReadyLabel = null, string? issueBlockedLabel = null) =>
        new(name, repository, "main", "", [], issueReadyLabel, issueBlockedLabel);

    private static GitHubBlockingIssue BlockingIssue(int number, string state) =>
        new(number, "Prerequisite", state, $"https://github.com/team/project/issues/{number}");

    private static ManagedGitHubIssue Issue(int number, IReadOnlyList<string>? labels = null,
        IReadOnlyList<GitHubBlockingIssue>? blockers = null) =>
        new(number, $"Issue {number}", "Task details", "OPEN", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            $"https://github.com/team/project/issues/{number}", labels ?? [], blockers ?? [], true, []);

    private static CodexServer.WorkerCapability[] AuthenticationCapabilities(string repository) =>
        [new("authentication", "github-api", Scope: repository), new("authentication", "git-repository", Scope: repository),
         new("agent-provider", "codex")];

    private static string[] Args(string url, string database) =>
        [$"--Server:ListenUrl={url}", $"--Server:DataDirectory={Path.GetDirectoryName(database)}", $"--Server:DatabasePath={database}"];

    private static int ReservePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeServerGitHubReadService(GitHubReadUnavailableException? failure = null) : IServerGitHubReadService
    {
        private readonly Dictionary<(string Repository, int Number), ManagedGitHubIssue> _issues = [];
        public void Add(string repository, ManagedGitHubIssue issue) => _issues[(repository, issue.Number)] = issue;

        public Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitHubRepositoryAccess(project.Repository, true, true, "read-only access", DateTimeOffset.UnixEpoch));

        public Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query,
            CancellationToken cancellationToken = default)
        {
            if (failure is not null) return Task.FromException<IReadOnlyList<ManagedGitHubIssue>>(failure);
            var issues = _issues.Where(entry => entry.Key.Repository == project.Repository)
                .Select(entry => entry.Value)
                .Where(issue => query.State == "all" || query.State == "open" && issue.State == "OPEN" || query.State == "closed" && issue.State == "CLOSED")
                .Where(issue => query.Label is null || issue.Labels.Contains(query.Label, StringComparer.OrdinalIgnoreCase))
                .Take(query.Limit)
                .Select(issue =>
                {
                    var reasons = new List<string>();
                    if (issue.State != "OPEN") reasons.Add("Issue is closed.");
                    if (project.IssueReadyLabel is { } ready && !issue.Labels.Contains(ready, StringComparer.OrdinalIgnoreCase))
                        reasons.Add($"Issue is missing ready label '{ready}'.");
                    if (project.IssueBlockedLabel is { } blocked && issue.Labels.Contains(blocked, StringComparer.OrdinalIgnoreCase))
                        reasons.Add($"Issue has blocked label '{blocked}'.");
                    if (issue.BlockedBy.Any(blocker => blocker.State == "open")) reasons.Add("Issue is blocked by open Issue(s).");
                    return issue with { IsEligible = reasons.Count == 0, EligibilityReasons = reasons };
                }).ToArray();
            return Task.FromResult<IReadOnlyList<ManagedGitHubIssue>>(issues);
        }

        public Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default)
        {
            if (failure is not null) return Task.FromException<ManagedGitHubIssue?>(failure);
            if (!_issues.TryGetValue((project.Repository, issueNumber), out var issue)) return Task.FromResult<ManagedGitHubIssue?>(null);
            var reasons = new List<string>();
            if (issue.State != "OPEN") reasons.Add("Issue is closed.");
            if (project.IssueReadyLabel is { } ready && !issue.Labels.Contains(ready, StringComparer.OrdinalIgnoreCase))
                reasons.Add($"Issue is missing ready label '{ready}'.");
            if (project.IssueBlockedLabel is { } blocked && issue.Labels.Contains(blocked, StringComparer.OrdinalIgnoreCase))
                reasons.Add($"Issue has blocked label '{blocked}'.");
            if (issue.BlockedBy.Any(blocker => blocker.State == "open")) reasons.Add("Issue is blocked by open Issue(s): " +
                string.Join(", ", issue.BlockedBy.Where(blocker => blocker.State == "open").Select(blocker => "#" + blocker.Number)) + ".");
            return Task.FromResult<ManagedGitHubIssue?>(issue with { IsEligible = reasons.Count == 0, EligibilityReasons = reasons });
        }
    }

    private sealed class StubGitHubAdministrationFactory(IServerGitHubAdministrationService github) : IServerAdministrationServiceFactory
    {
        public IServerAdministrationService Create(ServerConfiguration configuration)
        {
            var registry = new SqliteRegistryStore(configuration.ResolveDatabasePath());
            return new LocalServerAdministrationService(configuration, registry, new ServerHealthService(registry), githubAdministration: github);
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"server-github-admin-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}

public sealed class AlwaysEligibleServerGitHubReadService : IServerGitHubReadService
{
    public Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GitHubRepositoryAccess(project.Repository, true, true, "read-only access", DateTimeOffset.UnixEpoch));

    public Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ManagedGitHubIssue>>([]);

    public Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult<ManagedGitHubIssue?>(new ManagedGitHubIssue(issueNumber, $"Issue {issueNumber}", "", "OPEN",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, $"https://github.com/{project.Repository}/issues/{issueNumber}",
            ["ready"], [], true, []));
}
