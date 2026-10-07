using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitHubCancellationTests
{
    [Fact]
    public async Task CancelledReadyIssueQueryPropagatesApplicationCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new GitHubClient("owner/repo", (_, ct) =>
            Task.FromException<ProcessResult>(new OperationCanceledException(ct)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FindOldestReadyAsync("ready", cancellation.Token));
    }

    [Fact]
    public async Task CancelledIssueMutationRemainsInfrastructureFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new GitHubClient("owner/repo", (args, ct) =>
        {
            if (args.Take(2).SequenceEqual(new[] { "issue", "view" }))
                return Task.FromResult(new ProcessResult(0, "{\"state\":\"OPEN\",\"labels\":[{\"name\":\"ready\"}]}", ""));
            cancellation.Cancel();
            return Task.FromException<ProcessResult>(new OperationCanceledException(ct));
        });

        var failure = await Assert.ThrowsAsync<GitHubOperationException>(() =>
            client.ReplaceLabelAsync(6, "ready", "working", cancellation.Token));
        Assert.Equal(GitHubFailureKind.Cancellation, failure.FailureKind);
        Assert.Equal(GitHubRemoteState.Uncertain, failure.RemoteState);
        Assert.IsType<OperationCanceledException>(failure.InnerException);
    }
}
