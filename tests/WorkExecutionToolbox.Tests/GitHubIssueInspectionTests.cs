using System.Globalization;
using System.Net;
using System.Text.Json;

namespace WorkExecutionToolbox.Tests;

public sealed class GitHubIssueInspectionTests
{
    [Fact]
    public async Task DirectRelationshipsImplementExistingContractAndKeepHumanMetadata()
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        handler.Parents[3] = 2;
        handler.Dependencies[2] = [4];
        handler.Dependencies[5] = [2];
        using var http = new HttpClient(handler);
        IIssueRelationshipProvider provider = Provider(http);
        var result = await provider.GetRelationshipsAsync(Issue(2));
        Assert.NotNull(result);
        Assert.Equal(1, result.Parent?.Issue.Number);
        Assert.Equal(3, Assert.Single(result.Children).Issue.Number);
        Assert.Equal(4, Assert.Single(result.BlockedBy).Issue.Number);
        Assert.Equal(5, Assert.Single(result.Blocking).Issue.Number);
        Assert.Equal("Issue 2", result.Issue.Title);
        Assert.Equal(IssueState.Closed, result.Issue.State);
        Assert.Equal("https://github.com/team/project/issues/2", result.Issue.Url?.AbsoluteUri);
        AssertSafe(result);
    }

    [Fact]
    public async Task MixedGraphExpandsBothDirectionsAndSharedReferencesOnce()
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        handler.Parents[3] = 1;
        handler.Dependencies[2] = [1, 4];
        handler.Dependencies[3] = [4];
        using var http = new HttpClient(handler);
        IIssueGraphProvider provider = Provider(http);
        var graph = await provider.GetGraphAsync(Issue(2));
        Assert.NotNull(graph);
        Assert.Equal(new[] { 1, 2, 3, 4 }, graph.Issues.Select(item => item.Issue.Number));
        Assert.Equal(5, graph.Edges.Count);
        Assert.Contains(new(1, 2, IssueGraphEdgeKind.ParentChild), graph.Edges);
        Assert.Contains(new(2, 1, IssueGraphEdgeKind.BlockedBy), graph.Edges);
        Assert.False(graph.CycleDetected);
        Assert.False(graph.IsDepthTruncated);
        Assert.Equal(20, handler.Paths.Count);
        Assert.Equal(handler.Paths.Count, handler.Paths.Distinct().Count());
        AssertSafe(graph);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CyclesAreReportedWithinEachEdgeKind(bool hierarchy)
    {
        using var handler = new GraphHandler();
        if (hierarchy)
        {
            handler.Parents[2] = 1;
            handler.Parents[3] = 2;
            handler.Parents[1] = 3;
        }
        else
        {
            handler.Dependencies[1] = [2];
            handler.Dependencies[2] = [3];
            handler.Dependencies[3] = [1];
        }
        using var http = new HttpClient(handler);
        var graph = await Provider(http).GetGraphAsync(Issue(1));
        Assert.NotNull(graph);
        Assert.True(graph.CycleDetected);
        Assert.Contains(graph.Edges, edge => edge.IsCycle &&
            edge.Kind == (hierarchy ? IssueGraphEdgeKind.ParentChild : IssueGraphEdgeKind.BlockedBy));
        Assert.Equal(15, handler.Paths.Count);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("children")]
    [InlineData("blocked-by")]
    [InlineData("blocking")]
    public async Task AsymmetricRelationsFailRatherThanReturningAValidGraph(string mismatch)
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        handler.Dependencies[1] = [2];
        handler.Override = uri => mismatch switch
        {
            "parent" when uri.AbsolutePath.EndsWith("/2/parent", StringComparison.Ordinal) => new(HttpStatusCode.NotFound),
            "children" when uri.AbsolutePath.EndsWith("/1/sub_issues", StringComparison.Ordinal) => Response("[]"),
            "blocked-by" when uri.AbsolutePath.EndsWith("/1/dependencies/blocked_by", StringComparison.Ordinal) => Response("[]"),
            "blocking" when uri.AbsolutePath.EndsWith("/2/dependencies/blocking", StringComparison.Ordinal) => Response("[]"),
            _ => null
        };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetGraphAsync(Issue(1)));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
        Assert.Contains("inconsistent", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedIssueMustRemainVisibleAndHaveSameIdentity(bool changedIdentity)
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        handler.Override = uri => uri.AbsolutePath.EndsWith("/issues/2", StringComparison.Ordinal)
            ? changedIdentity ? Response(Body(2, id: 9999)) : new(HttpStatusCode.NotFound) : null;
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetGraphAsync(Issue(1)));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 3, false)]
    public async Task DepthBoundIsExplicitAndNeverExpandsBeyondIt(int depth, int count, bool truncated)
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        handler.Parents[3] = 2;
        using var http = new HttpClient(handler);
        var graph = await Provider(http).GetGraphAsync(Issue(1), new() { MaxDepth = depth });
        Assert.NotNull(graph);
        Assert.Equal(count, graph.Issues.Count);
        Assert.Equal(truncated, graph.IsDepthTruncated);
        Assert.Equal(count * 5, handler.Paths.Count);
    }

    [Theory]
    [InlineData("issues", 5)]
    [InlineData("requests", 3)]
    [InlineData("edges", 15)]
    public async Task SafetyLimitsFailWithoutUnboundedCalls(string limit, int expectedRequests)
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        handler.Parents[3] = 2;
        var options = limit switch
        {
            "issues" => new IssueGraphOptions { MaxIssues = 1 },
            "requests" => new IssueGraphOptions { MaxRequests = 3 },
            _ => new IssueGraphOptions { MaxEdges = 1 }
        };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetGraphAsync(Issue(1), options));
        Assert.Equal(GitHubIssueFailure.LimitExceeded, error.Failure);
        Assert.Equal(expectedRequests, handler.Paths.Count);
    }

    [Theory]
    [InlineData("sub_issues")]
    [InlineData("dependencies/blocked_by")]
    [InlineData("dependencies/blocking")]
    public async Task DirectReadsPaginateAllRelationLists(string suffix)
    {
        using var handler = new GraphHandler();
        handler.Override = uri => uri.AbsolutePath.EndsWith('/' + suffix, StringComparison.Ordinal)
            ? Response(uri.Query.EndsWith("page=1", StringComparison.Ordinal)
                ? ArrayBody(Enumerable.Range(2, 100)) : ArrayBody([102])) : null;
        using var http = new HttpClient(handler);
        var result = await Provider(http).GetRelationshipsAsync(Issue(1));
        Assert.NotNull(result);
        Assert.Equal(101, (suffix switch
        {
            "sub_issues" => result.Children,
            "dependencies/blocked_by" => result.BlockedBy,
            _ => result.Blocking
        }).Count);
        Assert.Equal(6, handler.Paths.Count);
    }

    [Fact]
    public async Task GraphPaginationBoundDoesNotSilentlyReturnAnIncompleteList()
    {
        using var handler = new GraphHandler();
        handler.Override = uri => uri.AbsolutePath.EndsWith("/sub_issues", StringComparison.Ordinal)
            ? Response(ArrayBody(Enumerable.Range(2, 100))) : null;
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetGraphAsync(Issue(1),
            new() { MaxPagesPerRelation = 1 }));
        Assert.Equal(GitHubIssueFailure.LimitExceeded, error.Failure);
        Assert.Equal(3, handler.Paths.Count);
    }

    [Fact]
    public async Task GraphCanReadMultiplePagesBeforeApplyingDepthBound()
    {
        using var handler = new GraphHandler();
        for (var number = 2; number <= 102; number++) handler.Parents[number] = 1;
        using var http = new HttpClient(handler);
        var graph = await Provider(http).GetGraphAsync(Issue(1), new() { MaxDepth = 0, MaxPagesPerRelation = 2 });
        Assert.NotNull(graph);
        Assert.Single(graph.Issues);
        Assert.Empty(graph.Edges);
        Assert.True(graph.IsDepthTruncated);
        Assert.Equal(6, handler.Paths.Count);
    }

    [Fact]
    public async Task DuplicateRelationsAcrossPagesAreRejected()
    {
        using var handler = new GraphHandler();
        handler.Override = uri => uri.AbsolutePath.EndsWith("/sub_issues", StringComparison.Ordinal)
            ? Response(uri.Query.EndsWith("page=1", StringComparison.Ordinal)
                ? ArrayBody(Enumerable.Range(2, 100)) : ArrayBody([2])) : null;
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetRelationshipsAsync(Issue(1)));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
        Assert.Equal(4, handler.Paths.Count);
    }

    [Theory]
    [InlineData("duplicate-number")]
    [InlineData("duplicate-id")]
    [InlineData("identity-between-relations")]
    [InlineData("pull-request")]
    [InlineData("cross-repository")]
    [InlineData("self")]
    public async Task InvalidRelationPayloadsAreRejected(string invalid)
    {
        using var handler = new GraphHandler();
        handler.Parents[1] = 2;
        handler.Override = uri => uri.AbsolutePath.EndsWith("/sub_issues", StringComparison.Ordinal)
            ? Response(invalid switch
            {
                "duplicate-number" => $"[{Body(3)},{Body(3)}]",
                "duplicate-id" => $"[{Body(3)},{Body(4, id: 1003)}]",
                "identity-between-relations" => $"[{Body(2, id: 9999)}]",
                "pull-request" => $"[{Body(3, pullRequest: true)}]",
                "cross-repository" => $"[{Body(3, repository: "other/project")}]",
                _ => $"[{Body(1)}]"
            }) : null;
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetRelationshipsAsync(Issue(1)));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRootAndPullRequestsReturnNull(bool pullRequest)
    {
        using var handler = new GraphHandler();
        handler.Override = _ => pullRequest ? Response(Body(1, pullRequest: true)) : new(HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        Assert.Null(await Provider(http).GetGraphAsync(Issue(1)));
        Assert.Null(await Provider(http).GetRelationshipsAsync(Issue(1)));
        Assert.Equal(2, handler.Paths.Count);
    }

    [Theory]
    [InlineData(401, GitHubIssueFailure.Authorization)]
    [InlineData(429, GitHubIssueFailure.RateLimited)]
    [InlineData(500, GitHubIssueFailure.Provider)]
    public async Task RelationshipApiFailuresPropagateSafely(int status, GitHubIssueFailure failure)
    {
        using var handler = new GraphHandler();
        handler.Override = uri => uri.AbsolutePath.EndsWith("/dependencies/blocked_by", StringComparison.Ordinal)
            ? new((HttpStatusCode)status) { Content = new StringContent("secret-response") } : null;
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetGraphAsync(Issue(1)));
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain("secret-response", error.ToString());
    }

    [Fact]
    public async Task CancellationPropagatesDuringTraversal()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        handler.Override = uri =>
        {
            if (uri.AbsolutePath.EndsWith("/issues/2", StringComparison.Ordinal)) cancellation.Cancel();
            return null;
        };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(http).GetGraphAsync(Issue(1),
            cancellationToken: cancellation.Token));
        Assert.Equal(6, handler.Paths.Count);
    }

    [Fact]
    public async Task InvalidOptionsFailBeforeHttpOrCredentials()
    {
        using var http = new HttpClient();
        var provider = new GitHubIssueProvider(http, _ => throw new InvalidOperationException("Unexpected credentials"));
        foreach (var options in new[]
        {
            new IssueGraphOptions { MaxDepth = -1 }, new IssueGraphOptions { MaxDepth = 21 },
            new IssueGraphOptions { MaxIssues = 0 }, new IssueGraphOptions { MaxIssues = 201 },
            new IssueGraphOptions { MaxEdges = 0 }, new IssueGraphOptions { MaxEdges = 2001 },
            new IssueGraphOptions { MaxRequests = 0 }, new IssueGraphOptions { MaxRequests = 5001 },
            new IssueGraphOptions { MaxPagesPerRelation = 0 }, new IssueGraphOptions { MaxPagesPerRelation = 101 }
        })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.GetGraphAsync(Issue(1), options));
    }

    private static void AssertSafe(object value)
    {
        var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("DatabaseId", json);
        Assert.DoesNotContain("node_id", json);
        Assert.DoesNotContain("fake-test-credential", json);
        Assert.DoesNotContain("secret-body", json);
    }

    private static IssueReference Issue(int number) => new(new("team/project"), number);
    private static GitHubIssueProvider Provider(HttpClient http) => new(http, _ => Task.FromResult("fake-test-credential"));
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static string ArrayBody(IEnumerable<int> numbers) => "[" + string.Join(',', numbers.Select(number => Body(number))) + "]";
    private static string Body(int number, long? id = null, bool pullRequest = false, string repository = "team/project") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id ?? 1000L + number, ["number"] = number, ["title"] = $"Issue {number}",
            ["state"] = number % 2 == 0 ? "closed" : "open", ["body"] = "secret-body",
            ["html_url"] = $"https://github.com/{repository}/issues/{number}"
        }.Concat(pullRequest ? new Dictionary<string, object> { ["pull_request"] = new { } } : []).ToDictionary());

    private sealed class GraphHandler : HttpMessageHandler
    {
        public Dictionary<int, int> Parents { get; } = [];
        public Dictionary<int, int[]> Dependencies { get; } = [];
        public List<string> Paths { get; } = [];
        public Func<Uri, HttpResponseMessage?>? Override { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
            Assert.Equal("api.github.com", uri.Host);
            Paths.Add(uri.PathAndQuery);
            var overridden = Override?.Invoke(uri);
            cancellationToken.ThrowIfCancellationRequested();
            if (overridden is not null) return Task.FromResult(overridden);
            var parts = uri.AbsolutePath.Split('/');
            var number = int.Parse(parts[5], CultureInfo.InvariantCulture);
            if (parts.Length == 6) return Task.FromResult(Response(Body(number)));
            if (parts[6] == "parent")
                return Task.FromResult(Parents.TryGetValue(number, out var parent) ? Response(Body(parent)) : new(HttpStatusCode.NotFound));
            var page = int.Parse(uri.Query.Split("page=")[^1], CultureInfo.InvariantCulture);
            var references = parts[6] == "sub_issues"
                ? Parents.Where(pair => pair.Value == number).Select(pair => pair.Key)
                : parts[7] == "blocked_by" ? Dependencies.GetValueOrDefault(number, [])
                : Dependencies.Where(pair => pair.Value.Contains(number)).Select(pair => pair.Key);
            return Task.FromResult(Response(ArrayBody(references.Order().Skip((page - 1) * 100).Take(100))));
        }
    }
}
