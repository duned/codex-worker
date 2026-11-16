using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CodexFailureDuringRequestedShutdownDoesNotBecomeWorkerFailure()
    {
        await WorkerHost.AwaitShutdownExecutionsAsync([
            Task.FromException<IssueProcessingResult?>(new CodexExecutionInfrastructureException("Codex usage limit", "Execution preserved") { Recoverable = true })]);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
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
        Assert.Contains(reason, preserved.FailureReason, StringComparison.Ordinal);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(false, "context_length_exceeded", true)]
    [InlineData(true, "context_length_exceeded", true)]
    [InlineData(false, "usage_limit_reached", false)]
    [InlineData(true, "usage_limit_reached", false)]
    public async Task CodexLimitsContinuePreservedWorkWithBoundedAttempts(bool managed, string evidence, bool freshSession)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var clock = new RecoveryClock();
        using var h = new Harness(history: history, timeProvider: clock, telegramEnabled: true);
        h.GitHub.CancelWhenEmpty = false;
        h.Git.Snapshot = new("feature/example-task-17", "base-sha", "preserved changes");
        var failure = CodexFailure.Classify(evidence);
        h.Codex.InitialException = new CodexExecutionInfrastructureException(failure.Category, failure.Summary)
            { Recoverable = failure.Recoverable, FailureSummary = failure.Summary };
        if (managed)
        {
            var now = clock.GetUtcNow();
            var initial = await h.Worker.ClaimAssignedAsync(new WorkerAssignmentContract("original-assignment", "original-server",
                new("project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
                new("github-issue", "17"), "worker", new Dictionary<string, string>(),
                new("original-server", "worker", 1, now, now.AddMinutes(5), "Active")), CancellationToken.None);
            Assert.NotNull(initial);
            await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(async () => await initial);
        }
        else await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => h.ProcessOneAsync());
        var original = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("codex-interrupted", original.RecoveryState);
        Assert.Equal(freshSession, CodexFailure.RequiresFreshSession(original));
        Assert.Contains(failure.Category, h.GitHub.Comments[^1], StringComparison.Ordinal);
        Assert.Contains(h.TelegramMessages, message => message.Contains(failure.Category, StringComparison.Ordinal) &&
            message.Contains("after execution readiness and cooldown", StringComparison.Ordinal));
        h.GitHub.Issue = h.GitHub.Issue with { Labels = ["ready"] };
        for (var attempt = 1; attempt <= CodexInterruptionRecovery.MaximumResumes; attempt++)
        {
            // Frequent scheduler polling cannot spend a resume during cooldown.
            for (var poll = 0; poll < 3; poll++) Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
            clock.Advance(TimeSpan.FromMinutes(5));
            Task<IssueProcessingResult?>? task;
            if (managed)
            {
                Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
                var source = (await history.ReadAllAsync()).MaxBy(entry => entry.AttemptNumber)
                    ?? throw new InvalidOperationException("Missing recovery source");
                var now = clock.GetUtcNow();
                var serverId = "server-" + attempt;
                task = await h.Worker.ClaimAssignedAsync(new WorkerAssignmentContract("assignment-" + attempt, serverId,
                    new("project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
                    new("github-issue", "17"), "worker", new Dictionary<string, string>
                    {
                        ["codexRecoveryExecutionId"] = source.ExecutionId.ToString(),
                        ["originalServerExecutionId"] = source.ServerExecutionId ?? ""
                    }, new(serverId, "worker", attempt + 1, now, now.AddMinutes(5), "Active")), CancellationToken.None);
            }
            else task = await h.Worker.ClaimNextAsync(CancellationToken.None);
            Assert.NotNull(task);
            await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(async () => await task);
        }
        var exhausted = (await history.ReadAllAsync()).MaxBy(entry => entry.AttemptNumber)
            ?? throw new InvalidOperationException("Missing exhausted attempt");
        Assert.Equal("codex-recovery-exhausted", exhausted.RecoveryState);
        Assert.Contains("budget exhausted", exhausted.FailureReason, StringComparison.Ordinal);
        Assert.Contains("budget exhausted", h.GitHub.Comments[^1], StringComparison.Ordinal);
        Assert.Contains(h.TelegramMessages, message => message.Contains("budget exhausted", StringComparison.Ordinal) &&
            message.Contains("ready label does not reset", StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await h.Worker.ClaimNextAsync(CancellationToken.None));
        Assert.Equal(1, h.Git.Started);
        Assert.Equal(CodexInterruptionRecovery.MaximumResumes, h.Git.CodexResumes);
        Assert.Equal(0, h.Git.Cleanups);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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
        h.GitHub.Issue = h.GitHub.Issue with { Labels = ["ready"] };
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
}
