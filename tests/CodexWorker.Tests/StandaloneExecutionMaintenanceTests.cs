namespace CodexWorker.Tests;

public sealed class StandaloneExecutionMaintenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedPassRefusesManagedAndOrphanRowsAndRestoresDrain(bool apply)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var history = new ExecutionHistoryStore(Path.Combine(root, "history.db"));
            var started = DateTimeOffset.UtcNow.AddDays(-40);
            ExecutionHistoryEntry Entry(int issue) => new(Guid.NewGuid(), "missing", "o/r", issue, "intent", $"feature/{issue}", "main",
                started.AddMinutes(issue), started.AddMinutes(issue + 1), "InfrastructureFailure", null, null, null, 0, [], null, null, null, "retained", "uncertain");
            var managed = Entry(1) with { OwnershipGeneration = 4 };
            var orphan = Entry(2);
            await history.CreateAsync(managed);
            await history.CreateAsync(orphan);
            await history.CreateAsync(Entry(3));
            var registry = new ProjectRuntimeRegistry([]);
            var service = new StandaloneExecutionMaintenance(history, registry, new(history, registry), false);
            for (var pass = 0; pass < 2; pass++)
            {
                var result = await service.RunAsync(new(apply, Limit: 2), CancellationToken.None);
                Assert.Equal(2, result.Scanned);
                Assert.Equal(2, result.NeedsReview);
                Assert.Equal(0, result.ResourcesCleaned);
                Assert.Equal(0, result.Archived);
                Assert.Equal("server-authority-required", result.Items[0].Inspection.ReasonCode);
                Assert.Equal("project-unavailable", result.Items[1].Inspection.ReasonCode);
                Assert.False(registry.WorkerDraining);
            }
            Assert.Equal("uncertain", (await history.ReadExecutionAsync(managed.ExecutionId))?.RecoveryState);
            Assert.Equal("uncertain", (await history.ReadExecutionAsync(orphan.ExecutionId))?.RecoveryState);
            Assert.Null(await history.ReadArchiveAuditAsync(managed.ExecutionId));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationWhileWaitingRestoresSchedulingWithoutInterruptingActiveExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var history = new ExecutionHistoryStore(Path.Combine(root, "history.db"));
            var config = new WorkerConfiguration { Project = new() { Name = "p", Repository = "o/r" } };
            var registry = new ProjectRuntimeRegistry([("p.yml", config)]);
            Assert.True(registry.TryReserve("p"));
            var service = new StandaloneExecutionMaintenance(history, registry, new(history, registry), false);
            using var cancellation = new CancellationTokenSource();
            var pending = service.RunAsync(new(Apply: true), cancellation.Token);
            Assert.True(registry.WorkerDraining);
            Assert.False(registry.WorkerDrainComplete);
            cancellation.Cancel();
            var result = await pending;
            Assert.True(result.Interrupted);
            Assert.False(registry.WorkerDraining);
            Assert.Equal(1, registry.Lifecycle.Snapshot.ActiveExecutions);
            registry.Release("p");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManagedModeIsRejectedBeforeDrainOrHistoryMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var history = new ExecutionHistoryStore(Path.Combine(root, "history.db"));
            var registry = new ProjectRuntimeRegistry([]);
            var service = new StandaloneExecutionMaintenance(history, registry, new(history, registry), true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new(Apply: true), CancellationToken.None));
            Assert.False(registry.WorkerDraining);
        }
        finally { Directory.Delete(root, true); }
    }
}
