namespace CodexWorker.Tests;

using System.Text.Json;
using CodexServer;

public sealed class GitHubIssueGraphTests
{
    [Fact]
    public async Task GraphSeparatesHierarchyAndDependencyEdgesAndRendersStateAndLabels()
    {
        var relationships = new Dictionary<int, GitHubIssueRelationships>
        {
            [1] = Relations(1, children: [Ref(2, "closed", ["done"])], blockedBy: [Ref(3, "closed", ["ready"])],
                labels: ["ready"]),
            [2] = Relations(2, parent: Ref(1), labels: ["done"], state: "closed")
        };

        var graph = await Build(1, relationships);

        Assert.NotNull(graph);
        Assert.Equal(1, graph.ContractVersion);
        Assert.Equal(new[] { 1, 2, 3 }, graph.Nodes.Select(node => node.Number));
        Assert.Contains(new GitHubIssueGraphEdge(1, 2, "parent-child"), graph.Edges);
        Assert.Contains(new GitHubIssueGraphEdge(1, 3, "blocked-by"), graph.Edges);
        Assert.Equal("reference", Assert.Single(graph.Nodes, node => node.Number == 3).Availability);
        Assert.Equal(
            "#1 [open] Issue 1 (labels: ready)\n" +
            "    Blocked by: #3 [closed] Issue 3 (labels: ready)\n" +
            "└── #2 [closed] Issue 2 (labels: done)\n",
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
    public async Task GraphPreservesMissingAndInaccessibleChildReferencesAndMissingRootReturnsNull()
    {
        var root = Relations(1, children: [Ref(2), Ref(3)]);
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
        Assert.Equal("unavailable", Assert.Single(graph.Nodes, node => node.Number == 3).Availability);
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
        Assert.Contains(graph.Edges, edge => edge.Kind == "blocked-by" && edge.FromIssueNumber == 3 && edge.ToIssueNumber == 1);
        Assert.Contains("Blocking: #3 [open] Issue 3", GitHubIssueGraphBuilder.RenderText(graph), StringComparison.Ordinal);
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
