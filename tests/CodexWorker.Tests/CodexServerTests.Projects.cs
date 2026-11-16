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
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task PersistenceSchemaSurvivesRepeatedInitializationAndRestart()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "nested", "server.db");
        var firstStore = new SqliteRegistryStore(databasePath);
        await firstStore.InitializeAsync();
        Assert.True(await firstStore.IsAvailableAsync());
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO workers (worker_id, display_name, registered_at_utc, status_json) VALUES ('worker-1', 'test worker', '2026-01-01T00:00:00Z', NULL);";
            await command.ExecuteNonQueryAsync();
        }
        var secondStore = new SqliteRegistryStore(databasePath);
        await secondStore.InitializeAsync();
        Assert.True(await secondStore.IsAvailableAsync());
        Assert.True(File.Exists(databasePath));
        await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM workers WHERE worker_id = 'worker-1';";
        Assert.Equal(1L, (long)(await verifyCommand.ExecuteScalarAsync())!);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CentralProjectsSupportValidatedCrudDurabilityUniquenessAndRevisionConflicts()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "projects.db"));
        await store.InitializeAsync();
        Assert.Null(CentralProjectValidation.Error(new CentralProjectDefinition("Compiler", "team/compiler", "main", "", [new("runtime", ".NET", ">=10.0"), new("service", "postgresql")])));
        Assert.NotNull(CentralProjectValidation.Error(new CentralProjectDefinition("", "bad", "", "", [])));
        var requirements = new ProjectRequirement[] { new("runtime", ".NET", " >=010.0 "), new("tool", "Git"), new("service", "PostgreSQL") };
        var definition = new CentralProjectDefinition("Compiler", "team/compiler", "main", "Compiler source", requirements);
        var created = await store.CreateProjectAsync(definition);
        Assert.Equal("compiler", created.Id);
        Assert.Equal(1, created.Revision);
        ProjectRequirement[] normalizedRequirements = [new("runtime", ".net", ">=10.0"), new("tool", "git"), new("service", "postgresql")];
        Assert.Equal(normalizedRequirements, created.Requirements);
        Assert.False(JsonSerializer.Serialize(created).Contains("directory", StringComparison.OrdinalIgnoreCase));
        Assert.False(JsonSerializer.Serialize(created).Contains("secret", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateProjectAsync(definition with { Name = "Other" }));

        var attempts = await Task.WhenAll(
            AttemptUpdateAsync(store, created.Id, definition with { Description = "Updated A" }),
            AttemptUpdateAsync(store, created.Id, definition with { Description = "Updated B" }));
        Assert.Equal(1, attempts.Count(x => x is not null));
        Assert.Equal(1, attempts.Count(x => x is null));

        var restarted = new SqliteRegistryStore(Path.Combine(temporary.Path, "projects.db"));
        await restarted.InitializeAsync();
        var persisted = Assert.Single(await restarted.GetProjectsAsync());
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(created.Requirements, persisted.Requirements);
        Assert.Contains(persisted.Description, new[] { "Updated A", "Updated B" });
        Assert.True(await restarted.RemoveProjectAsync(created.Id, 2));
        Assert.Empty(await restarted.GetProjectsAsync());

        var minimal = new CentralProjectDefinition("Minimal", "team/minimal", "main", "", null);
        Assert.Null(CentralProjectValidation.Error(minimal));
        var withoutRequirements = await restarted.CreateProjectAsync(minimal);
        Assert.Empty(withoutRequirements.Requirements);
        Assert.Empty(Assert.IsType<CentralProject>(await restarted.GetProjectAsync(withoutRequirements.Id)).Requirements);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CentralProjectMutationReturnsItsOwnRevisionWhenTheStoredRowAdvances(bool lifecycle)
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "project-update-result.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var definition = new CentralProjectDefinition("Result", "team/result", "main", "Original", []);
        var project = await store.CreateProjectAsync(definition);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Deterministically advance the row after the update's result is captured,
            // exercising the same response hazard as a subsequent concurrent writer.
            command.CommandText = """
                CREATE TRIGGER advance_project AFTER UPDATE ON projects
                WHEN json_extract(NEW.configuration_json, '$.revision') = 2
                BEGIN
                    UPDATE projects SET configuration_json = json_set(configuration_json,
                        '$.revision', 3, '$.description', 'Later edit', '$.enabled', json('true'))
                    WHERE project_id = NEW.project_id;
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = Assert.IsType<CentralProject>(lifecycle
            ? await store.UpdateProjectLifecycleAsync(project.Id, false, 1)
            : await store.UpdateProjectAsync(project.Id, definition with { Description = "My edit" }, 1));
        Assert.Equal(2, result.Revision);
        Assert.Equal(lifecycle ? "Original" : "My edit", result.Description);
        Assert.Equal(!lifecycle, result.Enabled);
        var persisted = Assert.IsType<CentralProject>(await store.GetProjectAsync(project.Id));
        Assert.Equal(3, persisted.Revision);
        Assert.Equal("Later edit", persisted.Description);
        Assert.True(persisted.Enabled);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CentralProjectMutationsRejectNonpositiveRevisionsWithoutChangingState(long revision)
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "project-revisions.db"));
        await store.InitializeAsync();
        var definition = new CentralProjectDefinition("Revision", "team/revision", "main", "Original", []);
        var project = await store.CreateProjectAsync(definition);

        foreach (var id in new[] { project.Id, "missing" })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateProjectAsync(id,
                definition with { Description = "Rejected" }, revision));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateProjectLifecycleAsync(id, false, revision));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.RemoveProjectAsync(id, revision));
        }

        var persisted = Assert.IsType<CentralProject>(await store.GetProjectAsync(project.Id));
        Assert.Equal(project.Revision, persisted.Revision);
        Assert.Equal("Original", persisted.Description);
        Assert.True(persisted.Enabled);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CentralProjectLifecyclePersistsAndSharesOptimisticRevisionWithDefinitionUpdates()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "project-lifecycle.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        var definition = new CentralProjectDefinition("Lifecycle", "team/lifecycle", "main", "Managed project",
            [new("runtime", "dotnet", ">=10.0")]);
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var created = await store.CreateProjectAsync(definition);
        Assert.True(created.Enabled);

        var disabled = await store.UpdateProjectLifecycleAsync(created.Id, false, created.Revision);
        Assert.NotNull(disabled);
        Assert.False(disabled.Enabled);
        Assert.Equal(2, disabled.Revision);
        Assert.Equal(created.Requirements, disabled.Requirements);
        var restarted = new SqliteRegistryStore(database, timeProvider: clock);
        await restarted.InitializeAsync();
        var persisted = Assert.IsType<CentralProject>(await restarted.GetProjectAsync(created.Id));
        Assert.False(persisted.Enabled);
        Assert.Equal(2, persisted.Revision);
        await Assert.ThrowsAsync<ProjectRevisionConflictException>(() => restarted.UpdateProjectLifecycleAsync(created.Id, true, 1));

        var edited = await restarted.UpdateProjectAsync(created.Id, definition with { Description = "Updated while disabled" }, persisted.Revision);
        Assert.NotNull(edited);
        Assert.False(edited.Enabled);
        Assert.Equal(3, edited.Revision);
        Assert.Equal(created.Requirements, edited.Requirements);
        var enabled = await restarted.UpdateProjectLifecycleAsync(created.Id, true, edited.Revision);
        Assert.NotNull(enabled);
        Assert.True(enabled.Enabled);
        Assert.Equal(4, enabled.Revision);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ExistingCentralProjectRowsWithoutLifecycleFieldDefaultToEnabled()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "existing-project.json");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var created = await store.CreateProjectAsync(new CentralProjectDefinition("Existing", "team/existing", "main", "", []));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projects SET configuration_json = json_remove(configuration_json, '$.enabled') WHERE project_id = $id;";
            command.Parameters.AddWithValue("$id", created.Id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();

        Assert.True((await restarted.GetProjectAsync(created.Id))!.Enabled);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task DisabledCentralProjectPausesQueuedWorkButPreservesActiveLeaseAndBlocksUnsafeDeletion()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "disabled-project.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Dispatch", "team/dispatch", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "dispatch worker", "1.0", "test", 2, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 2, capabilities, []));
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        WorkerAssignmentRequest request = new(workerId, true, 2, new Dictionary<string, int> { [project.Id] = 2 });

        var assignmentResponse = await store.RequestAssignmentAsync(request);
        var assignment = Assert.IsType<WorkAssignment>(assignmentResponse.Assignment);
        Assert.Equal(first.Id, assignment.ServerExecutionId);
        var beforeDisable = await Assert.ThrowsAsync<ProjectInUseException>(() => store.RemoveProjectAsync(project.Id, project.Revision));
        Assert.Equal(1, beforeDisable.Queued);
        Assert.Equal(1, beforeDisable.Assigned);
        Assert.Equal(0, beforeDisable.Running);

        var disabled = await store.UpdateProjectLifecycleAsync(project.Id, false, project.Revision);
        Assert.NotNull(disabled);
        await Assert.ThrowsAsync<ProjectDisabledException>(() => store.EnqueueExecutionAsync(
            new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3"))));
        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        await Assert.ThrowsAsync<ExecutionRequestTransitionException>(() => store.TransitionExecutionAsync(queued.Id,
            new ExecutionStateTransition("Assigned", workerId)));
        var paused = (await store.GetExecutionsAsync()).Single(execution => execution.Id == queued.Id);
        Assert.Equal("Queued", paused.State);
        Assert.Equal("project is disabled", paused.PendingReason);

        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        var started = await store.ReportExecutionAsync(first.Id, new WorkerExecutionReport(workerId, assignment.AssignmentId,
            "worker-run-1", "Running", "Codex", clock.GetUtcNow(), Generation: lease.Generation));
        Assert.Equal("Running", started!.State);
        var whileRunning = await Assert.ThrowsAsync<ProjectInUseException>(() => store.RemoveProjectAsync(project.Id, disabled.Revision));
        Assert.Equal(1, whileRunning.Queued);
        Assert.Equal(0, whileRunning.Assigned);
        Assert.Equal(1, whileRunning.Running);
        var completed = await store.ReportExecutionAsync(first.Id, new WorkerExecutionReport(workerId, assignment.AssignmentId,
            "worker-run-1", "Completed", CompletedAtUtc: clock.GetUtcNow(), Generation: lease.Generation));
        Assert.Equal("Completed", completed!.State);
        var queuedOnly = await Assert.ThrowsAsync<ProjectInUseException>(() => store.RemoveProjectAsync(project.Id, disabled.Revision));
        Assert.Equal(1, queuedOnly.Queued);
        Assert.Equal(0, queuedOnly.Assigned);
        Assert.Equal(0, queuedOnly.Running);

        var enabled = await store.UpdateProjectLifecycleAsync(project.Id, true, disabled.Revision);
        Assert.NotNull(enabled);
        Assert.True((await store.RequestAssignmentAsync(request)).HasWork);
        var secondExecution = Assert.Single(await store.GetExecutionsAsync(), execution => execution.Id == queued.Id);
        var secondLease = Assert.IsType<ExecutionLease>(secondExecution.Lease);
        var secondCompleted = await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(workerId,
            Assert.IsType<string>(secondExecution.AssignmentId), "worker-run-2", "Completed",
            CompletedAtUtc: clock.GetUtcNow(), Generation: secondLease.Generation));
        Assert.Equal("Completed", secondCompleted!.State);
        Assert.True(await store.RemoveProjectAsync(project.Id, enabled.Revision));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Fact]
    public async Task CentralProjectHttpApiRequiresAuthorizationAndExposesCrudContracts()
    {
        using var temporary = new TemporaryDirectory();
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "project-test-token");
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "server.db")));
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var workerId = Guid.NewGuid().ToString("N");
            const string workerToken = "project-snapshot-worker-token-with-sufficient-entropy";
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "snapshot worker", "1.0", "test", 1, []));
            var bootstrap = await store.CreateWorkerAuthorizationAsync(workerId, "rotate", TimeSpan.FromMinutes(1));
            Assert.True(await EnrollWorkerAsync(store, bootstrap, workerId, workerToken));
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var definition = new CentralProjectDefinition("Widget", "team/widget", "main", "Portable definition", [new("runtime", "node", "20.1")]);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/projects", definition)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "registration-only-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/projects", definition)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "project-test-token");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/projects", definition with { Repository = "invalid" })).StatusCode);
            using var create = await client.PostAsJsonAsync("/api/v1/projects", definition);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var project = (await create.Content.ReadFromJsonAsync<CentralProject>())!;
            Assert.Equal(new ProjectRequirement("runtime", "node", "20.1"), Assert.Single(project.Requirements));
            using var list = await client.GetAsync("/api/v1/projects");
            Assert.Equal(project.Requirements, Assert.Single(await list.Content.ReadFromJsonAsync<CentralProject[]>() ?? []).Requirements);
            using var invalidUpdate = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new ProjectUpdateRequest(definition with { Repository = "invalid" }, 1));
            Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
            using var beforeValidUpdate = await client.GetAsync($"/api/v1/projects/{project.Id}");
            Assert.Equal(1, (await beforeValidUpdate.Content.ReadFromJsonAsync<CentralProject>())!.Revision);
            using var update = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new ProjectUpdateRequest(definition with { Description = "Changed" }, 1));
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            using var stale = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new ProjectUpdateRequest(definition, 1));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var workerClient = new HttpClient { BaseAddress = new Uri(url) };
            workerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
            using var beforeLifecycleSnapshot = JsonDocument.Parse(await workerClient.GetStringAsync($"/api/v1/workers/{workerId}/configuration"));
            var beforeLifecycleVersion = beforeLifecycleSnapshot.RootElement.GetProperty("version").GetString();
            Assert.Equal(project.Id, beforeLifecycleSnapshot.RootElement.GetProperty("projects")[0].GetProperty("id").GetString());
            using var disable = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}/lifecycle",
                new ProjectLifecycleUpdateRequest(false, 2));
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
            var disabled = (await disable.Content.ReadFromJsonAsync<CentralProject>())!;
            Assert.False(disabled.Enabled);
            Assert.Equal(3, disabled.Revision);
            using var afterLifecycleSnapshot = JsonDocument.Parse(await workerClient.GetStringAsync($"/api/v1/workers/{workerId}/configuration"));
            Assert.NotEqual(beforeLifecycleVersion, afterLifecycleSnapshot.RootElement.GetProperty("version").GetString());
            var snapshotProject = afterLifecycleSnapshot.RootElement.GetProperty("projects")[0];
            Assert.False(snapshotProject.GetProperty("enabled").GetBoolean());
            Assert.Equal(project.Requirements.Count, snapshotProject.GetProperty("requirements").GetArrayLength());
            using var staleLifecycle = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}/lifecycle",
                new ProjectLifecycleUpdateRequest(true, 2));
            Assert.Equal(HttpStatusCode.Conflict, staleLifecycle.StatusCode);
            using var rejectedEnqueue = await client.PostAsJsonAsync("/api/v1/executions",
                new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3")));
            Assert.Equal(HttpStatusCode.Conflict, rejectedEnqueue.StatusCode);
            using var delete = await client.DeleteAsync($"/api/v1/projects/{project.Id}?expectedRevision=3");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", prior);
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CentralProjectDeleteApiReportsQueuedReferencesAndPreservesProject()
    {
        using var temporary = new TemporaryDirectory();
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "project-delete-token");
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "server.db")));
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var project = await store.CreateProjectAsync(new CentralProjectDefinition("Protected", "team/protected", "main", "", []));
            var execution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "42")));
            await store.UpdateProjectLifecycleAsync(project.Id, false, project.Revision);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "project-delete-token");

            using var assignment = await client.PostAsJsonAsync($"/api/v1/executions/{execution.Id}/state",
                new ExecutionStateTransition("Assigned", "manual-worker"));
            Assert.Equal(HttpStatusCode.Gone, assignment.StatusCode);
            Assert.Contains("Worker assignment", await assignment.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            using var delete = await client.DeleteAsync($"/api/v1/projects/{project.Id}?expectedRevision=2");

            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            using var diagnostic = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
            Assert.Equal(1, diagnostic.RootElement.GetProperty("queued").GetInt32());
            Assert.Equal(0, diagnostic.RootElement.GetProperty("assigned").GetInt32());
            Assert.Equal(0, diagnostic.RootElement.GetProperty("running").GetInt32());
            Assert.NotNull(await store.GetProjectAsync(project.Id));
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void CentralProjectRequirementsRejectMalformedVersionsAndContradictionsAndReadLegacyValues()
    {
        static CentralProjectDefinition Definition(params ProjectRequirement[] requirements) =>
            new("Compiler", "team/compiler", "main", "", requirements);

        Assert.NotNull(CentralProjectValidation.Error(Definition(new ProjectRequirement("runtime", "dotnet", "~10"))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new ProjectRequirement("runtime", "dotnet"), new ProjectRequirement("Runtime", "Dotnet", ">=10"))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new ProjectRequirement("runtime", "dotnet", ">=10..1"))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new ProjectRequirement("tool", "git", Scope: "team/compiler"))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new ProjectRequirement("authentication", "github-api", Scope: "invalid"))));
        var legacy = JsonSerializer.Deserialize<ProjectRequirement>("\"dotnet:10\"");
        Assert.Equal(new ProjectRequirement("runtime", "dotnet", "10"), legacy);
        Assert.Equal("{\"type\":\"runtime\",\"name\":\"dotnet\",\"version\":\"10\"}", JsonSerializer.Serialize(legacy));
        var scoped = JsonSerializer.Deserialize<ProjectRequirement>("{\"type\":\"authentication\",\"name\":\"github-api\",\"scope\":\"team/compiler\"}");
        Assert.Equal("team/compiler", scoped?.Scope);
        Assert.Contains("\"scope\":\"team/compiler\"", JsonSerializer.Serialize(scoped));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void AutomaticDiscoveryDefaultsAndBoundsAreValidatedEvenWhenDisabled()
    {
        var defaults = new AutomaticIssueDiscovery();
        Assert.False(defaults.Enabled);
        Assert.Equal(300, defaults.IntervalSeconds);
        Assert.Equal(25, defaults.PageSize);
        Assert.Equal(120, defaults.DeadlineSeconds);

        static CentralProjectDefinition Definition(AutomaticIssueDiscovery policy) =>
            new("Compiler", "team/compiler", "main", "", [], AutomaticDiscovery: policy);

        Assert.Null(CentralProjectValidation.Error(Definition(new(false, 30, 1, 10))));
        Assert.Null(CentralProjectValidation.Error(Definition(new(false, 86400, 100, 120))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new(false, 29, 25, 120))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new(false, 86401, 25, 120))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new(false, 300, 0, 120))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new(false, 300, 101, 120))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new(false, 300, 25, 9))));
        Assert.NotNull(CentralProjectValidation.Error(Definition(new(false, 300, 25, 121))));
    }
}
