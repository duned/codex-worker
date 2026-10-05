namespace CodexWorker.Tests;

using CodexServer;
using System.Text.Json;

public sealed class GitHubIssueReadProviderTests
{
    [Fact]
    public async Task DiscoveryUsesBatchesCacheFreshnessAndAliasPolicyWithoutCreatingExecutions()
    {
        using var directory = new TemporaryDirectory();
        var registry = new SqliteRegistryStore(directory.Database);
        await registry.InitializeAsync();
        var first = await registry.CreateProjectAsync(new("First", "TEAM/PROJECT", "main", "",
            IssueReadyLabel: "custom-ready", IssueBlockedLabel: "hold"));
        var alias = first with { Repository = "team/project", IssueBlockedLabel = "other-hold" };
        var clock = new Clock();
        var source = new Source { Count = 12, Labels = ["custom-ready", "hold"], NextCursor = "later" };
        var provider = new GitHubIssueReadProvider(source, directory.Database, clock);
        var service = new ServerGitHubAdministrationService(registry, provider, clock);
        var result = await service.DiscoverIssuesAsync(first.Id, new(12));
        Assert.NotNull(result);
        Assert.False(result.IsComplete);
        Assert.Equal("later", result.NextCursor);
        Assert.Equal("team/project", result.Repository);
        Assert.Equal(12, result.Candidates.Count);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal("ineligible", candidate.Classification);
            Assert.Contains("Issue has blocked label 'hold'.", candidate.Reasons);
            Assert.Equal("github-issue", candidate.WorkReference.Type);
            Assert.StartsWith("https://github.com/team/project/issues/", candidate.WorkReference.Url, StringComparison.Ordinal);
        });
        Assert.Equal(2, source.Batches.Count);
        Assert.Equal(10, source.Batches[0].Count);
        var aliasPage = await provider.ReadDiscoveryPageAsync(alias, new(12, "later"));
        Assert.All(aliasPage.Issues, issue => Assert.True(issue.IsEligible));
        Assert.Equal(2, source.Batches.Count);
        source.Labels = [];
        clock.Advance(TimeSpan.FromSeconds(31));
        aliasPage = await provider.ReadDiscoveryPageAsync(alias, new(12));
        Assert.All(aliasPage.Issues, issue => Assert.Contains("Issue is missing ready label 'custom-ready'.", issue.EligibilityReasons));
        Assert.Equal(4, source.Batches.Count);
        Assert.Empty(await registry.ListExecutionsAsync(new()));
    }

    [Theory]
    [InlineData("OPEN", "open", "ineligible")]
    [InlineData("OPEN", "closed", "eligible")]
    [InlineData("CLOSED", "closed", "ineligible")]
    public async Task DiscoveryReevaluatesStateAndNativeDependenciesIgnoresHierarchyAndDeduplicates(
        string state, string blockerState, string classification)
    {
        using var directory = new TemporaryDirectory();
        var registry = new SqliteRegistryStore(directory.Database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(new("Project", "team/project", "main", "", IssueReadyLabel: "ready"));
        var source = new Source { State = state, Labels = ["ready"], BlockerState = blockerState,
            DuplicateDiscovery = true, Overlap = true };
        var service = new ServerGitHubAdministrationService(registry, new GitHubIssueReadProvider(source, directory.Database));
        var result = await service.DiscoverIssuesAsync(project.Id, new());
        Assert.NotNull(result);
        Assert.True(result.IsComplete);
        Assert.Equal(classification, Assert.Single(result.Candidates).Classification);
        Assert.Single(source.Batches);
    }

    [Theory]
    [InlineData("read-unavailable")]
    [InlineData("rate-limited")]
    [InlineData("authentication-failed")]
    public async Task DiscoveryFailsClosedWhenStaleSnapshotCannotRefresh(string code)
    {
        using var directory = new TemporaryDirectory();
        var registry = new SqliteRegistryStore(directory.Database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(new("Project", "team/project", "main", ""));
        var source = new Source();
        var clock = new Clock();
        var service = new ServerGitHubAdministrationService(registry, new GitHubIssueReadProvider(source, directory.Database, clock));
        Assert.NotNull(await service.DiscoverIssuesAsync(project.Id, new()));
        clock.Advance(TimeSpan.FromSeconds(31));
        source.Failure = new(project.Repository, "Read unavailable", code);
        var error = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.DiscoverIssuesAsync(project.Id, new()));
        Assert.Equal(code, error.Code);
        Assert.Empty(await registry.GetExecutionsAsync());
    }

    [Fact]
    public async Task DiscoveryDoesNotReturnPartialPageWhenAnIssueDisappears()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source { Count = 2, OmitLast = true };
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        var error = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => provider.ReadDiscoveryPageAsync(Project(), new()));
        Assert.Equal("read-unavailable", error.Code);
    }

    [Fact]
    public async Task IdentitiesSurviveRestartRefreshAndRepositoryAliasesWithoutCollisions()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source();
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        await provider.GetIssueAsync(Project(), 1);
        Assert.Null(Assert.Single(source.Batches)[1]);
        provider = new(source, directory.Database);
        await provider.GetIssueAsync(Project("TEAM/PROJECT"), 1);
        Assert.Single(source.Batches);
        using (provider.BeginOperation(refresh: true))
        {
            await provider.GetIssueAsync(Project(), 1);
            await provider.GetIssueRelationshipsAsync(Project(), 1);
            await provider.GetIssueAsync(Project(), 1);
            Assert.Equal(101, await provider.GetDatabaseIdentityAsync(Project(), 1, CancellationToken.None));
        }
        Assert.Equal(2, source.Batches.Count);
        Assert.Equal("node-team/project-1", source.Batches[1][1]);
        await provider.GetIssueAsync(Project("other/project"), 1);
        Assert.Null(source.Batches[2][1]);
        Assert.Equal(3, source.Batches.Count);
    }

    [Theory]
    [InlineData("OPEN", "codex-ready", 31, 2)]
    [InlineData("CLOSED", "codex-failed", 31, 2)]
    [InlineData("CLOSED", "codex-done", 86400, 1)]
    [InlineData("CLOSED", "codex-done", 2678400, 2)]
    public async Task SnapshotFreshnessIsConservativeExceptForCompletedHistoricalIssues(string state, string label,
        int elapsedSeconds, int expectedCalls)
    {
        using var directory = new TemporaryDirectory();
        var clock = new Clock();
        var source = new Source { State = state, Labels = [label] };
        var provider = new GitHubIssueReadProvider(source, directory.Database, clock);
        await provider.GetIssueAsync(Project(), 1);
        clock.Advance(TimeSpan.FromSeconds(29));
        await provider.GetIssueRelationshipsAsync(Project(), 1);
        Assert.Single(source.Batches);
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds - 29));
        provider = new(source, directory.Database, clock);
        await provider.GetIssueAsync(Project(), 1);
        Assert.Equal(expectedCalls, source.Batches.Count);
    }

    [Fact]
    public async Task RefreshDeduplicatesOverlappingHierarchyAndDependenciesAndBatchesTheFrontier()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source { Overlap = true };
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        using var operation = provider.BeginOperation(refresh: true);
        var project = Project();
        var graph = await GitHubIssueGraphBuilder.BuildAsync(1,
            (number, token) => provider.GetIssueRelationshipsAsync(project, number, token),
            prefetch: (numbers, token) => provider.PrefetchAsync(project, numbers, token));
        Assert.NotNull(graph);
        Assert.Equal(new[] { 1, 2, 3 }, graph.Nodes.Select(node => node.Number));
        Assert.Contains(new GitHubIssueGraphEdge(1, 2, "parent-child"), graph.Edges);
        Assert.Contains(new GitHubIssueGraphEdge(1, 2, "blocked-by"), graph.Edges);
        Assert.False(graph.CycleDetected);
        Assert.Equal(2, source.Batches.Count);
        Assert.Equal(2, source.Batches[1].Count);
        Assert.Equal("node-team/project-2", source.Batches[1][2]);
        await provider.GetIssueAsync(project, 2);
        await provider.GetIssueRelationshipsAsync(project, 3);
        Assert.Equal(3, source.Batches.Sum(batch => batch.Count));
    }

    [Fact]
    public async Task ListsBatchSnapshotsAndReevaluateEligibilityForProjectAliases()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source { Count = 25 };
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        var issues = await provider.ListIssuesAsync(Project(), new(Limit: 25));
        Assert.Equal(25, issues.Count);
        Assert.Equal(new[] { 10, 10, 5 }, source.Batches.Select(batch => batch.Count));
        Assert.All(issues, issue => Assert.True(issue.IsEligible));
        var alias = Project() with { IssueReadyLabel = "different-ready-label" };
        var issue = await provider.GetIssueAsync(alias, 1);
        Assert.NotNull(issue);
        Assert.False(issue.IsEligible);
        Assert.Equal(3, source.Batches.Count);
    }

    [Fact]
    public async Task ListBatchesReuseIdentitiesDiscoveredInEarlierRelationshipResults()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source { Count = 11, KnownReference = 11 };
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        await provider.ListIssuesAsync(Project(), new(Limit: 11));
        Assert.Equal(2, source.Batches.Count);
        Assert.Equal("node-team/project-11", source.Batches[1][11]);
    }

    [Fact]
    public async Task FreshMutationScopesInvalidateIssueDataButRetainIdentities()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source { State = "CLOSED", Labels = ["codex-done"] };
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        await using (provider.BeginMutationOperation())
            await provider.GetIssueAsync(Project(), 1);
        source.State = "OPEN";
        var issue = await provider.GetIssueAsync(Project(), 1);
        Assert.NotNull(issue);
        Assert.Equal("OPEN", issue.State);
        Assert.Equal(2, source.Batches.Count);
        Assert.Equal("node-team/project-1", source.Batches[1][1]);
    }

    [Fact]
    public async Task ConcurrentReadsShareFreshCacheAndCallerCancellationDoesNotBecomeInfrastructureFailure()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source();
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetIssueAsync(Project(), 1)));
        Assert.Single(source.Batches);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetIssueAsync(Project(), 1, cancellation.Token));
        Assert.Single(source.Batches);
    }

    [Fact]
    public async Task SnapshotTransportCombinesReadsAndUsesPermanentNodeIdentityOnRefresh()
    {
        using var directory = new TemporaryDirectory();
        var commands = new List<IReadOnlyList<string>>();
        var source = new ServerGitHubReadService((arguments, _) =>
        {
            commands.Add(arguments);
            var item = GraphItem(1);
            object projection = arguments.Any(argument => argument.Contains("i1:node", StringComparison.Ordinal))
                ? item : new { issue = item };
            return Task.FromResult(new GitHubReadCommandResult(0,
                JsonSerializer.Serialize(new { data = new Dictionary<string, object> { ["i1"] = projection } }), ""));
        });
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        await provider.GetIssueRelationshipsAsync(Project(), 1);
        using (provider.BeginOperation(refresh: true))
        {
            await provider.GetIssueAsync(Project(), 1);
            await provider.GetIssueRelationshipsAsync(Project(), 1);
        }
        Assert.Equal(2, commands.Count);
        Assert.Contains(commands[0], argument => argument.Contains("i1:repository", StringComparison.Ordinal));
        Assert.Contains(commands[1], argument => argument.Contains("i1:node", StringComparison.Ordinal));
        Assert.All(commands, command => Assert.Contains(command, argument =>
            argument.Contains("blockedBy(first:100)", StringComparison.Ordinal) &&
            argument.Contains("blocking(first:100)", StringComparison.Ordinal) &&
            argument.Contains("subIssues(first:100)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NetworkTimeoutDiagnosticsIdentifyGitHubContextAndUserCancellationPropagates()
    {
        var source = new ServerGitHubReadService((_, _) => throw new GitHubCommandTimeoutException());
        var exception = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() =>
            source.ReadSnapshotsAsync(Project(), new Dictionary<int, string?> { [1] = null }, CancellationToken.None));
        Assert.Equal("query-timeout", exception.Code);
        Assert.Contains("snapshot/relationship", exception.Message, StringComparison.Ordinal);
        Assert.Contains("team/project", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("database", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(120, ServerAdministrationTimeoutPolicy.Seconds(["github", "graph", "project-id"]));
        Assert.Equal(15, ServerAdministrationTimeoutPolicy.Seconds(["project", "list"]));
        Assert.Contains("github graph project-id", ServerAdministrationTimeoutPolicy.Diagnostic(["github", "graph", "project-id"]), StringComparison.Ordinal);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        source = new((_, token) => Task.FromCanceled<GitHubReadCommandResult>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadSnapshotsAsync(Project(),
            new Dictionary<int, string?> { [1] = null }, cancellation.Token));
    }

    [Fact]
    public async Task RelationshipWritesUseThePersistentDatabaseIdentityWithoutRawIdentityCalls()
    {
        using var directory = new TemporaryDirectory();
        var source = new Source();
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        await provider.GetIssueAsync(Project(), 2);
        var commands = new List<IReadOnlyList<string>>();
        var writer = new ServerGitHubIssueWriteService((arguments, _) =>
        {
            commands.Add(arguments);
            return Task.FromResult(new GitHubReadCommandResult(0, "", ""));
        }, provider.GetDatabaseIdentityAsync);
        await writer.AddBlockedByAsync(Project(), 1, 2);
        await writer.AddSubIssueAsync(Project(), 1, 2);
        Assert.Single(source.Batches);
        Assert.Equal(2, commands.Count);
        Assert.Contains("issue_id=102", commands[0]);
        Assert.Contains("sub_issue_id=102", commands[1]);
        Assert.All(commands, command => Assert.Contains("POST", command));
    }

    [Fact]
    public async Task SnapshotTransportPaginatesRelationshipsAndRetainsRelatedIdentities()
    {
        var root = JsonSerializer.SerializeToNode(GraphItem(1)) ?? throw new InvalidOperationException();
        root["subIssues"] = JsonSerializer.SerializeToNode(new
        {
            nodes = new[] { GraphItem(2) }, pageInfo = new { hasNextPage = true, endCursor = "next-page" }
        });
        var commands = new List<IReadOnlyList<string>>();
        var source = new ServerGitHubReadService((arguments, _) =>
        {
            commands.Add(arguments);
            var response = commands.Count == 1
                ? JsonSerializer.Serialize(new { data = new { i1 = new { issue = root } } })
                : JsonSerializer.Serialize(new { data = new { node = new { subIssues = new
                {
                    nodes = new[] { GraphItem(3) }, pageInfo = new { hasNextPage = false, endCursor = "last-page" }
                } } } });
            return Task.FromResult(new GitHubReadCommandResult(0, response, ""));
        });
        var snapshot = Assert.Single(await source.ReadSnapshotsAsync(Project(),
            new Dictionary<int, string?> { [1] = null }, CancellationToken.None));
        Assert.Equal(new[] { 2, 3 }, snapshot.Relationships.SubIssues.Select(issue => issue.Number));
        Assert.Equal("node-team/project-3", snapshot.Identities[3].NodeId);
        Assert.Equal(2, commands.Count);
        Assert.Contains(commands[1], argument => argument.Contains("after:\"next-page\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("issues")]
    [InlineData("relationships")]
    [InlineData("graph")]
    public async Task CliRefreshForcesSnapshotsButKeepsPersistentIdentity(string command)
    {
        using var directory = new TemporaryDirectory();
        var registry = new SqliteRegistryStore(directory.Database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(new("Project", "team/project", "main", "", []));
        var source = new Source();
        var provider = new GitHubIssueReadProvider(source, directory.Database);
        var service = new ServerGitHubAdministrationService(registry, provider);
        await service.GetIssueAsync(project.Id, 1);
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new ServerAdministrationCli(new ServerConfigurationAdministrationService(),
            new Factory(service), output, error);
        var arguments = new List<string> { "github", command, project.Id };
        if (command != "issues") arguments.Add("1");
        arguments.AddRange(["--refresh", "--json", $"--Server:DatabasePath={directory.Database}"]);
        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(arguments));
        Assert.Empty(error.ToString());
        Assert.Equal(2, source.Batches.Count);
        Assert.Equal("node-team/project-1", source.Batches[1][1]);
        arguments.Remove("--refresh");
        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(arguments));
        Assert.Equal(2, source.Batches.Count);
    }

    private sealed class Factory(IServerGitHubAdministrationService github) : IServerAdministrationServiceFactory
    {
        public IServerAdministrationService Create(ServerConfiguration configuration)
        {
            var registry = new SqliteRegistryStore(configuration.ResolveDatabasePath());
            return new LocalServerAdministrationService(configuration, registry, new ServerHealthService(registry), githubAdministration: github);
        }
    }

    private static object GraphItem(int number) => new
    {
        id = $"node-team/project-{number}", fullDatabaseId = 100 + number, number, title = "Issue " + number,
        body = "Body", state = "OPEN", createdAt = DateTimeOffset.UnixEpoch, updatedAt = DateTimeOffset.UnixEpoch,
        url = $"https://github.com/team/project/issues/{number}",
        labels = new { nodes = new[] { new { name = "codex-ready" } }, pageInfo = new { hasNextPage = false } },
        parent = (object?)null,
        subIssues = new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false, endCursor = (string?)null } },
        blockedBy = new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false, endCursor = (string?)null } },
        blocking = new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false, endCursor = (string?)null } }
    };

    private static CentralProject Project(string repository = "team/project") =>
        new("project-id", "Project", repository, "main", "", [], 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Source : IGitHubIssueSnapshotSource
    {
        public Task<GitHubIssueNumberPage> ListDiscoveryIssueNumbersAsync(CentralProject project,
            GitHubIssueDiscoveryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new GitHubIssueNumberPage(DuplicateDiscovery ? [1, 1] : Enumerable.Range(1, Count).ToArray(), NextCursor));
        public Task<ManagedGitHubIssuePage> ReadDiscoveryPageAsync(CentralProject project,
            GitHubIssueDiscoveryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public List<IReadOnlyDictionary<int, string?>> Batches { get; } = [];
        public string State { get; set; } = "OPEN";
        public IReadOnlyList<string> Labels { get; set; } = ["codex-ready"];
        public int Count { get; init; } = 1;
        public GitHubReadUnavailableException? Failure { get; set; }
        public bool OmitLast { get; init; }
        public bool DuplicateDiscovery { get; init; }
        public string? NextCursor { get; init; }
        public string? BlockerState { get; init; }
        public bool Overlap { get; init; }
        public int? KnownReference { get; init; }
        public Task<IReadOnlyList<int>> ListIssueNumbersAsync(CentralProject project, GitHubIssueQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(Enumerable.Range(1, Count).ToArray());

        public Task<IReadOnlyList<GitHubIssueSnapshot>> ReadSnapshotsAsync(CentralProject project,
            IReadOnlyDictionary<int, string?> identities, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw failure;
            Batches.Add(identities);
            GitHubRelationshipIssue Reference(int number) => new(number, "Issue " + number, State.ToLowerInvariant(),
                $"https://github.com/{project.Repository}/issues/{number}") { Labels = Labels };
            GitHubIssueIdentity Identity(int number) => new($"node-{project.Repository}-{number}", 100 + number);
            return Task.FromResult<IReadOnlyList<GitHubIssueSnapshot>>(identities.Select(pair =>
            {
                var number = pair.Key;
                var issue = new ManagedGitHubIssue(number, "Issue " + number, "", State, DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, Reference(number).Url, Labels,
                    BlockerState is { } blockerState ? [new(2, "Prerequisite", blockerState, "https://github.com/team/project/issues/2")] : [],
                    true, []);
                var children = Overlap && number == 1 ? new[] { Reference(2), Reference(3) } : [];
                var blockers = Overlap && number == 1 ? new[] { Reference(2) } : [];
                var relationships = new GitHubIssueRelationships(1, project.Repository, number, Reference(number),
                    Overlap && number != 1 ? Reference(1) : null, children, blockers, []);
                var known = children.Select(child => child.Number).Append(number).Append(KnownReference ?? number).Distinct().ToDictionary(id => id, Identity);
                return new GitHubIssueSnapshot(Identity(number), issue, relationships, known);
            }).Where(snapshot => !OmitLast || snapshot.Issue.Number != Count).ToArray());
        }

        public Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubIssueRelationships?> GetIssueRelationshipsAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "github-read-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(_path);
        public string Database => Path.Combine(_path, "registry.db");
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
