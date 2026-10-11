namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;

public sealed class WorkerUpdateTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "worker-update-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static readonly WorkerCapability[] Capabilities = [new("protocol", WorkerUpdateProtocol.Capability), new("tool", "git"),
        new("authentication", "github-api", Scope: "team/project"), new("authentication", "git-repository", Scope: "team/project"), new("agent-provider", "codex")];
    private static Task Beat(SqliteRegistryStore store, string worker, string version = "1.0.0", int active = 0, string state = "running") =>
        store.HeartbeatWorkerAsync(new(2, worker, version, state, active, 2, Capabilities, [], ConfigurationSynchronization: "synchronized"));
    private static async Task<(SqliteRegistryStore Store, string Worker)> Create(string database, Clock clock)
    {
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var worker = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new(2, worker, "Worker", "1.0.0", "test", 2, Capabilities));
        await Beat(store, worker);
        return (store, worker);
    }

    [Fact]
    public async Task InvalidManagedContractCanUpdateAfterDrainingButCannotRestoreSchedulingUntilSynchronized()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        Task InvalidBeat(int active, string version = "1.0.0") => store.HeartbeatWorkerAsync(new(2, worker,
            version, "not-ready", active, 2, Capabilities, [], ConfigurationSynchronization: "error",
            ManagedDiagnostics: new("retrieved", "error", "server-retrieved", null, null,
                "contract-validation", "managed-contract-invalid", [])));
        await InvalidBeat(1);
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal("waiting-for-idle", (await store.RequestWorkerUpdateAsync(worker, id)).State);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvanceWorkerUpdateAsync(worker, id, "1.1.0"));
        await InvalidBeat(0);
        Assert.NotNull(await store.AdvanceWorkerUpdateAsync(worker));
        Assert.Equal("updating", (await store.AdvanceWorkerUpdateAsync(worker, id, "1.1.0"))?.State);
        await store.AdvanceWorkerUpdateAsync(worker, id, verified: true);
        clock.Now += TimeSpan.FromSeconds(1);
        await InvalidBeat(0, "1.1.0");
        Assert.Equal("reconnecting", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        var project = await store.CreateProjectAsync(new("Project", "team/project", "main", ""));
        Assert.False((await store.RequestAssignmentAsync(new(worker, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }))).HasWork);
        await Beat(store, worker, "1.1.0");
        Assert.Equal("succeeded", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal("Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
    }

    [Theory]
    [InlineData("stopped", true)]
    [InlineData("running", false)]
    public async Task UnsafeLifecycleOrMissingUpdateCapabilityRejectsRequest(string lifecycle, bool supported)
    {
        using var temporary = new TemporaryDirectory();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), new Clock());
        await store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", lifecycle, 0, 2,
            supported ? Capabilities : [], [], ConfigurationSynchronization: "error"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N")));
        Assert.Equal("worker-update-unavailable", failure.Message);
        Assert.Null(await store.GetWorkerUpdateAsync(worker));
        Assert.Equal("Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
    }

    [Fact]
    public async Task ActiveWorkWaitsAndAdmissionStopsEvenAfterOperatorEnablesScheduling()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        await Beat(store, worker, active: 1);
        var request = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"));
        Assert.Equal("waiting-for-idle", request.State);
        Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        var project = await store.CreateProjectAsync(new("Project", "team/project", "main", ""));
        await store.SetWorkerSchedulingPolicyAsync(worker, "Enabled");
        var assignment = await store.RequestAssignmentAsync(new(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 }));
        Assert.False(assignment.HasWork);
        Assert.Equal("scheduling-paused", assignment.Reason);
        await Beat(store, worker);
        Assert.NotNull(await store.AdvanceWorkerUpdateAsync(worker));
    }

    [Fact]
    public async Task ServerLeaseMustFinishEvenWhenWorkerReportsZeroActiveExecutions()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        var project = await store.CreateProjectAsync(new("Project", "team/project", "main", ""));
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var assignment = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(
            new(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 }))).Assignment);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        await store.ReportExecutionAsync(assignment.ServerExecutionId, new(worker, assignment.AssignmentId,
            "local-execution", "Running", "Codex", clock.Now, Generation: lease.Generation));
        await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"));
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        await store.ReportExecutionAsync(assignment.ServerExecutionId, new(worker, assignment.AssignmentId,
            "local-execution", "Completed", CompletedAtUtc: clock.Now, Generation: lease.Generation));
        Assert.NotNull(await store.AdvanceWorkerUpdateAsync(worker));
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    [InlineData("Draining")]
    public async Task IdleUpdateReconnectionRestoresOnlyPriorPolicy(string policy)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        await store.SetWorkerSchedulingPolicyAsync(worker, policy);
        var id = Guid.NewGuid().ToString("N");
        var operation = await store.RequestWorkerUpdateAsync(worker, id);
        Assert.Equal(operation.OperationId, (await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"))).OperationId);
        Assert.NotNull(await store.AdvanceWorkerUpdateAsync(worker));
        Assert.Equal("updating", (await store.AdvanceWorkerUpdateAsync(worker, id, "1.1.0"))?.State);
        await store.AdvanceWorkerUpdateAsync(worker, id, verified: true);
        clock.Now += TimeSpan.FromSeconds(1);
        await Beat(store, worker, "1.1.0", state: "not-ready");
        Assert.Equal("reconnecting", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal(policy == "Enabled" ? "Draining" : policy, (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        await Beat(store, worker, "1.1.0");
        Assert.Equal("succeeded", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal(policy, (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Draining")]
    [InlineData("Enabled")]
    public async Task ConcurrentPolicyChangeRevokesTemporaryDrainOwnership(string changedPolicy)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        var operation = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"));
        await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, "1.1.0");
        await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, verified: true);
        await store.SetWorkerSchedulingPolicyAsync(worker, changedPolicy);
        clock.Now += TimeSpan.FromSeconds(1);
        await Beat(store, worker, "1.1.0");
        Assert.Equal("succeeded", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.False((await store.GetWorkerUpdateAsync(worker))?.OwnsDrain);
        Assert.Equal(changedPolicy, (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
    }

    [Fact]
    public async Task LostStartResponseAndServerRestartCannotReplayAndCanReconcileReadiness()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "registry.db");
        var clock = new Clock();
        var (store, worker) = await Create(path, clock);
        var id = Guid.NewGuid().ToString("N");
        await store.RequestWorkerUpdateAsync(worker, id);
        await store.AdvanceWorkerUpdateAsync(worker, id, "1.1.0");
        var restarted = new SqliteRegistryStore(path, timeProvider: clock);
        await restarted.InitializeAsync();
        Assert.Equal("updating", (await restarted.RequestWorkerUpdateAsync(worker, id)).State);
        Assert.Equal("updating", (await restarted.AdvanceWorkerUpdateAsync(worker))?.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AdvanceWorkerUpdateAsync(worker, id, "1.1.0"));
        await restarted.AdvanceWorkerUpdateAsync(worker, id, verified: true);
        clock.Now += TimeSpan.FromSeconds(1);
        await Beat(restarted, worker, "1.1.0");
        Assert.Equal("succeeded", (await restarted.GetWorkerUpdateAsync(worker))?.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrTimeoutRemainsBlockedAndDuplicatesReturnRetainedOutcome(bool timeout)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        var id = Guid.NewGuid().ToString("N");
        await store.RequestWorkerUpdateAsync(worker, id);
        await store.AdvanceWorkerUpdateAsync(worker, id, "1.1.0");
        if (timeout) clock.Now += TimeSpan.FromMinutes(21);
        else await store.AdvanceWorkerUpdateAsync(worker, id, fail: true);
        Assert.Equal("failed", (await store.GetWorkerUpdateAsync(worker))?.State);
        clock.Now += TimeSpan.FromSeconds(1);
        await Beat(store, worker, "1.1.0");
        Assert.Equal("failed", (await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"))).State);
        Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
    }

    [Fact]
    public async Task ReconnectRequiresInstallerResultAndExplicitRecoveryNeverDispatchesAgain()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        var id = Guid.NewGuid().ToString("N");
        await store.RequestWorkerUpdateAsync(worker, id);
        await store.AdvanceWorkerUpdateAsync(worker, id, "1.1.0");
        clock.Now += TimeSpan.FromSeconds(1);
        await Beat(store, worker, "1.1.0");
        Assert.Equal("reconnecting", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        await store.AdvanceWorkerUpdateAsync(worker, id, fail: true);
        Assert.Equal("failed", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal("succeeded", (await store.ReconcileWorkerUpdateAsync(worker))?.State);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        Assert.StartsWith("reconciled:", (await store.GetWorkerUpdateAsync(worker))?.Diagnostic);
    }

    [Fact]
    public async Task AlreadyCurrentTemporaryLocalDrainWaitsForReadyBeforeRestoringPolicy()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        var id = Guid.NewGuid().ToString("N");
        await store.RequestWorkerUpdateAsync(worker, id);
        await Beat(store, worker, state: "drained");
        var result = await store.AdvanceWorkerUpdateAsync(worker, id, "1.0.0");
        Assert.Equal("reconnecting", result?.State);
        Assert.True(result?.InstallerVerified);
        Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        clock.Now += TimeSpan.FromSeconds(1);
        await Beat(store, worker);
        Assert.Equal("succeeded", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal("Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
    }

    [Fact]
    public async Task AlreadyCurrentDoesNotInstallAndStaleWorkersAreUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        var id = Guid.NewGuid().ToString("N");
        await store.RequestWorkerUpdateAsync(worker, id);
        Assert.Equal("succeeded", (await store.AdvanceWorkerUpdateAsync(worker, id, "1.0.0"))?.State);
        Assert.Equal("Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        clock.Now += TimeSpan.FromMinutes(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N")));
        // A retry of the original operation retains its outcome while offline.
        Assert.Equal("succeeded", (await store.RequestWorkerUpdateAsync(worker, id)).State);
    }
}
