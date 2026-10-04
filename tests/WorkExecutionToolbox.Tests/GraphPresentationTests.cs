using System.Text.Json;

namespace WorkExecutionToolbox.Tests;

public sealed partial class ToolboxCommandTests
{
    [Fact]
    public async Task GraphHierarchyShowsCompactChildAndRootPrerequisites()
    {
        var graph = GraphOf([9, 11, 12, 13],
            [Child(9, 13), Blocked(13, 12), Child(9, 11), Blocked(9, 13),
                Child(9, 12), Blocked(13, 11), Blocked(12, 11)]);
        var output = await GraphText(graph);
        Assert.Equal("""
            #9 [closed] Task 9 (root)
            ├── #11 [closed] Task 11
            ├── #12 [closed] Task 12
            │   └── blocked by #11
            ├── #13 [closed] Task 13
            │   └── blocked by #11, #12
            └── blocked by #13
            """, output);
        Assert.DoesNotContain("parent of", output);
        Assert.DoesNotContain(" blocks ", output);
    }

    [Fact]
    public async Task GraphDependencyBranchesIncludeBlockersAndDependentsWithTheirHierarchy()
    {
        var output = await GraphText(GraphOf([9, 1, 2, 3, 4, 5],
            [Blocked(9, 2), Child(1, 2), Child(1, 3), Blocked(4, 9), Child(4, 5)]));
        Assert.Equal("""
            #9 [closed] Task 9 (root)
            └── blocked by #2

            Related branches:
            #1 [closed] Task 1
            ├── #2 [closed] Task 2
            └── #3 [closed] Task 3

            #4 [closed] Task 4
            ├── #5 [closed] Task 5
            └── blocked by #9
            """, output);
    }

