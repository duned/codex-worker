using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExhaustedAutomaticRecoveryRemainsPreservedAcrossPollsAndRestartAndRearmsOnChangedBase()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        await history.CreateAsync(source);
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(false, "codex")]
    [InlineData(true, "codex")]
    [InlineData(false, "ccc")]
    [InlineData(true, "ccc")]
    public async Task RestartReconcilesCleanClaimButPreservesUncertainWorkspace(bool unsafeWorkspace, string engine)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var source = PreservedConflict() with { Engine = engine };
        await history.CreateAsync(source);
        var attempt = source with { ExecutionId = Guid.NewGuid(), State = "Integrating", CompletedAtUtc = null,
            RecoveryState = null, RetryOfExecutionId = source.ExecutionId, AttemptNumber = 2 };
        Assert.True(await history.TryClaimIntegrationRecoveryAsync(source, attempt, "current-main", false));
        using var restarted = new Harness(history: history);
        restarted.Git.RecoveryRejection = unsafeWorkspace ? "unfinished Git operation" : null;
        await restarted.Worker.ReconcileIntegrationRecoveryAsync(CancellationToken.None);
        var rows = await history.ReadAllAsync();
        Assert.All(rows, entry => Assert.Equal(engine, entry.Engine));
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ManagedPreservedCodexAssignmentResumesInterruptedPostRepairValidation()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var sourceId = Guid.NewGuid();
        var sourceServerId = "original-server-execution";
        var now = DateTimeOffset.UtcNow;
        var source = PreservedConflict() with
        {
            ExecutionId = sourceId,
            State = "Cancelled",
            CompletedAtUtc = now,
            RecoveryState = "codex-interrupted",
            ValidationOutcome = "pending: validation",
            RepairCount = 1,
            Repairs = [new("authoritative-check", 1, 3, "Repair succeeded", false, "failed check")],
            ServerExecutionId = sourceServerId,
            OriginalIssueBody = h.GitHub.Issue.Body,
            CodexRecovery = new(sourceId, 1, 0, CodexInterruptionRecovery.Fingerprint(h.Worker.Configuration),
                "session-id", ImplementationSucceeded: true)
        };
        await history.CreateAsync(source);
        var assignment = new WorkerAssignmentContract("recovery-assignment", "recovery-server-execution",
            new("project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new("github-issue", source.IssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), "worker",
            new Dictionary<string, string>
            {
                ["codexRecoveryExecutionId"] = sourceId.ToString(),
                ["originalServerExecutionId"] = sourceServerId
            },
            new("recovery-server-execution", "worker", 4, now, now.AddMinutes(5), "Active"));

        var task = await h.Worker.ClaimAssignedAsync(assignment, CancellationToken.None);

        Assert.NotNull(task);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await task)?.Kind);
        Assert.Equal(1, h.Git.CodexResumes);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(1, h.Validation.Calls); // The interrupted validation is rerun before integration.
        Assert.Empty(h.Codex.Issues); // Implementation and the consumed repair are not rerun.
        var attempt = Assert.Single(await history.ReadAllAsync(), entry => entry.ExecutionId != sourceId);
        Assert.Equal(2, attempt.AttemptNumber);
        Assert.Equal(1, attempt.RepairCount);
        Assert.Equal(1, attempt.CodexRecovery?.ResumeCount);
        Assert.Equal("recovery-server-execution", attempt.ServerExecutionId);
        Assert.Equal("recovery-assignment", attempt.AssignmentId);
        Assert.Equal(4, attempt.OwnershipGeneration);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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
}
