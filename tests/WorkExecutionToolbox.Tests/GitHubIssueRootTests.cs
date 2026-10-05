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
            Assert.Single(handler.Paths);
            var provider = Create();
            using var scope = provider.BeginReadOperation(refresh: true);
            Assert.Equal(4, (await provider.ListRootsAsync(new("owner/repo"))).Count);
            Assert.Equal(4, (await provider.ListRootsAsync(new("owner/repo"))).Count);
            Assert.Equal(4, handler.Paths.Count(path => path.EndsWith("/parent", StringComparison.Ordinal)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
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
        Assert.Equal(2, handler.Paths.Count);
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
        public int Count { get; init; } = 5;
        public bool RepeatFirstPage { get; init; }
        public bool AllPullRequests { get; init; }
        public bool ParentExists { get; set; } = true;
        public int? ParentFailure { get; init; }
        public Action? OnParent { get; init; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing URI");
            Paths.Add(uri.PathAndQuery);
            if (uri.AbsolutePath.EndsWith("/parent", StringComparison.Ordinal))
            {
                OnParent?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (ParentFailure is { } failure) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)failure));
                return Task.FromResult(ParentExists && uri.AbsolutePath.EndsWith("/2/parent", StringComparison.Ordinal)
                    ? Response(JsonSerializer.Serialize(Body(1))) : new(HttpStatusCode.NotFound));
            }
            var page = int.Parse(uri.Query.Split("page=")[^1], CultureInfo.InvariantCulture);
            if (RepeatFirstPage) page = 1;
            var items = Enumerable.Range(1, Count)
                .Where(number => uri.Query.Contains("state=all", StringComparison.Ordinal) ||
                    uri.Query.Contains(number == 4 ? "state=closed" : "state=open", StringComparison.Ordinal))
                .Skip((page - 1) * 100).Take(100).Select(Body);
            return Task.FromResult(Response(JsonSerializer.Serialize(items)));
        }

        private Dictionary<string, object> Body(int number)
        {
            var body = new Dictionary<string, object>
            {
                ["id"] = 1000 + number, ["number"] = number, ["title"] = $"Title {number}",
                ["state"] = number == 4 ? "closed" : "open", ["html_url"] = $"https://github.com/owner/repo/issues/{number}"
            };
            if (AllPullRequests || number == 5) body["pull_request"] = new { };
            return body;
        }

        private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