    [Fact]
    public async Task GraphSharedNodesUseReferencesWithoutRepeatingMetadata()
    {
        var output = await GraphText(GraphOf([9, 10, 11, 12],
            [Child(9, 10), Child(9, 11), Child(10, 12), Child(11, 12), Blocked(11, 12)]));
        Assert.Contains("│   └── #12 [closed] Task 12", output);
        Assert.Contains("    ├── #12 (reference; already shown)", output);
        Assert.Contains("    └── blocked by #12", output);
        Assert.Equal(1, output.Split("Task 12", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task GraphHierarchyAndDependencyCyclesRemainDistinctAndTerminate()
    {
        var graph = GraphOf([9, 10], [Child(9, 10), Child(10, 9) with { IsCycle = true },
            Blocked(9, 10), Blocked(10, 9) with { IsCycle = true }]) with { CycleDetected = true };
        var output = await GraphText(graph);
        Assert.Contains("│   ├── #9 (cycle reference)", output);
        Assert.Contains("│   └── blocked by #9 (cycle)", output);
        Assert.Contains("└── blocked by #10", output);
        Assert.Contains("Cycles detected:", output);
        Assert.Equal(1, output.Split("Task 9", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task GraphSingleRootAndDepthTruncationAreExplicit()
    {
        var graph = GraphOf([9], []);
        Assert.Equal("#9 [closed] Task 9 (root)", await GraphText(graph));
        var output = await GraphText(graph with { IsDepthTruncated = true });
        Assert.Contains("Depth truncated: additional relationships are outside the graph depth limit.", output);
        Assert.DoesNotContain("Related branches", output);
    }

    [Fact]
    public async Task GraphOrderingIsIndependentOfProviderNodeAndEdgeOrder()
    {
        var graph = GraphOf([1, 2, 3, 4, 9, 10, 11, 12],
            [Child(1, 9), Child(9, 12), Child(9, 10), Child(10, 11), Child(2, 3),
                Blocked(9, 4), Blocked(9, 3), Blocked(4, 9)]);
        var output = await GraphText(graph);
        Assert.StartsWith("#1 [closed] Task 1", output);
        Assert.Contains("#9 [closed] Task 9 (root)", output);
        Assert.Equal(output, await GraphText(graph with
        {
            Issues = graph.Issues.Reverse().ToArray(), Edges = graph.Edges.Reverse().ToArray()
        }));
    }

    [Fact]
    public async Task GraphRootlessRelatedCycleAndUnmarkedHierarchyCycleAreSafe()
    {
        var output = await GraphText(GraphOf([9, 1, 2],
            [Blocked(9, 1), Child(1, 2), Child(2, 1)]));
        Assert.Contains("Related branches:\n#1 [closed] Task 1", output);
        Assert.Contains("    └── #1 (cycle reference)", output);
    }

    [Fact]
    public async Task GraphSanitizesHumanTitlesAndPreservesJsonGraphContract()
    {
        var graph = GraphOf([9, 10], [Child(9, 10), Blocked(9, 10) with { IsCycle = true }]) with
        {
            IsDepthTruncated = true, CycleDetected = true
        };
        graph = graph with { Issues = graph.Issues.Select(node => node with { Title = "Title\u001b\n\r\t\0" }).ToArray() };
        var output = await GraphText(graph);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\r', output);
        Assert.DoesNotContain('\t', output);
        Assert.DoesNotContain('\0', output);
        Assert.Contains("#10 [closed] Title     ", output);

        var run = await RunAsync(["graph", "9", "--repo", "owner/repo", "--json"], new FakeProvider { Graph = graph });
        Assert.Equal(0, run.Exit);
        Assert.Empty(run.Error);
        using var json = JsonDocument.Parse(run.Output);
        var envelope = json.RootElement;
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("graph", envelope.GetProperty("command").GetString());
        Assert.Equal("owner/repo", envelope.GetProperty("repository").GetString());
        Assert.Equal(9, envelope.GetProperty("issue").GetInt32());
        var data = envelope.GetProperty("data");
        Assert.Equal(9, data.GetProperty("root").GetProperty("number").GetInt32());
        Assert.Equal(2, data.GetProperty("issues").GetArrayLength());
        Assert.Equal("Title\u001b\n\r\t\0", data.GetProperty("issues")[0].GetProperty("title").GetString());
        Assert.Equal("closed", data.GetProperty("issues")[0].GetProperty("state").GetString());
        var edges = data.GetProperty("edges");
        Assert.Equal(2, edges.GetArrayLength());
        Assert.Equal("parentChild", edges[0].GetProperty("kind").GetString());
        Assert.Equal("blockedBy", edges[1].GetProperty("kind").GetString());
        Assert.Equal(9, edges[1].GetProperty("fromIssueNumber").GetInt32());
        Assert.Equal(10, edges[1].GetProperty("toIssueNumber").GetInt32());
        Assert.True(edges[1].GetProperty("isCycle").GetBoolean());
        Assert.True(data.GetProperty("isDepthTruncated").GetBoolean());
        Assert.True(data.GetProperty("cycleDetected").GetBoolean());
        Assert.Equal(5, data.EnumerateObject().Count());
    }

    private static IssueGraph GraphOf(int[] numbers, IssueGraphEdge[] edges)
    {
        var repository = GitHubRepositoryContext.Create("owner/repo");
        return new(new(repository, 9), numbers.Select(number => new IssueSummary(new(repository, number),
            $"Task {number}", IssueState.Closed)).ToArray(), edges, false, false);
    }

    private static IssueGraphEdge Child(int parent, int child) => new(parent, child, IssueGraphEdgeKind.ParentChild);
    private static IssueGraphEdge Blocked(int issue, int blocker) => new(issue, blocker, IssueGraphEdgeKind.BlockedBy);

    private static async Task<string> GraphText(IssueGraph graph)
    {
        var run = await RunAsync(["graph", "9", "--repo", "owner/repo"], new FakeProvider { Graph = graph });
        Assert.Equal(0, run.Exit);
        Assert.Empty(run.Error);
        return run.Output.ReplaceLineEndings("\n").TrimEnd('\n');
    }
}
