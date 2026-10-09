using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed class WorkerV011Tests
{
    [Theory]
    [InlineData("success", IssueOutcomeKind.Succeeded)]
    [InlineData("failed", IssueOutcomeKind.Failed)]
    public async Task QuotaLookupErrorsDoNotChangeExecutionOutcome(string status, IssueOutcomeKind expected)
    {
        using var h = new Harness();
        h.Codex.QuotaReader = new ThrowingQuotaReader();
        h.Codex.InitialOutcome = new(status, "task outcome", [], false, null);
        Assert.Equal(expected, (await h.ProcessOneAsync())?.Kind);
        Assert.Contains(h.OperationalMessages, message => message.Contains("Codex quota start", StringComparison.Ordinal));
        Assert.Contains(h.OperationalMessages, message => message.Contains("Codex quota end", StringComparison.Ordinal));
        Assert.DoesNotContain(h.OperationalMessages, message => message.Contains("private quota error", StringComparison.Ordinal));
    }

    private sealed class ThrowingQuotaReader : ICodexQuotaReader
    {
        public Task<CodexQuotaObservation> ReadAsync(CancellationToken ct) =>
            Task.FromException<CodexQuotaObservation>(new IOException("private quota error"));
    }

    [Fact]
    public async Task CodexFailureDuringRequestedShutdownDoesNotBecomeWorkerFailure()
    {
        await WorkerHost.AwaitShutdownExecutionsAsync([
            Task.FromException<IssueProcessingResult?>(new CodexExecutionInfrastructureException("Codex usage limit", "Execution preserved") { Recoverable = true })]);
    }

    [Theory]
    [InlineData("snapshot", "snapshot is missing")]
    [InlineData("intent", "Issue intent is missing")]
    [InlineData("configuration", "configuration changed")]
    [InlineData("owner", "ownership history is missing")]
    public async Task IncompleteInterruptedRecoveryIsParkedWithSpecificReasonInsteadOfReimplemented(string missing, string reason)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.GitHub.CancelWhenEmpty = false;
        var id = Guid.NewGuid();
        var snapshot = new CodexInterruptionRecovery(missing == "owner" ? Guid.NewGuid() : id, 1, 0,
            missing == "configuration" ? "changed" : CodexInterruptionRecovery.Fingerprint(h.Worker.Configuration));
        await history.CreateAsync(new ExecutionHistoryEntry(id, "Test Project", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            "InfrastructureFailure", 1, null, null, 0, [], null, null, null, "usage limit", "codex-interrupted", "base-sha",
            OriginalIssueBody: missing == "intent" ? null : "Original intent", EffectiveEffort: "medium",
            CodexRecovery: missing == "snapshot" ? null : snapshot));
        Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(0, h.Git.Started);
        Assert.Empty(h.GitHub.Labels);
        Assert.Empty(h.TelegramMessages);
        var preserved = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(id, preserved.ExecutionId);
        Assert.Equal("codex-recovery-inspection-required", preserved.RecoveryState);
        Assert.Contains(reason, h.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedCodexRecoveryRestoresStartVisibilityAndPreservesLineage(bool? telegramFailure)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var clock = new RecoveryClock();
        using var h = new Harness(history: history, timeProvider: clock, telegramEnabled: true);
        h.Git.Snapshot = new("feature/example-task-17", "base-sha", "1 changed path; preserved");
        h.Codex.CliModel = "original-model";
        h.Codex.InitialException = new CodexExecutionInfrastructureException("Codex usage limit", "usage limit reached") { Recoverable = true };
        await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => h.ProcessOneAsync());
        var original = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("codex-interrupted", original.RecoveryState);
        Assert.NotNull(original.CodexRecovery);
        Assert.Equal(0, original.CodexRecovery.ResumeCount);
        Assert.Equal("base-sha", original.RecoveryBaseCommit);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.DoesNotContain("working->blocked", h.GitHub.Labels);
        Assert.Contains("automatically", Assert.Single(h.GitHub.Comments), StringComparison.Ordinal);
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA INICIADA", StringComparison.Ordinal));
        h.GitHub.CancelWhenEmpty = false;
        Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("TAREA RECUPERADA", StringComparison.Ordinal));
        Assert.Single(h.GitHub.Labels, label => label == "ready->working");
        clock.Advance(TimeSpan.FromMinutes(5));
        h.TelegramFailure = telegramFailure;
        h.Codex.InitialException = null;
        h.GitHub.Issue = h.GitHub.Issue with { Body = "Changed issue intent" };
        var resume = await h.Worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(resume);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await resume)?.Kind);
        Assert.Equal(1, h.Git.Started);
        Assert.Equal(1, h.Git.CodexResumes);
        Assert.Equal("Implement this request", h.Codex.Issues[1].Body);
        Assert.Equal("original-model", h.Codex.Profiles[1].Model);
        var completed = (await history.ReadAllAsync()).Single(entry => entry.State == "Completed");
        Assert.Equal(original.ExecutionId, completed.RetryOfExecutionId);
        Assert.Equal(original.ExecutionId, completed.CodexRecovery?.WorkspaceExecutionId);
        Assert.Equal(1, completed.CodexRecovery?.ResumeCount);
        Assert.Equal(2, h.GitHub.Labels.Count(label => label == "ready->working"));
        Assert.Contains("working->done", h.GitHub.Labels);
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA RECUPERADA", StringComparison.Ordinal));
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA COMPLETADA", StringComparison.Ordinal));
        if (telegramFailure is not null) Assert.Contains("Telegram notification failed", h.Output.ToString());

        using var restarted = new Harness(history: history, timeProvider: clock, telegramEnabled: true);
        restarted.GitHub.CancelWhenEmpty = false;
        restarted.GitHub.ReturnIssueOnFirstQuery = false;
        Assert.Null(await restarted.Worker.ClaimNextAsync(CancellationToken.None));
        Assert.Empty(restarted.TelegramMessages);
        Assert.Empty(restarted.GitHub.Labels);
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    [Theory]
    [InlineData(false, "restart", true, "blocked")]
    [InlineData(false, "resume", true, "blocked")]
    [InlineData(true, "restart", true, "blocked")]
    [InlineData(true, "resume", true, "blocked")]
    [InlineData(false, "resume", false, "blocked")]
    [InlineData(true, "resume", false, "blocked")]
    [InlineData(false, "resume", false, "failed")]
    [InlineData(true, "resume", false, "failed")]
    public async Task BlockedRetryFetchesNewUnblockContextForStandaloneAndManagedWork(bool managed, string retryMode, bool usefulChanges, string outcome)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = retryMode;
        h.Git.Recovery = usefulChanges ? new GitRecoveryInfo("feature/example-task-17", "base-sha", "Workspace retained.") : null;
        h.GitHub.CommentContext = "Original human clarification";
        h.Codex.InitialOutcome = outcome == "blocked"
            ? new("blocked", "Need authorization", [], true, "Authorize this task?", "human_input")
            : new("failed", "No implementation completed", [], false, null, null);

        async Task<IssueProcessingResult?> RunAttemptAsync(int attempt)
        {
            if (!managed) return await h.ProcessOneAsync();
            var now = DateTimeOffset.UtcNow;
            var id = $"server-{attempt}";
            var assignment = new WorkerAssignmentContract($"assignment-{attempt}", id,
                new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
                new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
                new ServerExecutionLeaseContract(id, "worker-id", 1, now, now.AddMinutes(5), "Active"));
            var task = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
            Assert.NotNull(task);
            return await task;
        }

        Assert.Equal(outcome == "blocked" ? IssueOutcomeKind.Blocked : IssueOutcomeKind.Failed, (await RunAttemptAsync(1))?.Kind);
        var source = Assert.Single(await history.ReadAllAsync());
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.IssueLabels = ["ready", outcome];
        h.GitHub.Issue = h.GitHub.Issue with { Body = "Updated description", Labels = h.GitHub.IssueLabels };
        h.GitHub.CommentContext = "Original human clarification\nNew unblock authorization";
        h.Codex.InitialOutcome = Success("Completed authorized task");

        Assert.Equal(IssueOutcomeKind.Succeeded, (await RunAttemptAsync(2))?.Kind);

        Assert.Equal(2, h.GitHub.CommentFetches);
        Assert.Equal("Original human clarification", h.Codex.Issues[0].CommentContext);
        Assert.Contains("New unblock authorization", h.Codex.Issues[1].CommentContext);
        Assert.Equal("Updated description", h.Codex.Issues[1].Body);
        var retry = (await history.ReadAllAsync()).Single(entry => entry.AttemptNumber == 2);
        Assert.Equal(source.ExecutionId, retry.RetryOfExecutionId);
        Assert.Equal(usefulChanges && retryMode == "resume", retry.Resumed);
        Assert.NotEqual(source.ExecutionId, retry.ExecutionId);
        Assert.Contains(outcome, h.GitHub.RemovedLabels);
        if (!usefulChanges)
        {
            Assert.Equal("cleaned-no-changes", source.RecoveryState);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(source),
                System.Text.Json.JsonSerializer.Serialize((await history.ReadAllAsync()).Single(entry => entry.ExecutionId == source.ExecutionId)));
            Assert.False(h.Git.LastResume);
            Assert.Contains(h.GitHub.Comments, comment => comment.Contains("No useful file changes remained", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("New unblock authorization", System.Text.Json.JsonSerializer.Serialize(await history.ReadAllAsync()));
        Assert.DoesNotContain("New unblock authorization", h.Output.ToString());
        Assert.DoesNotContain(h.OperationalMessages, message => message.Contains("New unblock authorization", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningExecutionKeepsItsPreparedCommentSnapshot()
    {
        using var h = new Harness();
        h.GitHub.CommentContext = "prepared clarification";
        h.Codex.BlockRuns = true;
        var claimed = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(claimed);
        await h.Codex.BlockedRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        h.GitHub.CommentContext = "later comment";
        h.Codex.ReleaseRuns.TrySetResult();
        Assert.Equal(IssueOutcomeKind.Succeeded, (await claimed)?.Kind);

        Assert.Equal("prepared clarification", Assert.Single(h.Codex.Issues).CommentContext);
        Assert.Equal(1, h.GitHub.CommentFetches);
    }

    [Fact]
    public async Task CommentFetchFailurePreventsClaimAndWorkspaceMutationAndCanBeFetchedAgain()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.CommentFetchFailure = new GitHubOperationException("Issue comments read", 17, false,
            GitHubFailureKind.TransientProvider, GitHubRemoteState.NotApplicable, "GitHub unavailable");

        await Assert.ThrowsAsync<GitHubOperationException>(() => h.ProcessOneAsync());

        Assert.Empty(h.Codex.Issues);
        Assert.Equal(0, h.Git.Started);
        Assert.Empty(h.GitHub.Labels);
        Assert.Empty(await history.ReadAllAsync());
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.CommentFetchFailure = null;
        h.GitHub.CommentContext = "fresh instructions";
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
        Assert.Equal("fresh instructions", Assert.Single(h.Codex.Issues).CommentContext);
    }

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

        Assert.Contains(h.OperationalMessages, message => message.StartsWith("Scheduler · Test Project / #17 claimed · execution [", StringComparison.Ordinal));
        Assert.DoesNotContain("Scheduler · Test Project / #17 claimed", h.Output.ToString());
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

    [Theory]
    [InlineData(null)]
    [InlineData("uncertain")]
    [InlineData("missing")]
    [InlineData("cleanup-pending")]
    public async Task UnrecoverableResumeIsRejectedAndAnotherIssueRunsWithoutStoppingPolling(string? recoveryState)
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
            "Failed", 1000, "Partial work", null, 0, [], null, null, null, "failed", RecoveryState: recoveryState));

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
        Assert.Contains("has no verified safe recoverable state", notification);
        Assert.Contains("TEST PROJECT", notification);
        Assert.Contains("https://github.com/owner/repo/issues/17", notification);
    }

    [Fact]
    public async Task CorrectedMetadataCanRetryAfterCleanPreparationFailureInResumeMode()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.IssueLabels = ["ready"];
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: turbo" };

        var rejected = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Blocked, rejected!.Kind);
        var preparation = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("preparation-failed", preparation.RecoveryState);
        Assert.Null(preparation.RetryOfExecutionId);
        Assert.Equal(0, h.Git.Started);

        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: low" };
        var retried = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, retried!.Kind);
        var entries = await history.ReadAllAsync();
        Assert.Equal(2, entries.Count);
        var fresh = entries.Single(entry => entry.AttemptNumber == 2);
        Assert.Null(fresh.RetryOfExecutionId);
        Assert.False(fresh.Resumed);
        Assert.Equal("low", fresh.EffectiveEffort);
        Assert.Equal("Blocked", preparation.State);
        Assert.Equal("preparation-failed", entries.Single(entry => entry.ExecutionId == preparation.ExecutionId).RecoveryState);
        Assert.Equal(1, h.Git.Started);
        Assert.False(h.Git.LastResume);
        Assert.Null(h.Git.LastRetryOf);
        Assert.Contains(h.OperationalMessages, message => message.Contains("clean preparation failure", StringComparison.Ordinal));
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
    public async Task IncompatibleAssignmentCanRetryCleanlyInResumeModeOnceRequirementsAreRestored()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.GitHub.IssueLabels = ["ready"];
        var now = DateTimeOffset.UtcNow;
        var project = new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now);
        var assignment = new WorkerAssignmentContract("assignment", "server-execution", project,
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-execution", "worker-id", 1, now, now.AddMinutes(5), "Active"));
        var host = new WorkerHost(new GlobalWorkerConfiguration(), []);
        await host.RejectIncompatibleAssignmentAsync(assignment, h.Worker.Configuration, history,
            "Required runtime is missing.", CancellationToken.None);
        Assert.Empty(h.GitHub.Labels);
        Assert.Equal(0, h.Git.Started);

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, result?.Kind);
        var entries = await history.ReadAllAsync();
        var retry = entries.Single(entry => entry.State == "Completed");
        Assert.Equal(2, retry.AttemptNumber);
        Assert.False(retry.Resumed);
        Assert.Null(retry.RetryOfExecutionId);
        Assert.Equal("preparation-failed", entries.Single(entry => entry.State == "Blocked").RecoveryState);
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
        Assert.Equal(1, persisted.OwnershipGeneration);
        Assert.DoesNotContain(h.OperationalMessages, message => message.StartsWith("Managed ·", StringComparison.Ordinal));
        Assert.Contains(h.OperationalMessages, message => message.StartsWith("Scheduler · Test Project / #17 claimed", StringComparison.Ordinal));
        var lines = h.Output.ToString().Split(Environment.NewLine);
        var started = Array.FindIndex(lines, line => line.StartsWith("▶ Issue ·", StringComparison.Ordinal));
        Assert.True(started >= 0);
        Assert.Equal("↳ Codex · model unknown (CLI model unavailable) · effort medium", lines[started + 1]);
        Assert.Null(persisted.EffectiveModel);
        Assert.True(persisted.ModelSelectedByCli);
        Assert.Equal("medium", persisted.EffectiveEffort);
        Assert.Contains(lines, line => line.Contains("Codex working OK", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Validation OK", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Integrating OK", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("completed ·", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManagedPreExecutionFailureLogsCorrelatedRedactedReasonWithoutRunningCodex()
    {
        using var h = new Harness();
        h.Worker.Configuration.Environment.Variables = new Dictionary<string, string> { ["TEST_SECRET"] = "private-test-value" };
        h.Git.StartFailure = new ProjectCheckoutDirtyException("Checkout is dirty: private-test-value token=unsafe-value");
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("assignment-failed", "server-failed",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-failed", "worker-id", 3, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        Assert.Null(await execution);

        Assert.Null(h.Codex.InitialDirectory);
        Assert.Contains(h.OperationalMessages, message => message.Contains("preparation rejected", StringComparison.Ordinal) &&
            message.Contains("assignment assignment-failed · Server execution server-failed · lease generation 3", StringComparison.Ordinal) &&
            message.Contains("Checkout is dirty", StringComparison.Ordinal));
        Assert.DoesNotContain(h.OperationalMessages, message => message.Contains(" · stage ", StringComparison.Ordinal));
        Assert.DoesNotContain("Managed ·", h.Output.ToString(), StringComparison.Ordinal);
        Assert.All(h.OperationalMessages, message =>
        {
            Assert.DoesNotContain("private-test-value", message, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-value", message, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("private-test-value", h.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-value", h.Output.ToString(), StringComparison.Ordinal);
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
        var diagnostics = h.ErrorOutput.ToString();
        Assert.Equal(3, diagnostics.Split("Validation diagnostic", StringSplitOptions.None).Length - 1);
        Assert.Contains("repair attempt 0", diagnostics);
        Assert.Contains("repair attempt 1", diagnostics);
        Assert.Contains("repair attempt 2", diagnostics);
        Assert.Contains("exit 7", diagnostics);
        Assert.Contains("initial failure", diagnostics);
        Assert.Contains("repair two still fails", diagnostics);
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

        Assert.Equal(3, h.GitHub.FindCalls);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Git.Cleanups);
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", execution.State);
        Assert.Contains("service authentication failed", execution.FailureReason);
        File.Delete(database);
    }

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

    [Fact]
    public async Task SecondaryGitHubFailureDuringRequestedShutdownDoesNotBecomeFatalExit()
    {
        var shutdown = Task.FromException<IssueProcessingResult?>(new WorkerShutdownException(CancellationToken.None,
            new OperationCanceledException("controlled shutdown")));
        var reporting = Task.FromException<IssueProcessingResult?>(new GitHubOperationException("issue edit", 151, true,
            GitHubFailureKind.TransientProvider, GitHubRemoteState.Uncertain, "transient provider failure"));

        await WorkerHost.AwaitShutdownExecutionsAsync([shutdown, reporting]);
    }

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

    [Theory]
    [InlineData("restart")]
    [InlineData("resume")]
    public async Task RetryPreservesRecordedProfileDespiteIssueAndProjectEdits(string mode)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\nmodel: task-model\neffort: low" };
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "Workspace retained.");
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial work", [], false, null);
        Assert.Equal(IssueOutcomeKind.Failed, (await h.ProcessOneAsync())!.Kind);
        h.Worker.Configuration.Worker.RetryMode = mode;
        h.Worker.Configuration.Codex.Model = "new-project-model";
        h.Worker.Configuration.Codex.ReasoningEffort = "xhigh";
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\nmodel: edited-model\neffort: invalid" };
        h.GitHub.ReadyIssueCount = 2;
        h.Codex.InitialOutcome = Success("Completed");
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);
        Assert.Equal(2, h.Codex.Profiles.Count);
        Assert.All(h.Codex.Profiles, profile => Assert.Equal(new CodexExecutionProfile("task-model", "low"), profile));
        Assert.All(await history.ReadAllAsync(), entry =>
        {
            Assert.Equal("task-model", entry.EffectiveModel);
            Assert.Equal("low", entry.EffectiveEffort);
        });
        Assert.Contains(h.TelegramMessages, message => message.Contains("Codex · task-model · low", StringComparison.Ordinal));
        Assert.Contains("Codex · model task-model · effort low", h.Output.ToString());
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Codex model: `task-model` · effort: `low`", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("success", IssueOutcomeKind.Succeeded, "account-model")]
    [InlineData("failed", IssueOutcomeKind.Failed, "account-model")]
    [InlineData("success", IssueOutcomeKind.Succeeded, null)]
    public async Task CliModelIsDurableAndUsedInFinalReports(string status, IssueOutcomeKind kind, string? model)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Codex.CliModel = model;
        h.Codex.InitialOutcome = new CodexOutcome(status, "Implementation result", [], false, null);

        Assert.Equal(kind, (await h.ProcessOneAsync())!.Kind);

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(model, entry.EffectiveModel);
        Assert.True(entry.ModelSelectedByCli);
        Assert.Equal("medium", entry.EffectiveEffort);
        Assert.Null(Assert.Single(h.Codex.Profiles).Model);
        Assert.Contains($"Codex · model {model ?? "unknown (CLI model unavailable)"} · effort medium", h.Output.ToString());
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains($"Codex model: `{model ?? "unknown (CLI model unavailable)"}` · effort: `medium`", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("resume")]
    public async Task CliRetryRecordsItsOwnModelWithoutTurningSourceModelIntoOverride(string mode)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "Workspace retained.");
        h.Codex.CliModel = "first-model";
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial work", [], false, null);
        Assert.Equal(IssueOutcomeKind.Failed, (await h.ProcessOneAsync())!.Kind);
        h.Worker.Configuration.Worker.RetryMode = mode;
        h.Worker.Configuration.Codex.Model = "edited-project-model";
        h.Codex.CliModel = "second-model";
        h.Codex.InitialOutcome = Success("Completed");
        h.GitHub.ReadyIssueCount = 2;

        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);

        Assert.All(h.Codex.Profiles, profile => Assert.Null(profile.Model));
        var entries = await history.ReadAllAsync();
        Assert.Equal("first-model", Assert.Single(entries, entry => entry.AttemptNumber == 1).EffectiveModel);
        Assert.Equal("second-model", Assert.Single(entries, entry => entry.AttemptNumber == 2).EffectiveModel);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Codex model: `second-model`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CodexInfrastructureFailureRetainsObservedModelInHistoryAndInterruptionReport()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Codex.CliModel = "account-model";
        h.Codex.InitialException = new CodexExecutionInfrastructureException("Codex process failure", "CLI failed after startup");

        await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => h.ProcessOneAsync());

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.Equal("account-model", entry.EffectiveModel);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Codex model: `account-model` · effort: `medium`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImplementationAndValidationRepairUseTheSameProfile()
    {
        using var h = new Harness();
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: high" };
        h.Worker.Configuration.Codex.Model = "project-model";
        h.Validation.Results.Enqueue(new ValidationResult(new ValidationFailure(1, "check", 1, "failed", "", false)));
        h.Codex.Repairs.Enqueue(Success("Repaired"));
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);
        Assert.Single(h.Codex.RepairAttempts);
        Assert.All(h.Codex.Profiles, profile => Assert.Equal(new CodexExecutionProfile("project-model", "high"), profile));
    }

    [Fact]
    public async Task InvalidMetadataBlocksBeforePreparingGitAndCanBeCorrected()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: invalid" };
        Assert.Equal(IssueOutcomeKind.Blocked, (await h.ProcessOneAsync())!.Kind);
        Assert.Equal(0, h.Git.Started);
        Assert.Empty(h.Codex.Profiles);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("effort must be low, medium, high, or xhigh", StringComparison.Ordinal));
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: low" };
        h.GitHub.ReadyIssueCount = 2;
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);
        Assert.Equal("low", Assert.Single(h.Codex.Profiles).Effort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictIsAutomaticallyDiscoveredDuringPollingAndAfterRestart(bool restart)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.CancelWhenEmpty = false;
        h.GitHub.ReadyIssueCount = 0;
        Assert.Null(await h.ProcessOneAsync());
        var source = PreservedConflict();
        await history.CreateAsync(source);
        using var restarted = new Harness(history: history);
        var runtime = restart ? restarted : h;
        runtime.GitHub.CancelWhenEmpty = false;
        runtime.GitHub.ReadyIssueCount = 0;
        runtime.GitHub.ReturnConflictIssue = true;
        var result = await runtime.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.Succeeded, result?.Kind);
        Assert.Equal(1, runtime.Git.RecoveryStarted);
        Assert.Equal(0, runtime.Git.Started);
        Assert.Null(runtime.Codex.InitialDirectory);
        var recovered = (await history.ReadAllAsync()).Single(entry => entry.ExecutionId != source.ExecutionId);
        Assert.Equal(source.ExecutionId, recovered.RetryOfExecutionId);
        Assert.True(recovered.Resumed);
        Assert.Contains("without rerunning implementation", result!.Summary);
        Assert.Contains(runtime.OperationalMessages, message => message.Contains("Integration recovery claimed", StringComparison.Ordinal));
        Assert.Null((await history.ReadAllAsync()).Single(entry => entry.ExecutionId == source.ExecutionId).IntegrationRecoveryClaim);
    }

    [Fact]
    public async Task ExhaustedAutomaticRecoveryRemainsPreservedAcrossPollsAndRestartAndRearmsOnChangedBase()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var source = PreservedConflict();
        await history.CreateAsync(source);
        using var h = new Harness(history: history);
        h.GitHub.ReturnConflictIssue = true;
        h.GitHub.ReadyIssueCount = 0;
        h.GitHub.CancelWhenEmpty = false;
        h.Git.Recovery = new(source.FeatureBranch, "repaired-head", "preserved implementation", "current-main");
        h.Git.IntegrationFailure = new PostRebaseValidationException("bounded repair exhausted");
        Assert.Equal(IssueOutcomeKind.IntegrationConflict, (await h.ProcessOneAsync())?.Kind);
        Assert.Null(await h.ProcessOneAsync());
        Assert.Equal(1, h.Git.RecoveryStarted);
        using var restarted = new Harness(history: history);
        restarted.GitHub.ReturnConflictIssue = true;
        restarted.GitHub.ReadyIssueCount = 0;
        restarted.GitHub.CancelWhenEmpty = false;
        Assert.Null(await restarted.ProcessOneAsync());
        Assert.Equal(0, restarted.Git.RecoveryStarted);
        restarted.Git.IntegrationBase = "new-main";
        Assert.Equal(IssueOutcomeKind.Succeeded, (await restarted.ProcessOneAsync())?.Kind);
        Assert.Equal(1, restarted.Git.RecoveryStarted);
        Assert.Equal("repaired-head", restarted.Git.LastRetryOf?.RecoveryBaseCommit);
        Assert.Null(restarted.Codex.InitialDirectory);
    }

    [Fact]
    public async Task OperatorRecoveryLabelRearmsAnUnchangedExhaustedBase()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        await history.CreateAsync(PreservedConflict() with { IntegrationRecoveryAttemptBase = "current-main" });
        using var h = new Harness(history: history);
        h.GitHub.ReturnRecoveryIssueOnFirstQuery = true;
        h.GitHub.ReadyIssueCount = 0;
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
        Assert.Equal(1, h.Git.RecoveryStarted);
    }

    [Fact]
    public async Task UnsafeConflictReportsReasonAndDoesNotBlockUnrelatedImplementation()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var source = PreservedConflict();
        await history.CreateAsync(source);
        using var h = new Harness(history: history);
        h.GitHub.ReturnConflictIssue = true;
        h.Git.RecoveryRejection = "persisted workspace commit does not match";
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.ReturnDistinctIssues = true;
        // The first ready Issue is the conflict and is explicitly excluded by the fake queue.
        h.GitHub.ExcludeReadyIssue = 17;
        var result = await h.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.Succeeded, result?.Kind);
        Assert.Equal(0, h.Git.RecoveryStarted);
        Assert.Equal(1, h.Git.Started);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("persisted workspace commit does not match", StringComparison.Ordinal));
        Assert.Equal("integration-conflict", (await history.ReadAllAsync()).Single(entry => entry.ExecutionId == source.ExecutionId).RecoveryState);
    }

    [Fact]
    public async Task ConflictLabelWithoutHistoryNeverStartsImplementation()
    {
        using var h = new Harness();
        h.GitHub.ReturnConflictIssue = true;
        h.GitHub.ReadyIssueCount = 0;
        Assert.Null(await h.ProcessOneAsync());
        Assert.Equal(0, h.Git.Started);
        Assert.Equal(0, h.Git.RecoveryStarted);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("no recoverable integration-conflict execution history", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartReconcilesCleanClaimButPreservesUncertainWorkspace(bool unsafeWorkspace)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var source = PreservedConflict();
        await history.CreateAsync(source);
        var attempt = source with { ExecutionId = Guid.NewGuid(), State = "Integrating", CompletedAtUtc = null,
            RecoveryState = null, RetryOfExecutionId = source.ExecutionId, AttemptNumber = 2 };
        Assert.True(await history.TryClaimIntegrationRecoveryAsync(source, attempt, "current-main", false));
        using var restarted = new Harness(history: history);
        restarted.Git.RecoveryRejection = unsafeWorkspace ? "unfinished Git operation" : null;
        await restarted.Worker.ReconcileIntegrationRecoveryAsync(CancellationToken.None);
        var rows = await history.ReadAllAsync();
        Assert.Equal("current-main", rows.Single(entry => entry.ExecutionId == source.ExecutionId).IntegrationRecoveryAttemptBase);
        if (unsafeWorkspace)
        {
            Assert.Equal(attempt.ExecutionId, rows.Single(entry => entry.ExecutionId == source.ExecutionId).IntegrationRecoveryClaim);
            Assert.Contains(restarted.GitHub.Comments, comment => comment.Contains("unfinished Git operation", StringComparison.Ordinal));
        }
        else
        {
            Assert.Null(rows.Single(entry => entry.ExecutionId == source.ExecutionId).IntegrationRecoveryClaim);
            Assert.Equal("Cancelled", rows.Single(entry => entry.ExecutionId == attempt.ExecutionId).State);
            Assert.Contains("working->codex-integration-conflict", restarted.GitHub.Labels);
            restarted.GitHub.ReturnConflictIssue = true;
            restarted.GitHub.CancelWhenEmpty = false;
            restarted.GitHub.ReadyIssueCount = 0;
            Assert.Null(await restarted.ProcessOneAsync());
        }
    }

    [Fact]
    public async Task RecoveryClaimReservesCapacityAndCannotBeDoubleScheduledOrReimplemented()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        await history.CreateAsync(PreservedConflict());
        using var h = new Harness(history: history);
        h.GitHub.ReturnConflictIssue = true;
        h.GitHub.CancelWhenEmpty = false;
        h.GitHub.ReadyIssueCount = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Git.IntegrationAction = async () => { entered.SetResult(); await release.Task; };
        var registry = new ProjectRuntimeRegistry([("project.yml", h.Worker.Configuration)], new RuntimeEventLog());
        Assert.True(registry.TryReserve("Test Project"));
        var first = await h.Worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(first);
        await entered.Task;
        Assert.Equal(1, registry.WorkerActiveExecutionCount);
        using var competitor = new Harness(history: history);
        competitor.GitHub.ReturnConflictIssue = true;
        competitor.GitHub.IssueLabels = ["ready"];
        competitor.GitHub.CancelWhenEmpty = false;
        Assert.Null(await competitor.Worker.ClaimNextAsync(CancellationToken.None));
        Assert.Equal(0, competitor.Git.Started);
        release.SetResult();
        Assert.Equal(IssueOutcomeKind.Succeeded, (await first)!.Kind);
        registry.Release("Test Project");
        Assert.Equal(0, registry.WorkerActiveExecutionCount);
    }

    [Fact]
    public async Task ManagedRecoveryAssignmentContinuesPreservedImplementationWithLeaseLineageAndFrozenProfile()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var source = PreservedConflict() with { ServerExecutionId = Guid.NewGuid().ToString("N"), EffectiveEffort = "low", EffectiveModel = "original-model", OriginalIssueBody = "Original Issue intent" };
        await history.CreateAsync(source);
        using var h = new Harness(history: history);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\nmodel: edited-model\neffort: xhigh" };
        var candidates = await h.Worker.DiscoverManagedIntegrationRecoveriesAsync("project", CancellationToken.None);
        Assert.Equal(source.ExecutionId.ToString(), Assert.Single(candidates).WorkerExecutionId);
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("recovery-assignment", "server-recovery",
            new("project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new("github-issue", "17"), "worker", new Dictionary<string, string>
            { ["integrationRecoveryExecutionId"] = source.ExecutionId.ToString(), ["originalServerExecutionId"] = source.ServerExecutionId },
            new("server-recovery", "worker", 2, now, now.AddMinutes(5), "Active"));
        var task = await h.Worker.ClaimAssignedAsync(assignment, CancellationToken.None);
        Assert.NotNull(task);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await task)?.Kind);
        Assert.Equal(new CodexExecutionProfile("original-model", "low"), Assert.Single(h.Codex.Profiles));
        Assert.Null(h.Codex.InitialDirectory);
        var attempt = (await history.ReadAllAsync()).Single(entry => entry.ExecutionId != source.ExecutionId);
        Assert.Equal(source.ExecutionId, attempt.RetryOfExecutionId);
        Assert.Equal("server-recovery", attempt.ServerExecutionId);
        Assert.Equal(2, attempt.OwnershipGeneration);
        Assert.Equal("Original Issue intent", attempt.OriginalIssueBody);
    }

    [Fact]
    public async Task ConflictFromNormalImplementationRetryRecoversItsOwnWorkspaceRatherThanEarlierFailedWork()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var previous = PreservedConflict() with { State = "Failed", RecoveryState = "recoverable" };
        var source = PreservedConflict() with { AttemptNumber = 2, FeatureBranch = "feature/example-task-17-retry-2",
            RetryOfExecutionId = previous.ExecutionId, StartedAtUtc = DateTimeOffset.UtcNow };
        await history.CreateAsync(previous);
        await history.CreateAsync(source);
        using var h = new Harness(history: history);
        h.GitHub.ReturnConflictIssue = true;
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
        Assert.Equal(source.ExecutionId, h.Git.LastRetryOf?.ExecutionId);
        Assert.Equal(source.FeatureBranch, h.Git.LastRetryOf?.FeatureBranch);
        Assert.Null(h.Codex.InitialDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatedIntegrationWithLostPushResponseResumesCompletionWithoutImplementation(bool remoteReceivedPush)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = remoteReceivedPush;
        h.Git.IntegrationException = new WorkerInfrastructureException("Push response uncertain");
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("passed", pending.ValidationOutcome);
        Assert.Equal("main", pending.IntegrationBranch);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", pending.CommitSha);
        Assert.False(ExecutionCompletion.Read(pending).RemoteConfirmed);
        Assert.DoesNotContain("working->blocked", h.GitHub.Labels);
        Assert.Empty(h.GitHub.Comments);
        if (!remoteReceivedPush)
        {
            // Terminal Issue state must not bypass proof for a modern completion.
            h.GitHub.Closed = true;
            h.GitHub.CompletionLabels = ["done"];
            var missing = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.ResumeCompletionAsync(pending, CancellationToken.None));
            Assert.Contains("missing the exact", missing.Message, StringComparison.Ordinal);
            Assert.Empty(h.GitHub.Comments);
            h.GitHub.Closed = false;
            h.GitHub.CompletionLabels = ["working"];
            // Operator retries only the exact push. Reconciliation never publishes the base.
            h.Git.RemoteContainsIntegration = true;
        }
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var completed = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Completed", completed.State);
        Assert.True(ExecutionCompletion.Read(completed).Finished);
        Assert.True(h.GitHub.Closed);
        Assert.Single(h.GitHub.Comments);
        Assert.Contains("working->done", h.GitHub.Labels);
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA COMPLETADA", StringComparison.Ordinal));
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Single(h.GitHub.Comments);
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(1, h.Git.Started);
    }

    [Theory]
    [InlineData("label-before")]
    [InlineData("label-after")]
    [InlineData("comment-before")]
    [InlineData("comment-after")]
    [InlineData("close-before")]
    [InlineData("close-after")]
    [InlineData("cleanup-before")]
    [InlineData("cleanup-after")]
    public async Task CompletionRestartsDeduplicateEachDurableSuccessEffect(string failurePhase)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.GitHub.CompletionFailurePhase = failurePhase;
        h.Git.CompletionFailurePhase = failurePhase;
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        h.GitHub.CompletionFailurePhase = null;
        h.Git.CompletionFailurePhase = null;
        var pending = Assert.Single(await history.ReadAllAsync());
        // Recreate the coordinator with retained external effects and durable history.
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var completed = Assert.Single(await history.ReadAllAsync());
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Equal("Completed", completed.State);
        Assert.True(h.GitHub.Closed);
        Assert.Single(h.GitHub.Comments);
        Assert.Single(h.GitHub.Labels, label => label == "working->done");
        Assert.Equal(1, h.GitHub.CloseCalls);
        Assert.True(ExecutionCompletion.Read(completed).NotificationAttempted);
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA COMPLETADA", StringComparison.Ordinal));
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Fact]
    public async Task ManuallyClosedPublishedCompletionAcknowledgesSuccessWithoutGitHubEffects()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.GitHub.CompletionFailurePhase = "comment-before";
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        h.GitHub.CompletionFailurePhase = null;
        await h.GitHub.CloseAsync(1, CancellationToken.None);
        var closeCalls = h.GitHub.CloseCalls;
        var pending = Assert.Single(await history.ReadAllAsync());
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var completed = Assert.Single(await history.ReadAllAsync());
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Equal("Completed", completed.State);
        Assert.True(ExecutionCompletion.Read(completed).Finished);
        Assert.True(ExecutionCompletion.Read(completed).TerminalIssueAcknowledged);
        Assert.Empty(h.GitHub.Comments);
        Assert.Equal(closeCalls, h.GitHub.CloseCalls);
        Assert.Single(h.GitHub.Labels, label => label == "working->done");
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Fact]
    public async Task ConfirmedBaseWithPendingArchiveRemainsInCompletionLifecycle()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationFailure = new GitIntegrationArchiveException("Archive response uncertain",
            new WorkerInfrastructureException("Original archive failure"));
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", pending.State);
        Assert.True(ExecutionCompletion.Read(pending).RemoteConfirmed);
        Assert.DoesNotContain(h.GitHub.Labels, label => label.Contains("codex-integration-conflict", StringComparison.Ordinal));
        Assert.Empty(h.GitHub.Comments);
        await h.Worker.ResumeCompletionAsync(pending, CancellationToken.None);
        Assert.True(h.GitHub.Closed);
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Fact]
    public async Task PendingIntegrationRecoveryCompletionRetainsClaimUntilSuccessAndNeverReportsAnotherRecoveryRejection()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var source = PreservedConflict();
        await history.CreateAsync(source);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationException = new WorkerInfrastructureException("Lost push response");
        h.GitHub.ReturnConflictIssue = true;
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var entries = await history.ReadAllAsync();
        var pending = entries.Single(entry => entry.ExecutionId != source.ExecutionId);
        Assert.Equal(pending.ExecutionId, entries.Single(entry => entry.ExecutionId == source.ExecutionId).IntegrationRecoveryClaim);
        var commentCount = h.GitHub.Comments.Count;
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ReconcileIntegrationRecoveryAsync(CancellationToken.None);
        Assert.Equal(commentCount, h.GitHub.Comments.Count);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var reconciled = await history.ReadExecutionAsync(source.ExecutionId);
        Assert.NotNull(reconciled);
        Assert.Null(reconciled.IntegrationRecoveryClaim);
        Assert.Equal("integration-recovered", reconciled.RecoveryState);
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("ready")]
    [InlineData("blocked")]
    [InlineData("closed")]
    [InlineData("new-attempt")]
    [InlineData("managed")]
    [InlineData("corrupt")]
    [InlineData("corrupt-terminal")]
    [InlineData("inconsistent-terminal")]
    [InlineData("missing")]
    [InlineData("validation")]
    public async Task CompletionFailsClosedOnConflictingIntentOwnershipOrProvenance(string conflict)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationException = new WorkerInfrastructureException("Lost push response");
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync());
        switch (conflict)
        {
            case "body": h.GitHub.Issue = h.GitHub.Issue with { Body = "Operator changed intent" }; break;
            case "ready": h.GitHub.CompletionLabels = ["ready"]; break;
            case "blocked": h.GitHub.CompletionLabels = ["blocked"]; break;
            case "closed": h.GitHub.Closed = true; break;
            case "new-attempt": await history.CreateAsync(pending with { ExecutionId = Guid.NewGuid(), AttemptNumber = 2 }); break;
            case "managed": pending = pending with { ServerExecutionId = "original-managed-execution" }; h.Git.RemoteContainsIntegration = false; break;
            case "corrupt": pending = pending with { CompletionJson = "{" }; break;
            case "corrupt-terminal":
                pending = pending with { CompletionJson = "{" };
                h.GitHub.Closed = true;
                h.GitHub.CompletionLabels = ["done"];
                break;
            case "inconsistent-terminal":
                pending = pending with { CompletionJson = "{}" };
                h.GitHub.Closed = true;
                h.GitHub.CompletionLabels = ["done"];
                break;
            case "missing": pending = pending with { CompletionJson = null }; break;
            case "validation": pending = pending with { ValidationOutcome = null }; break;
        }
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.ResumeCompletionAsync(pending, CancellationToken.None));
        Assert.Empty(h.GitHub.Comments);
        Assert.DoesNotContain("working->done", h.GitHub.Labels);
        Assert.Single(h.Codex.Issues);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ManagedHistoricalCompletionOnlyQuarantinesProvenIntegration(bool remoteContainsCommit, bool legacy)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationException = new WorkerInfrastructureException("Lost push response");
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync()) with
        {
            ServerExecutionId = "original-server-execution", AssignmentId = "original-assignment", OwnershipGeneration = 7
        };
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE executions SET server_execution_id=$server, assignment_id=$assignment, ownership_generation=$generation WHERE execution_id=$id";
            command.Parameters.AddWithValue("$server", pending.ServerExecutionId);
            command.Parameters.AddWithValue("$assignment", pending.AssignmentId);
            command.Parameters.AddWithValue("$generation", 7);
            command.Parameters.AddWithValue("$id", pending.ExecutionId.ToString());
            await command.ExecuteNonQueryAsync();
        }
        if (legacy)
        {
            pending = pending with { CompletionJson = null };
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE executions SET completion_json=NULL WHERE execution_id=$id";
            command.Parameters.AddWithValue("$id", pending.ExecutionId.ToString());
            await command.ExecuteNonQueryAsync();
        }
        h.Git.RemoteContainsIntegration = remoteContainsCommit;
        var config = h.Worker.Configuration;
        var global = new GlobalWorkerConfiguration();
        var runtime = new WorkerRuntimeReadModel(global, [("project.yml", config)], history);
        var host = new WorkerHost(global, [("project.yml", config)]);
        await host.ReconcileCompletionAsync(h.Worker, pending, runtime, CancellationToken.None);
        var retained = await history.ReadExecutionAsync(pending.ExecutionId);
        Assert.NotNull(retained);
        Assert.Equal(pending.CompletionJson, retained.CompletionJson);
        Assert.Equal(pending.ServerExecutionId, retained.ServerExecutionId);
        Assert.Equal(pending.AssignmentId, retained.AssignmentId);
        Assert.Equal(pending.OwnershipGeneration, retained.OwnershipGeneration);
        Assert.Equal(pending.CommitSha, retained.CommitSha);
        if (remoteContainsCommit)
        {
            Assert.Equal("managed-completion-quarantined", retained.RecoveryState);
            Assert.False(WorkerHost.NeedsCompletionReconciliation(retained));
            Assert.Equal(ProjectLifecycleState.Enabled, runtime.Registry.Get(config.Project.Name)?.State);
            Assert.True(runtime.Registry.TryReserve(config.Project.Name, config));
            runtime.Registry.Release(config.Project.Name);
        }
        else
        {
            Assert.True(WorkerHost.NeedsCompletionReconciliation(retained));
            Assert.Equal(ProjectLifecycleState.Unavailable, runtime.Registry.Get(config.Project.Name)?.State);
        }
        Assert.Empty(h.GitHub.Comments);
        Assert.DoesNotContain("working->done", h.GitHub.Labels);
        Assert.Equal(0, h.GitHub.CloseCalls);
        Assert.Single(h.Codex.Issues);
        if (remoteContainsCommit)
        {
            using var reopened = new ExecutionHistoryStore(database.Path);
            var restartedEntry = await reopened.ReadExecutionAsync(pending.ExecutionId);
            Assert.NotNull(restartedEntry);
            Assert.False(WorkerHost.NeedsCompletionReconciliation(restartedEntry));
            h.Git.IntegrationException = null;
            h.Git.CompletionMode = false;
            h.GitHub.CompletionMode = false;
            h.GitHub.ReadyIssueCount = 2;
            h.GitHub.ReturnDistinctIssues = true;
            Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
            Assert.Equal(2, h.Codex.Issues.Count);
            Assert.Equal("managed-completion-quarantined", (await reopened.ReadExecutionAsync(pending.ExecutionId))?.RecoveryState);
        }
    }

    [Fact]
    public async Task TerminalLegacyCompletionLeavesSchedulingAvailableAcrossRestarts()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.GitHub.Closed = true;
        h.GitHub.CompletionLabels = ["DONE", "unrelated-label"];
        for (var i = 0; i < 3; i++)
            await history.CreateAsync(PreservedConflict() with
            {
                IssueNumber = 100 + i, State = "InfrastructureFailure",
                RecoveryState = GitHubOperationException.ReconciliationRequiredState,
                CompletionJson = null
            });
        var entries = await history.ReadAllAsync();
        for (var restart = 0; restart < 2; restart++)
        {
            var config = h.Worker.Configuration;
            var global = new GlobalWorkerConfiguration();
            var runtime = new WorkerRuntimeReadModel(global, [("project.yml", config)], history);
            var host = new WorkerHost(global, [("project.yml", config)]);
            var worker = new Worker(config, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram,
                history: history, operationalLog: h.OperationalMessages.Add);
            foreach (var entry in entries)
                await host.ReconcileCompletionAsync(worker, entry, runtime, CancellationToken.None);
            Assert.Equal(ProjectLifecycleState.Enabled, runtime.Registry.Get(config.Project.Name)?.State);
            Assert.True(runtime.Registry.TryReserve(config.Project.Name, config));
            runtime.Registry.Release(config.Project.Name);
        }
        Assert.Equal(6, h.OperationalMessages.Count(message => message.Contains("legacy completion record acknowledged", StringComparison.Ordinal)));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(entries),
            System.Text.Json.JsonSerializer.Serialize(await history.ReadAllAsync()));
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(0, h.Git.Started);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Empty(h.GitHub.Labels);
        Assert.Empty(h.GitHub.Comments);
        Assert.Equal(0, h.GitHub.CloseCalls);
        Assert.Empty(h.TelegramMessages);
        // Discovery and claiming of a different ready Issue remain available.
        h.GitHub.Closed = false;
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
        Assert.Single(h.Codex.Issues);
    }

    [Theory]
    [InlineData(true, "working")]
    [InlineData(true, "done")]
    [InlineData(false, "")]
    [InlineData(false, "failed")]
    [InlineData(false, "done,ready")]
    [InlineData(false, "done,working")]
    [InlineData(false, "done,blocked")]
    [InlineData(false, "done,failed")]
    [InlineData(false, "done,codex-integration-conflict")]
    [InlineData(false, "done,codex-integration-recovery")]
    [InlineData(false, "unverifiable")]
    public async Task AmbiguousLegacyCompletionStillPausesProject(bool open, string labels)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var entry = PreservedConflict() with { State = "InfrastructureFailure",
            RecoveryState = GitHubOperationException.ReconciliationRequiredState };
        await history.CreateAsync(entry);
        h.GitHub.Closed = !open;
        h.GitHub.CompletionLabels = labels.Split(',');
        if (labels == "unverifiable") h.GitHub.StateReadFailure = new WorkerInfrastructureException("Issue read unavailable");
        var config = h.Worker.Configuration;
        var global = new GlobalWorkerConfiguration();
        var runtime = new WorkerRuntimeReadModel(global, [("project.yml", config)], history);
        var host = new WorkerHost(global, [("project.yml", config)]);
        await host.ReconcileCompletionAsync(h.Worker, entry, runtime, CancellationToken.None);
        Assert.Equal(ProjectLifecycleState.Unavailable, runtime.Registry.Get(config.Project.Name)?.State);
        Assert.False(runtime.Registry.TryReserve(config.Project.Name, config));
        Assert.Empty(h.Codex.Issues);
        Assert.Empty(h.GitHub.Labels);
        Assert.Empty(h.GitHub.Comments);
        Assert.Equal(0, h.GitHub.CloseCalls);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(entry),
            System.Text.Json.JsonSerializer.Serialize(await history.ReadExecutionAsync(entry.ExecutionId)));
    }

    private static ExecutionHistoryEntry PreservedConflict() => new(Guid.NewGuid(), "Test Project", "owner/repo", 17,
        "Example task", "feature/example-task-17", "main", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
        "IntegrationConflict", 1000, "Original implementation intent", "passed", 0, [], "preserved-head", "main", null,
        "integration conflict", "integration-conflict", "preserved-head", "preserved implementation", EffectiveEffort: "high");

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
        public TelegramNotifier Telegram => _telegram;
        public StringWriter Output { get; } = new();
        public StringWriter ErrorOutput { get; } = new();
        public List<string> OperationalMessages { get; } = [];
        private readonly TelegramNotifier _telegram;
        private readonly HttpClient? _telegramClient;
        private readonly StubTelegramHandler? _telegramHandler;

        public Harness(bool interactive = false, bool telegramEnabled = false, ExecutionHistoryStore? history = null, bool gracefulShutdown = false, TimeProvider? timeProvider = null)
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
                operationalLog: OperationalMessages.Add, shutdownToken: gracefulShutdown ? Cancellation.Token : default, timeProvider: timeProvider);
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
        public bool CompletionMode { get; set; }
        public bool Closed { get; set; }
        public int CloseCalls { get; private set; }
        public IReadOnlyList<string> CompletionLabels { get; set; } = ["working"];
        public string? CompletionFailurePhase { get; set; }
        private void FailCompletion(string phase)
        {
            if (CompletionFailurePhase == phase)
                throw new GitHubOperationException(phase, Issue.Number, true, GitHubFailureKind.TransientProvider,
                    GitHubRemoteState.Uncertain, "Simulated lost reporting response");
        }
        public Exception? StateReadFailure { get; set; }
        public Task<GitHubIssueState> ReadIssueStateAsync(int issueNumber, CancellationToken ct) =>
            StateReadFailure is null ? Task.FromResult(new GitHubIssueState(!Closed, CompletionLabels))
                : Task.FromException<GitHubIssueState>(StateReadFailure);
        public async Task EnsureSuccessCommentAsync(int issueNumber, Guid executionId, string comment, CancellationToken ct, bool allowCreate = true)
        {
            FailCompletion("comment-before");
            if (!Comments.Contains(comment)) await CommentAsync(issueNumber, comment, ct);
            FailCompletion("comment-after");
        }
        public string CommentContext { get; set; } = "";
        public Exception? CommentFetchFailure { get; set; }
        public int CommentFetches { get; private set; }
        public Task<string> GetIssueCommentContextAsync(int issueNumber, CancellationToken cancellationToken,
            IReadOnlyList<string>? secretValues = null)
        {
            CommentFetches++;
            return CommentFetchFailure is null ? Task.FromResult(CommentContext) : Task.FromException<string>(CommentFetchFailure);
        }
        private int _returned;
        public int ReadyIssueCount { get; set; } = 1;
        public bool ReturnIssueOnFirstQuery { get; set; } = true;
        public bool CancelWhenEmpty { get; set; } = true;
        public bool CancelDuringQuery { get; set; }
        public bool CancelDuringClaim { get; set; }
        public bool ReturnDistinctIssues { get; set; }
        public bool ReturnRecoveryIssueOnFirstQuery { get; set; }
        public bool ReturnConflictIssue { get; set; }
        public int? ExcludeReadyIssue { get; set; }
        public string ReadyLabel { get; set; } = "ready";
        public string RecoveryLabel { get; set; } = "codex-integration-recovery";
        public IReadOnlyList<string> IssueLabels { get; set; } = [];
        public List<string> RemovedLabels { get; } = [];
        public GitHubIssue Issue { get; set; } = new(17, "Example task", "Implement this request", DateTimeOffset.UtcNow);
        public int FindCalls { get; private set; }
        public List<string> Labels { get; } = [];
        public List<string> Comments { get; } = [];
        public Func<Task>? CommentAction { get; set; }
        public GitHubOperationException? InterruptionReportingFailure { get; set; }
        public GitHubOperationException? ResultReportingFailure { get; set; }

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
            if (label == "codex-integration-conflict" && ReturnConflictIssue && !excludedIssueNumbers.Contains(Issue.Number))
                return Task.FromResult<GitHubIssue?>(Issue with { Labels = [label] });
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
                    if (!excludedIssueNumbers.Contains(number) && number != ExcludeReadyIssue)
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
            if (CompletionMode && add == "done") FailCompletion("label-before");
            Labels.Add($"{remove}->{add}");
            if (CompletionMode)
            {
                CompletionLabels = CompletionLabels.Where(label => label != remove).Append(add).ToArray();
                if (add == "done") FailCompletion("label-after");
            }
            if (InterruptionReportingFailure is not null && remove == "working" && add == "blocked")
                return Task.FromException(InterruptionReportingFailure);
            if (ResultReportingFailure is not null && remove == "working" && add == "done")
                return Task.FromException(ResultReportingFailure);
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
        public Task CloseAsync(int issueNumber, CancellationToken ct)
        {
            if (CompletionMode) FailCompletion("close-before");
            Closed = true;
            CloseCalls++;
            if (CompletionMode) FailCompletion("close-after");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGit : IGitRepository
    {
        public bool CompletionMode { get; set; }
        public bool RemoteContainsIntegration { get; set; }
        public string? CompletionFailurePhase { get; set; }
        private Func<GitIntegrationResult, bool, CancellationToken, Task>? _integrationObserver;
        public IGitRepository WithIntegrationObserver(Func<GitIntegrationResult, bool, CancellationToken, Task> observer)
        { _integrationObserver = observer; return this; }
        public Task<bool> VerifyRemoteIntegrationAsync(ExecutionHistoryEntry entry, CancellationToken ct) => Task.FromResult(RemoteContainsIntegration);
        public Task FinishIntegratedExecutionAsync(ExecutionHistoryEntry entry, CancellationToken ct)
        {
            if (CompletionFailurePhase is "cleanup-before" or "cleanup-after")
                throw new WorkerInfrastructureException("Cleanup interrupted");
            return Task.CompletedTask;
        }
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
        public GitRecoveryInfo? Snapshot { get; set; }
        public int CodexResumes { get; private set; }
        public Task<GitRecoveryInfo?> InspectExecutionWorkspaceAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public Task<string?> ValidateCodexRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task StartCodexRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
        { CodexResumes++; return Task.CompletedTask; }
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, CancellationToken ct) { Started++; LastExecutionId = executionId; return Task.CompletedTask; }
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resume, int attemptNumber, CancellationToken ct)
        { Started++; LastExecutionId = executionId; LastResume = resume; LastRetryOf = retryOf; LastAttemptNumber = attemptNumber;
            var failure = FailIssueNumber is null || issue.Number == FailIssueNumber ? StartFailure : null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure); }
        public string? IntegrationBase { get; set; } = "current-main";
        public string? RecoveryRejection { get; set; }
        public Func<Task>? IntegrationAction { get; set; }
        public Task<string?> GetIntegrationBaseAsync(CancellationToken ct) => Task.FromResult(IntegrationBase);
        public Task<GitRecoveryInfo?> PreserveIntegrationConflictAsync(CancellationToken ct) => Task.FromResult(Recovery);
        public Task StartIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
        { RecoveryStarted++; LastRetryOf = source; return Task.CompletedTask; }
        public Task<string?> ValidateIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
            Task.FromResult(RecoveryRejection);
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
        public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
            Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct)
        {
            if (IntegrationAction is not null) await IntegrationAction();
            Integrations++;
            BeforeIntegration?.Invoke();
            var prepared = new GitIntegrationResult(true,
                "Committed as `0123456789ab`. Merged into `main`. Preserved on origin as `completed/17`.",
                "0123456789abcdef0123456789abcdef01234567", "main", "completed/17",
                RecoveryStarted > 0 ? LastRetryOf?.ExecutionId : LastExecutionId,
                RecoveryStarted > 0 ? LastRetryOf?.AttemptNumber : LastAttemptNumber);
            if (CompletionMode && _integrationObserver is not null)
                await _integrationObserver(prepared, false, ct);
            if (IntegrationException is not null) throw IntegrationException;
            if (CompletionMode && _integrationObserver is not null)
                await _integrationObserver(prepared, true, ct);
            if (IntegrationFailure is not null) throw IntegrationFailure;
            if (CompletionMode) return prepared;
            return new GitIntegrationResult(true,
            "Committed as `0123456789ab`. Merged into `main`. Preserved on origin as `completed/17`.",
            "0123456789abcdef0123456789abcdef01234567", "main", "completed/17");
        }
    }

    private sealed class FakeCodex(List<string> events) : ICodexExecutor
    {
        public ICodexQuotaReader? QuotaReader { get; set; }
        public List<GitHubIssue> Issues { get; } = [];
        private Action<string?>? _modelObserver;
        public string? CliModel { get; set; }
        public ICodexExecutor WithModelObserver(Action<string?> observer)
        {
            _modelObserver = observer;
            return this;
        }
        public List<CodexExecutionProfile> Profiles { get; } = [];
        public ICodexExecutor WithProfile(CodexExecutionProfile profile)
        {
            Profiles.Add(profile);
            return this;
        }
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
            RunAsync(projectDirectory, instructionsFile, issue, null, false, 1, ct);
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct)
        {
            Issues.Add(issue);
            return RunCoreAsync(projectDirectory, ct);
        }
        private async Task<CodexOutcome> RunCoreAsync(string projectDirectory, CancellationToken ct)
        {
            if (CliModel is not null) _modelObserver?.Invoke(CliModel);
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
