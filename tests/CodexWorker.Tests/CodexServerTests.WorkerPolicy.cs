namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodexProvisioning;
using CodexServer;
using CodexWorker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServerWorkerCapability = CodexServer.WorkerCapability;
using WorkerCapability = CodexServer.WorkerCapability;

public sealed partial class CodexServerTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerSchedulingPolicyPersistsAndDrainPreservesActiveLeaseAndLifecycleObservations()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "worker-policy.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-15T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Policy project", "team/policy", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(2, workerId, "policy worker", "1.0", "test", 2, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(2, workerId, "1.0", "running", 0, 2, capabilities, []));
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var second = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var request = new WorkerAssignmentRequest(workerId, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var assignment = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(assignment);
        Assert.Equal(first.Id, assignment.ServerExecutionId);

        var draining = await store.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Draining);
        Assert.NotNull(draining);
        Assert.Equal("running", draining.LifecycleState);
        Assert.Equal("online", draining.Availability);
        Assert.Equal(1, draining.ActiveAssignments);

        var restarted = new SqliteRegistryStore(database, timeProvider: clock);
        await restarted.InitializeAsync();
        var persisted = await restarted.GetWorkerAsync(workerId);
        Assert.NotNull(persisted);
        Assert.Equal(WorkerSchedulingPolicy.Draining, persisted.SchedulingPolicy);
        Assert.Equal(1, persisted.ActiveAssignments);
        persisted = await restarted.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Disabled);
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted.ActiveAssignments);
        Assert.Equal("running", persisted.LifecycleState);
        Assert.False((await restarted.RequestAssignmentAsync(request)).HasWork);
        Assert.Equal("compatible Workers are disabled or draining", Assert.Single(await restarted.GetExecutionsAsync(), x => x.Id == second.Id).PendingReason);

        var renewed = await restarted.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(workerId, assignment.Lease!.Generation));
        Assert.NotNull(renewed);
        Assert.NotNull(await restarted.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Draining));
        var completed = await restarted.ReportExecutionAsync(first.Id, new WorkerExecutionReport(workerId, assignment.AssignmentId,
            "worker-run-1", "Completed", CompletedAtUtc: clock.GetUtcNow(), Generation: assignment.Lease.Generation));
        Assert.NotNull(completed);
        persisted = await restarted.GetWorkerAsync(workerId);
        Assert.Equal(0, persisted!.ActiveAssignments);
        Assert.Equal(WorkerSchedulingPolicy.Draining, persisted.SchedulingPolicy);
        Assert.Equal("online", persisted.Availability);

        Assert.NotNull(await restarted.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Disabled));
        Assert.False((await restarted.RequestAssignmentAsync(request)).HasWork);
        Assert.NotNull(await restarted.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Enabled));
        var enabledAssignment = (await restarted.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(enabledAssignment);
        Assert.Equal(second.Id, enabledAssignment.ServerExecutionId);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerApiTokenRevocationIsPersistentAndSeparateFromCredentialDeliveryAuthorization()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "worker-auth.db");
        var workerId = Guid.NewGuid().ToString("N");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "auth worker", "1.0", "test", 1, []));
        var bootstrap = await store.CreateWorkerAuthorizationAsync(workerId, "rotate", TimeSpan.FromMinutes(1));
        Assert.True(await EnrollWorkerAsync(store, bootstrap, workerId, new string('w', 40)));
        Assert.True(await store.IsWorkerTokenValidAsync(workerId, new string('w', 40)));

        var credentialStore = new SqliteCredentialStore(database);
        await credentialStore.InitializeAsync();
        const string deliveryToken = "worker-delivery-token-with-adequate-entropy";
        await credentialStore.SetWorkerDeliveryTokenAsync(workerId, new CredentialSecretInput(deliveryToken));
        Assert.Equal("active", (await credentialStore.GetWorkerDeliveryAuthorizationStatusAsync(workerId)).Status);

        Assert.True(await store.RevokeWorkerTokenAsync(workerId));
        Assert.False(await store.IsWorkerTokenValidAsync(workerId, new string('w', 40)));
        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        var worker = await restarted.GetWorkerAsync(workerId);
        Assert.Equal("revoked", worker!.AuthenticationCredentialStatus);
        Assert.NotNull(worker.AuthenticationCredentialRevokedAtUtc);
        Assert.True(await credentialStore.IsWorkerDeliveryTokenValidAsync(workerId, deliveryToken));
        Assert.True(await credentialStore.RevokeWorkerDeliveryTokenAsync(workerId));
        Assert.False(await credentialStore.IsWorkerDeliveryTokenValidAsync(workerId, deliveryToken));
        Assert.Equal("revoked", (await credentialStore.GetWorkerDeliveryAuthorizationStatusAsync(workerId)).Status);
        Assert.Equal("revoked", (await restarted.GetWorkerAsync(workerId))!.AuthenticationCredentialStatus);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task LocalWorkerAdministrationCliPersistsSchedulingChanges()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "worker-admin-cli.db");
        var workerId = Guid.NewGuid().ToString("N");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "cli worker", "1.0", "test", 1, []));
        var bootstrap = await registry.CreateWorkerAuthorizationAsync(workerId, "rotate", TimeSpan.FromMinutes(1));
        Assert.True(await EnrollWorkerAsync(registry, bootstrap, workerId, "cli-worker-api-token-with-sufficient-entropy"));
        var credentials = new SqliteCredentialStore(database);
        await credentials.InitializeAsync();
        await credentials.SetWorkerDeliveryTokenAsync(workerId, new CredentialSecretInput("cli-worker-delivery-token-with-sufficient-entropy"));

        Assert.Equal(0, await CodexServer.Program.Main(["worker", "drain", workerId, database]));
        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        Assert.Equal(WorkerSchedulingPolicy.Draining, (await restarted.GetWorkerAsync(workerId))!.SchedulingPolicy);
        Assert.Equal(0, await CodexServer.Program.Main(["worker", "enable", workerId, database]));
        Assert.Equal(WorkerSchedulingPolicy.Enabled, (await restarted.GetWorkerAsync(workerId))!.SchedulingPolicy);
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, await CodexServer.Program.Main(["worker", "show", workerId, database]));
        }
        finally { Console.SetOut(originalOutput); }
        using var projection = JsonDocument.Parse(output.ToString());
        Assert.Equal("Enabled", projection.RootElement.GetProperty("worker").GetProperty("schedulingPolicy").GetString());
        Assert.Equal("active", projection.RootElement.GetProperty("worker").GetProperty("authenticationCredentialStatus").GetString());
        Assert.Equal("active", projection.RootElement.GetProperty("credentialDeliveryAuthorization").GetProperty("status").GetString());
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RegistryUpgradeDefaultsExistingWorkersToEnabledScheduling()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "worker-policy-upgrade.db");
        var workerId = Guid.NewGuid().ToString("N");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "existing worker", "1.0", "test", 1, []));
        var project = await registry.CreateProjectAsync(new CentralProjectDefinition("Existing project", "team/existing", "main", "", []));
        var execution = await registry.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "19")));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE worker_bootstrap_tokens DROP COLUMN worker_id; ALTER TABLE worker_bootstrap_tokens DROP COLUMN operation; ALTER TABLE workers DROP COLUMN scheduling_policy; ALTER TABLE workers DROP COLUMN worker_execution_slots; ALTER TABLE workers DROP COLUMN telegram_json; ALTER TABLE workers DROP COLUMN configured_display_name; ALTER TABLE workers DROP COLUMN configured_maximum_capacity; ALTER TABLE workers DROP COLUMN configuration_revision; ALTER TABLE execution_requests DROP COLUMN stage_started_at_utc_json; ALTER TABLE execution_requests DROP COLUMN managed_eligibility_checked_at_utc; ALTER TABLE execution_requests DROP COLUMN managed_eligibility_reasons_json; ALTER TABLE execution_requests DROP COLUMN managed_eligibility_state; UPDATE schema_metadata SET schema_version=11 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = new SqliteRegistryStore(database);
        await upgraded.InitializeAsync();
        var worker = await upgraded.GetWorkerAsync(workerId);
        Assert.Equal(WorkerSchedulingPolicy.Enabled, worker!.SchedulingPolicy);
        Assert.Equal(1, worker.ConfigurationRevision);
        Assert.Null(worker.ConfiguredMaximumCapacity);
        Assert.Null(worker.WorkerExecutionSlots);
        Assert.Equal("eligible", (await upgraded.GetExecutionAsync(execution.Id))!.ManagedEligibilityState);
        Assert.Null((await upgraded.GetExecutionAsync(execution.Id))!.StageStartedAtUtc);
    }
}
