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
}
