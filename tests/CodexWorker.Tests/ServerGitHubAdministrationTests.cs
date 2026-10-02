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
    public async Task IssueWriteServiceUsesProjectScopedTypedArgumentsAndReturnsSafeFailures()
    {
        var commands = new List<IReadOnlyList<string>>();
        Task<GitHubReadCommandResult> Run(IReadOnlyList<string> arguments, CancellationToken _)
        {
            commands.Add(arguments.ToArray());
            if (arguments.SequenceEqual(["api", "repos/team/project/issues/32", "--jq", ".id"]))
                return Task.FromResult(new GitHubReadCommandResult(0, "12345\n", ""));
            if (arguments.Contains("--jq", StringComparer.Ordinal))
                return Task.FromResult(new GitHubReadCommandResult(0,
                    "{\"number\":31,\"title\":\"Created\",\"body\":\"Details\",\"html_url\":\"https://github.com/team/project/issues/31\"}", ""));
            return Task.FromResult(new GitHubReadCommandResult(0, "", ""));
        }

        var writer = new ServerGitHubIssueWriteService(Run);
        var project = Project(issueReadyLabel: "ready", issueBlockedLabel: "blocked");
        var created = await writer.CreateIssueAsync(project, "Add feature", "Details");
        var updated = await writer.UpdateIssueAsync(project, 31, "Changed", "New body");
        await writer.AddLabelAsync(project, 31, "ready");
        await writer.RemoveLabelAsync(project, 31, "blocked");
        await writer.AddBlockedByAsync(project, 31, 32);
        await writer.RemoveBlockedByAsync(project, 31, 32);

        Assert.Equal(31, created.Number);
        Assert.Equal("https://github.com/team/project/issues/31", updated.Url);
        Assert.Contains(commands, args => args.SequenceEqual(["api", "--method", "POST", "repos/team/project/issues", "-f",
            "title=Add feature", "-f", "body=Details", "--jq", "{number,title,body,html_url}"]));
        Assert.Contains(commands, args => args.SequenceEqual(["api", "--method", "PATCH", "repos/team/project/issues/31", "-f",
            "title=Changed", "-f", "body=New body", "--jq", "{number,title,body,html_url}"]));
        Assert.Contains(commands, args => args.SequenceEqual(["api", "--method", "POST", "repos/team/project/issues/31/labels", "-f", "labels[]=ready"]));
        Assert.Contains(commands, args => args.SequenceEqual(["api", "--method", "DELETE", "repos/team/project/issues/31/labels/blocked"]));
        Assert.Contains(commands, args => args.SequenceEqual(["api", "--method", "POST", "repos/team/project/issues/31/dependencies/blocked_by", "-F", "issue_id=12345"]));
        Assert.Contains(commands, args => args.SequenceEqual(["api", "--method", "DELETE", "repos/team/project/issues/31/dependencies/blocked_by/12345"]));
        Assert.DoesNotContain(commands.SelectMany(args => args), argument => argument is "close" or "comment");
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.AddLabelAsync(project, 31, "worker:done"));

        var failingWriter = new ServerGitHubIssueWriteService((_, _) => Task.FromResult(
            new GitHubReadCommandResult(1, "private-output", "HTTP 403: token=do-not-report")));
        var failure = await Assert.ThrowsAsync<GitHubIssueWriteUnavailableException>(() =>
            failingWriter.CreateIssueAsync(project, "Safe title", "Safe body"));
        Assert.Equal("write-permission", failure.Code);
        Assert.Contains("scoped Issue write permission", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-report", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-output", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueMutationPreviewValidationAndIdempotencyStayProjectScoped()
    {
        using var temporary = new TemporaryDirectory();
        var registry = new SqliteRegistryStore(Path.Combine(temporary.Path, "github-write-preview.db"));
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(ProjectDefinition(issueReadyLabel: "ready", issueBlockedLabel: "blocked"));
        var read = new FakeServerGitHubReadService();
        read.Add(project.Repository, Issue(41, labels: ["ready"], blockers: [BlockingIssue(42, "open")]));
        read.Add(project.Repository, Issue(42));
        var writer = new FakeGitHubIssueWriteService();
        var service = new ServerGitHubAdministrationService(registry, read, issueWriter: writer);

        var createPreview = await service.CreateIssueAsync(project.Id, new("New task", "Task body", PreviewOnly: true));
        Assert.True(createPreview.PreviewOnly);
        Assert.Null(createPreview.IssueNumber);
        Assert.Empty(writer.Operations);
        var updatePreview = await service.UpdateIssueAsync(project.Id, 41, new(Title: "Edited title", PreviewOnly: true));
        Assert.True(updatePreview.Changed);
        Assert.Equal("Edited title", updatePreview.Title);
        Assert.Equal("Task details", updatePreview.Body);
        Assert.Empty(writer.Operations);

        var alreadyReady = await service.SetIssueLabelAsync(project.Id, 41, new("ready", Applied: true));
        Assert.False(alreadyReady.Changed);
        var removePreview = await service.SetIssueLabelAsync(project.Id, 41, new("ready", Applied: false, PreviewOnly: true));
        Assert.True(removePreview.PreviewOnly);
        Assert.True(removePreview.Changed);
        var existingRelationship = await service.SetIssueBlockedByAsync(project.Id, 41, new(42, Applied: true));
        Assert.False(existingRelationship.Changed);
        var removeRelationshipPreview = await service.SetIssueBlockedByAsync(project.Id, 41, new(42, Applied: false, PreviewOnly: true));
        Assert.True(removeRelationshipPreview.Changed);
        Assert.Empty(writer.Operations);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetIssueLabelAsync(project.Id, 41,
            new("worker:done", Applied: true)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetIssueBlockedByAsync(project.Id, 41,
            new(41, Applied: true)));
        Assert.Empty(writer.Operations);

        var created = await service.CreateIssueAsync(project.Id, new("New task", "Task body"));
        var updated = await service.UpdateIssueAsync(project.Id, 41, new(Body: "Updated body"));
        var labelAdded = await service.SetIssueLabelAsync(project.Id, 41, new("blocked", Applied: true));
        var dependencyRemoved = await service.SetIssueBlockedByAsync(project.Id, 41, new(42, Applied: false));
        Assert.Equal("create", created.Operation);
        Assert.Equal("update", updated.Operation);
        Assert.True(labelAdded.Changed);
        Assert.True(dependencyRemoved.Changed);
        Assert.Equal(new[] { "create", "update", "label:add", "blocked-by:remove" }, writer.Operations);
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
    public async Task GitHubMutationApiRequiresManagementAuthAndPreviewsBeforeScopedWrites()
    {
        using var temporary = new TemporaryDirectory();
        const string managementToken = "github-mutation-admin-token";
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", managementToken);
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            var github = new FakeServerGitHubReadService();
            github.Add("team/project", Issue(55, labels: ["ready"]));
            github.Add("team/project", Issue(56));
            var writer = new FakeGitHubIssueWriteService();
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "github-mutation-api.db")),
                githubReadService: github, githubIssueWriteService: writer);
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var project = await registry.CreateProjectAsync(ProjectDefinition("Mutation API", "team/project", "ready", "blocked"));
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            const string issuePath = "/api/v1/projects/";
            using var unauthenticated = await client.PostAsJsonAsync($"{issuePath}{project.Id}/github/issues",
                new GitHubIssueCreateRequest("New issue", "Body", PreviewOnly: true));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);

            using var previewResponse = await client.PostAsJsonAsync($"{issuePath}{project.Id}/github/issues",
                new GitHubIssueCreateRequest("New issue", "Body", PreviewOnly: true));
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            var preview = await previewResponse.Content.ReadFromJsonAsync<GitHubIssueMutationResult>();
            Assert.True(preview!.PreviewOnly);
            Assert.Empty(writer.Operations);

            using var createdResponse = await client.PostAsJsonAsync($"{issuePath}{project.Id}/github/issues",
                new GitHubIssueCreateRequest("New issue", "Body"));
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
            var created = await createdResponse.Content.ReadFromJsonAsync<GitHubIssueMutationResult>();
            Assert.Equal(101, created!.IssueNumber);

            using var updateRequest = new HttpRequestMessage(HttpMethod.Patch, $"{issuePath}{project.Id}/github/issues/55")
            {
                Content = JsonContent.Create(new GitHubIssueUpdateRequest(Title: "Edited"))
            };
            using var updateResponse = await client.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            var updated = await updateResponse.Content.ReadFromJsonAsync<GitHubIssueMutationResult>();
            Assert.Equal("Edited", updated!.Title);

            using var invalidLabel = await client.PutAsJsonAsync($"{issuePath}{project.Id}/github/issues/55/labels/configured",
                new GitHubIssueLabelRequest("worker:done", Applied: true));
            Assert.Equal(HttpStatusCode.BadRequest, invalidLabel.StatusCode);
            using var labelResponse = await client.PutAsJsonAsync($"{issuePath}{project.Id}/github/issues/55/labels/configured",
                new GitHubIssueLabelRequest("blocked", Applied: true));
            Assert.Equal(HttpStatusCode.OK, labelResponse.StatusCode);
            var labelResult = await labelResponse.Content.ReadFromJsonAsync<GitHubIssueMutationResult>();
            Assert.True(labelResult!.Applied);

            using var dependencyPreview = await client.PutAsJsonAsync($"{issuePath}{project.Id}/github/issues/55/dependencies/blocked-by",
                new GitHubIssueDependencyRequest(56, Applied: true, PreviewOnly: true));
            Assert.Equal(HttpStatusCode.OK, dependencyPreview.StatusCode);
            Assert.True((await dependencyPreview.Content.ReadFromJsonAsync<GitHubIssueMutationResult>())!.PreviewOnly);
            using var dependencyWrite = await client.PutAsJsonAsync($"{issuePath}{project.Id}/github/issues/55/dependencies/blocked-by",
                new GitHubIssueDependencyRequest(56, Applied: true));
            Assert.Equal(HttpStatusCode.OK, dependencyWrite.StatusCode);

            Assert.Equal(new[] { "create", "update", "label:add", "blocked-by:add" }, writer.Operations);
            Assert.Empty(await registry.GetExecutionsAsync());
            Assert.DoesNotContain(writer.Operations, operation => operation.Contains("comment", StringComparison.OrdinalIgnoreCase) ||
                operation.Contains("close", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task LocalCliPreviewsIssueMutationsAndScopesLabelAdministration()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "github-mutation-cli.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(ProjectDefinition("CLI project", issueReadyLabel: "ready", issueBlockedLabel: "blocked"));
        var read = new FakeServerGitHubReadService();
        read.Add(project.Repository, Issue(31, labels: ["ready"]));
        var writer = new FakeGitHubIssueWriteService();
        var service = new ServerGitHubAdministrationService(registry, read, issueWriter: writer);
        var factory = new StubGitHubAdministrationFactory(service);
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new ServerAdministrationCli(new ServerConfigurationAdministrationService(), factory, output, error);
        var configuration = new[] { $"--Server:DataDirectory={temporary.Path}", $"--Server:DatabasePath={database}" };

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["github", "create", project.Id,
            "--title", "New issue", "--body", "Details", "--preview", .. configuration]));
        Assert.Contains("Preview: create Issue", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(writer.Operations);
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["github", "update", project.Id, "31",
            "--body", "Updated", "--preview", .. configuration]));
        Assert.Contains("Body: Updated", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(writer.Operations);
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["github", "label", project.Id, "31", "remove", "ready", .. configuration]));
        Assert.Equal(new[] { "label:remove" }, writer.Operations);
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

    private sealed class FakeGitHubIssueWriteService : IServerGitHubIssueWriteService
    {
        public List<string> Operations { get; } = [];

        public Task<GitHubIssueWriteResponse> CreateIssueAsync(CentralProject project, string title, string body,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("create");
            return Task.FromResult(new GitHubIssueWriteResponse(101, title, body, $"https://github.com/{project.Repository}/issues/101"));
        }

        public Task<GitHubIssueWriteResponse> UpdateIssueAsync(CentralProject project, int issueNumber, string? title, string? body,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("update");
            return Task.FromResult(new GitHubIssueWriteResponse(issueNumber, title ?? $"Issue {issueNumber}", body ?? "Task details",
                $"https://github.com/{project.Repository}/issues/{issueNumber}"));
        }

        public Task AddLabelAsync(CentralProject project, int issueNumber, string label, CancellationToken cancellationToken = default)
        {
            Operations.Add("label:add");
            return Task.CompletedTask;
        }

        public Task RemoveLabelAsync(CentralProject project, int issueNumber, string label, CancellationToken cancellationToken = default)
        {
            Operations.Add("label:remove");
            return Task.CompletedTask;
        }

        public Task AddBlockedByAsync(CentralProject project, int issueNumber, int blockerIssueNumber,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("blocked-by:add");
            return Task.CompletedTask;
        }

        public Task RemoveBlockedByAsync(CentralProject project, int issueNumber, int blockerIssueNumber,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("blocked-by:remove");
            return Task.CompletedTask;
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
