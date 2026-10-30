namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;

[Collection("ServerTokenEnvironment")]
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

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void HeartbeatCannotDefaultAnUnknownExecutionCountToZero()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<WorkerHeartbeatRequest>(
            """{"ContractVersion":2,"WorkerId":"worker","WorkerVersion":"1.0.0","LifecycleState":"stopped","MaximumCapacity":2,"Capabilities":[],"ActiveProjects":[]}"""));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("safe", null)]
    [InlineData("stopped", null)]
    [InlineData("timeout", null)]
    [InlineData("policy-changed", null)]
    [InlineData("stale", "fresh-heartbeat")]
    [InlineData("executions", "no-active-executions")]
    [InlineData("unknown", "no-active-executions")]
    [InlineData("missing", "fresh-heartbeat")]
    [InlineData("assignment", "no-active-assignments")]
    [InlineData("lease", "no-active-leases")]
    [InlineData("pre-operation", "post-operation-heartbeat")]
    public async Task FailedRestartDrainRecoveryPreservesFailureAndRequiresFreshQuiescence(string scenario, string? blocker)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        WorkerCapability[] capabilities = [.. Capabilities, new("protocol", WorkerUpdateProtocol.RestartCapability)];
        Task Heartbeat(string state, int active = 0) => store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", state,
            active, 2, capabilities, [], ConfigurationSynchronization: "synchronized", InstanceId: "original"));
        await Heartbeat("running");
        var project = await store.CreateProjectAsync(new("Project", "team/project", "main", ""));
        var capacities = new Dictionary<string, int> { [project.Id] = 1 };
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var assignment = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(new(worker, true, 1, capacities))).Assignment);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        if (scenario is not ("assignment" or "lease"))
            await store.ReportExecutionAsync(assignment.ServerExecutionId, new(worker, assignment.AssignmentId,
                "local-execution", "Completed", CompletedAtUtc: clock.Now, Generation: lease.Generation));
        // Execution completion precedes the dashboard restart; no completion is replayed by recovery.
        await Heartbeat("running");
        var operation = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"), restart: true);
        if (scenario is not ("assignment" or "lease"))
            await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, "1.0.0");
        if (scenario == "timeout") clock.Now += TimeSpan.FromMinutes(21);
        else await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, fail: true);
        if (scenario == "policy-changed") await store.SetWorkerSchedulingPolicyAsync(worker, "Disabled");
        if (scenario != "pre-operation")
        {
            clock.Now += TimeSpan.FromSeconds(1);
            await Heartbeat(scenario == "stopped" ? "stopped" : "draining", scenario == "executions" ? 1 : scenario == "unknown" ? -1 : 0);
        }
        if (scenario == "missing")
            await store.RegisterWorkerAsync(new(2, worker, "Worker", "1.0.0", "test", 2, capabilities));
        if (scenario == "stale") clock.Now += TimeSpan.FromHours(1);
        if (blocker is not null)
        {
            var error = await Assert.ThrowsAsync<WorkerUpdateRecoveryException>(() => store.ReleaseWorkerRestartDrainAsync(worker, operation.OperationId));
            Assert.Contains(error.Recovery.Checks, check => check.Code == blocker && !check.Satisfied);
            Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
            return;
        }
        var failed = Assert.IsType<WorkerUpdateOperation>(await store.GetWorkerUpdateAsync(worker));
        var released = Assert.IsType<WorkerUpdateOperation>(await store.ReleaseWorkerRestartDrainAsync(worker, operation.OperationId));
        Assert.Equal("failed", released.State);
        Assert.Equal(failed.Diagnostic, released.Diagnostic);
        Assert.NotNull(released.DrainReleasedAtUtc);
        var repeated = Assert.IsType<WorkerUpdateOperation>(await store.ReleaseWorkerRestartDrainAsync(worker, operation.OperationId));
        Assert.Equal(released.OperationId, repeated.OperationId);
        Assert.Equal(released.DrainReleasedAtUtc, repeated.DrainReleasedAtUtc);
        Assert.Equal(released.State, repeated.State);
        Assert.Equal(released.Diagnostic, repeated.Diagnostic);
        Assert.Equal(scenario == "policy-changed" ? "Disabled" : "Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        Assert.Null((await store.GetWorkerAsync(worker))?.SchedulingBlocker);
        Assert.NotEqual("running", (await store.GetWorkerAsync(worker))?.LifecycleState);
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "2")));
        Assert.False((await store.RequestAssignmentAsync(new(worker, true, 1, capacities))).HasWork);
        var other = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new(2, other, "Other", "1.0.0", "test", 2, capabilities));
        await Beat(store, other);
        Assert.True((await store.RequestAssignmentAsync(new(other, true, 1, capacities))).HasWork);
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "3")));
        if (scenario == "policy-changed") await store.SetWorkerSchedulingPolicyAsync(worker, "Enabled");
        clock.Now += TimeSpan.FromSeconds(1);
        await Heartbeat("running");
        Assert.True((await store.RequestAssignmentAsync(new(worker, true, 1, capacities))).HasWork);
        Assert.Equal("Completed", (await store.GetExecutionAsync(assignment.ServerExecutionId))?.State);
        Assert.Equal("failed", (await store.GetWorkerUpdateAsync(worker))?.State);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true, "Enabled")]
    [InlineData(true, "Disabled")]
    [InlineData(false, "Enabled")]
    [InlineData(false, "Draining")]
    public async Task VersionMismatchRecoveryRestoresPolicyAndQueueWithoutReplayingWork(bool restart, string policy)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        WorkerCapability[] capabilities = [.. Capabilities, new("protocol", WorkerUpdateProtocol.RestartCapability)];
        Task Heartbeat(string version) => store.HeartbeatWorkerAsync(new(2, worker, version, "running", 0, 2,
            capabilities, [], ConfigurationSynchronization: "synchronized", InstanceId: "same-instance"));
        await Heartbeat("1.0.0");
        await store.SetWorkerSchedulingPolicyAsync(worker, policy);
        var project = await store.CreateProjectAsync(new("Project", "team/project", "main", ""));
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var original = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"), restart: restart);
        await store.AdvanceWorkerUpdateAsync(worker, original.OperationId, restart ? "1.0.0" : "1.1.0");
        await store.AdvanceWorkerUpdateAsync(worker, original.OperationId, fail: true);
        clock.Now += TimeSpan.FromSeconds(1);
        await Heartbeat(restart ? "1.1.0" : "1.0.0");
        var status = Assert.IsType<WorkerUpdateOperation>(await store.InspectWorkerUpdateAsync(worker));
        Assert.Equal(restart ? "1.1.0" : "1.0.0", status.Recovery?.ReportedVersion);
        Assert.Equal("1.0.0", status.OriginalVersion);
        var capacities = new Dictionary<string, int> { [project.Id] = 1 };
        Assert.False((await store.RequestAssignmentAsync(new(worker, true, 1, capacities))).HasWork);
        if (restart)
        {
            Assert.True(status.Recovery?.Eligible);
            Assert.False(status.Recovery?.RetryEligible);
            Assert.Equal("succeeded", (await store.ReconcileWorkerUpdateAsync(worker))?.State);
            Assert.Equal("succeeded", (await store.ReconcileWorkerUpdateAsync(worker))?.State);
            // Preserve the original expectation as audit evidence; reconciliation dispatches nothing.
            Assert.Equal("1.0.0", (await store.GetWorkerUpdateAsync(worker))?.ExpectedVersion);
        }
        else
        {
            Assert.False(status.Recovery?.Eligible);
            Assert.True(status.Recovery?.RetryEligible);
            await Assert.ThrowsAsync<WorkerUpdateRecoveryException>(() => store.ReconcileWorkerUpdateAsync(worker));
            var retryId = Guid.NewGuid().ToString("N");
            var retry = Assert.IsType<WorkerUpdateOperation>(await store.RetryWorkerUpdateAsync(worker, retryId));
            Assert.Equal("waiting-for-idle", retry.State);
            Assert.Equal("1.1.0", retry.ExpectedVersion);
            Assert.Equal(original.OperationId, retry.RetryOfOperationId);
            Assert.Equal(retryId, (await store.RetryWorkerUpdateAsync(worker, retryId))?.OperationId);
            await Assert.ThrowsAsync<WorkerUpdateRecoveryException>(() => store.RetryWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N")));
            // Late reports from the failed operation cannot fail the retry or authorize another target.
            await store.AdvanceWorkerUpdateAsync(worker, original.OperationId, fail: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AdvanceWorkerUpdateAsync(worker, retryId, "1.2.0"));
            Assert.Equal("updating", (await store.AdvanceWorkerUpdateAsync(worker, retryId, "1.1.0"))?.State);
            await store.AdvanceWorkerUpdateAsync(worker, retryId, verified: true);
            clock.Now += TimeSpan.FromSeconds(1);
            await Heartbeat("1.1.0");
            Assert.Equal("succeeded", (await store.GetWorkerUpdateAsync(worker))?.State);
        }
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        Assert.Null((await store.GetWorkerAsync(worker))?.SchedulingBlocker);
        Assert.Equal(policy, (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        Assert.Equal(policy == "Enabled", (await store.RequestAssignmentAsync(new(worker, true, 1, capacities))).HasWork);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true, "stale")]
    [InlineData(true, "protocol")]
    [InlineData(true, "capabilities")]
    [InlineData(true, "configuration")]
    [InlineData(true, "executions")]
    [InlineData(false, "stale")]
    [InlineData(false, "protocol")]
    [InlineData(false, "capabilities")]
    [InlineData(false, "configuration")]
    [InlineData(false, "executions")]
    [InlineData(false, "newer-version")]
    public async Task VersionMismatchRecoveryRejectsUnsafeCurrentState(bool restart, string blocker)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        WorkerCapability[] capabilities = [.. Capabilities, new("protocol", WorkerUpdateProtocol.RestartCapability)];
        await store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", "running", 0, 2, capabilities, [],
            ConfigurationSynchronization: "synchronized", InstanceId: "original"));
        var operation = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"), restart: restart);
        await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, restart ? "1.0.0" : "1.1.0");
        await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, fail: true);
        clock.Now += TimeSpan.FromSeconds(1);
        await store.HeartbeatWorkerAsync(new(blocker == "protocol" ? 99 : 2, worker,
            restart || blocker == "newer-version" ? "1.2.0" : "1.0.0", "running", blocker == "executions" ? 1 : 0,
            2, blocker == "capabilities" ? [] : capabilities, [],
            ConfigurationSynchronization: blocker == "configuration" ? "error" : "synchronized", InstanceId: "original"));
        if (blocker == "stale") clock.Now += TimeSpan.FromMinutes(5);
        var status = Assert.IsType<WorkerUpdateOperation>(await store.InspectWorkerUpdateAsync(worker));
        Assert.False(status.Recovery?.Eligible);
        Assert.False(status.Recovery?.RetryEligible);
        await Assert.ThrowsAsync<WorkerUpdateRecoveryException>(() => store.ReconcileWorkerUpdateAsync(worker));
        await Assert.ThrowsAsync<WorkerUpdateRecoveryException>(() => store.RetryWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N")));
        Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RecoveryApiReturnsActionableConflictAndRechecksBeforeRestoringPolicy()
    {
        using var temporary = new TemporaryDirectory();
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "update-recovery-test-management");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync([
                $"--Server:ListenUrl={url}", $"--Server:DatabasePath={Path.Combine(temporary.Path, "server.db")}",
                "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var worker = Guid.NewGuid().ToString("N");
            await store.RegisterWorkerAsync(new(2, worker, "Worker", "1.0.0", "test", 2, Capabilities));
            await store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", "running", 0, 2, Capabilities, [], ConfigurationSynchronization: "error"));
            var operation = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"));
            await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, fail: true);
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var path = $"/api/v1/workers/{worker}/update";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{path}/recovery")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "update-recovery-test-management");
            var releasePath = $"{path}/release-drain";
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{releasePath}?nodeQuiescent=false", new WorkerUpdateRequest(operation.OperationId))).StatusCode);
            // A failed update must never be abandoned through the restart-only release action.
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{releasePath}?nodeQuiescent=true", new WorkerUpdateRequest(operation.OperationId))).StatusCode);

            var status = await client.GetFromJsonAsync<WorkerUpdateOperation>($"{path}/recovery");
            Assert.False(status?.Recovery?.Eligible);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{path}/reconcile?nodeQuiescent=false", null)).StatusCode);
            var rejected = await client.PostAsync($"{path}/reconcile?nodeQuiescent=true", null);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            using var payload = System.Text.Json.JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal("worker-update-recovery-readiness-required", payload.RootElement.GetProperty("reason").GetString());
            var checks = payload.RootElement.GetProperty("recovery").GetProperty("checks").EnumerateArray().ToArray();
            Assert.Contains(checks, check => check.GetProperty("code").GetString() == "configuration-synchronized" && !check.GetProperty("satisfied").GetBoolean());
            Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
            await store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", "running", 0, 2, Capabilities, [], ConfigurationSynchronization: "synchronized"));
            var recovered = await client.PostAsync($"{path}/reconcile?nodeQuiescent=true", null);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal("succeeded", (await recovered.Content.ReadFromJsonAsync<WorkerUpdateOperation>())?.State);
            Assert.Equal("Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
            Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
            var failedUpdate = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"));
            await store.AdvanceWorkerUpdateAsync(worker, failedUpdate.OperationId, "1.1.0");
            await store.AdvanceWorkerUpdateAsync(worker, failedUpdate.OperationId, fail: true);
            await Beat((SqliteRegistryStore)store, worker);
            var retryId = Guid.NewGuid().ToString("N");
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"{path}/retry?nodeQuiescent=false", new WorkerUpdateRequest(retryId))).StatusCode);
            var retry = await client.PostAsJsonAsync($"{path}/retry?nodeQuiescent=true", new WorkerUpdateRequest(retryId));
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal("waiting-for-idle", (await retry.Content.ReadFromJsonAsync<WorkerUpdateOperation>())?.State);
            var repeated = await client.PostAsJsonAsync($"{path}/retry?nodeQuiescent=true", new WorkerUpdateRequest(retryId));
            Assert.Equal(retryId, (await repeated.Content.ReadFromJsonAsync<WorkerUpdateOperation>())?.OperationId);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", prior); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RecoveryChecklistIsReadOnlyAndExplainsAllCurrentBlockers()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var path = Path.Combine(temporary.Path, "registry.db");
        var (store, worker) = await Create(path, clock);
        var operation = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"));
        await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, "1.1.0");
        clock.Now = operation.DeadlineUtc.AddDays(1);
        await store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", "not-ready", 1, 2, [], [], ConfigurationSynchronization: "error"));
        var markers = (await store.GetResourceStatusAsync()).Freshness;
        var status = Assert.IsType<WorkerUpdateOperation>(await store.InspectWorkerUpdateAsync(worker));
        Assert.Equal(markers, (await store.GetResourceStatusAsync()).Freshness);
        Assert.Equal("failed", status.State);
        var recovery = Assert.IsType<WorkerUpdateRecovery>(status.Recovery);
        Assert.True(recovery.SchedulingBlocked);
        Assert.False(recovery.Eligible);
        Assert.Equal(new[] { "running-lifecycle", "configuration-synchronized", "expected-version", "no-active-executions", "required-capabilities" },
            recovery.Checks.Where(check => !check.Satisfied).Select(check => check.Code));
        // Inspection must not even persist an elapsed deadline as a failure.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_json FROM worker_updates WHERE operation_id=$id";
        command.Parameters.AddWithValue("$id", operation.OperationId);
        var retained = System.Text.Json.JsonSerializer.Deserialize<WorkerUpdateOperation>((string)(await command.ExecuteScalarAsync() ?? ""));
        Assert.Equal("updating", retained?.State);
        var rejected = await Assert.ThrowsAsync<WorkerUpdateRecoveryException>(() => store.ReconcileWorkerUpdateAsync(worker));
        Assert.Equal(recovery.Checks, rejected.Recovery.Checks);
        clock.Now += TimeSpan.FromMinutes(2);
        var stale = Assert.IsType<WorkerUpdateOperation>(await store.InspectWorkerUpdateAsync(worker));
        Assert.Contains(stale.Recovery?.Checks ?? [], check => check.Code == "fresh-heartbeat" && !check.Satisfied);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    [InlineData("Draining")]
    public async Task ReconciliationRestoresPriorPolicyAndResumesEligibleQueueWithoutReplay(string policy)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        await store.SetWorkerSchedulingPolicyAsync(worker, policy);
        var project = await store.CreateProjectAsync(new("Project", "team/project", "main", ""));
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var operation = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"));
        await store.AdvanceWorkerUpdateAsync(worker, operation.OperationId, fail: true);
        var status = Assert.IsType<WorkerUpdateOperation>(await store.InspectWorkerUpdateAsync(worker));
        Assert.True(status.Recovery?.Eligible);
        Assert.Equal("worker-operation-unresolved", (await store.GetWorkerAsync(worker))?.SchedulingBlocker);
        Assert.Contains("unresolved Worker", (await store.GetExecutionsAsync()).Single().PendingReason);
        var capacities = new Dictionary<string, int> { [project.Id] = 1 };
        Assert.Equal("worker-operation-unresolved", (await store.RequestAssignmentAsync(new(worker, true, 1, capacities))).Reason);
        Assert.Equal("succeeded", (await store.ReconcileWorkerUpdateAsync(worker))?.State);
        Assert.Null((await store.GetWorkerAsync(worker))?.SchedulingBlocker);
        Assert.Equal(policy, (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        Assert.Equal(policy == "Enabled", (await store.RequestAssignmentAsync(new(worker, true, 1, capacities))).HasWork);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RestartWaitsForIdleAndDifferentReadyInstanceWithoutVersionChange()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        WorkerCapability[] capabilities = [.. Capabilities, new("protocol", WorkerUpdateProtocol.RestartCapability)];
        Task Heartbeat(string instance, int active = 0, string sync = "synchronized") => store.HeartbeatWorkerAsync(
            new(2, worker, "1.0.0", "running", active, 2, capabilities, [], ConfigurationSynchronization: sync, InstanceId: instance));
        await Heartbeat("original", 1);
        var id = Guid.NewGuid().ToString("N");
        var operation = await store.RequestWorkerUpdateAsync(worker, id, restart: true);
        Assert.True(operation.Restart);
        Assert.Equal("waiting-for-idle", operation.State);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        Assert.Equal(id, (await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"), restart: true)).OperationId);
        await Heartbeat("original");
        Assert.Equal("updating", (await store.AdvanceWorkerUpdateAsync(worker, id, "1.0.0"))?.State);
        await store.AdvanceWorkerUpdateAsync(worker, id, verified: true);
        clock.Now += TimeSpan.FromSeconds(1);
        await Heartbeat("original");
        Assert.Equal("reconnecting", (await store.GetWorkerUpdateAsync(worker))?.State);
        await Heartbeat("replacement", sync: "error");
        Assert.Equal("reconnecting", (await store.GetWorkerUpdateAsync(worker))?.State);
        await Heartbeat("replacement");
        Assert.Equal("succeeded", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal("Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RestartTimeoutRetainsDrainAndCannotBeReplayed()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var (store, worker) = await Create(Path.Combine(temporary.Path, "registry.db"), clock);
        await store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", "running", 0, 2,
            [.. Capabilities, new("protocol", WorkerUpdateProtocol.RestartCapability)], [], InstanceId: "original"));
        var operation = await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"), restart: true);
        clock.Now = operation.DeadlineUtc;
        Assert.Equal("failed", (await store.GetWorkerUpdateAsync(worker))?.State);
        Assert.Equal("Draining", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
        Assert.Null(await store.AdvanceWorkerUpdateAsync(worker));
        Assert.Equal(operation.OperationId, (await store.RequestWorkerUpdateAsync(worker, Guid.NewGuid().ToString("N"), restart: true)).OperationId);
        await store.HeartbeatWorkerAsync(new(2, worker, "1.0.0", "running", 0, 2,
            [.. Capabilities, new("protocol", WorkerUpdateProtocol.RestartCapability)], [],
            ConfigurationSynchronization: "synchronized", InstanceId: "original"));
        Assert.Equal("succeeded", (await store.ReconcileWorkerUpdateAsync(worker))?.State);
        Assert.Equal("Enabled", (await store.GetWorkerAsync(worker))?.SchedulingPolicy);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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
        Assert.Equal("worker-operation-unresolved", assignment.Reason);
        await Beat(store, worker);
        Assert.NotNull(await store.AdvanceWorkerUpdateAsync(worker));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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
        await store.AdvanceWorkerUpdateAsync(worker, (await store.GetWorkerUpdateAsync(worker))?.OperationId, fail: true);
        await Beat(store, worker, "1.1.0");
        var recovery = (await store.InspectWorkerUpdateAsync(worker))?.Recovery;
        Assert.False(recovery?.Eligible);
        Assert.False(recovery?.RetryEligible);
        await Assert.ThrowsAsync<WorkerUpdateRecoveryException>(() => store.ReconcileWorkerUpdateAsync(worker));
        Assert.Contains(recovery?.Checks ?? [], check => check.Code == "no-active-assignments" && !check.Satisfied);
        Assert.Contains(recovery?.Checks ?? [], check => check.Code == "no-active-leases" && !check.Satisfied);
        await store.ReportExecutionAsync(assignment.ServerExecutionId, new(worker, assignment.AssignmentId,
            "local-execution", "Completed", CompletedAtUtc: clock.Now, Generation: lease.Generation));
        await Beat(store, worker);
        Assert.Equal("succeeded", (await store.ReconcileWorkerUpdateAsync(worker))?.State);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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
