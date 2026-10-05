using System.Globalization;
using System.Net;
using System.Text.Json;
using WorkExecutionToolbox.Cli;

namespace WorkExecutionToolbox.Tests;

public sealed class GitHubIssueRootTests
{
    [Theory]
    [InlineData(IssueListState.All, 3)]
    [InlineData(IssueListState.Open, 2)]
    [InlineData(IssueListState.Closed, 1)]
    public async Task NativeParentsAloneDetermineRootsAndStates(IssueListState state, int count)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var roots = await Provider(http).ListRootsAsync(new("owner/repo"), state);
        Assert.Equal(count, roots.Count);
        Assert.DoesNotContain(roots, issue => issue.Issue.Number is 2 or 5);
        if (state == IssueListState.All) Assert.Equal(new[] { 1, 3, 4 }, roots.Select(issue => issue.Issue.Number));
        Assert.DoesNotContain(handler.Paths, path => path.Contains("/sub_issues", StringComparison.Ordinal));
        Assert.Contains($"state={state.ToString().ToLowerInvariant()}", handler.Paths[0]);
    }

    [Fact]
    public async Task PaginationCountsPullRequestsAndReadsAnEmptyTerminalPage()
    {
        using var handler = new Handler { Count = 200, AllPullRequests = true };
        using var http = new HttpClient(handler);
        Assert.Empty(await Provider(http).ListRootsAsync(new("owner/repo")));
        Assert.Equal(3, handler.Paths.Count);
        Assert.EndsWith("page=3", handler.Paths[^1]);
    }

    [Fact]
    public async Task MultiPageListingReturnsAllRootsInNumberOrder()
    {
        using var handler = new Handler { Count = 101 };
        using var http = new HttpClient(handler);
        var roots = await Provider(http).ListRootsAsync(new("owner/repo"));
        Assert.Equal(99, roots.Count);
        Assert.Equal(101, roots[^1].Issue.Number);
        Assert.Contains(handler.Paths, path => path.EndsWith("page=2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExhaustedLimitsFailInsteadOfReturningPartialResults(bool pagination)
    {
        using var handler = new Handler { Count = 100 };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).ListRootsAsync(new("owner/repo"),
            options: pagination ? new() { MaxPages = 1 } : new() { MaxRequests = 1 }));
        Assert.Equal(GitHubIssueFailure.LimitExceeded, error.Failure);
    }

    [Fact]
    public async Task DuplicateIssuesAcrossPagesAreRejected()
    {
        using var handler = new Handler { Count = 101, RepeatFirstPage = true };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).ListRootsAsync(new("owner/repo")));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task EmptyJsonResultIsASuccessfulArray()
    {
        using var handler = new Handler { Count = 0 };
        using var http = new HttpClient(handler);
        var run = await RunAsync(["roots", "--repo", "owner/repo", "--json"], Provider(http));
        Assert.Equal(0, run.Exit);
        Assert.Empty(run.Error);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Empty(json.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task InvalidBoundsAndStateFailBeforeHttp()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = Provider(http);
        foreach (var options in new[]
        {
            new IssueRootOptions { MaxPages = 0 }, new IssueRootOptions { MaxPages = 101 },
            new IssueRootOptions { MaxRequests = 0 }, new IssueRootOptions { MaxRequests = 5001 }
        })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.ListRootsAsync(new("owner/repo"), options: options));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.ListRootsAsync(new("owner/repo"), (IssueListState)99));
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData(401, GitHubIssueFailure.Authorization)]
    [InlineData(429, GitHubIssueFailure.RateLimited)]
    [InlineData(500, GitHubIssueFailure.Provider)]
    public async Task ParentReadFailuresDoNotProduceRoots(int status, GitHubIssueFailure failure)
    {
        using var handler = new Handler { ParentFailure = status };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).ListRootsAsync(new("owner/repo")));
        Assert.Equal(failure, error.Failure);
    }

    [Fact]
    public async Task RefreshBypassesCachedParentDataAndRetainsReadDeduplication()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new Handler();
            using var http = new HttpClient(handler);
            GitHubIssueProvider Create() => new(http, _ => Task.FromResult("test-credential"), new() { DirectoryPath = directory });
            await Create().ListRootsAsync(new("owner/repo"));
            handler.ParentExists = false;
            handler.Paths.Clear();
            Assert.Equal(3, (await Create().ListRootsAsync(new("owner/repo"))).Count);
            Assert.Empty(handler.Paths);
            var provider = Create();
            using var scope = provider.BeginReadOperation(refresh: true);
            Assert.Equal(4, (await provider.ListRootsAsync(new("owner/repo"))).Count);
            Assert.Equal(4, (await provider.ListRootsAsync(new("owner/repo"))).Count);
            Assert.Equal(4, handler.Paths.Count(path => path.EndsWith("/parent", StringComparison.Ordinal)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task PersistentMultiPageScanRefreshesOnlyOpenOrChangedIssues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            using var handler = new Handler { Count = 201, MostlyClosed = true };
            using var http = new HttpClient(handler);
            GitHubIssueProvider Create() => new(http, _ => Task.FromResult("test-credential"),
                new() { DirectoryPath = directory, TimeProvider = clock });
            var cold = await Create().ListRootsAsync(new("owner/repo"));
            Assert.Equal(199, cold.Count);
            Assert.Equal(203, handler.Paths.Count); // Three pages and 200 native parents, no metadata GETs.
            handler.Paths.Clear();
            Assert.Equal(cold, await Create().ListRootsAsync(new("owner/repo")));
            Assert.Empty(handler.Paths);

            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(cold, await Create().ListRootsAsync(new("owner/repo")));
            Assert.Equal(5, handler.Paths.Count); // One delta page, two open metadata GETs and two parents.
            Assert.Single(handler.Paths, path => path.Contains("since=", StringComparison.Ordinal));
            Assert.Equal(2, handler.Paths.Count(path => path.EndsWith("/parent", StringComparison.Ordinal)));
            handler.Paths.Clear();

            clock.Advance(TimeSpan.FromDays(6));
            handler.ChangedNumbers = [4];
            Assert.Equal(cold, await Create().ListRootsAsync(new("owner/repo")));
            Assert.Equal(5, handler.Paths.Count); // Updated closed metadata does not expire its parent.
            Assert.DoesNotContain(handler.Paths, path => path.EndsWith("/4/parent", StringComparison.Ordinal));
            handler.Paths.Clear();

            var fresh = Create();
            using (fresh.BeginReadOperation(refresh: true))
                Assert.Equal(cold, await fresh.ListRootsAsync(new("owner/repo")));
            Assert.Equal(203, handler.Paths.Count);
            Assert.DoesNotContain(handler.Paths, path => path.Contains("since=", StringComparison.Ordinal));
            handler.Paths.Clear();

            clock.Advance(TimeSpan.FromDays(7));
            Assert.Equal(cold, await Create().ListRootsAsync(new("owner/repo")));
            Assert.Equal(203, handler.Paths.Count);
            Assert.DoesNotContain(handler.Paths, path => path.Contains("since=", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ClosedParentsRemainFreshWhileListingDiscoversUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            using var handler = new Handler { Count = 201, MostlyClosed = true };
            using var http = new HttpClient(handler);
            GitHubIssueProvider Create() => new(http, _ => Task.FromResult("test-credential"),
                new() { DirectoryPath = directory, TimeProvider = clock });
            var roots = await Create().ListRootsAsync(new("owner/repo"), IssueListState.Closed);
            Assert.Equal(198, roots.Count);
            Assert.Equal(200, handler.Paths.Count);
            handler.Paths.Clear();
            clock.Advance(TimeSpan.FromDays(6));
            Assert.Equal(roots, await Create().ListRootsAsync(new("owner/repo"), IssueListState.Closed));
            Assert.Single(handler.Paths); // Only the update listing, no closed parent or metadata GETs.
            handler.Paths.Clear();
            clock.Advance(TimeSpan.FromDays(1));
            Assert.Equal(roots, await Create().ListRootsAsync(new("owner/repo"), IssueListState.Closed));
            Assert.Equal(200, handler.Paths.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IncrementalScanDetectsClosuresReopensAndDisappearance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            using var handler = new Handler { Count = 6, MostlyClosed = true };
            using var http = new HttpClient(handler);
            GitHubIssueProvider Create() => new(http, _ => Task.FromResult("test-credential"),
                new() { DirectoryPath = directory, TimeProvider = clock });
            await Create().ListRootsAsync(new("owner/repo"));
            handler.ClosedOverrides[1] = true;
            handler.ClosedOverrides[3] = false;
            handler.MissingNumbers.Add(2);
            handler.ChangedNumbers = [1, 3];
            handler.Paths.Clear();
            clock.Advance(TimeSpan.FromSeconds(31));
            var roots = await Create().ListRootsAsync(new("owner/repo"));
            Assert.Equal(IssueState.Closed, roots.Single(issue => issue.Issue.Number == 1).State);
            Assert.Equal(IssueState.Open, roots.Single(issue => issue.Issue.Number == 3).State);
            Assert.DoesNotContain(roots, issue => issue.Issue.Number == 2);
            Assert.Equal(3, handler.Paths.Count);
            Assert.Contains(handler.Paths, path => path.EndsWith("/issues/2", StringComparison.Ordinal));
            Assert.Contains(handler.Paths, path => path.EndsWith("/3/parent", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(IssueListState.Open)]
    [InlineData(IssueListState.Closed)]
    public async Task FilteredIncrementalScansDiscoverStateTransitions(IssueListState state)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            using var handler = new Handler { Count = 6, MostlyClosed = true };
            using var http = new HttpClient(handler);
            GitHubIssueProvider Create() => new(http, _ => Task.FromResult("test-credential"),
                new() { DirectoryPath = directory, TimeProvider = clock });
            await Create().ListRootsAsync(new("owner/repo"), state);
            handler.ClosedOverrides[1] = true;
            handler.ClosedOverrides[3] = false;
            handler.ChangedNumbers = [1, 3];
            clock.Advance(TimeSpan.FromSeconds(31));
            var roots = await Create().ListRootsAsync(new("owner/repo"), state);
            if (state == IssueListState.Open)
            {
                Assert.DoesNotContain(roots, issue => issue.Issue.Number == 1);
                Assert.Contains(roots, issue => issue.Issue.Number == 3);
            }
            else Assert.Contains(roots, issue => issue.Issue.Number == 1);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedPaginationCannotPublishACompleteListing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new Handler { Count = 101 };
            using var http = new HttpClient(handler);
            GitHubIssueProvider Create() => new(http, _ => Task.FromResult("test-credential"), new() { DirectoryPath = directory });
            await Assert.ThrowsAsync<GitHubIssueException>(() => Create().ListRootsAsync(new("owner/repo"),
                options: new() { MaxPages = 1 }));
            handler.Paths.Clear();
            Assert.Equal(99, (await Create().ListRootsAsync(new("owner/repo"))).Count);
            Assert.Contains(handler.Paths, path => path.EndsWith("page=2", StringComparison.Ordinal));
            handler.Paths.Clear();
            var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Create().ListRootsAsync(new("owner/repo"),
                options: new() { MaxPages = 1 }));
            Assert.Equal(GitHubIssueFailure.LimitExceeded, error.Failure);
            Assert.Empty(handler.Paths);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task IncrementalGrowthCannotBypassPaginationBounds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-roots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock();
            using var handler = new Handler { Count = 101, MostlyClosed = true };
            using var http = new HttpClient(handler);
            GitHubIssueProvider Create() => new(http, _ => Task.FromResult("test-credential"),
                new() { DirectoryPath = directory, TimeProvider = clock });
            await Create().ListRootsAsync(new("owner/repo"), options: new() { MaxPages = 2 });
            handler.Count = 201;
            handler.ChangedNumbers = Enumerable.Range(102, 100).ToArray();
            clock.Advance(TimeSpan.FromSeconds(31));
            var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Create().ListRootsAsync(new("owner/repo"),
                options: new() { MaxPages = 2 }));
            Assert.Equal(GitHubIssueFailure.LimitExceeded, error.Failure);
            Assert.Equal(199, (await Create().ListRootsAsync(new("owner/repo"))).Count);
            error = await Assert.ThrowsAsync<GitHubIssueException>(() => Create().ListRootsAsync(new("owner/repo"),
                options: new() { MaxPages = 2 }));
            Assert.Equal(GitHubIssueFailure.LimitExceeded, error.Failure);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ParentFanOutIsBoundedAndJoined()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parents = 0;
        using var handler = new Handler
        {
            Count = 20, ParentGate = release.Task,
            OnParent = () => { if (Interlocked.Increment(ref parents) == 4) started.SetResult(); }
        };
        using var http = new HttpClient(handler);
        var scan = Provider(http).ListRootsAsync(new("owner/repo"));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(4, Volatile.Read(ref parents));
            Assert.False(scan.IsCompleted);
        }
        finally
        {
            release.SetResult();
            await scan;
        }
        Assert.Equal(19, parents);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliUsesDiscoveryOrExplicitRepositoryAndVersionedStructuredOutput(bool explicitRepository)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var provider = Provider(http);
        var exit = await ToolboxCommand.RunAsync(explicitRepository
                ? ["roots", "--repo", "owner/repo", "--state", "closed", "--json", "--refresh"]
                : ["roots", "--json", "--state", "closed"], provider, provider, output, error,
            readRemoteUrls: _ => explicitRepository ? throw new InvalidOperationException("Unexpected discovery")
                : Task.FromResult<IReadOnlyList<string>>(["https://github.com/owner/repo.git"]));
        Assert.Equal(0, exit);
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("roots", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("owner/repo", json.RootElement.GetProperty("repository").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("issue").ValueKind);
        var issue = Assert.Single(json.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(4, issue.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("closed", issue.GetProperty("state").GetString());
        Assert.DoesNotContain("DatabaseId", output.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task TextOutputReportsEmptySuccessOrInspectableIssues(int count)
    {
        using var handler = new Handler { Count = count };
        using var http = new HttpClient(handler);
        var run = await RunAsync(["roots", "--repo", "owner/repo"], Provider(http));
        Assert.Equal(0, run.Exit);
        Assert.Empty(run.Error);
        if (count == 0) Assert.Contains("Roots: none", run.Output);
        else
        {
            Assert.Contains("Issue 1 [Open]: Title 1", run.Output);
            Assert.Contains("Issue 4 [Closed]: Title 4", run.Output);
        }
    }

    [Theory]
    [InlineData("roots --state unknown")]
    [InlineData("roots --state open --state closed")]
    [InlineData("roots 1")]
    [InlineData("children 1 --state all")]
    public async Task InvalidArgumentsFailBeforeProviderAccess(string command)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var run = await RunAsync([.. command.Split(' '), "--repo", "owner/repo"], Provider(http));
        Assert.Equal(2, run.Exit);
        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task CancellationDuringParentReadPropagatesAndHasCliExit130()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler { OnParent = cancellation.Cancel };
        using var http = new HttpClient(handler);
        var run = await RunAsync(["roots", "--repo", "owner/repo"], Provider(http), cancellation.Token);
        Assert.Equal(130, run.Exit);
        Assert.Empty(run.Output);
        Assert.InRange(handler.Paths.Count, 2, 5); // One page and at most four in-flight parents.
    }

    [Fact]
    public async Task ProviderFailureUsesExistingJsonErrorEnvelope()
    {
        using var handler = new Handler { ParentFailure = 500 };
        using var http = new HttpClient(handler);
        var run = await RunAsync(["roots", "--repo", "owner/repo", "--json"], Provider(http));
        Assert.Equal(1, run.Exit);
        Assert.Empty(run.Output);
        using var json = JsonDocument.Parse(run.Error);
        Assert.Equal("provider", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static GitHubIssueProvider Provider(HttpClient http) => new(http, _ => Task.FromResult("test-credential"));

    private static async Task<(int Exit, string Output, string Error)> RunAsync(string[] args, GitHubIssueProvider provider,
        CancellationToken token = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ToolboxCommand.RunAsync(args, provider, provider, output, error, token);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Count { get; set; } = 5;
        public bool RepeatFirstPage { get; init; }
        public bool AllPullRequests { get; init; }
        public bool ParentExists { get; set; } = true;
        public bool MostlyClosed { get; init; }
        public int[] ChangedNumbers { get; set; } = [];
        public Dictionary<int, bool> ClosedOverrides { get; } = [];
        public HashSet<int> MissingNumbers { get; } = [];
        public Task? ParentGate { get; init; }
        public int? ParentFailure { get; init; }
        public Action? OnParent { get; init; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
            lock (Paths) Paths.Add(uri.PathAndQuery);
            if (uri.AbsolutePath.EndsWith("/parent", StringComparison.Ordinal))
            {
                OnParent?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (ParentFailure is { } failure) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)failure));
                var response = ParentExists && uri.AbsolutePath.EndsWith("/2/parent", StringComparison.Ordinal)
                    ? Response(JsonSerializer.Serialize(Body(1))) : new(HttpStatusCode.NotFound);
                return ParentGate is { } gate ? WaitForParentAsync(gate, response, cancellationToken) : Task.FromResult(response);
            }
            if (uri.Query.Length == 0)
            {
                var number = int.Parse(uri.AbsolutePath.Split('/')[^1], CultureInfo.InvariantCulture);
                return Task.FromResult(MissingNumbers.Contains(number) ? new(HttpStatusCode.NotFound) : Response(JsonSerializer.Serialize(Body(number))));
            }
            var page = int.Parse(uri.Query.TrimStart('?').Split('&').Single(part => part.StartsWith("page=", StringComparison.Ordinal))[5..], CultureInfo.InvariantCulture);
            if (RepeatFirstPage) page = 1;
            var items = Enumerable.Range(1, Count)
                .Where(number => !MissingNumbers.Contains(number))
                .Where(number => uri.Query.Contains("state=all", StringComparison.Ordinal) ||
                    uri.Query.Contains(IsClosed(number) ? "state=closed" : "state=open", StringComparison.Ordinal))
                .Where(number => !uri.Query.Contains("since=", StringComparison.Ordinal) || ChangedNumbers.Contains(number))
                .Skip((page - 1) * 100).Take(100).Select(Body);
            return Task.FromResult(Response(JsonSerializer.Serialize(items)));
        }

        private bool IsClosed(int number) => ClosedOverrides.TryGetValue(number, out var closed)
            ? closed : MostlyClosed ? number > 2 : number == 4;

        private static async Task<HttpResponseMessage> WaitForParentAsync(Task gate, HttpResponseMessage response,
            CancellationToken token)
        {
            try { await gate.WaitAsync(token); return response; }
            catch { response.Dispose(); throw; }
        }

        private Dictionary<string, object> Body(int number)
        {
            var body = new Dictionary<string, object>
            {
                ["id"] = 1000 + number, ["number"] = number, ["title"] = $"Title {number}",
                ["state"] = IsClosed(number) ? "closed" : "open", ["html_url"] = $"https://github.com/owner/repo/issues/{number}"
            };
            if (AllPullRequests || number == 5) body["pull_request"] = new { };
            return body;
        }

        private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
