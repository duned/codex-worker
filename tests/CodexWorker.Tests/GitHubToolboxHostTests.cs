using System.Net;
using WorkExecutionToolbox;

namespace CodexWorker.Tests;

public sealed class GitHubToolboxHostTests
{
    [Fact]
    public async Task HostUsesGhCredentialOnlyForScopedRequest()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var calls = 0;
        var provider = GitHubToolboxHost.CreateProvider(http, token =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult(new ProcessResult(0, "fake-test-credential\n", ""));
        });
        Assert.Null(await provider.GetIssueAsync(new(new("team/project"), 42)));
        Assert.Equal(1, calls);
        Assert.Equal("fake-test-credential", handler.Credential);
        Assert.Equal("https://api.github.com/repos/team/project/issues/42", handler.Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedGhAuthenticationNeverExposesOutputOrExceptions(bool throws)
    {
        using var http = new HttpClient(new Handler());
        var provider = GitHubToolboxHost.CreateProvider(http, _ => throws
            ? throw new IOException("fake-test-credential")
            : Task.FromResult(new ProcessResult(1, "fake-test-credential", "fake-test-credential")));
        var error = await Assert.ThrowsAsync<GitHubIssueException>(() => provider.GetIssueAsync(new(new("team/project"), 42)));
        Assert.Equal(GitHubIssueFailure.Authorization, error.Failure);
        Assert.DoesNotContain("fake-test-credential", error.ToString());
    }

    [Fact]
    public async Task HostCredentialCancellationPropagates()
    {
        using var http = new HttpClient(new Handler());
        using var cancellation = new CancellationTokenSource();
        var provider = GitHubToolboxHost.CreateProvider(http, async token =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new ProcessResult(0, "fake-test-credential", "");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetIssueAsync(new(new("team/project"), 42), cancellation.Token));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Credential { get; private set; }
        public string? Url { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credential = request.Headers.Authorization?.Parameter;
            Url = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
