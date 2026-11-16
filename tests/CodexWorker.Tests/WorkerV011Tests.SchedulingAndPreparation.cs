using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false, "restart", true, "blocked", null)]
    [InlineData(false, "resume", true, "blocked", null)]
    [InlineData(true, "restart", true, "blocked", null)]
    [InlineData(true, "resume", true, "blocked", null)]
    [InlineData(false, "resume", false, "blocked", null)]
    [InlineData(true, "resume", false, "blocked", null)]
    [InlineData(false, "resume", false, "failed", null)]
    [InlineData(true, "resume", false, "failed", null)]
    [InlineData(false, "resume", true, "blocked", "restart")]
    [InlineData(true, "resume", true, "blocked", "restart")]
    [InlineData(false, "restart", true, "blocked", "resume")]
    [InlineData(true, "restart", true, "blocked", "resume")]
    public async Task BlockedRetryFetchesNewUnblockContextForStandaloneAndManagedWork(bool managed, string retryMode, bool usefulChanges, string outcome, string? issueRetryMode)
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
        h.GitHub.Issue = h.GitHub.Issue with { Body = "Updated description" + (issueRetryMode is null ? "" : $"\n## Worker\nretryMode: {issueRetryMode}"), Labels = h.GitHub.IssueLabels };
        h.GitHub.CommentContext = "Original human clarification\nNew unblock authorization";
        h.Codex.InitialOutcome = Success("Completed authorized task");

        Assert.Equal(IssueOutcomeKind.Succeeded, (await RunAttemptAsync(2))?.Kind);

        Assert.Equal(2, h.GitHub.CommentFetches);
        Assert.Equal("Original human clarification", h.Codex.Issues[0].CommentContext);
        Assert.Contains("New unblock authorization", h.Codex.Issues[1].CommentContext);
        Assert.StartsWith("Updated description", h.Codex.Issues[1].Body);
        var retry = (await history.ReadAllAsync()).Single(entry => entry.AttemptNumber == 2);
        Assert.Equal(source.ExecutionId, retry.RetryOfExecutionId);
        Assert.Equal(issueRetryMode ?? retryMode, retry.RetryMode);
        Assert.Equal(retryMode, source.RetryMode);
        Assert.Equal(usefulChanges && (issueRetryMode ?? retryMode) == "resume", retry.Resumed);
        Assert.Equal(retry.Resumed, h.Git.LastResume);
        Assert.Equal(source.ExecutionId, h.Git.LastRetryOf?.ExecutionId);
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(false, "retryMode: invalid")]
    [InlineData(true, "retryMode: invalid")]
    [InlineData(false, "retryMode: resume\nretryMode: resume")]
    [InlineData(true, "retryMode: resume\nretryMode: resume")]
    [InlineData(false, "retryMode: restart\n## Codex\nretryMode: resume")]
    [InlineData(true, "retryMode: restart\n## Codex\nretryMode: resume")]
    public async Task InvalidRetryMetadataRejectsEvenWhenPreviousProfileWasFrozen(bool managed, string metadata)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "Workspace retained.");
        h.Codex.InitialOutcome = new("failed", "Partial work", [], false, null);
        Assert.Equal(IssueOutcomeKind.Failed, (await h.ProcessOneAsync())?.Kind);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\n" + metadata };
        h.GitHub.ReadyIssueCount = 2;
        if (managed)
        {
            var now = DateTimeOffset.UtcNow;
            var assignment = new WorkerAssignmentContract("assignment", "server-execution",
                new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
                new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
                new ServerExecutionLeaseContract("server-execution", "worker-id", 1, now, now.AddMinutes(5), "Active"));
            var error = await Assert.ThrowsAsync<IssuePreparationRejectedException>(() => h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token));
            Assert.Contains("Invalid ## Worker metadata", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(IssueOutcomeKind.Blocked, (await h.ProcessOneAsync())?.Kind);
            Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Invalid ## Worker metadata", StringComparison.Ordinal));
            Assert.Equal("preparation-failed", (await history.ReadAllAsync()).Single(entry => entry.AttemptNumber == 2).RecoveryState);
        }
        Assert.Equal(1, h.Git.Started);
        Assert.Single(h.Codex.Issues);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task UnavailableCccEngineLeavesStandaloneIssueReadyWithoutStartingCodex()
    {
        using var h = new Harness();
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\nengine: ccc\nmodel: sonnet\neffort: low" };
        Assert.Null(await h.ProcessOneAsync());
        Assert.Equal(0, h.Git.Started);
        Assert.Empty(h.GitHub.Labels);
        Assert.Contains(h.OperationalMessages, message => message.Contains("engine ccc is unavailable", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task InstalledButUnreadyCccLeavesIssueReadyWhileCodexCanRun()
    {
        using var h = new Harness();
        h.GitHub.CancelWhenEmpty = false;
        var worker = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram,
            engineReady: engine => engine == "codex");
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\nengine: ccc\nmodel: sonnet\neffort: low" };
        Assert.Null(await worker.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(0, h.Git.Started);
        Assert.Empty(h.GitHub.Labels);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "Implement task" };
        h.GitHub.ReadyIssueCount = 2;
        Assert.NotNull(await worker.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(1, h.Git.Started);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task SchedulerClaimMessagesCanBeCapturedWithoutUsingProcessConsole()
    {
        using var h = new Harness();

        Assert.NotNull(await h.ProcessOneAsync());

        Assert.Contains(h.OperationalMessages, message => message.StartsWith("Scheduler · Test Project / #17 claimed · execution [", StringComparison.Ordinal));
        Assert.DoesNotContain("Scheduler · Test Project / #17 claimed", h.Output.ToString());
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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
        await Task.WhenAll(second!, replacement!);
        // A claimed execution can still be waiting for the repository gate while
        // the second execution finishes integration.
        Assert.Equal(3, h.Git.Started);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task FirstAttemptConflictIsPreservedWithoutEngineResolutionAndSchedulerContinues()
    {
        using var historyDatabase = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(historyDatabase.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReadyIssueCount = 2;
        h.Git.IntegrationFailure = new GitIntegrationConflictException("rebase conflict was safely aborted") { RebaseAborted = true };
        h.Git.CheckConflictPolicy = true;

        var conflicted = await h.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.IntegrationConflict, conflicted!.Kind);
        Assert.Equal(1, h.Git.ConflictPolicyChecks);
        Assert.Equal(0, h.Codex.ConflictResolutions);
        Assert.Single(h.Codex.Issues);
        Assert.Equal("IntegrationConflict", Assert.Single(await history.ReadAllAsync()).State);
        // A standalone first attempt that only lost the rebase returns to the ready queue once.
        Assert.Contains("working->ready", h.GitHub.Labels);
        Assert.DoesNotContain(h.GitHub.Labels, label => label.EndsWith("->codex-integration-conflict", StringComparison.Ordinal));

        h.Git.IntegrationFailure = null;
        var succeeded = await h.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.Succeeded, succeeded!.Kind);
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ConflictThatIsNotAnAbortedRebaseStaysLabelledForReview()
    {
        using var historyDatabase = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(historyDatabase.Path);
        using var h = new Harness(history: history);
        h.Git.IntegrationFailure = new GitIntegrationConflictException("the integration base kept advancing");

        var conflicted = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.IntegrationConflict, conflicted!.Kind);
        Assert.Contains("working->codex-integration-conflict", h.GitHub.Labels);
        Assert.DoesNotContain("working->ready", h.GitHub.Labels);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("uncertain", false)]
    [InlineData("uncertain", true)]
    [InlineData("missing", false)]
    [InlineData("cleanup-pending", false)]
    public async Task UnrecoverableResumeIsRejectedAndAnotherIssueRunsWithoutStoppingPolling(string? recoveryState, bool issueOverride)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(telegramEnabled: true, history: history);
        h.Worker.Configuration.Worker.RetryMode = issueOverride ? "restart" : "resume";
        if (issueOverride) h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\nretryMode: resume" };
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
        Assert.Equal("resume", rejected.RetryMode);
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExplicitRecoveryLabelStillUsesIntegrationRecoveryPath()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReturnRecoveryIssueOnFirstQuery = true;
        h.Git.CheckConflictPolicy = true;
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
        Assert.Equal(1, h.Git.ConflictPolicyChecks);
        Assert.Equal(0, h.Codex.ConflictResolutions);
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(0, h.Git.Started);
        var recovery = (await history.ReadAllAsync()).Single(entry => entry.ExecutionId != sourceId);
        Assert.Equal(sourceId, recovery.RetryOfExecutionId);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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
}
