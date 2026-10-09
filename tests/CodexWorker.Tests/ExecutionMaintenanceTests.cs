using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ExecutionMaintenanceTests
{
    [Fact]
    public async Task ArchiveReceiptSurvivesRestartAndRepeatedApplyWithoutLosingLineage()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "history.db");
        var started = DateTimeOffset.UtcNow.AddDays(-40);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "p", "o/r", 1, "issue", "feature/1", "main",
            started, started.AddMinutes(1), "Failed", null, "retained", null, 0, [], null, null, null, null, "operator-cleaned");
        var proof = new ExecutionCleanupInspection(entry.ExecutionId, "safe", "already-clean", "No resources remain.");
        using (var store = new ExecutionHistoryStore(path))
        {
            await store.CreateAsync(entry);
            await store.ArchiveAsync(entry, proof, CancellationToken.None);
            var audit = await store.ReadArchiveAuditAsync(entry.ExecutionId);
            await store.ArchiveAsync(entry, proof with { Message = "changed" }, CancellationToken.None);
            Assert.Equal(audit, await store.ReadArchiveAuditAsync(entry.ExecutionId));
            Assert.Empty(await store.ReadRecentAsync(20));
            Assert.Empty(await store.ReadInventoryAsync(new(), DateTimeOffset.UtcNow));
        }
        using var reopened = new ExecutionHistoryStore(path);
        Assert.NotNull(await reopened.ReadArchiveAuditAsync(entry.ExecutionId));
        Assert.Equal(entry.ImplementationSummary, (await reopened.ReadExecutionAsync(entry.ExecutionId))?.ImplementationSummary);
        Assert.Single(await reopened.ReadInventoryAsync(new(IncludeArchived: true), DateTimeOffset.UtcNow));
        Assert.Single(await reopened.ReadAllAsync());
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("cleanup")]
    [InlineData("purge")]
    public async Task ManagedMaintenanceRequiresServerAuthorityEvenWithOnlyStaleGeneration(string action)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "history.db");
        using var store = new ExecutionHistoryStore(path);
        var started = DateTimeOffset.UtcNow.AddDays(-40);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "p", "o/r", 1, "issue", "feature/1", "main",
            started, started.AddMinutes(1), "Completed", null, null, null, 0, [], null, null, null, null,
            "operator-cleaned", OwnershipGeneration: 1);
        await store.CreateAsync(entry);
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "p", Repository = "o/r" } };
        var registry = new ProjectRuntimeRegistry([("p.yml", config)]);
        registry.DrainWorker();
        var service = new ExecutionCleanupService(store, registry);
        var result = Assert.Single(await service.RunAsync(new(ExecutionId: entry.ExecutionId, Apply: true, Action: action), CancellationToken.None));
        Assert.Equal("server-authority-required", result.Inspection.ReasonCode);
        Assert.Null(await store.ReadArchiveAuditAsync(entry.ExecutionId));
        Assert.NotNull(await store.ReadExecutionAsync(entry.ExecutionId));
    }

    [Fact]
    public async Task InventoryFiltersSameIssueByProjectAndPaginatesWithinBound()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "history.db");
        using var store = new ExecutionHistoryStore(path);
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ExecutionHistoryEntry Entry(string project, int minute) => new(Guid.NewGuid(), project, $"owner/{project}", 42,
            "issue", $"feature/{project}", "main", started.AddMinutes(minute), started.AddMinutes(minute + 1),
            "Failed", 60000, null, null, 0, [], null, null, null, null);
        await store.CreateAsync(Entry("one", 1));
        await store.CreateAsync(Entry("two", 2));
        await store.CreateAsync(Entry("one", 3));

        var first = await store.ReadInventoryAsync(new ExecutionInventoryQuery(Project: "one", IssueNumber: 42, Limit: 1), started.AddDays(1));
        var second = await store.ReadInventoryAsync(new ExecutionInventoryQuery(Project: "one", IssueNumber: 42, Limit: 1, Offset: 1), started.AddDays(1));

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal("one", first[0].Project);
        Assert.NotEqual(first[0].ExecutionId, second[0].ExecutionId);
    }

    [Fact]
    public void OldAgeNeverMakesUncertainManagedExecutionSafeToClean()
    {
        var started = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "p", "o/r", 7, "issue", "feature/7", "main",
            started, started.AddMinutes(1), "InfrastructureFailure", null, null, null, 0, [], null, null, null, null,
            "uncertain", ServerExecutionId: "server-1", AssignmentId: "assignment-1", ReportingFailure: "offline");

        var result = ExecutionMaintenanceClassifier.Classify(entry, started.AddDays(100), projectConfigured: true);

        Assert.Equal("reconciliation-required", result.Status);
        Assert.Equal("managed", result.Authority);
        Assert.Equal("managed-report-unconfirmed", result.ReasonCode);
        Assert.DoesNotContain("cleanup", result.Actions);
    }

    [Fact]
    public void ActiveOldAndMissingProjectAreExplicitlyClassified()
    {
        var started = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "gone", "o/r", 7, "issue", "feature/7", "main",
            started, null, "Executing", null, null, null, 0, [], null, null, null, null);

        Assert.Equal("orphaned", ExecutionMaintenanceClassifier.Classify(entry, started.AddDays(1), false).Status);
        Assert.Equal("stale", ExecutionMaintenanceClassifier.Classify(entry, started.AddDays(8), true).Status);
    }
    [Theory]
    [InlineData("recoverable", "recoverable", "workspace-recoverable")]
    [InlineData("preparation-failed", "retained-review", "preparation-resource-proof-missing")]
    [InlineData("github-reconciliation-required", "reconciliation-required", "github-report-unconfirmed")]
    [InlineData("github-reconciled", "retained-review", "github-resource-proof-missing")]
    [InlineData("codex-recovered", "retained-review", "recovery-transfer-unverified")]
    [InlineData("codex-recovery-finished", "retained-review", "recovery-transfer-unverified")]
    [InlineData("cleanup-pending", "recoverable", "cleanup-pending")]
    [InlineData("uncertain", "retained-review", "recovery-ambiguous")]
    [InlineData("resumed-cleaned", "terminal-clean", "cleanup-recorded")]
    [InlineData("cleaned-no-changes", "terminal-clean", "cleanup-recorded")]
    [InlineData("new-unknown-state", "retained-review", "recovery-state-unknown")]
    [InlineData(null, "retained-review", "terminal-resource-proof-missing")]
    public void FailedAttemptsNeverInferSafetyFromRecency(string? recovery, string status, string reason)
    {
        var now = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "p", "o/r", 328, "issue", "feature/328", "main",
            now.AddMinutes(-2), now.AddMinutes(-1), "Failed", null, null, null, 0, [], null, null, null, null,
            recovery, RecoveryStatus: "15 modified / 12 staged", RecoveryExpiresAtUtc: now.AddDays(7));
        foreach (var age in new[] { 0, 100 })
        {
            var assessment = ExecutionMaintenanceClassifier.Classify(entry, now.AddDays(age), true);
            Assert.Equal(status, assessment.Status);
            Assert.Equal(reason, assessment.ReasonCode);
            Assert.Equal("failed", ExecutionMaintenanceClassifier.Outcome(entry.State));
            Assert.Equal(new[] { "inspect" }, assessment.Actions);
        }
    }

    [Fact]
    public async Task InventoryOutcomeCaseAndAttentionPagingPreserveBusinessFailure()
    {
        using var history = new ExecutionHistoryStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "history.db"));
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 3; index++)
            await history.CreateAsync(new(Guid.NewGuid(), "p", "o/r", 328, "issue", "feature/328", "main",
                now.AddDays(-40).AddMinutes(index), now.AddDays(-40).AddMinutes(index + 1), "Failed",
                null, null, null, 0, [], null, null, null, null, index == 0 ? "operator-cleaned" : "recoverable"));
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "p", Repository = "o/r" } };
        var model = new WorkerRuntimeReadModel(new GlobalWorkerConfiguration(), [("p.yml", config)], history);
        var query = new ExecutionInventoryQuery(Outcome: "Failed", OlderThanDays: 30, Attention: "recoverable", Limit: 1);
        var first = await model.ExecutionInventoryAsync(query, CancellationToken.None);
        var second = await model.ExecutionInventoryAsync(query with { Offset = 1 }, CancellationToken.None);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.NotEqual(Assert.Single(first.Items).Execution.ExecutionId, Assert.Single(second.Items).Execution.ExecutionId);
        var invalid = await Assert.ThrowsAsync<ArgumentException>(() => model.ExecutionInventoryAsync(query with { Outcome = "unknown" }, CancellationToken.None));
        Assert.Contains("Allowed values: succeeded", invalid.Message);
    }

    [Theory]
    [InlineData("github-reconciled")]
    [InlineData("operator-cleaned")]
    public void ReportingFailureOverridesReconciliationAndCleanupReceipts(string recovery)
    {
        var now = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "p", "o/r", 282, "issue", "feature/282", "main",
            now.AddMinutes(-2), now.AddMinutes(-1), "InfrastructureFailure", null, null, null, 0, [], null, null, null, null,
            recovery, ReportingFailure: "delivery failed");
        Assert.Equal("github-report-unconfirmed", ExecutionMaintenanceClassifier.Classify(entry, now, true).ReasonCode);
        Assert.Equal("infrastructure-failure", ExecutionMaintenanceClassifier.Outcome(entry.State));
        Assert.Equal("completion-pending", ExecutionMaintenanceClassifier.Classify(
            entry with { ReportingFailure = null, CompletionJson = "{}" }, now, true).ReasonCode);
    }

    [Fact]
    public void ActiveAndSuccessfulCompletionRemainDistinctFromCleanedFailures()
    {
        var now = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "p", "o/r", 1, "issue", "feature/1", "main",
            now.AddMinutes(-2), null, "Implementing", null, null, null, 0, [], null, null, null, null);
        Assert.Equal("healthy-active", ExecutionMaintenanceClassifier.Classify(entry, now, true).Status);
        Assert.Equal("healthy-terminal", ExecutionMaintenanceClassifier.Classify(
            entry with { State = "Completed", CompletedAtUtc = now, RecoveryState = "completion-reconciled" }, now.AddDays(100), true).Status);
    }

}
