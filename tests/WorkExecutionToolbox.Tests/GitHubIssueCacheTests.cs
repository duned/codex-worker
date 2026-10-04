using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkExecutionToolbox.Cli;

namespace WorkExecutionToolbox.Tests;

public sealed class GitHubIssueCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wet-cache-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();

    private GitHubIssueProvider Provider(HttpClient http) => new(http, _ => Task.FromResult("test-credential"),
        new GitHubIssueCacheOptions { DirectoryPath = _directory, TimeProvider = _clock });
    private static IssueReference Issue(int number = 1, string repository = "team/project") => new(new(repository), number);

    [Fact]
    public async Task FifteenIssueGraphReusesPersistedReadsAcrossProviderRestarts()
    {
        using var handler = new Handler();
        for (var number = 2; number <= 15; number++) handler.Parents[number] = 1;
        // Every child is also encountered as a dependency: equivalent endpoint reads stay unique.
        handler.Dependencies[1] = Enumerable.Range(2, 14).ToHashSet();
        using var http = new HttpClient(handler);
        var cold = await Provider(http).GetGraphAsync(Issue());
        Assert.NotNull(cold);
        Assert.Equal(15, cold.Issues.Count);
        Assert.Equal(75, handler.Reads.Count);
        Assert.Equal(handler.Reads.Count, handler.Reads.Distinct().Count());
        handler.Reads.Clear();
        var warm = await Provider(http).GetGraphAsync(Issue(), new() { MaxRequests = 1 });
        Assert.NotNull(warm);
        Assert.Equal(cold.Issues, warm.Issues);
        Assert.Equal(cold.Edges, warm.Edges);
        Assert.Empty(handler.Reads);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories))
        {
            var data = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("test-credential", data);
            Assert.DoesNotContain("private-body", data);
        }
    }

    [Fact]
    public async Task OperationDeduplicatesConcurrentAndOverlappingRefreshReads()
    {
        using var handler = new Handler();
        handler.Parents[2] = 1;
        handler.Dependencies[1] = [2];
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        using (provider.BeginReadOperation(refresh: true))
        {
            await Task.WhenAll(provider.GetIssueAsync(Issue()), provider.GetIssueAsync(Issue()));
            await provider.GetRelationshipsAsync(Issue());
            await provider.ListChildrenAsync(Issue());
            await provider.GetBlockedByAsync(Issue());
            await provider.GetParentAsync(Issue());
            await provider.GetGraphAsync(Issue());
            Assert.Equal(10, handler.Reads.Count);
            Assert.Equal(handler.Reads.Count, handler.Reads.Distinct().Count());
        }
        await provider.GetGraphAsync(Issue());
        Assert.Equal(10, handler.Reads.Count);
    }

    [Theory]
    [InlineData(false, 29, 0)]
    [InlineData(false, 30, 5)]
    [InlineData(true, 86400, 0)]
    [InlineData(true, 604800, 5)]
    public async Task MetadataAndCompleteRelationshipsHaveExplicitFreshness(bool closed, int seconds, int newReads)
    {
        using var handler = new Handler { Closed = closed };
        using var http = new HttpClient(handler);
        await Provider(http).GetRelationshipsAsync(Issue());
        handler.Reads.Clear();
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        await Provider(http).GetRelationshipsAsync(Issue());
        Assert.Equal(newReads, handler.Reads.Count);
    }

    [Fact]
    public async Task RepositoryAndNumberKeysAreSafeAndCaseInsensitive()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetIssueAsync(Issue());
        await Provider(http).GetIssueAsync(Issue(repository: "TEAM/PROJECT"));
        Assert.Single(handler.Reads);
        await Provider(http).GetIssueAsync(Issue(repository: "other/project"));
        await Provider(http).GetIssueAsync(Issue(2));
        Assert.Equal(3, handler.Reads.Count);
        Assert.Equal(3, Directory.GetFiles(_directory, "*-identity.json", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("relationships")]
    [InlineData("children")]
    public async Task CliRefreshReadsMutableDataButRetainsStableIdentityCache(string command)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetRelationshipsAsync(Issue());
        var identity = Assert.Single(Directory.GetFiles(_directory, "*-identity.json", SearchOption.AllDirectories));
        var original = await File.ReadAllTextAsync(identity);
        handler.Reads.Clear();
        handler.Title = "Updated";
        var provider = Provider(http);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await ToolboxCommand.RunAsync([command, "1", "--repo", "team/project", "--refresh"],
            provider, provider, output, error));
        Assert.Equal(5, handler.Reads.Count);
        Assert.Equal(original, await File.ReadAllTextAsync(identity));
        Assert.Contains("Updated", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task PersistentIdentitySurvivesExpirationAndDetectsChangedIdentity()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetIssueAsync(Issue());
        _clock.Advance(TimeSpan.FromDays(30));
        handler.IdentityOffset = 100;
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetIssueAsync(Issue()));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
        Assert.Equal(2, handler.Reads.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VerifiedWritesInvalidateBothEndpointsAndPreserveIdentity(bool hierarchy, bool lostResponse)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetGraphAsync(Issue());
        await Provider(http).GetGraphAsync(Issue(2));
        if (lostResponse) handler.AfterWrite = () => throw new HttpRequestException("private transport details");
        var provider = Provider(http);
        var result = hierarchy
            ? await provider.SetParentAsync(new(Issue(2), 1))
            : await provider.SetDependencyAsync(new(Issue(2), 1, true));
        Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        handler.Reads.Clear();
        var refreshed = await Provider(http).GetGraphAsync(Issue());
        Assert.NotNull(refreshed);
        Assert.Equal(2, refreshed.Issues.Count);
        Assert.Single(refreshed.Edges);
        Assert.NotEmpty(handler.Reads);
        Assert.Equal(2, Directory.GetFiles(_directory, "*-identity.json", SearchOption.AllDirectories).Length);
        handler.Reads.Clear();
        await Provider(http).GetGraphAsync(Issue());
        Assert.Empty(handler.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainOrCancelledWriteCannotReusePreflightData(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetRelationshipsAsync(Issue());
        await Provider(http).GetRelationshipsAsync(Issue(2));
        handler.AfterWrite = cancel ? cancellation.Cancel : () => handler.FailReads = true;
        var provider = Provider(http);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.SetDependencyAsync(new(Issue(), 2, true), cancellation.Token));
        else
        {
            var result = await provider.SetDependencyAsync(new(Issue(), 2, true));
            Assert.Equal(RelationshipChangeStatus.Partial, result.Status);
        }
        handler.FailReads = false;
        handler.Reads.Clear();
        var relationships = await Provider(http).GetRelationshipsAsync(Issue());
        Assert.NotNull(relationships);
        Assert.Equal(2, Assert.Single(relationships.BlockedBy).Issue.Number);
        Assert.Equal(5, handler.Reads.Count);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("version")]
    [InlineData("scope")]
    [InlineData("invalid-identity")]
    [InlineData("null-repository")]
    public async Task CorruptOrIncompatibleEntriesAreDiscarded(string corruption)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetIssueAsync(Issue());
        var file = Assert.Single(Directory.GetFiles(_directory, "1-issue.json", SearchOption.AllDirectories));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file)) ?? throw new InvalidOperationException();
        if (corruption == "version") json["Version"] = 99;
        if (corruption == "scope") json["Repository"] = "other/project";
        if (corruption == "invalid-identity") json["Issues"]?[0]?["DatabaseId"] = -1;
        if (corruption == "null-repository") json["Repository"] = null;
        await File.WriteAllTextAsync(file, corruption == "invalid-json" ? "{" : json.ToJsonString());
        Assert.NotNull(await Provider(http).GetIssueAsync(Issue()));
        Assert.Equal(2, handler.Reads.Count);
    }

    [Fact]
    public async Task CorruptIdentityIsResolvedAgainWithoutPoisoningTheProvider()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetIssueAsync(Issue());
        var identity = Assert.Single(Directory.GetFiles(_directory, "*-identity.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(identity, "invalid");
        var provider = Provider(http);
        using (provider.BeginReadOperation(refresh: true)) await provider.GetIssueAsync(Issue());
        using var repaired = JsonDocument.Parse(await File.ReadAllTextAsync(identity));
        Assert.Equal(1001, repaired.RootElement.GetProperty("DatabaseId").GetInt64());
    }

    [Fact]
    public async Task WarmCacheStillHonorsCancellationAndRefreshPreservesTypedFailures()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetGraphAsync(Issue());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(http).GetGraphAsync(Issue(),
            cancellationToken: cancellation.Token));
        handler.FailReads = true;
        var provider = Provider(http);
        using (provider.BeginReadOperation(refresh: true))
        {
            var error = await Assert.ThrowsAsync<GitHubIssueException>(() => provider.GetGraphAsync(Issue()));
            Assert.Equal(GitHubIssueFailure.Authorization, error.Failure);
        }
    }

    [Fact]
    public async Task MutationAlsoInvalidatesEnclosingLibraryReadScope()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        using var scope = provider.BeginReadOperation();
        var before = await provider.GetRelationshipsAsync(Issue());
        Assert.NotNull(before);
        Assert.Empty(before.BlockedBy);
        Assert.Equal(RelationshipChangeStatus.Changed, (await provider.SetDependencyAsync(new(Issue(), 2, true))).Status);
        var after = await provider.GetRelationshipsAsync(Issue());
        Assert.NotNull(after);
        Assert.Equal(2, Assert.Single(after.BlockedBy).Issue.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentRelationReadsOverlapAndAreJoined(bool failParent)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        handler.AfterRead = async uri =>
        {
            if (uri.AbsolutePath.EndsWith("/issues/1", StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref count) == 4) entered.SetResult();
            if (failParent && uri.AbsolutePath.EndsWith("/parent", StringComparison.Ordinal))
                throw new HttpRequestException("private transport details");
            await release.Task;
        };
        var inspection = Provider(http).GetRelationshipsAsync(Issue());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(inspection.IsCompleted);
        }
        finally { release.TrySetResult(); }
        if (failParent)
        {
            var error = await Assert.ThrowsAsync<GitHubIssueException>(() => inspection);
            Assert.Equal(GitHubIssueFailure.Transport, error.Failure);
        }
        else Assert.NotNull(await inspection);
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task ReadStartedBeforeMutationCannotRepublishStaleRelationships()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetRelationshipsAsync(Issue());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = 0;
        handler.AfterRead = async uri =>
        {
            if (uri.AbsolutePath.EndsWith("/1/dependencies/blocked_by", StringComparison.Ordinal) &&
                Interlocked.Increment(ref intercepted) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        };
        var reader = Provider(http);
        Task<IReadOnlyList<IssueSummary>?> pending;
        using (reader.BeginReadOperation(refresh: true)) pending = reader.GetBlockedByAsync(Issue());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await Provider(http).SetDependencyAsync(new(Issue(), 2, true));
            Assert.Equal(RelationshipChangeStatus.Changed, result.Status);
        }
        finally { release.TrySetResult(); }
        Assert.Empty(await pending ?? throw new InvalidOperationException());
        var fresh = await Provider(http).GetRelationshipsAsync(Issue());
        Assert.NotNull(fresh);
        Assert.Equal(2, Assert.Single(fresh.BlockedBy).Issue.Number);
    }

    [Fact]
    public async Task LostGenerationMarkerCannotResurrectInvalidatedData()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        await Provider(http).GetRelationshipsAsync(Issue());
        await Provider(http).SetDependencyAsync(new(Issue(), 2, true));
        File.Delete(Assert.Single(Directory.GetFiles(_directory, "generation", SearchOption.AllDirectories)));
        var fresh = await Provider(http).GetRelationshipsAsync(Issue());
        Assert.NotNull(fresh);
        Assert.Equal(2, Assert.Single(fresh.BlockedBy).Issue.Number);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Dictionary<int, int> Parents { get; } = [];
        public Dictionary<int, HashSet<int>> Dependencies { get; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<string> Reads { get; } = new();
        public bool Closed { get; init; }
        public string Title { get; set; } = "Issue";
        public long IdentityOffset { get; set; }
        public bool FailReads { get; set; }
        public Action? AfterWrite { get; set; }
        public Func<Uri, Task>? AfterRead { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            var parts = uri.AbsolutePath.Split('/');
            var repository = parts[2] + "/" + parts[3];
            var number = int.Parse(parts[5], CultureInfo.InvariantCulture);
            if (request.Method != HttpMethod.Get)
            {
                var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(text);
                if (parts[6] == "sub_issues")
                    Parents[(int)body.RootElement.GetProperty("sub_issue_id").GetInt64() - 1000] = number;
                else
                {
                    if (!Dependencies.TryGetValue(number, out var dependencies)) Dependencies[number] = dependencies = [];
                    dependencies.Add((int)body.RootElement.GetProperty("issue_id").GetInt64() - 1000);
                }
                AfterWrite?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                return new(HttpStatusCode.Created);
            }
            Reads.Enqueue(uri.PathAndQuery);
            if (FailReads) return new(HttpStatusCode.Unauthorized);
            object Summary(int n) => new { id = 1000L + n + IdentityOffset, number = n, title = Title,
                state = Closed ? "closed" : "open", html_url = $"https://github.com/{repository}/issues/{n}", body = "private-body" };
            object payload;
            if (parts.Length == 6) payload = Summary(number);
            else if (parts[6] == "parent")
            {
                if (!Parents.TryGetValue(number, out var parent))
                {
                    if (AfterRead is { } onMissing) await onMissing(uri);
                    return new(HttpStatusCode.NotFound);
                }
                payload = Summary(parent);
            }
            else
            {
                var related = parts[6] == "sub_issues" ? Parents.Where(pair => pair.Value == number).Select(pair => pair.Key)
                    : parts[7] == "blocked_by" ? Dependencies.GetValueOrDefault(number, [])
                    : Dependencies.Where(pair => pair.Value.Contains(number)).Select(pair => pair.Key);
                payload = related.Order().Select(Summary).ToArray();
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
            if (AfterRead is { } onRead) await onRead(uri);
            return response;
        }
    }
}
