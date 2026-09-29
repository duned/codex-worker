namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

[CollectionDefinition("ServerTokenEnvironment", DisableParallelization = true)]
public sealed class ServerTokenEnvironmentCollection { }

[Collection("ServerTokenEnvironment")]
public sealed class CodexServerTests
{
    [Fact]
    public async Task RegistrationIsAuthenticatedIdempotentAndDurableAcrossServerRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var port = ReservePort();
        var url = $"http://127.0.0.1:{port}";
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "test-registration-token");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "test-management-token");
        try
        {
            var app = await ServerApplication.BuildAsync(Args(url, database));
            await app.StartAsync();
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                var workerId = Guid.NewGuid().ToString("N");
                var request = new { contractVersion = 1, workerId, displayName = "test worker", workerVersion = "1.2.3",
                    platform = "test", capacity = 2, capabilities = new[] { "git", "codex-cli" } };
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-registration-token");
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                request = new { contractVersion = 1, workerId, displayName = "renamed worker", workerVersion = "1.2.4",
                    platform = "test", capacity = 3, capabilities = new[] { "git", "updated" } };
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                var heartbeat = new { contractVersion = 1, workerId, workerVersion = "1.2.4", lifecycleState = "running",
                    activeExecutions = 1, maximumCapacity = 3, capabilities = new[] { "git", "updated" }, activeProjects = new[] { "project-a" } };
                client.DefaultRequestHeaders.Authorization = null;
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/workers/{workerId}/heartbeat", heartbeat)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-registration-token");
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/workers/{workerId}/heartbeat", heartbeat)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/workers/{workerId}/heartbeat", heartbeat)).StatusCode);
                client.DefaultRequestHeaders.Authorization = null;
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/workers")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/workers/{workerId}")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
                using var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Single(list.RootElement.EnumerateArray());
                var item = list.RootElement[0];
                Assert.Equal("renamed worker", item.GetProperty("displayName").GetString());
                Assert.Equal(3, item.GetProperty("capacity").GetInt32());
                Assert.Equal("online", item.GetProperty("availability").GetString());
                Assert.Equal(1, item.GetProperty("activeExecutions").GetInt32());
                Assert.Equal(2, item.GetProperty("availableCapacity").GetInt32());
                Assert.Equal("project-a", item.GetProperty("activeProjects")[0].GetString());
                Assert.True(item.TryGetProperty("firstRegisteredAtUtc", out _));
                Assert.True(item.TryGetProperty("lastSeenAtUtc", out _));
                Assert.DoesNotContain("token", item.ToString(), StringComparison.OrdinalIgnoreCase);
                using (var streamResponse = await client.GetAsync("/api/v1/events/stream", HttpCompletionOption.ResponseHeadersRead))
                {
                    Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
                    await using var stream = await streamResponse.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);
                    Assert.Equal("event: workers", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                    Assert.StartsWith("data: ", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                }
                var otherId = Guid.NewGuid().ToString("N");
                var other = new { contractVersion = 1, workerId = otherId, displayName = "second", workerVersion = "1.2.3",
                    platform = "test", capacity = 1, capabilities = new[] { "git" } };
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-registration-token");
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{otherId}", other)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
                using var twoWorkers = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Equal(2, twoWorkers.RootElement.GetArrayLength());
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-registration-token");
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/workers/{Guid.NewGuid():N}", request)).StatusCode);
            }
            await app.StopAsync();
            await app.DisposeAsync();
            var restarted = await ServerApplication.BuildAsync(Args(url, database));
            await restarted.StartAsync();
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
                using var persisted = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Equal(2, persisted.RootElement.GetArrayLength());
                Assert.Contains(persisted.RootElement.EnumerateArray(), worker => worker.GetProperty("displayName").GetString() == "renamed worker");
            }
            await restarted.StopAsync();
            await restarted.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", prior);
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
        }
    }

    [Fact]
    public async Task HeartbeatAvailabilityTransitionsStaleAndReconnectsWithDeterministicClock()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), 30, clock);
        await store.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 2, ["git"]));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 2, 2, ["git"], ["one", "two"]));
        Assert.Equal("online", (await store.GetWorkerAsync(workerId))!.Availability);
        Assert.Equal(0, (await store.GetWorkerAsync(workerId))!.AvailableCapacity);
        clock.Advance(TimeSpan.FromSeconds(31));
        var stale = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("stale", stale.Availability);
        Assert.Equal(0, stale.ActiveExecutions);
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.1", "running", 1, 3, ["git", "new-capability"], ["three"]));
        var reconnected = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("online", reconnected.Availability);
        Assert.Equal(2, reconnected.AvailableCapacity);
        Assert.Contains("new-capability", reconnected.Capabilities);
    }

    [Fact]
    public async Task ServerStartsAndExposesStatusHealthAndVersion()
    {
        using var temporary = new TemporaryDirectory();
        var port = ReservePort();
        var url = $"http://127.0.0.1:{port}";
        await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "server.db")));
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            using var dashboardResponse = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
            Assert.Contains("Codex Server", await dashboardResponse.Content.ReadAsStringAsync());
            Assert.Contains("/api/v1/events/stream", await dashboardResponse.Content.ReadAsStringAsync());
            Assert.Contains("Worker details", await dashboardResponse.Content.ReadAsStringAsync());
            using var statusResponse = await client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal("ready", status.RootElement.GetProperty("state").GetString());
            Assert.Equal(ServerApplication.DisplayVersion, status.RootElement.GetProperty("version").GetString());
            Assert.True(status.RootElement.TryGetProperty("startedAtUtc", out _));

            using var versionResponse = await client.GetAsync("/api/version");
            using var version = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
            Assert.Equal(ServerApplication.DisplayVersion, version.RootElement.GetProperty("version").GetString());
            Assert.Equal("Codex Server", version.RootElement.GetProperty("product").GetString());

            using var healthResponse = await client.GetAsync("/health");
            using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            Assert.Equal("healthy", health.RootElement.GetProperty("status").GetString());
            Assert.True(health.RootElement.GetProperty("persistenceAvailable").GetBoolean());

        }
        finally { await app.StopAsync(); }
    }

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

    [Fact]
    public async Task CentralProjectsSupportValidatedCrudDurabilityUniquenessAndRevisionConflicts()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "projects.db"));
        await store.InitializeAsync();
        Assert.Null(CentralProjectValidation.Error(new CentralProjectDefinition("Compiler", "team/compiler", "main", "", ["dotnet:10", "postgresql"])));
        Assert.NotNull(CentralProjectValidation.Error(new CentralProjectDefinition("", "bad", "", "", [])));
        var definition = new CentralProjectDefinition("Compiler", "team/compiler", "main", "Compiler source", ["dotnet:10"]);
        var created = await store.CreateProjectAsync(definition);
        Assert.Equal("compiler", created.Id);
        Assert.Equal(1, created.Revision);
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
        Assert.Contains(persisted.Description, new[] { "Updated A", "Updated B" });
        Assert.True(await restarted.RemoveProjectAsync(created.Id, 2));
        Assert.Empty(await restarted.GetProjectsAsync());
    }

    private static async Task<CentralProject?> AttemptUpdateAsync(SqliteRegistryStore store, string id, CentralProjectDefinition definition)
    {
        try { return await store.UpdateProjectAsync(id, definition, 1); }
        catch (ProjectRevisionConflictException) { return null; }
    }

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
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var definition = new CentralProjectDefinition("Widget", "team/widget", "main", "Portable definition", ["node"]);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/projects", definition)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "registration-only-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/projects", definition)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "project-test-token");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/projects", definition with { Repository = "invalid" })).StatusCode);
            using var create = await client.PostAsJsonAsync("/api/v1/projects", definition);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var project = (await create.Content.ReadFromJsonAsync<CentralProject>())!;
            using var list = await client.GetAsync("/api/v1/projects");
            Assert.Single(await list.Content.ReadFromJsonAsync<CentralProject[]>() ?? []);
            using var invalidUpdate = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new ProjectUpdateRequest(definition with { Repository = "invalid" }, 1));
            Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
            using var beforeValidUpdate = await client.GetAsync($"/api/v1/projects/{project.Id}");
            Assert.Equal(1, (await beforeValidUpdate.Content.ReadFromJsonAsync<CentralProject>())!.Revision);
            using var update = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new ProjectUpdateRequest(definition with { Description = "Changed" }, 1));
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            using var stale = await client.PutAsJsonAsync($"/api/v1/projects/{project.Id}", new ProjectUpdateRequest(definition, 1));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var delete = await client.DeleteAsync($"/api/v1/projects/{project.Id}?expectedRevision=2");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", prior);
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
        }
    }

    [Fact]
    public async Task ExecutionQueuePersistsFifoTransitionsAndAllowsLaterAttemptsAcrossProjects()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "queue.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var projectA = await store.CreateProjectAsync(new CentralProjectDefinition("Alpha", "team/alpha", "main", "", []));
        var projectB = await store.CreateProjectAsync(new CentralProjectDefinition("Beta", "team/beta", "main", "", []));
        var work = new WorkReference("github-issue", "42", "https://github.com/team/alpha/issues/42");
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, work));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, new WorkReference("github-issue", "43")));
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, work)));
        var sameIssueOtherProject = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectB.Id, work));
        Assert.Equal(new[] { first.Id, second.Id, sameIssueOtherProject.Id }, (await store.GetExecutionsAsync()).Select(x => x.Id));

        var assigned = await store.TransitionExecutionAsync(first.Id, new ExecutionStateTransition("Assigned", "worker-a"));
        Assert.Equal("Assigned", assigned!.State);
        Assert.Equal("worker-a", assigned.AssignedWorkerId);
        Assert.NotNull(assigned.AssignedAtUtc);
        var started = DateTimeOffset.Parse("2026-02-01T00:00:03Z");
        var running = await store.ReportExecutionAsync(first.Id, new WorkerExecutionReport("worker-a", assigned.AssignmentId!, "run-a", "Running", "Implementing", started));
        Assert.Equal("Running", running!.State);
        Assert.Equal("run-a", running.ExecutionId);
        var finalReport = new WorkerExecutionReport("worker-a", assigned.AssignmentId!, "run-a", "Completed", null,
            started, started.AddMinutes(2), 120000, "passed", "integrated", null, false, "Implemented Issue #42.");
        var completed = await store.ReportExecutionAsync(first.Id, finalReport);
        var duplicate = await store.ReportExecutionAsync(first.Id, finalReport);
        Assert.Equal("Completed", completed!.State);
        Assert.Equal(completed, duplicate);
        Assert.Equal("Implementing", completed.CurrentStage);
        Assert.Equal("run-a", completed.WorkerExecutionId);
        Assert.Equal("passed", completed.ValidationResult);
        Assert.Equal("integrated", completed.IntegrationResult);
        Assert.Equal("Implemented Issue #42.", completed.CompletionSummary);
        var retry = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, work));
        Assert.NotEqual(first.Id, retry.Id);

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        var persisted = await restarted.GetExecutionsAsync();
        Assert.Equal(new[] { "Completed", "Queued", "Queued", "Queued" }, persisted.Select(x => x.State));
        Assert.Equal(new[] { projectA.Id, projectA.Id, projectB.Id, projectA.Id }, persisted.Select(x => x.ProjectId));
        Assert.Equal(first.Id, persisted[0].Id);
        Assert.Equal(work, persisted[0].WorkReference);
        Assert.Equal("Completed", persisted[0].State);
        Assert.Equal(120000, persisted[0].DurationMilliseconds);
        Assert.Equal(started.AddMinutes(2), persisted[0].CompletedAtUtc);
    }

    [Fact]
    public async Task WorkerAssignmentRequestsRespectEligibilityCapacityAndRetainOwnershipAfterRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "assignments.db");
        var url = $"http://127.0.0.1:{ReservePort()}";
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "assignment-test-token");
        var workerA = Guid.NewGuid().ToString("N");
        var workerB = Guid.NewGuid().ToString("N");
        var firstAssignmentId = "";
        var alphaId = "";
        try
        {
            var app = await ServerApplication.BuildAsync(Args(url, database));
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var alpha = await store.CreateProjectAsync(new CentralProjectDefinition("Alpha", "team/alpha", "main", "", []));
            var beta = await store.CreateProjectAsync(new CentralProjectDefinition("Beta", "team/beta", "main", "", []));
            alphaId = alpha.Id;
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(alpha.Id, new WorkReference("issue", "1")));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(alpha.Id, new WorkReference("issue", "2")));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(beta.Id, new WorkReference("issue", "3")));
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "assignment-test-token");
                foreach (var id in new[] { workerA, workerB })
                {
                    var registration = new WorkerRegistrationRequest(1, id, id, "1.0", "test", 2, ["git"]);
                    Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{id}", registration)).StatusCode);
                }
                async Task Heartbeat(string id, string state) => Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/v1/workers/{id}/heartbeat",
                        new WorkerHeartbeatRequest(1, id, "1.0", state, 0, 2, ["git"], []))).StatusCode);
                await Heartbeat(workerA, "running");
                await Heartbeat(workerB, "draining");

                async Task<HttpResponseMessage> Request(string id, bool enabled, int capacity, Dictionary<string, int> projectCapacities) =>
                    await client.PostAsJsonAsync($"/api/v1/workers/{id}/assignments/request",
                        new WorkerAssignmentRequest(id, enabled, capacity, projectCapacities));

                using var disabled = await Request(workerA, false, 2, new() { [alpha.Id] = 2 });
                Assert.False((await disabled.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);
                using var draining = await Request(workerB, true, 2, new() { [alpha.Id] = 2 });
                Assert.False((await draining.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);
                using var zeroCapacity = await Request(workerA, true, 0, new() { [alpha.Id] = 2 });
                Assert.False((await zeroCapacity.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);

                using var firstResponse = await Request(workerA, true, 2, new() { [alpha.Id] = 1, [beta.Id] = 1 });
                Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
                var first = (await firstResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!;
                Assert.True(first.HasWork);
                Assert.Equal(workerA, first.Assignment!.WorkerId);
                Assert.Equal(alpha.Id, first.Assignment.Project.Id);
                Assert.NotEqual(first.Assignment.AssignmentId, first.Assignment.ServerExecutionId);
                firstAssignmentId = first.Assignment.AssignmentId;

                // A project with no remaining declared slot is skipped while other projects remain eligible.
                using var secondResponse = await Request(workerA, true, 2, new() { [alpha.Id] = 1, [beta.Id] = 1 });
                var second = (await secondResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!;
                Assert.True(second.HasWork);
                Assert.Equal(beta.Id, second.Assignment!.Project.Id);
                var report = new WorkerExecutionReport(workerA, second.Assignment.AssignmentId, "worker-run-2", "Running", "Validation",
                    DateTimeOffset.UtcNow);
                using var lifecycle = await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/executions/{second.Assignment.ServerExecutionId}/report", report);
                Assert.Equal(HttpStatusCode.OK, lifecycle.StatusCode);
                report = report with { State = "Failed", Stage = null, CompletedAtUtc = DateTimeOffset.UtcNow,
                    FailureClassification = "TaskFailure", Recoverable = true, Summary = "Validation could not be repaired." };
                using var final = await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/executions/{second.Assignment.ServerExecutionId}/report", report);
                Assert.Equal(HttpStatusCode.OK, final.StatusCode);
                var reported = await final.Content.ReadFromJsonAsync<ExecutionRequest>();
                Assert.Equal("Failed", reported!.State);
                Assert.True(reported.Recoverable);
                Assert.Equal("TaskFailure", reported.FailureClassification);
                await Heartbeat(workerB, "running");
                using var otherWorkerResponse = await Request(workerB, true, 2, new() { [alpha.Id] = 1 });
                var otherWorkerAssignment = (await otherWorkerResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!;
                Assert.True(otherWorkerAssignment.HasWork);
                Assert.Equal(workerB, otherWorkerAssignment.Assignment!.WorkerId);
                Assert.Equal(alpha.Id, otherWorkerAssignment.Assignment.Project.Id);

                // The uncertain-delivery case is equivalent to dropping the response: server ownership is already durable.
                var owned = Assert.Single((await store.GetExecutionsAsync()).Where(x => x.AssignmentId == first.Assignment.AssignmentId));
                Assert.Equal("Assigned", owned.State);
                Assert.Equal(workerA, owned.AssignedWorkerId);
                Assert.Equal(first.Assignment.ServerExecutionId, owned.Id);
                await app.StopAsync();
                await app.DisposeAsync();
            }

            var restarted = await ServerApplication.BuildAsync(Args(url, database));
            await restarted.StartAsync();
            var restartedStore = restarted.Services.GetRequiredService<IRegistryStore>();
            var retained = Assert.Single((await restartedStore.GetExecutionsAsync()).Where(x => x.AssignmentId == firstAssignmentId));
            Assert.Equal("Assigned", retained.State);
            Assert.Equal(workerA, retained.AssignedWorkerId);
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "assignment-test-token");
                using var noWork = await client.PostAsJsonAsync($"/api/v1/workers/{workerB}/assignments/request",
                    new WorkerAssignmentRequest(workerB, true, 2, new Dictionary<string, int> { [alphaId] = 2 }));
                Assert.False((await noWork.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);
            }
            await restarted.StopAsync();
            await restarted.DisposeAsync();

        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", prior); }
    }

    [Fact]
    public async Task ExecutionQueueApiRequiresManagementTokenAndReturnsStructuredRequests()
    {
        using var temporary = new TemporaryDirectory();
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "execution-test-token");
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "server.db")));
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var projectDefinition = new CentralProjectDefinition("Queue project", "team/queue", "main", "", []);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "execution-test-token");
            var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", projectDefinition);
            var project = (await projectResponse.Content.ReadFromJsonAsync<CentralProject>())!;
            client.DefaultRequestHeaders.Authorization = null;
            var request = new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "7", "https://example.test/7"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/executions", request)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "execution-test-token");
            var created = await client.PostAsJsonAsync("/api/v1/executions", request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var representation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.Equal(project.Id, representation.RootElement.GetProperty("projectId").GetString());
            Assert.Equal("Queued", representation.RootElement.GetProperty("state").GetString());
            Assert.Equal("7", representation.RootElement.GetProperty("workReference").GetProperty("id").GetString());
            Assert.True(representation.RootElement.TryGetProperty("createdAtUtc", out _));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/executions", request)).StatusCode);
            var list = await client.GetFromJsonAsync<ExecutionRequest[]>("/api/v1/executions");
            Assert.Equal("Queued", Assert.Single(list!).State);
            Assert.Contains("Execution queue", await (await client.GetAsync("/")).Content.ReadAsStringAsync());
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", prior); }
    }

    [Theory]
    [InlineData("file:///tmp/server")]
    [InlineData("http://user:pass@127.0.0.1:5090")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://0.0.0.0:5090")]
    public async Task InvalidListenConfigurationIsRejected(string listenUrl)
    {
        using var temporary = new TemporaryDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(Args(listenUrl, Path.Combine(temporary.Path, "server.db"))));
    }

    [Fact]
    public async Task EmptyPersistenceLocationIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(["--Server:ListenUrl=http://127.0.0.1:5090", "--Server:DatabasePath="]));
    }

    private static string[] Args(string url, string databasePath) =>
        [$"--Server:ListenUrl={url}", $"--Server:DatabasePath={databasePath}"];

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-server-test-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
