using System.Net;
using System.Text.Json;

namespace WorkExecutionToolbox.Tests;

public sealed class GitHubIssueDependencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddAndRemoveUseInternalIdsOnlyAtBoundaryAndVerifyState(bool applied)
    {
        using var handler = new DependencyHandler();
        if (!applied) handler.Link(42, 10);
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        var result = await provider.SetDependencyAsync(new(Target(), 10, applied));
        Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        Assert.Equal(applied, handler.Dependencies[42].Contains(10));
        var write = Assert.Single(handler.Writes);
        Assert.Equal(applied ? HttpMethod.Post : HttpMethod.Delete, write.Method);
        Assert.Equal(applied ? "/repos/team/project/issues/42/dependencies/blocked_by" :
            "/repos/team/project/issues/42/dependencies/blocked_by/1010", write.Path);
        Assert.Equal(applied ? 1010 : null, write.Id);
        Assert.Equal(2, handler.TargetReads);
        Assert.DoesNotContain("DatabaseId", JsonSerializer.Serialize(result));
        Assert.All(handler.Paths, path => Assert.StartsWith("/repos/team/project/issues/", path));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("labels", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BatchesSkipSatisfiedRelationsAndAreIdempotent(bool applied)
    {
        using var handler = new DependencyHandler();
        handler.Link(42, 10);
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        var request = new SetDependenciesRequest(Target(), [10, 11, 12], applied);
        var result = await provider.SetDependenciesAsync(request);
        Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        Assert.Equal(applied ? RelationshipChangeStatus.Unchanged : RelationshipChangeStatus.Changed, result.Relations[0].Result.Status);
        Assert.Equal(applied ? 2 : 1, handler.Writes.Count);
        var again = await provider.SetDependenciesAsync(request);
        Assert.Equal(RelationshipChangeStatus.Unchanged, again.Status);
        Assert.All(again.Relations, item => Assert.Equal(RelationshipChangeStatus.Unchanged, item.Result.Status));
        Assert.Equal(applied ? 2 : 1, handler.Writes.Count);
    }

    [Fact]
    public async Task PreviewPreflightsButNeverWrites()
    {
        using var handler = new DependencyHandler();
        handler.Link(42, 10);
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependenciesAsync(new(Target(), [10, 11], true, previewOnly: true));
        Assert.Equal(RelationshipChangeStatus.Preview, result.Status);
        Assert.Equal(RelationshipChangeStatus.Unchanged, result.Relations[0].Result.Status);
        Assert.Equal(RelationshipChangeStatus.Preview, result.Relations[1].Result.Status);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingIssuesAndPullRequestsRejectEntireBatchBeforeWrites(bool pullRequest)
    {
        foreach (var number in new[] { 42, 11 })
        {
            using var handler = new DependencyHandler();
            if (pullRequest) handler.PullRequests.Add(number);
            else handler.Missing.Add(number);
            using var http = new HttpClient(handler);
            var result = await Provider(http).SetDependenciesAsync(new(Target(), [10, 11], true));
            Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
            Assert.All(result.Relations, item => Assert.Equal(RelationshipChangeStatus.Failed, item.Result.Status));
            Assert.Empty(handler.Writes);
        }
    }

    [Fact]
    public async Task TransitiveCycleInLaterBatchItemRejectsAllWrites()
    {
        using var handler = new DependencyHandler();
        handler.Link(11, 12);
        handler.Link(12, 42);
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependenciesAsync(new(Target(), [10, 11], true));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Contains("cycle", result.Relations[1].Result.Diagnostic);
        Assert.Empty(handler.Writes);
        // Removing an edge is safe even when a cycle exists.
        handler.Link(42, 11);
        Assert.Equal(RelationshipChangeStatus.Changed,
            (await Provider(http).SetDependencyAsync(new(Target(), 11, false))).Status);
    }

    [Fact]
    public async Task ExistingCycleInBlockerGraphIsRejected()
    {
        using var handler = new DependencyHandler();
        handler.Link(10, 11);
        handler.Link(11, 10);
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependencyAsync(new(Target(), 10, true));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Contains("cycle", result.Diagnostic);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task CancellationAfterMutationPropagatesAndStopsFurtherWrites()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new DependencyHandler { AfterWrite = cancellation.Cancel };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(http).SetDependenciesAsync(new(Target(), [10, 11], true), cancellation.Token));
        Assert.Single(handler.Writes);
        Assert.Contains(10, handler.Dependencies[42]);
    }

    [Fact]
    public async Task DiamondGraphDoesNotLookLikeCycle()
    {
        using var handler = new DependencyHandler();
        handler.Link(10, 11, 12);
        handler.Link(11, 13);
        handler.Link(12, 13);
        using var http = new HttpClient(handler);
        Assert.Equal(RelationshipChangeStatus.Changed,
            (await Provider(http).SetDependencyAsync(new(Target(), 10, true))).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PartialBatchRetainsVerifiedSuccessAndReportsFailedRelation(bool applied)
    {
        using var handler = new DependencyHandler { FailWriteNumber = 11 };
        if (!applied) handler.Link(42, 10, 11, 12);
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependenciesAsync(new(Target(), [10, 11, 12], applied));
        Assert.Equal(RelationshipChangeStatus.Partial, result.Status);
        Assert.Equal(new[] { RelationshipChangeStatus.Changed, RelationshipChangeStatus.Failed, RelationshipChangeStatus.Changed },
            result.Relations.Select(item => item.Result.Status));
        Assert.DoesNotContain("fake-secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task SuccessResponseWithoutMutationFailsReadBack()
    {
        using var handler = new DependencyHandler { IgnoreWrites = true };
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependencyAsync(new(Target(), 10, true));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Contains("read-back", result.Diagnostic);
    }

    [Fact]
    public async Task LostWriteResponseWithVerifiedMutationIsChanged()
    {
        using var handler = new DependencyHandler { LoseWriteResponse = true };
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependencyAsync(new(Target(), 10, true));
        Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        Assert.Contains("verified", result.Diagnostic);
        Assert.DoesNotContain("fake-secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task UnavailableReadBackReportsUncertaintyAndStopsBatch()
    {
        using var handler = new DependencyHandler { FailVerification = true };
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependenciesAsync(new(Target(), [10, 11], true));
        Assert.Equal(RelationshipChangeStatus.Partial, result.Status);
        Assert.Equal(RelationshipChangeStatus.Partial, result.Relations[0].Result.Status);
        Assert.Equal(RelationshipChangeStatus.Failed, result.Relations[1].Result.Status);
        Assert.Single(handler.Writes);
    }

    [Fact]
    public async Task ReadReturnsDirectListAndPaginatesInExplicitRepository()
    {
        using var handler = new DependencyHandler();
        handler.Link(42, Enumerable.Range(100, 101).ToArray());
        handler.Link(100, 11);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var result = await Provider(http).GetBlockedByAsync(Target());
        Assert.NotNull(result);
        Assert.Equal(101, result.Count);
        Assert.All(result, item => Assert.Equal("team/project", item.Issue.Repository.Repository));
        Assert.DoesNotContain(result, item => item.Issue.Number == 11);
        Assert.Contains(handler.Paths, path => path.EndsWith("page=2", StringComparison.Ordinal));
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task DependencyResponsesFromAnotherRepositoryAreRejectedBeforeWrites()
    {
        using var handler = new DependencyHandler { RelationshipRepository = "other/project" };
        handler.Link(10, 11);
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetDependencyAsync(new(Target(), 10, true));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Empty(handler.Writes);
        handler.Link(42, 10);
        await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetBlockedByAsync(Target()));
    }

    [Fact]
    public async Task MissingTargetReadReturnsNullAndCancellationPropagates()
    {
        using var handler = new DependencyHandler();
        handler.Missing.Add(42);
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        Assert.Null(await provider.GetBlockedByAsync(Target()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SetDependenciesAsync(new(Target(), [10], true), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetBlockedByAsync(Target(), cancellation.Token));
    }

    [Fact]
    public async Task InvalidContextNumbersAndSelfLinksAreRejectedBeforeHttp()
    {
        using var handler = new DependencyHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(http).SetDependencyAsync(new(new(new("invalid"), 42), 10, true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetDependenciesRequest(Target(), [0], true));
        Assert.Throws<ArgumentException>(() => new SetDependenciesRequest(Target(), [42], true));
        Assert.Throws<ArgumentException>(() => new SetDependenciesRequest(Target(), [10, 10], true));
        Assert.Throws<ArgumentException>(() => new SetDependenciesRequest(Target(), [], true));
        Assert.Throws<ArgumentException>(() => new SetDependenciesRequest(Target(), Enumerable.Range(100, 51).ToArray(), true));
        Assert.Empty(handler.Paths);
    }

    private static IssueReference Target() => new(new("team/project"), 42);
    private static GitHubIssueProvider Provider(HttpClient http) => new(http, _ => Task.FromResult("fake-secret"));

    private sealed class DependencyHandler : HttpMessageHandler
    {
        public Dictionary<int, HashSet<int>> Dependencies { get; } = [];
        public HashSet<int> Missing { get; } = [];
        public HashSet<int> PullRequests { get; } = [];
        public List<string> Paths { get; } = [];
        public List<(HttpMethod Method, string Path, long? Id)> Writes { get; } = [];
        public Action? AfterWrite { get; init; }
        public int? FailWriteNumber { get; init; }
        public bool IgnoreWrites { get; init; }
        public bool LoseWriteResponse { get; init; }
        public bool FailVerification { get; init; }
        public string RelationshipRepository { get; init; } = "team/project";
        public int TargetReads { get; private set; }

        public void Link(int target, params int[] blockers) => Dependencies[target] = blockers.ToHashSet();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            Paths.Add(uri.PathAndQuery);
            var parts = uri.AbsolutePath.Split('/');
            Assert.Equal("team", parts[2]);
            Assert.Equal("project", parts[3]);
            var number = int.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture);
            if (request.Method != HttpMethod.Get)
            {
                Assert.Equal("dependencies", parts[6]);
                Assert.Equal("blocked_by", parts[7]);
                long id;
                if (request.Method == HttpMethod.Post)
                {
                    Assert.NotNull(request.Content);
                    using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                    id = body.RootElement.GetProperty("issue_id").GetInt64();
                }
                else id = long.Parse(parts[8], System.Globalization.CultureInfo.InvariantCulture);
                var blocker = checked((int)id - 1000);
                Writes.Add((request.Method, uri.AbsolutePath, request.Method == HttpMethod.Post ? id : null));
                if (FailWriteNumber == blocker) return Response("fake-secret", HttpStatusCode.UnprocessableEntity);
                if (!Dependencies.TryGetValue(number, out var edges)) Dependencies[number] = edges = [];
                if (!IgnoreWrites)
                {
                    if (request.Method == HttpMethod.Post) edges.Add(blocker);
                    else edges.Remove(blocker);
                }
                AfterWrite?.Invoke();
                if (LoseWriteResponse) throw new HttpRequestException("fake-secret");
                return Response("{}", request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.NoContent);
            }
            if (Missing.Contains(number)) return Response("", HttpStatusCode.NotFound);
            if (parts.Length == 6) return Response(JsonSerializer.Serialize(Body(number, "team/project")));
            Assert.Equal("dependencies", parts[6]);
            Assert.Equal("blocked_by", parts[7]);
            if (number == 42)
            {
                TargetReads++;
                if (FailVerification && Writes.Count > 0) return Response("fake-secret", HttpStatusCode.ServiceUnavailable);
            }
            var page = int.Parse(uri.Query.Split("page=")[^1], System.Globalization.CultureInfo.InvariantCulture);
            var related = Dependencies.GetValueOrDefault(number, []).Order().Skip((page - 1) * 100).Take(100);
            return Response(JsonSerializer.Serialize(related.Select(item => Body(item, RelationshipRepository))));
        }

        private Dictionary<string, object> Body(int number, string repository)
        {
            var body = new Dictionary<string, object>
            {
                ["id"] = 1000 + number, ["number"] = number, ["title"] = "Example", ["state"] = "open",
                ["html_url"] = $"https://github.com/{repository}/issues/{number}"
            };
            if (PullRequests.Contains(number)) body["pull_request"] = new { };
            return body;
        }

        private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body) };
    }
}
