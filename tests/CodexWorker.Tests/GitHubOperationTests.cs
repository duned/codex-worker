using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitHubOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessCommentUsesExecutionMarkerToDeduplicateLostMutationResponse(bool loseResponse)
    {
        var id = Guid.NewGuid();
        var comments = new List<string>();
        var writes = 0;
        var client = new GitHubClient("owner/repo", (arguments, _) =>
        {
            var args = arguments.ToArray();
            if (args[0] == "api")
            {
                Assert.Contains("--paginate", args);
                Assert.Contains("--slurp", args);
                return Task.FromResult(new ProcessResult(0, System.Text.Json.JsonSerializer.Serialize(comments), ""));
            }
            writes++;
            comments.Add(args[Array.IndexOf(args, "--body") + 1]);
            return Task.FromResult(loseResponse && writes == 1
                ? new ProcessResult(1, "", "HTTP 500 Internal Server Error") : new ProcessResult(0, "", ""));
        });
        if (loseResponse)
            await Assert.ThrowsAsync<GitHubOperationException>(() => client.EnsureSuccessCommentAsync(17, id, "Success report", CancellationToken.None));
        else
            await client.EnsureSuccessCommentAsync(17, id, "Success report", CancellationToken.None);
        await client.EnsureSuccessCommentAsync(17, id, "Success report", CancellationToken.None);
        Assert.Single(comments);
        Assert.Contains($"<!-- codex-worker-success:{id:D} -->", comments[0], StringComparison.Ordinal);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("duplicate")]
    [InlineData("invalid-json")]
    public async Task AmbiguousSuccessCommentProofDoesNotAppendReport(string conflict)
    {
        var id = Guid.NewGuid();
        var body = $"Report\n\n<!-- codex-worker-success:{id:D} -->";
        var calls = 0;
        var client = new GitHubClient("owner/repo", (_, _) =>
        {
            calls++;
            var output = conflict switch
            {
                "changed" => System.Text.Json.JsonSerializer.Serialize(new[] { "operator edited " + body.Replace("Report", "Changed", StringComparison.Ordinal) }),
                "duplicate" => System.Text.Json.JsonSerializer.Serialize(new[] { body, body }),
                _ => "invalid-json"
            };
            return Task.FromResult(new ProcessResult(0, output, ""));
        });
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => client.EnsureSuccessCommentAsync(17, id, "Report", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RemovedConfirmedSuccessReportRequiresInspectionRatherThanRecreation()
    {
        var calls = 0;
        var client = new GitHubClient("owner/repo", (_, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(0, "[]", ""));
        });
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => client.EnsureSuccessCommentAsync(17, Guid.NewGuid(),
            "Report", CancellationToken.None, allowCreate: false));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompletionCanInspectClosedIssueWithoutMakingItEligibleForAssignedExecution()
    {
        var client = new GitHubClient("owner/repo", (_, _) => Task.FromResult(new ProcessResult(0,
            "{\"number\":17,\"title\":\"Task\",\"body\":\"Intent\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"state\":\"CLOSED\"}", "")));
        Assert.NotNull(await client.GetCompletionIssueAsync(17, CancellationToken.None));
        await Assert.ThrowsAsync<GitHubOperationException>(() => client.GetIssueAsync(17, CancellationToken.None));
    }

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
