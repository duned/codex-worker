using System.Net;
using System.Text;
using System.Text.Json;

namespace WorkExecutionToolbox.Tests;

public sealed class GitHubIssueProviderTests
{
    private const string Secret = "fake-test-credential";

    [Theory]
    [InlineData("open", IssueState.Open)]
    [InlineData("closed", IssueState.Closed)]
    public async Task ResolvesHumanNumberInExplicitRepository(string state, IssueState expected)
    {
        using var handler = new FakeHandler((request, token) =>
        {
            Assert.Equal("https://api.github.com/repos/team/project/issues/42", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Secret, request.Headers.Authorization?.Parameter);
            Assert.True(token.CanBeCanceled);
            return Task.FromResult(Response(Body(state: state)));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/ambient/") };
        var provider = new GitHubIssueProvider(http, _ => Task.FromResult(Secret));
        using var cancellation = new CancellationTokenSource();
        var summary = await provider.GetIssueAsync(Issue(), cancellation.Token);
        Assert.NotNull(summary);
        Assert.Equal("team/project", summary.Issue.Repository.Repository);
        Assert.Equal(42, summary.Issue.Number);
        Assert.Equal("Example", summary.Title);
        Assert.Equal(expected, summary.State);
        Assert.Equal("https://github.com/team/project/issues/42", summary.Url?.AbsoluteUri);
        var json = JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("DatabaseId", json);
        Assert.DoesNotContain(Secret, json);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task MissingIssuesAndPullRequestsReturnNull()
    {
        using var missingHttp = Client(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await Provider(missingHttp).GetIssueAsync(Issue()));
        using var pullHttp = Client(Response(Body(pullRequest: true)));
        Assert.Null(await Provider(pullHttp).GetIssueAsync(Issue()));
    }

    [Theory]
    [InlineData(401, false, GitHubIssueFailure.Authorization)]
    [InlineData(403, false, GitHubIssueFailure.Authorization)]
    [InlineData(403, true, GitHubIssueFailure.RateLimited)]
    [InlineData(429, false, GitHubIssueFailure.RateLimited)]
    [InlineData(500, false, GitHubIssueFailure.Provider)]
    public async Task HttpFailuresAreTypedAndDoNotExposeBodies(int status, bool exhausted, GitHubIssueFailure expected)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret) };
        if (exhausted) response.Headers.Add("X-RateLimit-Remaining", "0");
        using var http = Client(response);
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetIssueAsync(Issue()));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("{\"token\":\"fake-test-credential\"}")]
    [InlineData("not json fake-test-credential")]
    [InlineData("[]")]
    public async Task MalformedResponsesAreRedacted(string body)
    {
        using var http = Client(Response(body));
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetIssueAsync(Issue()));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task MismatchedRepositoryNumberAndInvalidIdentityAreRejected()
    {
        foreach (var body in new[] { Body(number: 43), Body(repository: "other/project"), Body(id: 0), Body(state: "unknown") })
        {
            using var http = Client(Response(body));
            var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetIssueAsync(Issue()));
            Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
        }
    }

    [Theory]
    [InlineData("project")]
    [InlineData("team/project/extra")]
    [InlineData("team/../project")]
    [InlineData("team/project?token=value")]
    [InlineData("team/project name")]
    public async Task InvalidRepositoryRejectedBeforeCredentialsOrHttp(string repository)
    {
        using var http = new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP")));
        var provider = new GitHubIssueProvider(http, _ => throw new InvalidOperationException("Unexpected credentials"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetIssueAsync(new(new(repository), 1)));
    }

    [Fact]
    public async Task CredentialAndTransportFailuresCannotExposeSecrets()
    {
        using var http = new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException(Secret)));
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetIssueAsync(Issue()));
        Assert.Equal(GitHubIssueFailure.Transport, error.Failure);
        Assert.DoesNotContain(Secret, error.ToString());
        var provider = new GitHubIssueProvider(http, _ => throw new InvalidOperationException(Secret));
        error = await Assert.ThrowsAsync<GitHubIssueException>(() => provider.GetIssueAsync(Issue()));
        Assert.Equal(GitHubIssueFailure.Authorization, error.Failure);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task OversizedResponsesAreRejected()
    {
        using var http = Client(Response(new string('x', 1_048_577)));
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => Provider(http).GetIssueAsync(Issue()));
        Assert.Equal(GitHubIssueFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid token")]
    [InlineData("invalid\ntoken")]
    public async Task InvalidCredentialsAreRejectedBeforeHttp(string credential)
    {
        using var http = new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP")));
        var provider = new GitHubIssueProvider(http, _ => Task.FromResult(credential));
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => provider.GetIssueAsync(Issue()));
        Assert.Equal(GitHubIssueFailure.Authorization, error.Failure);
    }

    [Fact]
    public async Task CancellationBeforeCredentialsAndDuringHttpRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new FakeHandler(async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            return Response(Body());
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(http).GetIssueAsync(Issue(), cancellation.Token));
        var provider = new GitHubIssueProvider(http, _ => throw new InvalidOperationException("Unexpected credentials"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetIssueAsync(Issue(), cancellation.Token));
    }

    [Fact]
    public void RemoteDiscoveryRequiresOneUnambiguousRepository()
    {
        var repository = GitHubRepositoryContext.Discover(["git@github.com:team/project.git", "https://github.com/team/project.git"]);
        Assert.Equal("team/project", repository?.Repository);
        Assert.Equal("team/project", GitHubRepositoryContext.Discover(["ssh://git@github.com/team/project.git"])?.Repository);
        Assert.Null(GitHubRepositoryContext.Discover([]));
        Assert.Null(GitHubRepositoryContext.Discover(["https://github.com/team/one", "https://github.com/team/two"]));
        Assert.Null(GitHubRepositoryContext.Discover(["https://github.com/team/one", "https://example.com/team/one"]));
        Assert.Null(GitHubRepositoryContext.Discover(["https://user:password@github.com/team/one"]));
    }

    private static IssueReference Issue() => new(new("team/project"), 42);
    private static GitHubIssueProvider Provider(HttpClient http) => new(http, _ => Task.FromResult(Secret));
    private static HttpClient Client(HttpResponseMessage response) => new(new FakeHandler((_, _) => Task.FromResult(response)));
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Body(int number = 42, string repository = "team/project", long id = 123456, string state = "open", bool pullRequest = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id, ["number"] = number, ["title"] = "Example", ["state"] = state,
            ["html_url"] = $"https://github.com/{repository}/issues/{number}"
        }.Concat(pullRequest ? new Dictionary<string, object> { ["pull_request"] = new { } } : []).ToDictionary());

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
