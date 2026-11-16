using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task PreflightRunsBeforeQueueAndSuccessfulPreflightAllowsClaim()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Equal("preflight", h.Events[0]);
        Assert.Equal("find", h.Events[1]);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.Contains("Codex preflight...", h.Output.ToString());
        Assert.Contains("Codex preflight OK ·", h.Output.ToString());
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CancellationWhileSafelyIdleUsesConciseShutdownPresentation()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.ReturnIssueOnFirstQuery = false;

        await h.RunAsync();

        Assert.Contains("○ Waiting for work...", h.Output.ToString());
        Assert.Contains("■ Worker stopped.", h.Output.ToString());
        Assert.DoesNotContain("Infrastructure failure", h.Output.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"⚫ {WorkerIdentity.DisplayName} {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("INFRAESTRUCTURA", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CancellationDuringReadyIssueQueryIsGracefulAndDoesNotThrowInfrastructureFailure()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.CancelDuringQuery = true;

        var exception = await Record.ExceptionAsync(() => h.RunAsync());

        Assert.Null(exception);
        Assert.Contains("■ Worker stopped.", h.Output.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"⚫ {WorkerIdentity.DisplayName} {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("INFRAESTRUCTURA", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CancellationDuringIssueClaimRetainsInfrastructureClassification()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.CancelDuringClaim = true;

        var exception = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.RunAsync());

        Assert.Contains("may be uncertain", exception.Message);
        Assert.Contains("Infrastructure failure", h.ErrorOutput.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"🚨 {WorkerIdentity.DisplayName} {ApplicationVersion.Display} · INFRAESTRUCTURA", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains($"{WorkerIdentity.DisplayName} {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CompletingIssueEntersInteractiveIdleAndCancellationCleansSpinner()
    {
        using var h = new Harness(interactive: true);
        h.GitHub.CancelWhenEmpty = false;
        var workerTask = h.Worker.RunAsync(h.Cancellation.Token);

        await WaitForOutputAsync(h.Output, "Example task #17 · completed");
        await WaitForOutputAsync(h.Output, "Waiting for work... 00:00", after: "Example task #17 · completed");
        h.Cancellation.Cancel();
        await workerTask;

        var output = h.Output.ToString();
        var completed = output.IndexOf("Example task #17 · completed", StringComparison.Ordinal);
        var idle = output.IndexOf("Waiting for work...", completed, StringComparison.Ordinal);
        var stopped = output.IndexOf("■ Worker stopped.", StringComparison.Ordinal);
        Assert.True(completed >= 0 && idle > completed && stopped > idle, output);
        Assert.Contains("\n■ Worker stopped.", output[idle..]);
        Assert.DoesNotContain("\r\u001b[2K", output[idle..stopped]);
    }

    private static async Task WaitForOutputAsync(StringWriter output, string value, string? after = null)
    {
        for (var i = 0; i < 50; i++)
        {
            // WorkerConsole locks this writer while updating the spinner. Snapshot
            // under the same lock because StringWriter's StringBuilder is not thread-safe.
            string snapshot;
            lock (output) snapshot = output.ToString();
            var start = after is null ? 0 : snapshot.IndexOf(after, StringComparison.Ordinal);
            if (start >= 0 && snapshot.IndexOf(value, start, StringComparison.Ordinal) >= 0) return;
            await Task.Delay(100);
        }
        lock (output)
        {
            var snapshot = output.ToString();
            var start = after is null ? 0 : snapshot.IndexOf(after, StringComparison.Ordinal);
            Assert.True(start >= 0 && snapshot.IndexOf(value, start, StringComparison.Ordinal) >= 0, snapshot);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task FailedPreflightStopsBeforeIssueQueryOrClaim()
    {
        using var h = new Harness();
        h.Codex.PreflightException = new WorkerInfrastructureException("401 unauthorized");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.RunAsync(h.Cancellation.Token));

        Assert.Equal(0, h.GitHub.FindCalls);
        Assert.Empty(h.GitHub.Labels);
        Assert.Equal(0, h.Git.Started);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task RuntimeCodexInfrastructureFailureStopsWithoutMarkingIssueFailed()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Codex.InitialException = new WorkerInfrastructureException("service authentication failed");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.RunAsync(h.Cancellation.Token));

        Assert.Equal(3, h.GitHub.FindCalls);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Git.Cleanups);
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", execution.State);
        Assert.Contains("service authentication failed", execution.FailureReason);
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task SecondaryGitHubFailurePreservesPrimaryExecutionFailureAndRecordsUncertainty()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Codex.InitialException = new WorkerInfrastructureException("primary Codex execution failure");
        h.GitHub.InterruptionReportingFailure = new GitHubOperationException("issue edit", 17, true,
            GitHubFailureKind.TransientProvider, GitHubRemoteState.Uncertain,
            "GraphQL: Something went wrong while executing your query");

        var failure = await Assert.ThrowsAsync<GitHubOperationException>(() => h.ProcessOneAsync());

        Assert.True(failure.IsSecondaryReportingFailure);
        Assert.Contains("primary Codex execution failure", failure.PrimaryFailure);
        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.Contains("primary Codex execution failure", entry.FailureReason);
        Assert.Contains("GraphQL: Something went wrong", entry.ReportingFailure);
        Assert.Equal(GitHubOperationException.ReconciliationRequiredState, entry.RecoveryState);
        Assert.Equal(1, h.GitHub.Labels.Count(label => label == "working->blocked"));
        Assert.Empty(h.GitHub.Comments);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task UncertainResultMutationIsNotFollowedByACompensatingGitHubMutation()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.ResultReportingFailure = new GitHubOperationException("issue edit", 17, true,
            GitHubFailureKind.TransientProvider, GitHubRemoteState.Uncertain,
            "GraphQL: Something went wrong while executing your query");

        await Assert.ThrowsAsync<GitHubOperationException>(() => h.ProcessOneAsync());

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Completed", entry.State);
        Assert.Null(entry.FailureReason);
        Assert.Contains("GraphQL: Something went wrong", entry.ReportingFailure);
        Assert.Equal(GitHubOperationException.ReconciliationRequiredState, entry.RecoveryState);
        Assert.DoesNotContain("working->blocked", h.GitHub.Labels);
        Assert.Empty(h.GitHub.Comments);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task StructuredTaskFailureStillCleansAndReportsFailedIssue()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Implementation could not be completed", [], false, null);

        await h.RunAsync();

        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Validation.Calls);
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Failed", execution.State);
        Assert.Contains("Implementation could not be completed", execution.FailureReason);
        var executionId = execution.ExecutionId;
        Assert.Contains($"Reason · execution [{ExecutionFormatting.ShortId(executionId)}] · Codex reported incomplete task · Implementation could not be completed", h.ErrorOutput.ToString());
        Assert.Contains($"Execution `[{ExecutionFormatting.ShortId(executionId)}]` (`{executionId}`)", Assert.Single(h.GitHub.Comments));
        Assert.Contains("### Implementation attempt\n\nImplementation could not be completed\n\n### Failure\n\n**Reason:** Codex reported incomplete task.", Assert.Single(h.GitHub.Comments));
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task StructuredFailurePersistsRecoverableWorkspaceMetadata()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "2 changed path(s); 0 staged path(s). Workspace retained for recovery.");
            h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial implementation remains", [], false, null);

            await h.RunAsync();

            Assert.Equal(0, h.Git.Cleanups);
            Assert.Equal(0, h.Git.Integrations);
            Assert.Contains("### Recovery\n\n- **Execution:**", Assert.Single(h.GitHub.Comments));
            Assert.Contains("- **Workspace:** Preserved\n- **Retry/resume:** Available", Assert.Single(h.GitHub.Comments));
        }

        using var reopened = new ExecutionHistoryStore(database);
        var execution = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("Failed", execution.State);
        Assert.Equal("recoverable", execution.RecoveryState);
        Assert.Equal("base-sha", execution.RecoveryBaseCommit);
        Assert.Contains("2 changed path(s)", execution.RecoveryStatus);
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task BlockedExecutionPreservesUsefulWorkspaceForAnExplicitRetry()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "1 changed path(s); 0 staged path(s). Workspace retained for recovery.");
            h.Codex.InitialOutcome = new CodexOutcome("blocked", "Need a product decision", [], false, "Choose a policy.");

            var result = await h.ProcessOneAsync();

            Assert.Equal(IssueOutcomeKind.Blocked, result!.Kind);
            Assert.Equal(0, h.Git.Cleanups);
            var execution = Assert.Single(await history.ReadAllAsync());
            Assert.Equal("Blocked", execution.State);
            Assert.Equal("recoverable", execution.RecoveryState);
        }

        using var reopened = new ExecutionHistoryStore(database);
        Assert.Equal("Blocked", Assert.Single(await reopened.ReadAllAsync()).State);
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExternalPrerequisiteBlockerUsesBlockedLifecycleAndPreservesRecovery()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Git.Recovery = new GitRecoveryInfo("feature/issue-122", "base-sha", "2 changed path(s); workspace retained.");
            h.Codex.InitialOutcome = new CodexOutcome("blocked", "Restore could not complete.",
                ["dotnet restore could not reach api.nuget.org."], false,
                "The required NuGet feed api.nuget.org is unreachable.", "external_prerequisite");

            var result = await h.ProcessOneAsync();

            Assert.Equal(IssueOutcomeKind.Blocked, result!.Kind);
            Assert.Contains("ready->working", h.GitHub.Labels);
            Assert.Contains("working->blocked", h.GitHub.Labels);
            var comment = Assert.Single(h.GitHub.Comments);
            Assert.Contains("api.nuget.org is unreachable", comment);
            Assert.Contains("Retry/resume:** Available", comment);
            Assert.Contains("The required NuGet feed api.nuget.org is unreachable", h.Output.ToString());
            Assert.Equal(0, h.Git.Integrations);
            Assert.Equal(0, h.Validation.Calls);
            var execution = Assert.Single(await history.ReadAllAsync());
            Assert.Equal("Blocked", execution.State);
            Assert.Equal("recoverable", execution.RecoveryState);
            Assert.Contains("api.nuget.org is unreachable", execution.FailureReason);
        }
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("failed", "Failed")]
    [InlineData("blocked", "Blocked")]
    public async Task ResumedRetryKeepsFailedAttemptAndRunsFreshValidationBeforeIntegration(string outcome, string state)
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "2 changed path(s); 0 staged path(s). Workspace retained for recovery.");
        h.GitHub.IssueLabels = ["ready"];
        h.Codex.InitialOutcome = new CodexOutcome(outcome, "Partial implementation remains; integration conflict needs investigation", [], false, null);

        var failed = await h.ProcessOneAsync();
        Assert.Equal(state == "Failed" ? IssueOutcomeKind.Failed : IssueOutcomeKind.Blocked, failed!.Kind);
        var first = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(state, first.State);
        h.GitHub.ReadyIssueCount = 2;
        h.Codex.InitialOutcome = Success("Completed the full task");

        var completed = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, completed!.Kind);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        var entries = await history.ReadAllAsync();
        Assert.Equal(2, entries.Count);
        var retry = entries.Single(entry => entry.AttemptNumber == 2);
        Assert.NotEqual(first.ExecutionId, retry.ExecutionId);
        Assert.Equal(first.ExecutionId, retry.RetryOfExecutionId);
        Assert.True(retry.Resumed);
        Assert.Equal(state, entries.Single(entry => entry.ExecutionId == first.ExecutionId).State);
        Assert.Equal("Completed", retry.State);
        Assert.True(h.Git.LastResume);
        Assert.Contains($"↳ Attempt 2 · resume from [{first.ExecutionId.ToString("N")[..8]}]", h.Output.ToString());
        Assert.Contains($"(Attempt 2 · from [{first.ExecutionId.ToString("N")[..8]}])", h.Output.ToString());
        Assert.DoesNotContain($"execution {retry.ExecutionId.ToString("N")[..8]}", h.Output.ToString());
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShutdownInterruptsActiveExecutionsAndRestartRequiresRecoveryProof(int count)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using (var h = new Harness(history: history, gracefulShutdown: true))
        {
            h.GitHub.ReadyIssueCount = count;
            h.GitHub.ReturnDistinctIssues = true;
            h.GitHub.CancelWhenEmpty = false;
            h.Codex.BlockRuns = true;
            var tasks = new List<Task<IssueProcessingResult?>>();
            for (var index = 0; index < count; index++)
                tasks.Add((await h.Worker.ClaimNextAsync(h.Cancellation.Token))!);
            await h.Codex.BlockedRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            h.Cancellation.Cancel();
            await WorkerHost.AwaitShutdownExecutionsAsync(tasks);
            Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
            Assert.Equal(count, h.Git.Started);
            Assert.Equal(0, h.Git.Cleanups);
            Assert.DoesNotContain("infrastructure failure", h.Output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Execution interrupted by Worker shutdown", h.Output.ToString());
            Assert.All(await history.ReadAllAsync(), entry =>
            {
                Assert.Equal("Cancelled", entry.State);
                Assert.Equal("uncertain", entry.RecoveryState);
                Assert.Contains("Worker shutdown", entry.FailureReason);
            });
            Assert.Empty(h.GitHub.Comments);
        }
        // A ready label alone cannot prove that an interrupted attempt is safe to replay.
        // This repository fake supplies no ownership/remote proof; real verified retry and
        // continuation are covered by StandaloneInterruptedExecutionTests.
        using var restarted = new Harness(history: history, gracefulShutdown: true);
        restarted.GitHub.CompletionLabels = ["ready"];
        restarted.GitHub.IssueLabels = ["ready"];
        Assert.Null(await restarted.ProcessOneAsync());
        Assert.Equal(count, (await history.ReadAllAsync()).Count);
        Assert.All(await history.ReadAllAsync(), entry => Assert.Equal("Cancelled", entry.State));
        Assert.Empty(restarted.Codex.Issues);
        Assert.Equal(0, restarted.Git.Started);
        Assert.Contains(restarted.OperationalMessages, message => message.Contains("inspection-unsupported", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDuringValidationOrIntegrationPreservesPassedValidation(bool integration)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, gracefulShutdown: true);
        if (integration)
            h.Git.IntegrationException = new WorkerInfrastructureException("Git interrupted", new OperationCanceledException(h.Cancellation.Token));
        h.Validation.CancelOnCall = 1;
        h.Validation.OnRun = h.Cancellation.Cancel;
        if (integration)
        {
            // Cancel at the integration operation itself, after the boundary checks.
            h.Validation.OnRun = null;
            h.Git.BeforeIntegration = h.Cancellation.Cancel;
        }
        var task = (await h.Worker.ClaimNextAsync(h.Cancellation.Token))!;
        await WorkerHost.AwaitShutdownExecutionsAsync([task]);
        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Cancelled", entry.State);
        Assert.Equal("passed", entry.ValidationOutcome);
        Assert.Equal("uncertain", entry.RecoveryState);
        Assert.Equal(0, h.Git.Cleanups);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ShutdownDuringIssueMutationKeepsUncertainOwnershipForInspection()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, gracefulShutdown: true);
        h.GitHub.CancelDuringClaim = true;
        await Assert.ThrowsAsync<WorkerShutdownException>(() => h.Worker.ClaimNextAsync(h.Cancellation.Token));
        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.True(WorkerHost.IsShutdownInterruption(entry));
        Assert.Equal("uncertain", entry.RecoveryState);
        Assert.Empty(h.GitHub.Comments);
        Assert.Equal(0, h.Git.Started);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task UnexpectedCancellationIsNotAControlledShutdownInterruption()
    {
        var cancelled = Task.FromCanceled<IssueProcessingResult?>(new CancellationToken(true));
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => WorkerHost.AwaitShutdownExecutionsAsync([cancelled]));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task GenuineFailureDuringShutdownStillFailsDrain()
    {
        var failure = Task.FromException<IssueProcessingResult?>(new WorkerInfrastructureException("repository failed"));
        var cancelled = Task.FromCanceled<IssueProcessingResult?>(new CancellationToken(true));
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => WorkerHost.AwaitShutdownExecutionsAsync([cancelled, failure]));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task SecondaryGitHubFailureDuringRequestedShutdownDoesNotBecomeFatalExit()
    {
        var shutdown = Task.FromException<IssueProcessingResult?>(new WorkerShutdownException(CancellationToken.None,
            new OperationCanceledException("controlled shutdown")));
        var reporting = Task.FromException<IssueProcessingResult?>(new GitHubOperationException("issue edit", 151, true,
            GitHubFailureKind.TransientProvider, GitHubRemoteState.Uncertain, "transient provider failure"));

        await WorkerHost.AwaitShutdownExecutionsAsync([shutdown, reporting]);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task GitHubReportingFailureDoesNotCancelAnUnrelatedActiveExecution()
    {
        var releaseExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelatedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelated = RunUnrelatedAsync();
        var reporting = Task.FromException<IssueProcessingResult?>(new GitHubOperationException("issue edit", 151, true,
            GitHubFailureKind.TransientProvider, GitHubRemoteState.Uncertain, "transient provider failure"));
        var join = WorkerHost.AwaitShutdownExecutionsAsync([reporting, unrelated]);

        Assert.True(unrelatedStarted.Task.IsCompleted);
        Assert.False(unrelated.IsCanceled);
        releaseExecution.SetResult();
        await join;

        async Task<IssueProcessingResult?> RunUnrelatedAsync()
        {
            unrelatedStarted.SetResult();
            await releaseExecution.Task;
            return null;
        }
    }

    private static ValidationResult Failure(string command, int exitCode, string stderr) =>
        new(new ValidationFailure(1, command, exitCode, "useful stdout", stderr, false));
}
