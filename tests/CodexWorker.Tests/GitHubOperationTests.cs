using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitHubOperationTests
{
    [Fact]
    public async Task TransientGraphQlMutationIsClassifiedUncertainAndNeverReplayed()
    {
        var calls = 0;
        var client = new GitHubClient("owner/repo", (_, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(1, "", "GraphQL: Something went wrong while executing your query"));
        });

        var failure = await Assert.ThrowsAsync<GitHubOperationException>(() =>
            client.ReplaceLabelAsync(151, "working", "blocked", CancellationToken.None));

        Assert.Equal(GitHubFailureKind.TransientProvider, failure.FailureKind);
        Assert.Equal(GitHubRemoteState.Uncertain, failure.RemoteState);
        Assert.Equal(151, failure.IssueNumber);
        Assert.True(failure.IsMutation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TransientReadRetriesOnceAndReturnsVerifiedState()
    {
        var calls = 0;
        var client = new GitHubClient("owner/repo", (_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new ProcessResult(1, "", "GraphQL: Something went wrong while executing your query")
                : new ProcessResult(0, "{\"state\":\"OPEN\",\"labels\":[{\"name\":\"ready\"}]}", ""));
        });

        var state = await client.ReadIssueStateAsync(151, CancellationToken.None);

        Assert.True(state.IsOpen);
        Assert.Contains("ready", state.Labels);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("HTTP 403: Resource not accessible by integration", GitHubFailureKind.AuthenticationOrAuthorization, GitHubRemoteState.NotChanged)]
    [InlineData("HTTP 422: Validation Failed", GitHubFailureKind.DeterministicRequest, GitHubRemoteState.NotChanged)]
    [InlineData("HTTP 403: API rate limit exceeded", GitHubFailureKind.TransientProvider, GitHubRemoteState.Uncertain)]
    public async Task MutationFailuresAreClassifiedConservatively(string error, GitHubFailureKind kind, GitHubRemoteState remoteState)
    {
        var client = new GitHubClient("owner/repo", (_, _) => Task.FromResult(new ProcessResult(1, "", error)));

        var failure = await Assert.ThrowsAsync<GitHubOperationException>(() =>
            client.CloseAsync(15, CancellationToken.None));

        Assert.Equal(kind, failure.FailureKind);
        Assert.Equal(remoteState, failure.RemoteState);
    }

    [Fact]
    public void RestartReconciliationQuarantinesOnlyAnOpenIssueThatRemainsReady()
    {
        var readyIssue = new GitHubIssueState(true, ["ready", "working"]);
        var claimedIssue = new GitHubIssueState(true, ["working"]);
        var closedIssue = new GitHubIssueState(false, ["ready"]);
        var configuration = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "Test", Repository = "owner/repo", Directory = Path.GetTempPath() }
        };
        var otherConfiguration = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "Other", Repository = "owner/other", Directory = Path.GetTempPath() }
        };
        var registry = new ProjectRuntimeRegistry([("project.yml", configuration), ("other.yml", otherConfiguration)]);

        Assert.True(WorkerHost.RequiresManualGitHubReconciliation(readyIssue, "ready"));
        registry.MarkUnavailable("Test", "GitHub reconciliation required after an uncertain mutation.");
        Assert.False(registry.TryReserve("Test", configuration));
        Assert.True(registry.TryReserve("Other", otherConfiguration));
        registry.Release("Other");
        Assert.False(WorkerHost.RequiresManualGitHubReconciliation(claimedIssue, "ready"));
        Assert.False(WorkerHost.RequiresManualGitHubReconciliation(closedIssue, "ready"));
    }

    [Fact]
    public async Task DependencyReadFailureIsReadOnlyAndDoesNotMutateEligibility()
    {
        var calls = new List<string[]>();
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            var command = args.ToArray();
            calls.Add(command);
            return Task.FromResult(command.Length > 1 && command[0] == "issue" && command[1] == "list"
                ? new ProcessResult(0, "[{\"number\":9,\"title\":\"task\",\"body\":\"\",\"createdAt\":\"2025-01-01T00:00:00Z\",\"labels\":[]}]", "")
                : new ProcessResult(1, "", "GraphQL: Something went wrong"));
        });

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            client.FindOldestReadyAsync("ready", CancellationToken.None));

        Assert.NotNull(GitHubOperationException.Find(failure));
        Assert.Equal(GitHubRemoteState.NotApplicable, GitHubOperationException.Find(failure)!.RemoteState);
        Assert.Equal(9, GitHubOperationException.Find(failure)!.IssueNumber);
        Assert.Equal(2, calls.Count(command => command.Length > 1 && command[0] == "api"));
        Assert.DoesNotContain(calls.SelectMany(command => command), arg => arg is "--add-label" or "--remove-label" or "comment");
    }
}
