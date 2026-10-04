using System.Net;
using System.Text.Json;

namespace WorkExecutionToolbox.Tests;

public sealed class GitHubIssueParentTests
{
    [Fact]
    public async Task SetClearAndRepeatsUseNativeIdsAndVerifyWrites()
    {
        using var handler = new GraphHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        var provider = Provider(http);
        Assert.Equal(RelationshipChangeStatus.Changed, (await provider.SetParentAsync(new(Issue(1), 2))).Status);
        Assert.Equal(2, (await provider.GetParentAsync(Issue(1)))?.Issue.Number);
        Assert.Equal(1, Assert.Single(await provider.ListChildrenAsync(Issue(2)) ?? []).Issue.Number);
        Assert.Equal(RelationshipChangeStatus.Unchanged, (await provider.SetParentAsync(new(Issue(1), 2))).Status);
        Assert.Equal(RelationshipChangeStatus.Changed, (await provider.ClearParentAsync(Issue(1))).Status);
        Assert.Equal(RelationshipChangeStatus.Unchanged, (await provider.ClearParentAsync(Issue(1))).Status);
        Assert.Empty(await provider.ListChildrenAsync(Issue(2)) ?? []);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Delete }, handler.Writes);
        Assert.Equal(new[] { 1001L, 1001L }, handler.WrittenIds);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostWriteResponseVerifiesSetAndClearWithoutRetry(bool clear)
    {
        using var handler = new GraphHandler { LoseWriteResponse = true };
        if (clear) handler.Parents[1] = 2;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentAsync(new(Issue(1), clear ? null : 2));
        Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        Assert.Contains("verified", result.Diagnostic);
        Assert.DoesNotContain("sensitive-response", result.Diagnostic);
        Assert.Single(handler.Writes);
        Assert.Equal(clear ? null : (int?)2, (await Provider(http).GetParentAsync(Issue(1)))?.Issue.Number);
    }

    [Fact]
    public async Task ExistingDifferentParentRequiresExplicitClear()
    {
        using var handler = new GraphHandler();
        handler.Parents[1] = 2;
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        var result = await provider.SetParentAsync(new(Issue(1), 3));
        Assert.Equal(RelationshipChangeStatus.Conflict, result.Status);
        Assert.Contains("clear", result.Diagnostic);
        Assert.Equal(2, handler.Parents[1]);
        Assert.Empty(handler.Writes);
        await provider.ClearParentAsync(Issue(1));
        Assert.Equal(RelationshipChangeStatus.Changed, (await provider.SetParentAsync(new(Issue(1), 3))).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CyclesAreRejectedBeforeWritingIncludingPreview(bool preview)
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 3;
        handler.Parents[3] = 1;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentAsync(new(Issue(1), 2, preview));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Contains("cycle", result.Diagnostic);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task ExistingAncestryCycleAlsoTerminatesWithoutWriting()
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 3;
        handler.Parents[3] = 2;
        using var http = new HttpClient(handler);
        Assert.Equal(RelationshipChangeStatus.Failed, (await Provider(http).SetParentAsync(new(Issue(1), 2))).Status);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task PreviewPreflightsButDoesNotMutate()
    {
        using var handler = new GraphHandler();
        using var http = new HttpClient(handler);
        Assert.Equal(RelationshipChangeStatus.Preview, (await Provider(http).SetParentAsync(new(Issue(1), 2, true))).Status);
        handler.Parents[1] = 2;
        Assert.Equal(RelationshipChangeStatus.Preview, (await Provider(http).SetParentAsync(new(Issue(1), null, true))).Status);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task MissingIssuesAndPullRequestsCannotBeLinked(int number, bool pullRequest)
    {
        using var handler = new GraphHandler();
        if (pullRequest) handler.PullRequests.Add(number);
        else handler.Numbers.Remove(number);
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentAsync(new(Issue(1), 2));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData("/issues/2", false)]
    [InlineData("/issues/1/parent", false)]
    [InlineData("/issues/2/parent", false)]
    [InlineData("/issues/1/parent", true)]
    public async Task CrossRepositoryResponsesAndPullRequestParentsAreRejected(string suffix, bool pullRequest)
    {
        using var handler = new GraphHandler();
        handler.Override = request => request.RequestUri?.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal) == true
            ? Response(Body(2, pullRequest ? "team/project" : "other/project", pullRequest)) : null;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentAsync(new(Issue(1), 2));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteReadBackMustMatchForSetAndClear(bool clear)
    {
        using var handler = new GraphHandler { IgnoreWrites = true };
        if (clear) handler.Parents[1] = 2;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentAsync(new(Issue(1), clear ? null : 2));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Contains("did not match", result.Diagnostic);
        Assert.Single(handler.Writes);
    }

    [Fact]
    public async Task ClearReadBackCannotTreatInvisibleChildAsSuccess()
    {
        using var handler = new GraphHandler { HideChildAfterWrite = true };
        handler.Parents[1] = 2;
        using var http = new HttpClient(handler);
        var result = await Provider(http).ClearParentAsync(Issue(1));
        Assert.Equal(RelationshipChangeStatus.Partial, result.Status);
        Assert.Contains("could not be verified", result.Diagnostic);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task WriteApiErrorsAreSafeFailuresWithRefreshGuidance(int status)
    {
        using var handler = new GraphHandler();
        handler.Override = request => request.Method != HttpMethod.Get
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("sensitive-response") } : null;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentAsync(new(Issue(1), 2));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Contains("refresh", result.Diagnostic);
        Assert.DoesNotContain("sensitive-response", result.Diagnostic);
        Assert.DoesNotContain("fake-test-credential", result.Diagnostic);
    }

    [Fact]
    public async Task FailedReadBackAndPreflightTransportRetainSafeDiagnostics()
    {
        using var handler = new GraphHandler();
        handler.Override = request => handler.Writes.Count > 0 && request.Method == HttpMethod.Get
            ? throw new HttpRequestException("sensitive-response") : null;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentAsync(new(Issue(1), 2));
        Assert.Equal(RelationshipChangeStatus.Partial, result.Status);
        Assert.Contains("refresh", result.Diagnostic);
        Assert.DoesNotContain("sensitive-response", result.Diagnostic);
        handler.Override = _ => throw new HttpRequestException("sensitive-response");
        result = await Provider(http).ClearParentAsync(Issue(1));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.DoesNotContain("sensitive-response", result.Diagnostic);
    }

    [Fact]
    public async Task ChildListingIsPaginatedAndDirectOnly()
    {
        using var handler = new GraphHandler();
        for (var number = 4; number <= 104; number++)
        {
            handler.Numbers.Add(number);
            handler.Parents[number] = 2;
        }
        handler.Parents[1] = 4;
        using var http = new HttpClient(handler);
        var children = await Provider(http).ListChildrenAsync(Issue(2));
        Assert.NotNull(children);
        Assert.Equal(101, children.Count);
        Assert.DoesNotContain(children, child => child.Issue.Number == 1);
        Assert.Equal(2, handler.ListPages);
    }

    [Theory]
    [InlineData("other/project", false)]
    [InlineData("team/project", true)]
    public async Task ChildListingRejectsOutOfScopeIssuesAndPullRequests(string repository, bool pullRequest)
    {
        using var handler = new GraphHandler();
        handler.Override = request => request.RequestUri?.AbsolutePath.EndsWith("/sub_issues", StringComparison.Ordinal) == true
            ? Response($"[{Body(1, repository, pullRequest)}]") : null;
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).ListChildrenAsync(Issue(2)));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task ReadsDistinguishMissingIssuesAndApiErrors()
    {
        using var handler = new GraphHandler();
        handler.Numbers.Remove(2);
        using var http = new HttpClient(handler);
        Assert.Null(await Provider(http).ListChildrenAsync(Issue(2)));
        Assert.Null(await Provider(http).GetParentAsync(Issue(2)));
        handler.Override = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).ListChildrenAsync(Issue(1)));
        Assert.Equal(GitHubIssueFailure.Authorization, error.Failure);
    }

    [Fact]
    public async Task CancellationPropagatesDuringAncestryAndAfterWrite()
    {
        foreach (var afterWrite in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new GraphHandler();
            handler.Override = request =>
            {
                if (afterWrite ? handler.Writes.Count > 0 : request.RequestUri?.AbsolutePath.EndsWith("/issues/2/parent", StringComparison.Ordinal) == true)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                return null;
            };
            using var http = new HttpClient(handler);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(http).SetParentAsync(new(Issue(1), 2), cancellation.Token));
            Assert.Equal(afterWrite ? 1 : 0, handler.Writes.Count);
        }
    }

    [Fact]
    public async Task BatchPreservesChangedAndUnchangedOutcomes()
    {
        using var handler = new GraphHandler();
        handler.Parents[1] = 3;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentsAsync(new(Issue(3), [1, 2]));
        Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        Assert.Equal(3, result.ParentIssueNumber);
        Assert.Equal([RelationshipChangeStatus.Unchanged, RelationshipChangeStatus.Changed],
            result.Relations.Select(item => item.Result.Status));
        Assert.Single(handler.Writes);
        Assert.Equal(3, handler.Parents[2]);
    }

    [Fact]
    public async Task BatchAssignsMultipleChildren()
    {
        using var handler = new GraphHandler();
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentsAsync(new(Issue(3), [1, 2]));
        Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        Assert.All(result.Relations, item => Assert.Equal(RelationshipChangeStatus.Changed, item.Result.Status));
        Assert.Equal(2, handler.Writes.Count);
    }

    [Fact]
    public async Task BatchConflictPreflightsEveryChildWithoutWriting()
    {
        using var handler = new GraphHandler();
        handler.Parents[2] = 1;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentsAsync(new(Issue(3), [1, 2]));
        Assert.Equal(RelationshipChangeStatus.Conflict, result.Status);
        Assert.Equal(RelationshipChangeStatus.Failed, result.Relations[0].Result.Status);
        Assert.Equal(RelationshipChangeStatus.Conflict, result.Relations[1].Result.Status);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchStopsAfterFailedOrUncertainWrite(bool uncertain)
    {
        using var handler = new GraphHandler { IgnoreWrites = !uncertain };
        handler.Override = request => uncertain && handler.Writes.Count > 0 && request.Method == HttpMethod.Get
            ? throw new HttpRequestException("sensitive-response") : null;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentsAsync(new(Issue(3), [1, 2]));
        Assert.Equal(uncertain ? RelationshipChangeStatus.Partial : RelationshipChangeStatus.Failed, result.Status);
        Assert.Equal(uncertain ? RelationshipChangeStatus.Partial : RelationshipChangeStatus.Failed, result.Relations[0].Result.Status);
        Assert.Contains("Not attempted", result.Relations[1].Result.Diagnostic);
        Assert.Single(handler.Writes);
    }

    [Fact]
    public async Task BatchRetainsSuccessBeforeLaterUncertainty()
    {
        using var handler = new GraphHandler();
        handler.Numbers.Add(4);
        handler.Override = request => handler.Writes.Count == 2 && request.Method == HttpMethod.Get
            ? throw new HttpRequestException("sensitive-response") : null;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentsAsync(new(Issue(4), [1, 2, 3]));
        Assert.Equal(RelationshipChangeStatus.Partial, result.Status);
        Assert.Equal(RelationshipChangeStatus.Changed, result.Relations[0].Result.Status);
        Assert.Equal(RelationshipChangeStatus.Partial, result.Relations[1].Result.Status);
        Assert.Contains("Not attempted", result.Relations[2].Result.Diagnostic);
        Assert.Equal(2, handler.Writes.Count);
    }

    [Fact]
    public async Task BatchPreviewChecksCyclesWithoutWriting()
    {
        using var handler = new GraphHandler();
        handler.Parents[3] = 2;
        using var http = new HttpClient(handler);
        var result = await Provider(http).SetParentsAsync(new(Issue(3), [1, 2], previewOnly: true));
        Assert.Equal(RelationshipChangeStatus.Failed, result.Status);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public void BatchRequestCopiesChildrenAndRejectsInvalidInput()
    {
        int[] numbers = [1, 2];
        var request = new SetParentsRequest(Issue(3), numbers);
        numbers[0] = 3;
        Assert.Equal([1, 2], request.ChildIssueNumbers);
        Assert.Throws<ArgumentException>(() => new SetParentsRequest(Issue(3), [1, 1]));
        Assert.Throws<ArgumentException>(() => new SetParentsRequest(Issue(3), [1, 3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetParentsRequest(Issue(3), [0]));
        Assert.Throws<ArgumentException>(() => new SetParentsRequest(Issue(3), []));
        Assert.Throws<ArgumentException>(() => new SetParentsRequest(Issue(100), Enumerable.Range(1, 51).ToArray()));
    }

    [Fact]
    public async Task BatchCancellationAfterWriteDoesNotAttemptRemainingChildren()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new GraphHandler();
        handler.Override = _ =>
        {
            if (handler.Writes.Count > 0)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return null;
        };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(http)
            .SetParentsAsync(new(Issue(3), [1, 2]), cancellation.Token));
        Assert.Single(handler.Writes);
        Assert.False(handler.Parents.ContainsKey(2));
    }

    private static IssueReference Issue(int number) => new(new("team/project"), number);
    private static GitHubIssueProvider Provider(HttpClient http) => new(http, _ => Task.FromResult("fake-test-credential"));
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static string Body(int number, string repository = "team/project", bool pullRequest = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = 1000L + number, ["number"] = number, ["title"] = "Example", ["state"] = "open",
            ["html_url"] = $"https://github.com/{repository}/issues/{number}"
        }.Concat(pullRequest ? new Dictionary<string, object> { ["pull_request"] = new { } } : []).ToDictionary());

    private sealed class GraphHandler : HttpMessageHandler
    {
        public HashSet<int> Numbers { get; } = [1, 2, 3];
        public HashSet<int> PullRequests { get; } = [];
        public Dictionary<int, int> Parents { get; } = [];
        public List<HttpMethod> Writes { get; } = [];
        public List<long> WrittenIds { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
        public bool LoseWriteResponse { get; init; }
        public bool IgnoreWrites { get; init; }
        public bool HideChildAfterWrite { get; init; }
        public int ListPages { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            Assert.Equal("fake-test-credential", request.Headers.Authorization?.Parameter);
            Assert.Contains("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version"));
            var overridden = Override?.Invoke(request);
            if (overridden is not null) return overridden;
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
            Assert.StartsWith("/repos/team/project/issues/", uri.AbsolutePath);
            var path = uri.AbsolutePath.Split('/');
            var number = int.Parse(path[5], System.Globalization.CultureInfo.InvariantCulture);
            if (!Numbers.Contains(number)) return new(HttpStatusCode.NotFound);
            var suffix = path.Length > 6 ? path[6] : "";
            if (request.Method != HttpMethod.Get)
            {
                Assert.Equal(request.Method == HttpMethod.Post ? "sub_issues" : "sub_issue", suffix);
                Assert.NotNull(request.Content);
                using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                Assert.Single(document.RootElement.EnumerateObject()); // No implicit replacement flag.
                var id = document.RootElement.GetProperty("sub_issue_id").GetInt64();
                var child = checked((int)(id - 1000));
                Writes.Add(request.Method);
                WrittenIds.Add(id);
                if (!IgnoreWrites)
                {
                    if (request.Method == HttpMethod.Post) Parents[child] = number;
                    else Parents.Remove(child);
                }
                if (HideChildAfterWrite) Numbers.Remove(child);
                if (LoseWriteResponse) throw new HttpRequestException("sensitive-response");
                return Response("{}");
            }
            if (suffix == "parent")
            {
                return Parents.TryGetValue(number, out var parent) ? Response(Body(parent)) : new(HttpStatusCode.NotFound);
            }
            if (suffix == "sub_issues")
            {
                ListPages++;
                var page = int.Parse(uri.Query.Split("page=")[^1], System.Globalization.CultureInfo.InvariantCulture);
                return Response("[" + string.Join(',', Parents.Where(pair => pair.Value == number)
                    .OrderBy(pair => pair.Key).Skip((page - 1) * 100).Take(100).Select(pair => Body(pair.Key))) + "]");
            }
            Assert.Equal("", suffix);
            return Response(Body(number, pullRequest: PullRequests.Contains(number)));
        }
    }
}
