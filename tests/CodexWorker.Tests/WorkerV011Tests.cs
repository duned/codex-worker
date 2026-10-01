using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed class WorkerV011Tests
{
    [Fact]
    public async Task RejectedIntegrationRecoveryIsJournaledAndReported()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(telegramEnabled: true, history: history);
        h.GitHub.ReturnRecoveryIssueOnFirstQuery = true;
        h.GitHub.ReadyIssueCount = 0;

        await h.ProcessOneAsync();

        Assert.Contains(h.OperationalMessages, message => message.Contains("integration recovery rejected", StringComparison.Ordinal) &&
            message.Contains("no recoverable integration-conflict execution history", StringComparison.Ordinal));
        Assert.Contains(h.TelegramMessages, message => message.Contains("RECUPERACIÓN DE INTEGRACIÓN RECHAZADA", StringComparison.Ordinal));
        Assert.Contains("codex-integration-recovery->codex-integration-conflict", h.GitHub.Labels);
    }

    [Fact]
    public async Task RejectedIntegrationRecoveryReleasesIssueReservationForExplicitReady()
    {
        using var h = new Harness();
        h.GitHub.ReturnRecoveryIssueOnFirstQuery = true;
        h.GitHub.ReadyIssueCount = 0;
        h.GitHub.CancelWhenEmpty = false;

        Assert.Null(await h.Worker.ClaimNextAsync(h.Cancellation.Token));
        h.GitHub.ReadyIssueCount = 1;
        h.GitHub.IssueLabels = ["ready"];
        var claim = await h.Worker.ClaimNextAsync(h.Cancellation.Token);

        Assert.NotNull(claim);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await claim!)!.Kind);
        Assert.Equal(1, h.Git.Started);
    }

    [Fact]
    public async Task SchedulerClaimMessagesCanBeCapturedWithoutUsingProcessConsole()
    {
        using var h = new Harness();

        Assert.NotNull(await h.ProcessOneAsync());

        Assert.Contains(h.OperationalMessages, message => message.StartsWith("Scheduler · #17 claimed · execution [", StringComparison.Ordinal));
        Assert.DoesNotContain("Scheduler · #17 claimed", h.Output.ToString());
    }

    [Fact]
    public async Task ConcurrentPollingDoesNotDispatchAnIssueWhileItsExecutionIsActive()
    {
        using var historyDatabase = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(historyDatabase.Path);
        using var h = new Harness(history: history);
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "1 changed path(s); 0 staged path(s). Workspace retained for recovery.");
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial work", [], false, null);
        Assert.Equal(IssueOutcomeKind.Failed, (await h.ProcessOneAsync())!.Kind);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.Codex.InitialOutcome = new CodexOutcome("success", "Resumed work", [], false, null);
        h.GitHub.ReadyIssueCount = 3;
        h.GitHub.CancelWhenEmpty = false;
        h.Codex.BlockRuns = true;

        var firstClaim = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(firstClaim);
        await h.Codex.BlockedRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicateClaim = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.Null(duplicateClaim);
        Assert.Equal(2, h.Git.Started);
        Assert.Equal(2, h.GitHub.Labels.Count(label => label == "ready->working"));
        Assert.Single(await history.ReadAllAsync(), entry => entry.AttemptNumber == 2);

        h.Codex.ReleaseRuns.TrySetResult();
        Assert.NotNull(await firstClaim);
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
    }

    [Fact]
    public async Task ActiveOldestIssueDoesNotPreventDispatchOfAnotherReadyIssue()
    {
        using var h = new Harness();
        h.GitHub.ReadyIssueCount = 3;
        h.GitHub.ReturnDistinctIssues = true;
        h.Codex.BlockRuns = true;

        var first = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(first);
        await h.Codex.BlockedRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(second);
        Assert.Equal(2, h.Git.Started);
        Assert.Equal(2, h.GitHub.Labels.Count(label => label == "ready->working"));

        h.Codex.ReleaseRuns.TrySetResult();
        await first!;
        var replacement = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(replacement);
        Assert.Equal(3, h.Git.Started);
        await Task.WhenAll(second!, replacement!);
    }

    [Fact]
    public async Task IntegrationConflictHasDedicatedStateAndSchedulerCanContinue()
    {
        using var historyDatabase = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(historyDatabase.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReadyIssueCount = 2;
        h.Git.IntegrationFailure = new GitIntegrationConflictException("rebase conflict was safely aborted");

        var conflicted = await h.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.IntegrationConflict, conflicted!.Kind);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal("IntegrationConflict", Assert.Single(await history.ReadAllAsync()).State);

        h.Git.IntegrationFailure = null;
        var succeeded = await h.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.Succeeded, succeeded!.Kind);
        Assert.Equal(2, h.Git.Integrations);
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
    }

    [Theory]
    [InlineData("IntegrationConflict", null)]
    [InlineData("IntegrationConflict", "integration-conflict")]
    [InlineData("IntegrationConflict", "integration-conflict-unavailable")]
    [InlineData("Failed", "integration-conflict")]
    public async Task ExplicitReadyLabelStartsFreshExecutionDespitePriorIntegrationConflict(string state, string? recoveryState)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        var config = h.Worker.Configuration.GitHub;
        config.ReadyLabel = "team-ready";
        config.IntegrationRecoveryLabel = "team-recover";
        config.IntegrationConflictLabel = "team-conflict";
        h.GitHub.ReadyLabel = config.ReadyLabel;
        h.GitHub.RecoveryLabel = config.IntegrationRecoveryLabel;
        h.GitHub.IssueLabels = [config.ReadyLabel, config.IntegrationRecoveryLabel, config.IntegrationConflictLabel];
        h.GitHub.ReturnRecoveryIssueOnFirstQuery = true;

        var oldId = Guid.NewGuid();
        var oldEntry = new ExecutionHistoryEntry(oldId, "Test Project", "owner/repo", 17, "Example task",
            "feature/17-example-task", "main", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
            state, 1000, "Prior implementation", "passed", 0, [], "old-commit", "main",
            "completed/17", null, recoveryState, null, "preserved");
        await history.CreateAsync(oldEntry);

        var claim = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(claim);
        var result = await claim!;

        Assert.Equal(IssueOutcomeKind.Succeeded, result!.Kind);
        var entries = await history.ReadAllAsync();
        Assert.Equal(2, entries.Count);
        var preserved = entries.Single(entry => entry.ExecutionId == oldId);
        Assert.Equal(state, preserved.State);
        Assert.Equal("old-commit", preserved.CommitSha);
        Assert.Null(preserved.RecoveryBaseCommit);
        Assert.Equal(oldEntry.RecoveryState, preserved.RecoveryState);
        Assert.Equal(oldEntry.StartedAtUtc, preserved.StartedAtUtc);
        var fresh = entries.Single(entry => entry.ExecutionId != oldId);
        Assert.NotEqual(oldId, fresh.ExecutionId);
        Assert.Null(fresh.RetryOfExecutionId);
        Assert.False(fresh.Resumed);
        Assert.Equal(2, fresh.AttemptNumber);
        Assert.Equal(1, h.Git.Started);
        Assert.False(h.Git.LastResume);
        Assert.Null(h.Git.LastRetryOf);
        Assert.NotNull(h.Codex.InitialDirectory);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.RecoveryStarted);
        Assert.Contains("team-recover", h.GitHub.RemovedLabels);
        Assert.Contains("team-conflict", h.GitHub.RemovedLabels);
        Assert.Contains(h.OperationalMessages, message => message.Contains("explicit ready label takes precedence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyConflictWithoutRecoveryOrValidationMetadataStartsFreshFromExplicitReady()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.GitHub.IssueLabels = ["ready"];
        // The #79 conflict was persisted as Failed, with no validation, commit, or
        // recovery metadata. Only the structured Git conflict message survived.
        var oldId = Guid.Parse("0cc4c541-a260-44bf-a4f0-9cd2f40dd35f");
        var oldEntry = new ExecutionHistoryEntry(oldId, "Test Project", "owner/repo", 17, "Example task",
            "feature/17-example-task-retry-2", "main", DateTimeOffset.UtcNow.AddMinutes(-15), DateTimeOffset.UtcNow.AddMinutes(-1),
            "Failed", 847848, "Completed implementation", null, 0, [], null, null, null,
            "Integration conflict for Issue #17; the rebase was aborted and its worktree was preserved. Rebasing (1/1)\rerror: could not apply implementation commit",
            AttemptNumber: 2);
        await history.CreateAsync(oldEntry);

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, result!.Kind);
        var entries = await history.ReadAllAsync();
        Assert.Equal(2, entries.Count);
        var preserved = entries.Single(entry => entry.ExecutionId == oldId);
        Assert.Equal(oldEntry with { Repairs = preserved.Repairs }, preserved);
        var fresh = entries.Single(entry => entry.ExecutionId != oldId);
        Assert.Equal("Completed", fresh.State);
        Assert.Equal(3, fresh.AttemptNumber);
        Assert.Null(fresh.RetryOfExecutionId);
        Assert.False(fresh.Resumed);
        Assert.Null(h.Git.LastRetryOf);
        Assert.False(h.Git.LastResume);
        Assert.NotNull(h.Codex.InitialDirectory);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.RecoveryStarted);
        Assert.DoesNotContain(h.GitHub.Comments, comment => comment.Contains("Preparation rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrecoverableResumeIsRejectedAndAnotherIssueRunsWithoutStoppingPolling()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(telegramEnabled: true, history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.ReturnDistinctIssues = true;
        var oldId = Guid.NewGuid();
        await history.CreateAsync(new ExecutionHistoryEntry(oldId, "Test Project", "owner/repo", 17, "Example task",
            "feature/17-example-task", "main", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
            "Failed", 1000, "Partial work", null, 0, [], null, null, null, "failed"));

        await h.RunAsync();

        Assert.Contains("ready->blocked", h.GitHub.Labels);
        Assert.Contains("working->done", h.GitHub.Labels);
        Assert.Equal(1, h.Git.Started);
        var entries = await history.ReadAllAsync();
        Assert.Equal("Failed", entries.Single(entry => entry.ExecutionId == oldId).State);
        var rejected = entries.Single(entry => entry.IssueNumber == 17 && entry.ExecutionId != oldId);
        Assert.Equal("InfrastructureFailure", rejected.State);
        Assert.Equal("Completed", entries.Single(entry => entry.IssueNumber == 18).State);
        Assert.Contains(h.OperationalMessages, message => message.Contains(rejected.ExecutionId.ToString(), StringComparison.Ordinal) &&
            message.Contains(oldId.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain("Infrastructure failure", h.ErrorOutput.ToString());
        Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
        var notification = Assert.Single(h.TelegramMessages, message => message.Contains("Preparación rechazada:", StringComparison.Ordinal));
        Assert.Contains("TAREA BLOQUEADA", notification);
        Assert.Contains(ExecutionFormatting.Display(rejected.ExecutionId), notification);
        Assert.Contains("has no safe recoverable state", notification);
        Assert.Contains("TEST PROJECT", notification);
        Assert.Contains("https://github.com/owner/repo/issues/17", notification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafePreparationRejectionAllowsNextReadyIssueToExecute(bool missingRecovery)
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.ReturnDistinctIssues = true;
        h.Git.FailIssueNumber = 17;
        h.Git.StartFailure = missingRecovery
            ? new IssuePreparationRejectedException("Previous execution workspace is missing.")
            : new ProjectCheckoutDirtyException("Checkout is dirty; refusing preparation.");

        var rejected = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(rejected);
        Assert.Null(await rejected!);
        await h.RunAsync();

        Assert.Equal(2, h.Git.Started);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains("working->done", h.GitHub.Labels);
        Assert.Single(h.GitHub.Comments, comment => comment.Contains("### Preparation rejected", StringComparison.Ordinal));
        Assert.Single(h.TelegramMessages, message => message.Contains("Preparación rechazada:", StringComparison.Ordinal));
        Assert.DoesNotContain("Infrastructure failure", h.ErrorOutput.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationNotificationFailureRemainsIsolatedAndRedactsReason(bool throws)
    {
        using var h = new Harness(telegramEnabled: true);
        h.TelegramFailure = throws;
        h.Worker.Configuration.Environment.Variables = new Dictionary<string, string> { ["TEST_SECRET"] = "private-test-value" };
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.ReturnDistinctIssues = true;
        h.Git.FailIssueNumber = 17;
        h.Git.StartFailure = new ProjectCheckoutDirtyException("Checkout is dirty: private-test-value");

        var rejected = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(rejected);
        Assert.Null(await rejected!);
        await h.RunAsync();

        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains("working->done", h.GitHub.Labels);
        var message = Assert.Single(h.TelegramMessages, message => message.Contains("Preparación rechazada:", StringComparison.Ordinal));
        Assert.DoesNotContain("private-test-value", message);
        Assert.Contains("[redacted]", message);
        Assert.Contains("Telegram notification failed", h.Output.ToString());
        Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
        Assert.Single(h.TelegramMessages, message => message.Contains("Preparación rechazada:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitRecoveryLabelStillUsesIntegrationRecoveryPath()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReturnRecoveryIssueOnFirstQuery = true;
        h.GitHub.IssueLabels = ["codex-integration-recovery", "codex-integration-conflict"];
        var sourceId = Guid.NewGuid();
        await history.CreateAsync(new ExecutionHistoryEntry(sourceId, "Test Project", "owner/repo", 17, "Example task",
            "feature/17-example-task", "main", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
            "IntegrationConflict", 1000, "Prior implementation", "passed", 0, [], "old-commit", "main",
            "completed/17", null, "integration-conflict", "base-commit", "preserved"));

        var claim = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(claim);
        await claim!;

        Assert.Equal(1, h.Git.RecoveryStarted);
        Assert.Equal(0, h.Git.Started);
        var recovery = (await history.ReadAllAsync()).Single(entry => entry.ExecutionId != sourceId);
        Assert.Equal(sourceId, recovery.RetryOfExecutionId);
    }

    [Fact]
    public async Task DirtyCheckoutPreparationIsBlockedWithoutResumeMetadata()
    {
        using var historyDatabase = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(historyDatabase.Path);
        using var h = new Harness(history: history);
        h.Git.StartFailure = new ProjectCheckoutDirtyException("Project checkout is dirty before Issue #17; refusing to proceed.");

        Assert.Null(await h.ProcessOneAsync());

        Assert.Equal(new[] { "ready->working", "working->blocked" }, h.GitHub.Labels);
        Assert.Null(h.Codex.InitialDirectory);
        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.Null(entry.RecoveryState);
        Assert.Contains(h.OperationalMessages, message => message.Contains("preparation rejected", StringComparison.Ordinal));
        Assert.Contains("### Recovery\n\n", Assert.Single(h.GitHub.Comments));
    }

    [Fact]
    public async Task DirtyCheckoutFailureDoesNotCancelAnIndependentRunningExecution()
    {
        using var h = new Harness();
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.ReturnDistinctIssues = true;
        h.Git.FailIssueNumber = 18;
        h.Git.StartFailure = new ProjectCheckoutDirtyException("Project checkout is dirty before Issue #18; refusing to proceed.");
        h.Codex.BlockRuns = true;

        var independent = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        await h.Codex.BlockedRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dirty = await h.Worker.ClaimNextAsync(h.Cancellation.Token);

        Assert.NotNull(dirty);
        Assert.Null(await dirty!);
        Assert.False(h.Cancellation.IsCancellationRequested);
        Assert.True(independent is { IsCompleted: false });
        Assert.Equal("working->blocked", h.GitHub.Labels[^1]);

        h.Codex.ReleaseRuns.TrySetResult();
        Assert.NotNull(await independent!);
    }

    [Fact]
    public async Task ExplicitNewAttemptAfterOlderCompletionProceedsToIntegration()
    {
        using var historyDatabase = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(historyDatabase.Path);
        var oldId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        await history.CreateAsync(new ExecutionHistoryEntry(oldId, "Test Project", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", started, started.AddMinutes(1), "Completed", 60_000, "done",
            "passed", 0, [], null, null, null, null, AttemptNumber: 1));
        using var h = new Harness(history: history);

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, result!.Kind);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Contains("working->done", h.GitHub.Labels);
        Assert.Single(h.GitHub.Comments);
        var entries = await history.ReadAllAsync();
        Assert.Equal("Completed", entries.Single(entry => entry.ExecutionId == oldId).State);
        var attempt = entries.Single(entry => entry.ExecutionId != oldId);
        Assert.Equal("Completed", attempt.State);
        Assert.Equal("passed", attempt.ValidationOutcome);
        Assert.Equal(2, attempt.AttemptNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptionImmediatelyAfterValidationIsPersistedReportedAndStopsQueue(bool cancel)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.CancelWhenEmpty = false;
        h.Git.VerifyAfterValidation = async ct =>
        {
            Assert.Equal("passed", Assert.Single(await history.ReadAllAsync()).ValidationOutcome);
            // The claimed Issue still occupies worker capacity during the handoff.
            Assert.Null(await h.Worker.ClaimNextAsync(ct));
            Assert.Equal(1, h.Git.Started);
            if (cancel)
            {
                h.Cancellation.Cancel();
                throw new OperationCanceledException(ct);
            }
            throw new WorkerInfrastructureException("verification interrupted after validation");
        };

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.RunAsync());

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.Equal("passed", entry.ValidationOutcome);
        Assert.Equal("uncertain", entry.RecoveryState);
        Assert.NotNull(entry.CompletedAtUtc);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Contains(entry.ExecutionId.ToString(), Assert.Single(h.GitHub.Comments));
        Assert.Contains(h.OperationalMessages, message => message.Contains(entry.ExecutionId.ToString(), StringComparison.Ordinal));
        Assert.Equal(1, h.Git.Started);
    }

    [Fact]
    public async Task ValidatedExecutionKeepsActiveClaimUntilTerminalReportingFinishes()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReadyIssueCount = 3;
        h.GitHub.CancelWhenEmpty = false;
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.GitHub.CommentAction = () => releaseReport.Task;

        var execution = await h.Worker.ClaimNextAsync(h.Cancellation.Token);

        Assert.NotNull(execution);
        Assert.False(execution.IsCompleted);
        Assert.Equal(1, h.Git.Integrations);
        var active = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Reporting", active.State);
        Assert.Equal("passed", active.ValidationOutcome);
        Assert.Null(active.CompletedAtUtc);
        Assert.Null(await h.Worker.ClaimNextAsync(h.Cancellation.Token));
        Assert.Equal(1, h.Git.Started);

        releaseReport.TrySetResult();
        Assert.Equal(IssueOutcomeKind.Succeeded, (await execution)!.Kind);
        Assert.Equal("Completed", Assert.Single(await history.ReadAllAsync()).State);
        h.GitHub.CommentAction = null;
        h.GitHub.ReadyIssueCount++;
        var next = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(next);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await next)!.Kind);
        Assert.Equal(2, h.Git.Started);
    }

    [Fact]
    public async Task LaterAttemptSupersedesValidatedExecutionWithExplicitReport()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var laterId = Guid.NewGuid();
        h.Git.VerifyAfterValidation = async _ =>
        {
            var current = Assert.Single(await history.ReadAllAsync());
            await history.CreateAsync(current with
            {
                ExecutionId = laterId, AttemptNumber = 2, State = "Completed",
                CompletedAtUtc = DateTimeOffset.UtcNow
            });
        };

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Superseded, result!.Kind);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains("### Execution superseded", Assert.Single(h.GitHub.Comments));
        var entry = (await history.ReadAllAsync()).Single(row => row.ExecutionId != laterId);
        Assert.Equal("Superseded", entry.State);
        Assert.Equal("passed", entry.ValidationOutcome);
        Assert.Equal("superseded", entry.RecoveryState);
    }

    [Fact]
    public async Task AssignedUnrecoverableResumeReturnsCompletedRejectionTaskInsteadOfFailingClaim()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        var now = DateTimeOffset.UtcNow;
        await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "Test Project", "owner/repo", 17, "Example task",
            "feature/17-example-task", "main", now.AddMinutes(-5), now,
            "Failed", 1000, "Partial work", null, 0, [], null, null, null, "failed"));
        var assignment = new WorkerAssignmentContract("assignment-rejected", "server-rejected",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-rejected", "worker-id", 1, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);

        Assert.NotNull(execution);
        Assert.Null(await execution!);
        Assert.Equal(0, h.Git.Started);
        Assert.Contains("ready->blocked", h.GitHub.Labels);
        var rejected = (await history.ReadAllAsync()).Single(entry => entry.ServerExecutionId == "server-rejected");
        Assert.Equal("InfrastructureFailure", rejected.State);
        Assert.Equal("assignment-rejected", rejected.AssignmentId);
    }

    [Fact]
    public async Task ServerAssignmentCreatesLinkedExecutionAndUsesNormalRunnerAndGitHubLifecycle()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("assignment-456", "server-request-123",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-request-123", "worker-id", 1, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        var result = await execution;

        Assert.NotNull(result);
        Assert.Equal(1, h.Git.Started);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.Contains("working->done", h.GitHub.Labels);
        var persisted = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("server-request-123", persisted.ServerExecutionId);
        Assert.Equal("assignment-456", persisted.AssignmentId);
        Assert.Equal("Completed", persisted.State);
    }

    [Fact]
    public async Task AssignmentCancellationBeforeIntegrationStopsAtTheSharedRepositoryBoundary()
    {
        using var h = new Harness();
        h.Validation.CancelOnCall = 1;
        h.Validation.OnRun = h.Cancellation.Cancel;
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("assignment-fence", "server-fence",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-fence", "worker-id", 4, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => execution!);
        Assert.Equal(0, h.Git.Integrations);
    }

    [Fact]
    public async Task QueuedServerWorkRunsThroughWorkerPipelineAndReportsTerminalState()
    {
        using var directory = new TempHistoryDatabase();
        var store = new SqliteRegistryStore(Path.Combine(Path.GetDirectoryName(directory.Path)!, "server.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Test Project", "owner/repo", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        CodexServer.WorkerCapability[] capabilities = [new("tool", "git"),
            .. WorkerAuthenticationRequirements.ForRepository(project.Repository).Select(requirement =>
                new CodexServer.WorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope)),
            new("agent-provider", "codex")];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "test worker", "test", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "test", "running", 0, 1, capabilities, []));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id,
            new WorkReference("github-issue", "17")));
        var assignmentResponse = await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }));
        var assignment = Assert.IsType<WorkAssignment>(assignmentResponse.Assignment);

        using var history = new ExecutionHistoryStore(Path.Combine(Path.GetDirectoryName(directory.Path)!, "worker.db"));
        using var h = new Harness(history: history);
        var workerAssignment = new WorkerAssignmentContract(assignment.AssignmentId, assignment.ServerExecutionId,
            new ServerProjectContract(project.Id, project.Name, project.Repository, project.DefaultBranch,
                project.Description, project.Requirements.Select(requirement => new ServerProjectRequirementContract(
                    requirement.Type, requirement.Name, requirement.Version)).ToArray(), project.Revision,
                project.CreatedAtUtc, project.UpdatedAtUtc),
            new ServerWorkReferenceContract(assignment.Work.Type, assignment.Work.Id, assignment.Work.Url), workerId,
            assignment.Metadata, new ServerExecutionLeaseContract(assignment.Lease!.ExecutionId, assignment.Lease.WorkerId,
                assignment.Lease.Generation, assignment.Lease.AcquiredAtUtc, assignment.Lease.ExpiresAtUtc, assignment.Lease.State));

        var execution = await h.Worker.ClaimAssignedAsync(workerAssignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        Assert.NotNull(await execution);
        var workerEntry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(queued.Id, workerEntry.ServerExecutionId);
        Assert.Equal(assignment.AssignmentId, workerEntry.AssignmentId);
        Assert.Equal("Completed", workerEntry.State);

        var reported = await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(workerId,
            assignment.AssignmentId, workerEntry.ExecutionId.ToString(), "Completed", StartedAtUtc: workerEntry.StartedAtUtc,
            CompletedAtUtc: workerEntry.CompletedAtUtc, DurationMilliseconds: workerEntry.DurationMilliseconds,
            ValidationResult: workerEntry.ValidationOutcome, IntegrationResult: "passed", Recoverable: false,
            Summary: workerEntry.ImplementationSummary, Generation: assignment.Lease.Generation));
        Assert.NotNull(reported);
        Assert.Equal("Completed", reported.State);
        Assert.Equal(workerEntry.ExecutionId.ToString(), reported.WorkerExecutionId);
        Assert.Equal(workerId, reported.AssignedWorkerId);
        Assert.False(reported.Recoverable);
    }

    [Fact]
    public async Task SchedulerDispatchesExplicitExecutionAndReceivesSafeTerminalOutcome()
    {
        using var h = new Harness();
        h.Codex.InitialOutcome = new CodexOutcome("blocked", "Needs a decision", [], true, "Which API?");

        var result = await h.ProcessOneAsync();

        Assert.NotNull(result);
        Assert.Equal(IssueOutcomeKind.Blocked, result.Kind);
        Assert.NotNull(result.Report.ExecutionId);
        Assert.Equal(result.Report.ExecutionId, h.Git.LastExecutionId);
        Assert.Equal(1, h.Git.Started);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.Contains("working->blocked", h.GitHub.Labels);
    }

    [Fact]
    public async Task InitialValidationSuccessDoesNotInvokeRepair()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Empty(h.Codex.RepairAttempts);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal("preflight", h.Events[0]);
        Assert.Equal("find", h.Events[1]);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("## " + IssueFormatting.Display(h.GitHub.Issue), StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Implementation", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Validation\n\nValidation passed successfully.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulIssuePersistsWorkerLifecycleAndIntegrationFacts()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Validation.Results.Enqueue(Failure("authoritative-check", 1, "failed once"));
            h.Validation.Results.Enqueue(ValidationResult.Success);
            h.Codex.Repairs.Enqueue(Success("fixed check"));
            await h.RunAsync();
        }

        using var reopened = new ExecutionHistoryStore(database);
        var execution = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("Completed", execution.State);
        Assert.Equal("implemented", execution.ImplementationSummary);
        Assert.Equal("passed", execution.ValidationOutcome);
        Assert.Equal(1, execution.RepairCount);
        Assert.True(Assert.Single(execution.Repairs).PassedAfterRepair);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", execution.CommitSha);
        Assert.Equal("main", execution.IntegrationBranch);
        Assert.Equal("completed/17", execution.CompletedBranch);
        File.Delete(database);
    }

    [Fact]
    public async Task CodexAndValidationUseTheExecutionWorktreeDirectory()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Equal(h.Git.ExecutionDirectory, h.Codex.InitialDirectory);
        Assert.Equal(h.Git.ExecutionDirectory, h.Validation.LastDirectory);
        Assert.NotEqual(h.Worker.Configuration.Project.Directory, h.Codex.InitialDirectory);
    }

    [Fact]
    public async Task InitialValidationFailureThenSuccessfulRepairIsRevalidatedAndIntegrated()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("dotnet test", 1, "restore error"));
        h.Validation.Results.Enqueue(ValidationResult.Success);
        h.Codex.Repairs.Enqueue(Success("fixed restore issue"));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(2, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.Contains("working->done", h.GitHub.Labels);
        var comment = Assert.Single(h.GitHub.Comments);
        Assert.StartsWith("## Example task #17\n\n", comment);
        Assert.Contains("implemented", comment);
        Assert.Contains("fixed restore issue", comment);
        Assert.Contains("Initial validation failed: `dotnet test`.", comment);
        Assert.Contains("Validation passed after repair 1/2.", comment);
        Assert.DoesNotContain("stdout", comment);
    }

    [Fact]
    public async Task SupportsMultipleBoundedRepairAttempts()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("check 1", 1, "first"));
        h.Validation.Results.Enqueue(Failure("check 1", 1, "second"));
        h.Validation.Results.Enqueue(ValidationResult.Success);
        h.Codex.Repairs.Enqueue(Success("repair one"));
        h.Codex.Repairs.Enqueue(Success("repair two"));

        await h.RunAsync();

        Assert.Equal(new[] { 1, 2 }, h.Codex.RepairAttempts);
        Assert.Equal(3, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        var comment = Assert.Single(h.GitHub.Comments);
        Assert.True(comment.IndexOf("repair one", StringComparison.Ordinal) < comment.IndexOf("repair two", StringComparison.Ordinal));
        Assert.Contains("implemented", comment);
    }

    [Fact]
    public async Task ExhaustedRepairAttemptsFailTaskWithoutIntegratingEvenWhenRepairsReportSuccess()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "initial failure"));
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "repair one still fails"));
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "repair two still fails"));
        h.Codex.Repairs.Enqueue(Success("repair one says ready"));
        h.Codex.Repairs.Enqueue(Success("repair two says ready"));

        await h.RunAsync();

        Assert.Equal(new[] { 1, 2 }, h.Codex.RepairAttempts);
        Assert.Equal(3, h.Validation.Calls);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("authoritative check", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Implementation attempt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BlockedRepairStopsLoopAndUsesBlockedWorkflow()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Validation.Results.Enqueue(Failure("check", 1, "failure"));
        h.Codex.Repairs.Enqueue(new CodexOutcome("blocked", "Need a decision", [], true, "Which API?"));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Which API?", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Work performed", StringComparison.Ordinal));
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Blocked", execution.State);
        Assert.Equal(1, execution.RepairCount);
        Assert.Contains("Which API?", execution.FailureReason);
        File.Delete(database);
    }

    [Fact]
    public async Task FailedRepairStopsLoopAndUsesFailedWorkflow()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("check", 1, "failure"));
        h.Codex.Repairs.Enqueue(new CodexOutcome("failed", "Could not repair", [], false, null));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
    }

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

    [Fact]
    public async Task CancellationWhileSafelyIdleUsesConciseShutdownPresentation()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.ReturnIssueOnFirstQuery = false;

        await h.RunAsync();

        Assert.Contains("○ Waiting for work...", h.Output.ToString());
        Assert.Contains("■ Worker stopped.", h.Output.ToString());
        Assert.DoesNotContain("Infrastructure failure", h.Output.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"⚫ CW {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("INFRAESTRUCTURA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringReadyIssueQueryIsGracefulAndDoesNotThrowInfrastructureFailure()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.CancelDuringQuery = true;

        var exception = await Record.ExceptionAsync(() => h.RunAsync());

        Assert.Null(exception);
        Assert.Contains("■ Worker stopped.", h.Output.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"⚫ CW {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("INFRAESTRUCTURA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringIssueClaimRetainsInfrastructureClassification()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.CancelDuringClaim = true;

        var exception = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.RunAsync());

        Assert.Contains("may be uncertain", exception.Message);
        Assert.Contains("Infrastructure failure", h.ErrorOutput.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"🚨 CW {ApplicationVersion.Display} · INFRAESTRUCTURA", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains($"CW {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletingIssueEntersInteractiveIdleAndCancellationCleansSpinner()
    {
        using var h = new Harness(interactive: true);
        h.GitHub.CancelWhenEmpty = false;
        var workerTask = h.Worker.RunAsync(h.Cancellation.Token);

        await WaitForOutputAsync(h.Output, "Example task #17 · completed");
        await WaitForOutputAsync(h.Output, "Waiting for work... 00:00");
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

    private static async Task WaitForOutputAsync(StringWriter output, string value)
    {
        for (var i = 0; i < 50; i++)
        {
            // WorkerConsole locks this writer while updating the spinner. Snapshot
            // under the same lock because StringWriter's StringBuilder is not thread-safe.
            string snapshot;
            lock (output) snapshot = output.ToString();
            if (snapshot.Contains(value, StringComparison.Ordinal)) return;
            await Task.Delay(100);
        }
        lock (output) Assert.Contains(value, output.ToString());
    }

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

    [Fact]
    public async Task RuntimeCodexInfrastructureFailureStopsWithoutMarkingIssueFailed()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Codex.InitialException = new WorkerInfrastructureException("service authentication failed");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.RunAsync(h.Cancellation.Token));

        Assert.Equal(2, h.GitHub.FindCalls);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Git.Cleanups);
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", execution.State);
        Assert.Contains("service authentication failed", execution.FailureReason);
        File.Delete(database);
    }

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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShutdownInterruptsActiveExecutionsAndRestartCanClaimFreshAttempts(int count)
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
        // An operator reconciles the preserved state and explicitly applies ready. A new
        // host has fresh capacity and starts another attempt without changing the old row.
        using var restarted = new Harness(history: history, gracefulShutdown: true);
        var result = await restarted.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.Succeeded, result!.Kind);
        Assert.Equal(2, result.Report.AttemptNumber);
        Assert.Equal(count, (await history.ReadAllAsync()).Count(entry => entry.State == "Cancelled"));
    }

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

    [Fact]
    public async Task UnexpectedCancellationIsNotAControlledShutdownInterruption()
    {
        var cancelled = Task.FromCanceled<IssueProcessingResult?>(new CancellationToken(true));
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => WorkerHost.AwaitShutdownExecutionsAsync([cancelled]));
    }

    [Fact]
    public async Task GenuineFailureDuringShutdownStillFailsDrain()
    {
        var failure = Task.FromException<IssueProcessingResult?>(new WorkerInfrastructureException("repository failed"));
        var cancelled = Task.FromCanceled<IssueProcessingResult?>(new CancellationToken(true));
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => WorkerHost.AwaitShutdownExecutionsAsync([cancelled, failure]));
    }

    private static ValidationResult Failure(string command, int exitCode, string stderr) =>
        new(new ValidationFailure(1, command, exitCode, "useful stdout", stderr, false));

    private static CodexOutcome Success(string summary) => new("success", summary, [], false, null);

    private sealed class Harness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"codex-worker-test-{Guid.NewGuid():N}");
        public CancellationTokenSource Cancellation { get; } = new();
        public List<string> Events { get; } = [];
        public FakeGitHub GitHub { get; }
        public FakeGit Git { get; } = new();
        public FakeCodex Codex { get; }
        public FakeValidation Validation { get; } = new();
        public Worker Worker { get; }
        public StringWriter Output { get; } = new();
        public StringWriter ErrorOutput { get; } = new();
        public List<string> OperationalMessages { get; } = [];
        private readonly TelegramNotifier _telegram;
        private readonly HttpClient? _telegramClient;
        private readonly StubTelegramHandler? _telegramHandler;

        public Harness(bool interactive = false, bool telegramEnabled = false, ExecutionHistoryStore? history = null, bool gracefulShutdown = false)
        {
            Directory.CreateDirectory(_directory);
            var instructions = Path.Combine(_directory, "AGENTS.md");
            File.WriteAllText(instructions, "test instructions");
            var config = new WorkerConfiguration
            {
                Project = new ProjectSettings { Name = "Test Project", Repository = "owner/repo", Directory = _directory },
                Git = new GitSettings(),
                GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" },
                Codex = new CodexSettings { InstructionsFile = instructions },
                Validation = new ValidationSettings { Commands = ["authoritative-check"], MaxFixAttempts = 2 },
                Worker = new WorkerSettings()
            };
            GitHub = new FakeGitHub(Events, Cancellation);
            Codex = new FakeCodex(Events);
            var output = new WorkerConsole(Output, interactive, ErrorOutput);
            if (telegramEnabled)
            {
                _telegramHandler = new StubTelegramHandler();
                _telegramClient = new HttpClient(_telegramHandler);
                _telegram = new TelegramNotifier(true, "fake-token", "fake-chat", _telegramClient, output);
            }
            else _telegram = new TelegramNotifier(false, output);
            Worker = new Worker(config, GitHub, Git, Codex, Validation, _telegram, output, history,
                operationalLog: OperationalMessages.Add, shutdownToken: gracefulShutdown ? Cancellation.Token : default);
        }

        public IEnumerable<string> TelegramMessages => _telegramHandler?.Messages ?? [];
        public bool? TelegramFailure { set => _telegramHandler!.Failure = value; }

        public async Task RunAsync() => await Worker.RunAsync(Cancellation.Token);
        public Task<IssueProcessingResult?> ProcessOneAsync() => Worker.ProcessOneAsync(Cancellation.Token);

        public void Dispose()
        {
            Cancellation.Dispose();
            _telegram.Dispose();
            _telegramClient?.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }

        private sealed class StubTelegramHandler : HttpMessageHandler
        {
            public List<string> Messages { get; } = [];
            public bool? Failure { get; set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(body);
                Messages.Add(document.RootElement.GetProperty("text").GetString()!);
                if (Failure == true) throw new HttpRequestException("Simulated Telegram delivery failure");
                return new HttpResponseMessage(Failure == false ? System.Net.HttpStatusCode.BadGateway : System.Net.HttpStatusCode.OK);
            }
        }
    }

    private sealed class TempHistoryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-worker-assignment-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(_directory, "history.db");
        public TempHistoryDatabase() => Directory.CreateDirectory(_directory);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private sealed class FakeGitHub(List<string> events, CancellationTokenSource cancellation) : IGitHubClient
    {
        private int _returned;
        public int ReadyIssueCount { get; set; } = 1;
        public bool ReturnIssueOnFirstQuery { get; set; } = true;
        public bool CancelWhenEmpty { get; set; } = true;
        public bool CancelDuringQuery { get; set; }
        public bool CancelDuringClaim { get; set; }
        public bool ReturnDistinctIssues { get; set; }
        public bool ReturnRecoveryIssueOnFirstQuery { get; set; }
        public string ReadyLabel { get; set; } = "ready";
        public string RecoveryLabel { get; set; } = "codex-integration-recovery";
        public IReadOnlyList<string> IssueLabels { get; set; } = [];
        public List<string> RemovedLabels { get; } = [];
        public GitHubIssue Issue { get; } = new(17, "Example task", "Implement this request", DateTimeOffset.UtcNow);
        public int FindCalls { get; private set; }
        public List<string> Labels { get; } = [];
        public List<string> Comments { get; } = [];
        public Func<Task>? CommentAction { get; set; }

        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken)
            => FindOldestReadyAsync(label, new HashSet<int>(), cancellationToken);

        public Task<GitHubIssue?> FindOldestReadyAsync(string label, IReadOnlySet<int> excludedIssueNumbers,
            CancellationToken cancellationToken)
        {
            FindCalls++;
            events.Add("find");
            if (label == RecoveryLabel && ReturnRecoveryIssueOnFirstQuery)
            {
                ReturnRecoveryIssueOnFirstQuery = false;
                return Task.FromResult<GitHubIssue?>(Issue with { Labels = IssueLabels });
            }
            if (label != ReadyLabel) return Task.FromResult<GitHubIssue?>(null);
            if (CancelDuringQuery)
            {
                cancellation.Cancel();
                return Task.FromException<GitHubIssue?>(new OperationCanceledException(cancellation.Token));
            }
            if (ReturnIssueOnFirstQuery)
            {
                while (_returned < ReadyIssueCount)
                {
                    var number = Issue.Number + (ReturnDistinctIssues ? _returned : 0);
                    _returned++;
                    if (!excludedIssueNumbers.Contains(number))
                        return Task.FromResult<GitHubIssue?>(Issue with { Number = number, Labels = IssueLabels });
                }
            }
            // End the polling loop without waiting; no real GitHub service is involved.
            if (CancelWhenEmpty) cancellation.Cancel();
            return Task.FromResult<GitHubIssue?>(null);
        }

        public Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubIssue?>(issueNumber == Issue.Number ? Issue : null);

        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        {
            Labels.Add($"{remove}->{add}");
            if (CancelDuringClaim && remove == "ready")
            {
                cancellation.Cancel();
                return Task.FromException(new WorkerInfrastructureException("GitHub operation was cancelled; remote Issue state may be uncertain.", new OperationCanceledException(ct)));
            }
            return Task.CompletedTask;
        }
        public Task RemoveLabelAsync(int issueNumber, string label, CancellationToken ct)
        { RemovedLabels.Add(label); return Task.CompletedTask; }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct)
        { Comments.Add(comment); return CommentAction?.Invoke() ?? Task.CompletedTask; }
        public Task CloseAsync(int issueNumber, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGit : IGitRepository
    {
        public string ExecutionDirectory { get; } = Path.Combine(Path.GetTempPath(), "execution-worktree");
        public int Started { get; private set; }
        public int Cleanups { get; private set; }
        public int Integrations { get; private set; }
        public int RecoveryStarted { get; private set; }
        public GitRecoveryInfo? Recovery { get; set; }
        public Guid? LastExecutionId { get; private set; }
        public bool LastResume { get; private set; }
        public ExecutionHistoryEntry? LastRetryOf { get; private set; }
        public int LastAttemptNumber { get; private set; }
        public GitIntegrationConflictException? IntegrationFailure { get; set; }
        public Exception? IntegrationException { get; set; }
        public Action? BeforeIntegration { get; set; }
        public WorkerInfrastructureException? StartFailure { get; set; }
        public int? FailIssueNumber { get; set; }
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, CancellationToken ct) { Started++; LastExecutionId = executionId; return Task.CompletedTask; }
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resume, int attemptNumber, CancellationToken ct)
        { Started++; LastExecutionId = executionId; LastResume = resume; LastRetryOf = retryOf; LastAttemptNumber = attemptNumber;
            var failure = FailIssueNumber is null || issue.Number == FailIssueNumber ? StartFailure : null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure); }
        public Task StartIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
        { RecoveryStarted++; return Task.CompletedTask; }
        public Task<string?> ValidateIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        private int _verifications;
        public Func<CancellationToken, Task>? VerifyAfterValidation { get; set; }
        public Task VerifyCodexStateAsync(CancellationToken ct) =>
            ++_verifications == 2 && VerifyAfterValidation is not null ? VerifyAfterValidation(ct) : Task.CompletedTask;
        public Task DiscardUncommittedIssueChangesAsync(CancellationToken ct) { Cleanups++; return Task.CompletedTask; }
        public Task<GitRecoveryInfo?> PreserveFailedIssueChangesAsync(CancellationToken ct)
        {
            if (Recovery is null) Cleanups++;
            return Task.FromResult(Recovery);
        }
        public Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
            Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct)
        {
            Integrations++;
            BeforeIntegration?.Invoke();
            if (IntegrationException is not null) return Task.FromException<GitIntegrationResult>(IntegrationException);
            if (IntegrationFailure is not null) return Task.FromException<GitIntegrationResult>(IntegrationFailure);
            return Task.FromResult(new GitIntegrationResult(true,
            "Committed as `0123456789ab`. Merged into `main`. Preserved on origin as `completed/17`.",
            "0123456789abcdef0123456789abcdef01234567", "main", "completed/17"));
        }
    }

    private sealed class FakeCodex(List<string> events) : ICodexExecutor
    {
        public WorkerInfrastructureException? PreflightException { get; set; }
        public Exception? InitialException { get; set; }
        public CodexOutcome InitialOutcome { get; set; } = Success("implemented");
        public Queue<CodexOutcome> Repairs { get; } = new();
        public List<int> RepairAttempts { get; } = [];
        public List<ValidationFailure> RepairFailures { get; } = [];
        public bool BlockRuns { get; set; }
        public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRuns { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreflightAsync(CancellationToken ct)
        {
            events.Add("preflight");
            return PreflightException is null ? Task.CompletedTask : Task.FromException(PreflightException);
        }
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct) =>
            RunCoreAsync(projectDirectory, ct);
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct) => RunCoreAsync(projectDirectory, ct);
        private async Task<CodexOutcome> RunCoreAsync(string projectDirectory, CancellationToken ct)
        {
            InitialDirectory = projectDirectory;
            RunStarted.TrySetResult();
            if (BlockRuns)
            {
                BlockedRunStarted.TrySetResult();
                await ReleaseRuns.Task.WaitAsync(ct);
            }
            if (InitialException is not null) throw InitialException;
            return InitialOutcome;
        }
        public string? InitialDirectory { get; private set; }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct)
        {
            RepairAttempts.Add(attempt);
            RepairFailures.Add(failure);
            return Task.FromResult(Repairs.Dequeue());
        }
    }

    private sealed class FakeValidation : IValidationRunner
    {
        public Queue<ValidationResult> Results { get; } = new();
        public int Calls { get; private set; }
        public string? LastDirectory { get; private set; }
        public int? CancelOnCall { get; set; }
        public Action? OnRun { get; set; }
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            Calls++;
            LastDirectory = directory;
            if (Calls == CancelOnCall) OnRun?.Invoke();
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : ValidationResult.Success);
        }
    }
}
