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
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void StatusTimingUsesMillisecondsWithInvariantDecimalFormatting()
    {
        Assert.Equal("0.100", StatusStageTiming.FormatMilliseconds(TimeSpan.FromTicks(1000)));
        Assert.Equal("100.000", StatusStageTiming.FormatMilliseconds(TimeSpan.FromMilliseconds(100)));
        Assert.Equal("0.142", StatusStageTiming.FormatMilliseconds(TimeSpan.FromTicks(1420)));
        Assert.Equal("142.000", StatusStageTiming.FormatMilliseconds(TimeSpan.FromMilliseconds(142)));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ResourceStatusIgnoresRollbackAndRestoresCommittedMarkersAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var database = Path.Combine(directory.Path, "registry.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        await store.CreateProjectAsync(new("Committed", "team/committed", "main", ""));
        var committed = await store.GetResourceStatusAsync();
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM projects;";
            await command.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }
        Assert.Equal(committed.Freshness, (await store.GetResourceStatusAsync()).Freshness);
        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        Assert.Equal(committed.Freshness, (await restarted.GetResourceStatusAsync()).Freshness);

        // Invalidate the schema after a successful read: readiness must be checked
        // on the next read, rather than retaining a previously ready observation.
        await using var failureConnection = new SqliteConnection($"Data Source={database}");
        await failureConnection.OpenAsync();
        await using var failure = failureConnection.CreateCommand();
        failure.CommandText = "UPDATE schema_metadata SET schema_version=-1 WHERE singleton=1;";
        await failure.ExecuteNonQueryAsync();
        var unavailable = await restarted.GetResourceStatusAsync();
        Assert.False(unavailable.PersistenceAvailable);
        Assert.Null(unavailable.Freshness);
        failure.CommandText = "DROP TABLE resource_freshness;";
        await failure.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => restarted.GetResourceStatusAsync());
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ResourceStatusReadsPersistedMarkersWithoutLoadingCollectionPayloads()
    {
        using var directory = new TemporaryDirectory();
        var database = Path.Combine(directory.Path, "registry.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var initial = await store.GetResourceStatusAsync();
        Assert.True(initial.PersistenceAvailable);
        Assert.NotNull(initial.Freshness);
        var project = await store.CreateProjectAsync(new("Freshness", "team/freshness", "main", ""));
        var created = await store.GetResourceStatusAsync();
        Assert.NotNull(created.Freshness);
        Assert.True(created.Freshness.Projects > initial.Freshness.Projects);
        Assert.Equal(initial.Freshness.Workers, created.Freshness.Workers);
        Assert.Equal(initial.Freshness.Executions, created.Freshness.Executions);

        await store.RegisterWorkerAsync(new(1, Guid.NewGuid().ToString("N"), "worker", "1.0", "test", 2,
            Array.Empty<WorkerCapability>()));
        var registered = await store.GetResourceStatusAsync();
        Assert.NotNull(registered.Freshness);
        Assert.True(registered.Freshness.Workers > created.Freshness.Workers);
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var queued = await store.GetResourceStatusAsync();
        Assert.NotNull(queued.Freshness);
        Assert.True(queued.Freshness.Executions > registered.Freshness.Executions);

        // Keep valid JSON for SQLite's repository expression index, but use an invalid
        // property type to prove status never deserializes the project collection.
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE projects SET configuration_json=json_set(configuration_json, '$.maxParallelTasks', 'invalid');
            """;
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<JsonException>(() => store.GetProjectAsync(project.Id));
        var updated = await new SqliteRegistryStore(database).GetResourceStatusAsync();
        Assert.NotNull(updated.Freshness);
        Assert.True(updated.Freshness.Projects > created.Freshness.Projects);
        command.CommandText = "DELETE FROM projects;";
        await command.ExecuteNonQueryAsync();
        var deleted = await store.GetResourceStatusAsync();
        Assert.NotNull(deleted.Freshness);
        Assert.True(deleted.Freshness.Projects > updated.Freshness.Projects);
        Assert.NotEmpty(project.Id);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ProjectValidationCommandsPersistThroughCreateUpdateAndReload()
    {
        using var directory = new TemporaryDirectory();
        var database = Path.Combine(directory.Path, "registry.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var definition = new CentralProjectDefinition("Validation", "team/validation", "main", "Description",
            ValidationCommands: ["build", "test"]);
        var created = await store.CreateProjectAsync(definition);
        Assert.Equal(definition.ValidationCommands, created.ValidationCommands);
        var updated = await store.UpdateProjectAsync(created.Id,
            definition with { ValidationCommands = ["test", "build", "check"] }, created.Revision);
        Assert.NotNull(updated);
        var reloaded = await new SqliteRegistryStore(database).GetProjectAsync(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(["test", "build", "check"], reloaded.ValidationCommands);
        Assert.Equal(definition.Description, reloaded.Description);
        var empty = await store.UpdateProjectAsync(created.Id, definition with { ValidationCommands = [] }, updated.Revision);
        Assert.NotNull(empty);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(empty.ValidationCommands));
        var legacy = await store.CreateProjectAsync(new("Legacy validation", "team/legacy", "main", ""));
        Assert.Null(legacy.ValidationCommands);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateProjectAsync(created.Id,
            definition with { ValidationCommands = [" "] }, empty.Revision));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task AuthenticatedSlotSettingsRoundTripThroughWorkerSynchronization(int? projectLimit)
    {
        using var temporary = new TemporaryDirectory();
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "slot-regression-management");
        try
        {
            var url = $"http://127.0.0.1:{ReservePort()}";
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "slots.db")));
            await app.StartAsync();
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            await registry.CreateProjectAsync(new("Slots", "team/slots", "main", "", MaxParallelTasks: projectLimit));
            var workerId = Guid.NewGuid().ToString("N");
            using var worker = new HttpClient { BaseAddress = new Uri(url) };
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            worker.DefaultRequestHeaders.Authorization = new("Bearer", bootstrap);
            worker.DefaultRequestHeaders.Add("X-Codex-Worker-Token", "slot-regression-worker-token-for-authentication");
            using var enrollment = await worker.PostAsJsonAsync("/api/v1/workers/register",
                new WorkerRegistrationRequest(2, workerId, "Slots worker", "1.0", "test", 2, []));
            Assert.Equal(HttpStatusCode.OK, enrollment.StatusCode);
            worker.DefaultRequestHeaders.Remove("X-Codex-Worker-Token");
            worker.DefaultRequestHeaders.Authorization = new("Bearer", "slot-regression-worker-token-for-authentication");
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            management.DefaultRequestHeaders.Authorization = new("Bearer", "slot-regression-management");
            var synchronizer = new ManagedConfigurationSynchronizer(Path.Combine(temporary.Path, "snapshot.json"),
                new ManagedProjectRuntimeSettings { CheckoutDirectory = Path.Combine(temporary.Path, "checkouts") });
            long revision = 1;
            var appliedSlots = 2;
            foreach (var slots in new[] { 2, 4, 2 })
            {
                using var saved = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/settings",
                    new WorkerConfigurationUpdateRequest("Slots worker", 8, revision++, slots));
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                var readBack = await management.GetFromJsonAsync<WorkerRegistrationResponse>($"/api/v1/workers/{workerId}");
                Assert.NotNull(readBack);
                Assert.Equal(slots, readBack.WorkerExecutionSlots);
                Assert.Equal(appliedSlots, readBack.MaximumCapacity);
                // Deserialize the actual HTTP response using the Worker's wire contract.
                var snapshot = await worker.GetFromJsonAsync<ServerManagedConfigurationContract>($"/api/v1/workers/{workerId}/configuration");
                Assert.NotNull(snapshot);
                Assert.Equal(3, snapshot.ContractVersion);
                Assert.Equal(slots, snapshot.WorkerExecutionSlots);
                Assert.Equal(projectLimit, Assert.Single(snapshot.Projects).MaxParallelTasks);
                Assert.Equal(ManagedConfigurationSynchronizer.CalculateVersion(snapshot.Projects, workerExecutionSlots: slots), snapshot.Version);
                // A pre-slots Worker hashes the projects alone: this is the exact incompatible input.
                Assert.NotEqual(ManagedConfigurationSynchronizer.CalculateVersion(snapshot.Projects), snapshot.Version);
                await synchronizer.RetrieveAndApplyAsync(_ => Task.FromResult(snapshot), CancellationToken.None);
                Assert.Equal(slots, synchronizer.AppliedWorkerExecutionSlots);
                Assert.Equal("synchronized", synchronizer.Status.SynchronizationStatus);
                Assert.Throws<InvalidDataException>(() => synchronizer.Apply(snapshot with { ContractVersion = 2 }));
                Assert.Equal("managed-contract-invalid", synchronizer.Status.Diagnostics?.DiagnosticCode);
                Assert.Equal(slots, synchronizer.AppliedWorkerExecutionSlots);
                await synchronizer.RetrieveAndApplyAsync(_ => Task.FromResult(snapshot), CancellationToken.None);
                using var heartbeat = await worker.PostAsJsonAsync($"/api/v1/workers/{workerId}/heartbeat",
                    new WorkerHeartbeatRequest(2, workerId, "1.0", "running", 0, slots, [], [],
                        synchronizer.Status.SynchronizationStatus, synchronizer.Status.AppliedVersion));
                Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
                var reported = await management.GetFromJsonAsync<WorkerRegistrationResponse>($"/api/v1/workers/{workerId}");
                Assert.NotNull(reported);
                Assert.Equal(slots, reported.MaximumCapacity);
                Assert.Equal(snapshot.Version, reported.ConfigurationVersion);
                Assert.Equal("synchronized", reported.ConfigurationSynchronization);
                appliedSlots = slots;
            }
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", prior); }
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(8)]
    public void ManagedSnapshotHashIncludesServerProjectConcurrency(int? limit)
    {
        var now = DateTimeOffset.UnixEpoch;
        var project = new CentralProject("project", "Project", "team/project", "main", "", [], 1, now, now,
            MaxParallelTasks: limit);
        var contract = new ServerProjectContract(project.Id, project.Name, project.Repository, project.DefaultBranch,
            project.Description, [], project.Revision, now, now, MaxParallelTasks: limit);
        Assert.Equal(ServerApplication.ManagedConfigurationVersion([project]),
            ManagedConfigurationSynchronizer.CalculateVersion([contract]));
        Assert.Equal(ServerApplication.ManagedConfigurationVersion([project], 4),
            ManagedConfigurationSynchronizer.CalculateVersion([contract], workerExecutionSlots: 4));
        Assert.NotEqual(ServerApplication.ManagedConfigurationVersion([project]),
            ServerApplication.ManagedConfigurationVersion([project with { MaxParallelTasks = limit == 1 ? 2 : 1 }]));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, 1)]
    public async Task ServerProjectPolicyControlsAdmissionAndRoundTrips(int? limit, int expected)
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "project-capacity.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var definition = new CentralProjectDefinition("Capacity", "team/capacity", "main", "", MaxParallelTasks: limit);
        var project = await store.CreateProjectAsync(definition);
        var worker = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new(1, worker, "worker", "1.0", "test", 2, capabilities));
        await store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 2, capabilities, []));
        for (var issue = 1; issue <= 3; issue++)
            await store.EnqueueExecutionAsync(new(project.Id, new("issue", issue.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        WorkerAssignmentRequest request = new(worker, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        for (var slot = 0; slot < expected; slot++)
        {
            var assignment = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(request)).Assignment);
            var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
            await store.ReportExecutionAsync(assignment.ServerExecutionId, new(worker, assignment.AssignmentId,
                "local-" + slot, "Running", "Codex", DateTimeOffset.UtcNow, Generation: lease.Generation));
            await store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", slot + 1, 2, capabilities, []));
            request = request with { AvailableCapacity = 2 - slot - 1 };
        }
        var noWork = await store.RequestAssignmentAsync(request);
        Assert.False(noWork.HasWork);
        Assert.Equal(limit == 1 ? "project-capacity" : "worker-capacity", noWork.Reason);
        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        Assert.Equal(limit, (await restarted.GetProjectAsync(project.Id))?.MaxParallelTasks);
        Assert.Equal(expected, (await restarted.GetExecutionsAsync()).Count(execution => execution.State == "Running"));
        if (limit == 1)
        {
            var executions = await restarted.GetExecutionsAsync();
            Assert.All(executions.Where(execution => execution.State == "Queued"),
                execution => Assert.Equal("project-capacity", execution.PendingReason));
            var running = Assert.Single(executions, execution => execution.State == "Running");
            await restarted.ReportExecutionAsync(running.Id, new(worker, Assert.IsType<string>(running.AssignmentId),
                Assert.IsType<string>(running.WorkerExecutionId), "Completed", CompletedAtUtc: DateTimeOffset.UtcNow,
                Generation: Assert.IsType<ExecutionLease>(running.Lease).Generation));
            await restarted.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 2, capabilities, []));
            Assert.True((await restarted.RequestAssignmentAsync(request with { AvailableCapacity = 2 })).HasWork);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerConfigurationCapacityLimitsNewServerAssignmentsWithoutChangingWorkerReportedCapacity()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "worker-capacity.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Worker capacity", "team/worker-capacity", "main", ""));
        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new(2, workerId, "Worker", "1.0.0", "linux-x64", 3, capabilities));
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 0, 3, capabilities, []));
        for (var issue = 1; issue <= 3; issue++)
            await store.EnqueueExecutionAsync(new(project.Id, new("issue", issue.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var configured = await store.UpdateWorkerConfigurationAsync(workerId, "Worker", 1, 1);
        Assert.NotNull(configured);
        Assert.Equal(3, configured.MaximumCapacity);
        Assert.Equal(1, configured.ConfiguredMaximumCapacity);
        var request = new WorkerAssignmentRequest(workerId, true, 3, new Dictionary<string, int> { [project.Id] = 3 });

        Assert.True((await store.RequestAssignmentAsync(request)).HasWork);
        var firstLimit = await store.RequestAssignmentAsync(request);
        Assert.False(firstLimit.HasWork);
        Assert.Equal("worker-capacity", firstLimit.Reason);

        var increased = await store.UpdateWorkerConfigurationAsync(workerId, "Worker", 2, 2);
        Assert.NotNull(increased);
        Assert.True((await store.RequestAssignmentAsync(request)).HasWork);
        var secondLimit = await store.RequestAssignmentAsync(request);
        Assert.False(secondLimit.HasWork);
        Assert.Equal("worker-capacity", secondLimit.Reason);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerSlotsRemainPendingUntilReportedAndPreserveIndependentAssignmentLimit()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "slots.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Slots", "team/slots", "main", ""));
        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new(2, workerId, "Worker", "1.0.0", "linux-x64", 2, capabilities));
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 1, 2, capabilities, []));
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var request = new WorkerAssignmentRequest(workerId, true, 8, new Dictionary<string, int> { [project.Id] = 8 });
        var saved = await store.UpdateWorkerConfigurationAsync(workerId, "Worker", 8, 1, workerExecutionSlots: 4);
        Assert.NotNull(saved);
        Assert.Equal(4, saved.WorkerExecutionSlots);
        Assert.Equal(2, saved.MaximumCapacity);
        Assert.Equal(1, saved.ActiveExecutions);
        Assert.Equal("worker-configuration-pending", (await store.RequestAssignmentAsync(request)).Reason);
        Assert.Equal(4, (await store.GetWorkerAsync(workerId))?.WorkerExecutionSlots);
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 0, 4, capabilities, []));
        Assert.Equal(4, (await store.GetWorkerAsync(workerId))?.MaximumCapacity);
        Assert.True((await store.RequestAssignmentAsync(request)).HasWork);
        var lowered = await store.UpdateWorkerConfigurationAsync(workerId, "Worker", 1, 2);
        Assert.NotNull(lowered);
        Assert.Equal(4, lowered.WorkerExecutionSlots);
        Assert.Equal(4, lowered.MaximumCapacity);
        Assert.Equal("worker-capacity", (await store.RequestAssignmentAsync(request)).Reason);
        await store.UpdateWorkerConfigurationAsync(workerId, "Worker", 8, 3, workerExecutionSlots: 1);
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 3, 4, capabilities, []));
        Assert.Equal("worker-configuration-pending", (await store.RequestAssignmentAsync(request)).Reason);
        Assert.Equal(3, (await store.GetWorkerAsync(workerId))?.ActiveExecutions);
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 1, 1, capabilities, []));
        Assert.Equal("worker-capacity", (await store.RequestAssignmentAsync(request)).Reason);
        // Restoring slots remains a revision-checked save while work is occupied.
        await store.UpdateWorkerConfigurationAsync(workerId, "Worker", 8, 4, workerExecutionSlots: 2);
        Assert.Equal(1, (await store.GetWorkerAsync(workerId))?.MaximumCapacity);
        Assert.Equal("worker-configuration-pending", (await store.RequestAssignmentAsync(request)).Reason);
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 0, 2, capabilities, []));
        Assert.Equal(2, (await store.GetWorkerAsync(workerId))?.MaximumCapacity);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ProjectCapacityIsAtomicAcrossWorkersAndLimitChangesPreserveActiveWork()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "fleet-capacity.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var definition = new CentralProjectDefinition("Fleet", "team/fleet", "main", "", MaxParallelTasks: 3);
        var project = await store.CreateProjectAsync(definition);
        var workers = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        foreach (var worker in workers)
        {
            await store.RegisterWorkerAsync(new(1, worker, "worker", "1.0", "test", 2, capabilities));
            await store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 2, capabilities, []));
        }
        for (var issue = 1; issue <= 6; issue++)
            await store.EnqueueExecutionAsync(new(project.Id, new("issue", issue.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
        {
            await ready.Task;
            var otherStore = new SqliteRegistryStore(database);
            return await otherStore.RequestAssignmentAsync(new(workers[index % 2], true, 2,
                new Dictionary<string, int> { [project.Id] = 2 }));
        })).ToArray();
        ready.SetResult();
        var responses = await Task.WhenAll(requests);
        Assert.Equal(3, responses.Count(response => response.HasWork));
        Assert.Equal(3, (await store.GetExecutionsAsync()).Count(execution => execution.State == "Assigned"));
        var lowered = await store.UpdateProjectAsync(project.Id, definition with { MaxParallelTasks = 1 }, project.Revision);
        Assert.NotNull(lowered);
        foreach (var worker in workers)
            Assert.False((await store.RequestAssignmentAsync(new(worker, true, 2,
                new Dictionary<string, int> { [project.Id] = 2 }))).HasWork);
        Assert.Equal(3, (await store.GetExecutionsAsync()).Count(execution => execution.State == "Assigned"));
        await store.UpdateProjectAsync(project.Id, definition with { MaxParallelTasks = null }, lowered.Revision);
        var availableWorker = workers.Single(worker => responses.Count(response => response.Assignment?.WorkerId == worker) == 1);
        Assert.True((await store.RequestAssignmentAsync(new(availableWorker, true, 2,
            new Dictionary<string, int> { [project.Id] = 2 }))).HasWork);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("Codex", true)]
    [InlineData("Integration", false)]
    public async Task ExpiredProjectReservationIsReleasedOnlyWhenOutcomeIsSafe(string stage, bool safe)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "expired-capacity.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new("Expiry", "team/expiry", "main", "", MaxParallelTasks: 1));
        var worker = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new(1, worker, "worker", "1.0", "test", 2, capabilities));
        await store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 2, capabilities, []));
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        await store.EnqueueExecutionAsync(new(project.Id, new("issue", "2")));
        WorkerAssignmentRequest request = new(worker, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var assignment = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(request)).Assignment);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        await store.ReportExecutionAsync(assignment.ServerExecutionId, new(worker, assignment.AssignmentId,
            "local", "Running", stage, clock.GetUtcNow(), Generation: lease.Generation));
        clock.Advance(TimeSpan.FromMinutes(16));
        await store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 2, capabilities, []));
        await store.GetExecutionsAsync();
        Assert.Equal(safe, (await store.RequestAssignmentAsync(request)).HasWork);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task LegacyStoredProjectWithoutConcurrencyDefaultsToAutomatic()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "legacy-policy.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new("Legacy", "team/legacy", "main", ""));
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projects SET configuration_json=json_remove(configuration_json, '$.maxParallelTasks');";
            await command.ExecuteNonQueryAsync();
        }
        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        Assert.Null((await restarted.GetProjectAsync(project.Id))?.MaxParallelTasks);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void ProjectConcurrencyRejectsInvalidLimits(int limit) =>
        Assert.NotNull(CentralProjectValidation.Error(new("Capacity", "team/capacity", "main", "", MaxParallelTasks: limit)));

    private static async Task<bool> EnrollWorkerAsync(IRegistryStore store, string authorization, string workerId, string token)
    {
        var existing = await store.GetWorkerAsync(workerId);
        var registration = existing is null ? new WorkerRegistrationRequest(2, workerId, "test worker", "1.0", "test", 1, []) :
            new WorkerRegistrationRequest(existing.ContractVersion, workerId, existing.DisplayName, existing.WorkerVersion,
                existing.Platform, existing.Capacity, existing.Capabilities, existing.CapabilityInventory);
        return await store.BootstrapWorkerAsync(authorization, registration, token, operation: existing is null ? "enroll" : "rotate");
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExhaustedEngineQuotaHoldsAssignmentUntilTheWindowResets()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T10:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "quota.db"), 300, clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Quota", "team/quota", "main", ""));
        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new(2, workerId, "Worker", "1.0.0", "linux-x64", 1, capabilities));
        var reset = clock.GetUtcNow().AddHours(2);
        EngineQuota Quota(double remaining) => new("codex", clock.GetUtcNow(), "available",
            [new EngineQuotaWindow(300, remaining, reset), new EngineQuotaWindow(10080, 60, reset.AddDays(3))]);
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 0, 1, capabilities, [], EngineQuotas: [Quota(3)]));
        var queued = await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var request = new WorkerAssignmentRequest(workerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 });

        Assert.Equal("engine-quota-exhausted", (await store.RequestAssignmentAsync(request)).Reason);
        Assert.Equal("engine-quota-exhausted", (await store.GetExecutionAsync(queued.Id))?.PendingReason);
        Assert.Equal(3, (await store.GetWorkerAsync(workerId))?.EngineQuotas?.Single().Windows[0].RemainingPercent);

        // A stale observation no longer blocks: the engine reports its own limit if it is still exhausted.
        clock.Advance(EngineQuota.Freshness + TimeSpan.FromMinutes(1));
        await store.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 0, 1, capabilities, [],
            EngineQuotas: [new EngineQuota("codex", clock.GetUtcNow() - EngineQuota.Freshness - TimeSpan.FromMinutes(1), "available", [new EngineQuotaWindow(300, 0, reset)])]));
        Assert.True((await store.RequestAssignmentAsync(request)).HasWork);
    }

    [Fact]
    public void EngineQuotaBlocksOnlyFreshExhaustedWindowsOfTheSelectedEngine()
    {
        var now = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        EngineQuota[] quotas = [new("codex", now, "available", [new EngineQuotaWindow(300, 5, now.AddHours(1))])];
        Assert.NotNull(EngineQuota.ExhaustedWindow(quotas, "codex", now));
        Assert.Null(EngineQuota.ExhaustedWindow(quotas, "ccc", now));
        Assert.Null(EngineQuota.ExhaustedWindow(quotas, "codex", now.AddHours(1)));
        Assert.Null(EngineQuota.ExhaustedWindow([quotas[0] with { Status = "query failed" }], "codex", now));
        Assert.Null(EngineQuota.ExhaustedWindow([new("codex", now, "available", [new EngineQuotaWindow(300, 5.1, now.AddHours(1))])], "codex", now));
        Assert.False(EngineQuota.Valid([new("other", now, "available", [])]));
        Assert.False(EngineQuota.Valid([new("codex", now, "available", [new EngineQuotaWindow(300, 101, null)])]));
    }
}
