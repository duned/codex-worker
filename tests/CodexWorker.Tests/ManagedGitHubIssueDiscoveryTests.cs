namespace CodexWorker.Tests;

using CodexServer;
using System.Text.Json;

public sealed class ManagedGitHubIssueDiscoveryTests
{
    [Fact]
    public async Task DiscoveryPagesUseConfiguredLabelIssueConnectionAndContinuation()
    {
        var queries = new List<string>();
        var service = new ServerGitHubReadService((arguments, _) =>
        {
            Assert.Equal(new[] { "api", "graphql", "-f" }, arguments.Take(3));
            var query = arguments[3];
            queries.Add(query);
            var later = query.Contains("after:\"later\"", StringComparison.Ordinal);
            return Task.FromResult(new GitHubReadCommandResult(0,
                Page(later ? [9] : [4, 2, 2], !later, later ? null : "later"), ""));
        });
        var first = await service.ListDiscoveryIssueNumbersAsync(Project(), new(3), CancellationToken.None);
        Assert.Equal(new[] { 2, 4 }, first.Numbers);
        Assert.Equal("later", first.NextCursor);
        var next = await service.ListDiscoveryIssueNumbersAsync(Project(), new(3, first.NextCursor), CancellationToken.None);
        Assert.Equal(new[] { 9 }, next.Numbers);
        Assert.Null(next.NextCursor);
        Assert.All(queries, query =>
        {
            Assert.Contains("issues(first:3,states:[OPEN],orderBy:{field:CREATED_AT,direction:ASC}", query, StringComparison.Ordinal);
            Assert.Contains("labels:[\"custom-ready\"]", query, StringComparison.Ordinal);
            Assert.Contains("owner:\"team\",name:\"project\"", query, StringComparison.Ordinal);
            Assert.DoesNotContain("search", query, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FullTerminalPageIsCompleteAndUnconfiguredLabelAddsNoFilter()
    {
        var service = new ServerGitHubReadService((arguments, _) =>
        {
            Assert.DoesNotContain("labels:", arguments[3], StringComparison.Ordinal);
            return Task.FromResult(new GitHubReadCommandResult(0, Page([1, 2], false, null), ""));
        });
        var result = await service.ListDiscoveryIssueNumbersAsync(Project() with { IssueReadyLabel = null }, new(2), CancellationToken.None);
        Assert.Equal(2, result.Numbers.Count);
        Assert.Null(result.NextCursor);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(1, "")]
    [InlineData(1, "\n")]
    public async Task InvalidBoundsPerformNoReads(int limit, string? after)
    {
        var service = new ServerGitHubReadService((_, _) => throw new InvalidOperationException("Must not read."));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(limit, after), CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"data\":{\"repository\":null}}")]
    [InlineData("{\"data\":{\"repository\":{\"issues\":{\"nodes\":[{\"number\":1,\"url\":\"https://github.com/other/project/issues/1\"}],\"pageInfo\":{\"hasNextPage\":false}}}}}")]
    [InlineData("{\"data\":{\"repository\":{\"issues\":{\"nodes\":[{\"number\":1,\"url\":\"https://github.com/team/project/pull/1\"}],\"pageInfo\":{\"hasNextPage\":false}}}}}")]
    [InlineData("{\"data\":{\"repository\":{\"issues\":{\"nodes\":[],\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"later\"}}}}}")]
    [InlineData("not json")]
    public async Task MalformedOrWrongScopePagesAreUnavailable(string output)
    {
        var service = new ServerGitHubReadService((_, _) => Task.FromResult(new GitHubReadCommandResult(0, output, "")));
        var error = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(), CancellationToken.None));
        Assert.Equal("invalid-response", error.Code);
    }

    [Fact]
    public async Task NonAdvancingCursorAndOversizedPageAreRejected()
    {
        var service = new ServerGitHubReadService((_, _) => Task.FromResult(new GitHubReadCommandResult(0, Page([1, 2], true, "later"), "")));
        await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(2, "later"), CancellationToken.None));
        await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(1), CancellationToken.None));
    }

    [Theory]
    [InlineData("Bad credentials (HTTP 401)", "authentication-failed")]
    [InlineData("To get started with GitHub CLI, please run: gh auth login", "authentication-failed")]
    [InlineData("API rate limit exceeded (HTTP 403)", "rate-limited")]
    [InlineData("HTTP 429", "rate-limited")]
    [InlineData("HTTP 500", "read-failed")]
    public async Task ProviderFailuresHaveDistinctCodesAndRedactedDiagnostics(string stderr, string code)
    {
        var service = new ServerGitHubReadService((_, _) => Task.FromResult(new GitHubReadCommandResult(1, "", stderr + " private-token")));
        var error = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(), CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private-token", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RATE_LIMITED", "rate-limited")]
    [InlineData("UNAUTHORIZED", "authentication-failed")]
    [InlineData("FORBIDDEN", "read-failed")]
    [InlineData("UNKNOWN", "invalid-response")]
    public async Task GraphQlFailuresAreDistinctAndDoNotEchoProviderMessages(string type, string code)
    {
        var output = JsonSerializer.Serialize(new { errors = new[] { new { type, message = "private-token" } } });
        var service = new ServerGitHubReadService((_, _) => Task.FromResult(new GitHubReadCommandResult(0, output, "")));
        var error = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(), CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private-token", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsPropagatedAndUnavailableTransportIsNotIneligibility()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ServerGitHubReadService((_, token) => Task.FromCanceled<GitHubReadCommandResult>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(), cancellation.Token));
        service = new ServerGitHubReadService((_, _) => throw new IOException("private-token"));
        var error = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.ListDiscoveryIssueNumbersAsync(Project(), new(), CancellationToken.None));
        Assert.Equal("read-unavailable", error.Code);
        Assert.DoesNotContain("private-token", error.Message, StringComparison.Ordinal);
    }

    private static CentralProject Project() => new("project", "Project", "team/project", "main", "", [], 1,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, IssueReadyLabel: "custom-ready");

    private static string Page(int[] numbers, bool hasNextPage, string? endCursor) => JsonSerializer.Serialize(new
    {
        data = new { repository = new { issues = new
        {
            nodes = numbers.Select(number => new { number, url = $"https://github.com/team/project/issues/{number}" }),
            pageInfo = new { hasNextPage, endCursor }
        } } }
    });
}
