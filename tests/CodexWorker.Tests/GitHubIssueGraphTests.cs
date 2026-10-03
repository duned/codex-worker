namespace CodexWorker.Tests;

using System.Text.Json;
using CodexServer;

public sealed class GitHubIssueGraphTests
{
    [Fact]
    public async Task HumanOutputShowsOperationalLabelsAndEachDependencyFromTheBlockedPerspective()
    {
        var unrelated = Enumerable.Range(1, 25).Select(number => "aaa-tag-" + number).ToArray();
        var relationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2), Ref(3)], labels: ["codex-roadmap"]),
            [2] = Relations(2, parent: Ref(1), blockedBy: [Ref(3)], labels: [.. unrelated, "codex-working"]),
            [3] = Relations(3, parent: Ref(1), blocking: [Ref(2)], state: "closed", labels: ["codex-done"])
        };
        var graph = await Build(1, relationships);
        Assert.NotNull(graph);
        var text = GitHubIssueGraphBuilder.RenderText(graph);
        Assert.Contains("#1 [open] Issue 1 (no execution label)", text, StringComparison.Ordinal);
        Assert.Contains("#2 [open] Issue 2 (labels: codex-working)", text, StringComparison.Ordinal);
        Assert.Contains("#3 [closed] Issue 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("codex-done", text, StringComparison.Ordinal);
        Assert.DoesNotContain("aaa-tag", text, StringComparison.Ordinal);
        Assert.DoesNotContain("codex-roadmap", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Blocking:", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("Blocked by:", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task GraphSeparatesHierarchyAndDependencyEdgesAndRendersStateAndLabels()
    {
        var relationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2, "closed", ["done"])], blockedBy: [Ref(3, "closed", ["ready"])],
                labels: ["ready"]),
            [2] = Relations(2, parent: Ref(1), labels: ["done"], state: "closed"),
            [3] = Relations(3, state: "closed", labels: ["ready"])
        };

        var graph = await Build(1, relationships);

        Assert.NotNull(graph);
        Assert.Equal(1, graph.ContractVersion);
        Assert.Equal(new[] { 1, 2, 3 }, graph.Nodes.Select(node => node.Number));
        Assert.Contains(new GitHubIssueGraphEdge(1, 2, "parent-child"), graph.Edges);
        Assert.Contains(new GitHubIssueGraphEdge(1, 3, "blocked-by"), graph.Edges);
        Assert.Equal("available", Assert.Single(graph.Nodes, node => node.Number == 3).Availability);
        Assert.Equal(
            "#1 [open] Issue 1 (labels: ready)\n" +
            "    Blocked by: #3 [closed] Issue 3 (labels: ready)\n" +
            "└── #2 [closed] Issue 2 (labels: done)\n" +
            "Related Issues:\n" +
            "#3 [closed] Issue 3 (labels: ready)\n",
            GitHubIssueGraphBuilder.RenderText(graph));
    }

    [Fact]
    public async Task GraphDeduplicatesSharedNodesAndStopsDefensiveHierarchyCycles()
    {
        var relationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2), Ref(3)]),
            [2] = Relations(2, children: [Ref(4)]),
            [3] = Relations(3, children: [Ref(4)]),
            [4] = Relations(4, children: [Ref(1)])
        };

        var graph = await Build(1, relationships);

        Assert.NotNull(graph);
        Assert.True(graph.CycleDetected);
        Assert.Equal(new[] { 1, 4 }, graph.RepeatedNodeNumbers);
        Assert.Equal(1, graph.Nodes.Count(node => node.Number == 4));
        Assert.Contains(graph.Edges, edge => edge.FromIssueNumber == 4 && edge.ToIssueNumber == 1 && edge.IsCycle);
        Assert.Contains("cycle reference", GitHubIssueGraphBuilder.RenderText(graph), StringComparison.Ordinal);
        Assert.Contains("already shown", GitHubIssueGraphBuilder.RenderText(graph), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphReportsDepthIssueAndEdgeTruncationExplicitly()
    {
        var depthRelationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2)]),
            [2] = Relations(2, parent: Ref(1), children: [Ref(3)])
        };
        var depthLimited = await Build(1, depthRelationships, new GitHubIssueGraphOptions(MaxDepth: 1));

        Assert.NotNull(depthLimited);
        Assert.True(depthLimited.IsTruncated);
        Assert.Equal(new[] { "max-depth" }, depthLimited.TruncationReasons);
        Assert.DoesNotContain(depthLimited.Nodes, node => node.Number == 3);
        Assert.Contains("Truncated: max-depth", GitHubIssueGraphBuilder.RenderText(depthLimited), StringComparison.Ordinal);

        var wide = new Dictionary<int, GitHubIssueRelationships> { [1] = Relations(1, children: [Ref(2), Ref(3)]) };
        var issueLimited = await Build(1, wide, new GitHubIssueGraphOptions(MaxIssues: 2));
        Assert.NotNull(issueLimited);
        Assert.Equal(new[] { "max-issues" }, issueLimited.TruncationReasons);
        Assert.Equal(2, issueLimited.Nodes.Count);

        var edgeLimited = await Build(1, wide, new GitHubIssueGraphOptions(MaxEdges: 1));
        Assert.NotNull(edgeLimited);
        Assert.Equal(new[] { "max-edges" }, edgeLimited.TruncationReasons);
        Assert.Single(edgeLimited.Edges);
    }

    [Fact]
    public async Task GraphPreservesMissingReferencesAndPropagatesReadFailures()
    {
        var root = Relations(1, children: [Ref(2)]);
        Task<GitHubIssueRelationships?> GetRelationship(int number, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return number switch
            {
                1 => Task.FromResult<GitHubIssueRelationships?>(root),
                2 => Task.FromResult<GitHubIssueRelationships?>(null),
                _ => Task.FromException<GitHubIssueRelationships?>(new GitHubReadUnavailableException(
                    "team/project", "GitHub read unavailable.", "read-unavailable"))
            };
        }

        var graph = await GitHubIssueGraphBuilder.BuildAsync(1, GetRelationship);
        var missingRoot = await GitHubIssueGraphBuilder.BuildAsync(9, (_, _) => Task.FromResult<GitHubIssueRelationships?>(null));

        Assert.NotNull(graph);
        Assert.Equal("missing", Assert.Single(graph.Nodes, node => node.Number == 2).Availability);
        await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => GitHubIssueGraphBuilder.BuildAsync(3, GetRelationship));
        await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => GitHubIssueGraphBuilder.BuildAsync(1,
            (number, token) => number == 1
                ? Task.FromResult<GitHubIssueRelationships?>(Relations(1, children: [Ref(3)]))
                : GetRelationship(number, token)));
        Assert.Null(missingRoot);
    }

    [Fact]
    public async Task JsonGraphContractAndTextRenderingAreStable()
    {
        var relationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2)], blocking: [Ref(3)]),
            [2] = Relations(2, parent: Ref(1))
        };
        var graph = await Build(1, relationships);
        Assert.NotNull(graph);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

        var firstJson = JsonSerializer.Serialize(graph, jsonOptions);
        var secondJson = JsonSerializer.Serialize(graph, jsonOptions);

        Assert.Equal(firstJson, secondJson);
        using var document = JsonDocument.Parse(firstJson);
        Assert.Equal(1, document.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("team/project", document.RootElement.GetProperty("repository").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("rootIssueNumber").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.All(document.RootElement.GetProperty("edges").EnumerateArray(), edge =>
            Assert.Contains(edge.GetProperty("kind").GetString(), new[] { "parent-child", "blocked-by" }));
        Assert.Contains(graph.Edges, edge => edge.Kind == "blocked-by" && edge.FromIssueNumber == 3 && edge.ToIssueNumber == 1);
        Assert.Contains("Blocked by: #1 [open] Issue 1", GitHubIssueGraphBuilder.RenderText(graph), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DependenciesExpandRecursivelyDeduplicateInverseEdgesAndDetectCycles()
    {
        var relationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2)], blockedBy: [Ref(3), Ref(3)]),
            [2] = Relations(2, parent: Ref(1), blockedBy: [Ref(3)]),
            [3] = Relations(3, blockedBy: [Ref(4)], blocking: [Ref(1), Ref(2)]),
            [4] = Relations(4, blockedBy: [Ref(3)])
        };
        var calls = new List<int>();
        var graph = await GitHubIssueGraphBuilder.BuildAsync(1, (number, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add(number);
            return Task.FromResult<GitHubIssueRelationships?>(relationships[number]);
        });
        Assert.NotNull(graph);
        Assert.Equal(new[] { 1, 2, 3, 4 }, calls);
        Assert.Equal(5, graph.Edges.Count);
        Assert.True(graph.CycleDetected);
        Assert.Contains(graph.Edges, edge => edge.Kind == "blocked-by" && edge.IsCycle);
        Assert.All(graph.Nodes, node => Assert.Equal("available", node.Availability));
    }

    [Fact]
    public async Task DependencyExpansionRespectsDepthIssueAndEdgeLimits()
    {
        var relationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, blockedBy: [Ref(2), Ref(3)]),
            [2] = Relations(2, blockedBy: [Ref(4)]),
            [3] = Relations(3),
            [4] = Relations(4)
        };
        var depth = await Build(1, relationships, new(MaxDepth: 0));
        Assert.NotNull(depth);
        Assert.Contains("max-depth", depth.TruncationReasons);
        Assert.DoesNotContain(depth.Nodes, node => node.Number == 4);
        Assert.Equal("reference", Assert.Single(depth.Nodes, node => node.Number == 2).Availability);
        var issues = await Build(1, relationships, new(MaxIssues: 2));
        Assert.NotNull(issues);
        Assert.Equal(2, issues.Nodes.Count);
        Assert.Contains("max-issues", issues.TruncationReasons);
        var edges = await Build(1, relationships, new(MaxEdges: 1));
        Assert.NotNull(edges);
        Assert.Single(edges.Edges);
        Assert.Equal(2, edges.Nodes.Count);
        Assert.Contains("max-edges", edges.TruncationReasons);
    }

    [Fact]
    public async Task NestedTreeKeepsAncestorContinuationLines()
    {
        var graph = await Build(1, new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2), Ref(3)]),
            [2] = Relations(2, children: [Ref(4), Ref(5)]),
            [3] = Relations(3),
            [4] = Relations(4, blockedBy: [Ref(3)]),
            [5] = Relations(5)
        });
        Assert.NotNull(graph);
        var text = GitHubIssueGraphBuilder.RenderText(graph);
        Assert.Contains("│   ├── #4", text, StringComparison.Ordinal);
        Assert.Contains("│   │   Blocked by: #3", text, StringComparison.Ordinal);
        Assert.Contains("│   └── #5", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortestDependencyPathAllowsExpansionAndMixedRelationshipsAreNotCycles()
    {
        var graph = await Build(1, new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2)], blockedBy: [Ref(4)]),
            [2] = Relations(2, children: [Ref(3)], blockedBy: [Ref(1)]),
            [3] = Relations(3, children: [Ref(4)]),
            [4] = Relations(4, children: [Ref(5)]),
            [5] = Relations(5)
        }, new(MaxDepth: 2));
        Assert.NotNull(graph);
        Assert.Equal("available", Assert.Single(graph.Nodes, node => node.Number == 5).Availability);
        Assert.False(graph.CycleDetected);
    }

    [Fact]
    public async Task MalformedResponsesAndCancellationAreNotConvertedToMissingNodes()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubIssueGraphBuilder.BuildAsync(1,
            (number, _) => Task.FromResult<GitHubIssueRelationships?>(number == 1
                ? Relations(1, children: [Ref(2)]) : Relations(3))));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitHubIssueGraphBuilder.BuildAsync(1,
            (number, token) =>
            {
                cancellation.Cancel();
                return Task.FromResult<GitHubIssueRelationships?>(Relations(number, children: [Ref(2)]));
            }, cancellationToken: cancellation.Token));
    }

    private static Task<GitHubIssueGraph?> Build(int rootNumber,
        IReadOnlyDictionary<int, GitHubIssueRelationships> relationships, GitHubIssueGraphOptions? options = null) =>
        GitHubIssueGraphBuilder.BuildAsync(rootNumber, (number, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(relationships.GetValueOrDefault(number));
        }, options);

    private static GitHubIssueRelationships Relations(int number,
        IReadOnlyList<GitHubRelationshipIssue>? children = null,
        IReadOnlyList<GitHubRelationshipIssue>? blockedBy = null,
        IReadOnlyList<GitHubRelationshipIssue>? blocking = null,
        GitHubRelationshipIssue? parent = null,
        IReadOnlyList<string>? labels = null,
        string state = "open") =>
        new(1, "team/project", number,
            Ref(number, state, labels), parent, children ?? [], blockedBy ?? [], blocking ?? []);

    private static GitHubRelationshipIssue Ref(int number, string state = "open", IReadOnlyList<string>? labels = null) =>
        new(number, $"Issue {number}", state,
            $"https://github.com/team/project/issues/{number}") { Labels = labels ?? [] };
}
