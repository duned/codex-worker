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
    public async Task GuidedAuthorizationUsesManagementBoundaryAndExactHttpsDestinationWithoutGrantingReadiness()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "onboarding.db");
        var origin = $"http://127.0.0.1:{ReservePort()}";
        const string destination = "https://server.example";
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "onboarding-management-token");
        try
        {
            await using var app = await ServerApplication.BuildAsync([.. Args(origin, database),
                $"--Server:AdministrationOrigin={destination}"]);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(origin) };
            var request = new WorkerPairingRequest(1, Guid.NewGuid().ToString("N"), "enroll", destination);
            using (var denied = await client.PostAsJsonAsync("/api/v1/workers/onboarding/authorize", request))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "onboarding-management-token");
            foreach (var invalid in new[] { request with { Server = "https://wrong.example" },
                request with { Operation = "recover" }, request with { Server = "http://server.example" } })
            {
                using var rejected = await client.PostAsJsonAsync("/api/v1/workers/onboarding/authorize", invalid);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            using var authorized = await client.PostAsJsonAsync("/api/v1/workers/onboarding/authorize", request);
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            Assert.True(authorized.Headers.CacheControl?.NoStore);
            using var result = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync());
            var authorization = result.RootElement.GetProperty("authorization").GetString();
            Assert.NotNull(authorization);
            Assert.Equal(900, result.RootElement.GetProperty("lifetimeSeconds").GetInt32());
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var registration = new WorkerRegistrationRequest(2, request.WorkerId, "guided Worker", "1.0", "test", 1, []);
            const string durableToken = "durable-node-only-credential-with-entropy";
            var otherServer = new SqliteRegistryStore(Path.Combine(temporary.Path, "other.db"));
            await otherServer.InitializeAsync();
            Assert.False(await otherServer.BootstrapWorkerAsync(authorization, registration, durableToken));
            Assert.True(await store.BootstrapWorkerAsync(authorization, registration, durableToken));
            using (var duplicate = await client.PostAsJsonAsync("/api/v1/workers/onboarding/authorize", request))
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            var worker = await store.GetWorkerAsync(request.WorkerId);
            Assert.NotNull(worker);
            Assert.Null(worker.LastHeartbeatAtUtc);
            Assert.Equal(WorkerSchedulingPolicy.Disabled, worker.SchedulingPolicy);
            await store.HeartbeatWorkerAsync(new(2, request.WorkerId, "1.0", "starting", 0, 1, [], []));
            worker = await store.GetWorkerAsync(request.WorkerId);
            Assert.NotNull(worker);
            Assert.NotNull(worker.LastHeartbeatAtUtc);
            Assert.Equal(WorkerSchedulingPolicy.Disabled, worker.SchedulingPolicy);
            var association = request with { Operation = "associate" };
            using var associationResponse = await client.PostAsJsonAsync("/api/v1/workers/onboarding/authorize", association);
            Assert.Equal(HttpStatusCode.OK, associationResponse.StatusCode);
            using var associationResult = JsonDocument.Parse(await associationResponse.Content.ReadAsStringAsync());
            var associationToken = associationResult.RootElement.GetProperty("authorization").GetString();
            Assert.NotNull(associationToken);
            var project = await store.CreateProjectAsync(new CentralProjectDefinition("Onboarding", "team/onboarding", "main", "", []));
            WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
            await store.RegisterWorkerAsync(registration with { Capabilities = capabilities });
            await store.HeartbeatWorkerAsync(new(2, request.WorkerId, "1.0", "running", 0, 1, capabilities, []));
            await store.SetWorkerSchedulingPolicyAsync(request.WorkerId, WorkerSchedulingPolicy.Enabled);
            await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "1")));
            var assigned = await store.RequestAssignmentAsync(new(request.WorkerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 }));
            Assert.NotNull(assigned.Assignment);
            using (var rejected = await client.PostAsJsonAsync("/api/v1/workers/onboarding/authorize", association))
                Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            // Check again at consumption: assignments can arrive after authorization was issued.
            Assert.False(await store.BootstrapWorkerAsync(associationToken, registration, new string('z', 64), operation: "associate"));
            Assert.True(await store.IsWorkerTokenValidAsync(request.WorkerId, durableToken));
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerRoutesRequireExactDurableIdentityAndRetainOwnershipChecks()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "isolation.db");
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        var priorRegistration = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        const string managementToken = "isolation-management-token";
        const string legacyToken = "isolation-legacy-shared-token";
        const string tokenA = "individual-worker-a-token-with-entropy";
        const string tokenB = "individual-worker-b-token-with-entropy";
        const string deliveryToken = "isolated-credential-delivery-token";
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", managementToken);
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", legacyToken);
        var workerA = Guid.NewGuid().ToString("N");
        var workerB = Guid.NewGuid().ToString("N");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, database));
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            WorkerRegistrationRequest Registration(string id) => new(2, id, id, "1.0", "test", 1, []);
            foreach (var (id, token) in new[] { (workerA, tokenA), (workerB, tokenB) })
            {
                var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
                Assert.True(await store.BootstrapWorkerAsync(bootstrap, Registration(id), token));
            }
            var unusedBootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            await app.Services.GetRequiredService<ICredentialStore>().SetWorkerDeliveryTokenAsync(workerB, new CredentialSecretInput(deliveryToken));
            var plan = await store.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerB, []));
            var commands = app.Services.GetRequiredService<ProvisioningCommandStore>();
            var command = await commands.CreateAsync(new(workerB, "git", ProvisioningCommandAction.Detect));
            var project = await store.CreateProjectAsync(new CentralProjectDefinition("Isolation", "team/isolation", "main", "", []));
            var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
            // Registry assignment is deterministic and uses the established readiness contract.
            WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
            await store.RegisterWorkerAsync(Registration(workerB) with { Capabilities = capabilities });
            await store.HeartbeatWorkerAsync(new(2, workerB, "1.0", "running", 0, 1, capabilities, []));
            var assigned = (await store.RequestAssignmentAsync(new(workerB, true, 1, new Dictionary<string, int> { [project.Id] = 1 }))).Assignment;
            Assert.NotNull(assigned);
            Assert.NotNull(assigned.Lease);
            var report = new WorkerExecutionReport(workerB, assigned.AssignmentId, "run-b", "Running", "Codex", Generation: assigned.Lease.Generation);
            var renewal = new ExecutionLeaseRenewal(workerB, assigned.Lease.Generation);
            var commandReport = new ProvisioningCommandReport(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed);
            (HttpMethod Method, string Path, object? Body)[] Routes(string pathId, string bodyId) =>
            [
                (HttpMethod.Put, $"/api/v1/workers/{pathId}", Registration(bodyId)),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/heartbeat", new WorkerHeartbeatRequest(2, bodyId, "1.0", "running", 0, 1, [], [])),
                (HttpMethod.Get, $"/api/v1/workers/{pathId}/configuration", null),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/assignments/request", new WorkerAssignmentRequest(bodyId, true, 1, new Dictionary<string, int>())),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/executions/{queued.Id}/report", report with { WorkerId = bodyId }),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/executions/{queued.Id}/lease/renew", renewal with { WorkerId = bodyId }),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/provisioning/request", null),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/provisioning/{plan.Id}/report", new ProvisioningWorkerReport(bodyId, "Completed")),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/provisioning/commands/request", null),
                (HttpMethod.Post, $"/api/v1/workers/{pathId}/provisioning/commands/{command.Id}/report", commandReport)
            ];
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            async Task AssertRoutes(string token, string pathId, string bodyId, HttpStatusCode expected, bool bodiesOnly = false)
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                foreach (var route in Routes(pathId, bodyId))
                {
                    if (bodiesOnly && (route.Body is null || route.Body is ProvisioningCommandReport)) continue;
                    using var request = new HttpRequestMessage(route.Method, route.Path);
                    if (route.Body is not null) request.Content = JsonContent.Create(route.Body, route.Body.GetType());
                    using var response = await client.SendAsync(request);
                    Assert.Equal(expected, response.StatusCode);
                }
            }
            foreach (var token in new[] { tokenA, managementToken, legacyToken, deliveryToken, unusedBootstrap, "invalid-token" })
                await AssertRoutes(token, workerB, workerB, HttpStatusCode.Unauthorized);
            await AssertRoutes(tokenB, workerB, workerA, HttpStatusCode.BadRequest, bodiesOnly: true);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenA);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/workers")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/projects", new CentralProjectDefinition("Denied", "team/denied", "main", "", []))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/executions/{queued.Id}/report", report with { WorkerId = workerA })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/executions/{queued.Id}/lease/renew", renewal with { WorkerId = workerA })).StatusCode);
            await store.AcceptProvisioningPlanAsync(workerB);
            await commands.ClaimAsync(workerB);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/provisioning/{plan.Id}/report", new ProvisioningWorkerReport(workerA, "Completed"))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/provisioning/commands/{command.Id}/report", commandReport)).StatusCode);
            Assert.Equal("Assigned", (await store.GetExecutionAsync(queued.Id))?.State);
            Assert.Equal(assigned.Lease, (await store.GetExecutionAsync(queued.Id))?.Lease);
            Assert.Equal("Accepted", (await store.GetProvisioningPlanAsync(plan.Id))?.State);
            Assert.Equal(ProvisioningCommandStatus.Running, (await commands.GetAsync(command.Id))?.Status);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenB);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/workers/{workerB}/configuration")).StatusCode);
            await store.RevokeWorkerTokenAsync(workerB);
            await AssertRoutes(tokenB, workerB, workerB, HttpStatusCode.Unauthorized);
            await app.StopAsync();
            await app.DisposeAsync();
            await using var restarted = await ServerApplication.BuildAsync(Args(url, database));
            await restarted.StartAsync();
            await AssertRoutes(tokenB, workerB, workerB, HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenA);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/workers/{workerA}/configuration")).StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", priorRegistration);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ManagedObservationsSurviveRegistryStorageAndRemainDistinctFromServerEligibility()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), 30, clock);
        await store.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        WorkerCapability[] capabilities = [new("authentication", "github-api", Scope: "owner/repo"),
            new("authentication", "git-repository", Scope: "owner/repo"), new("agent-provider", "codex")];
        await store.RegisterWorkerAsync(new(2, workerId, "Worker", "1", "linux", 1, capabilities));
        var observations = new ManagedWorkerDiagnostics("retrieved", "synchronized", "server-retrieved", "version", "version",
            null, null, [new("project", 1, "failed", "project-preparation-failed")]);
        await store.HeartbeatWorkerAsync(new(2, workerId, "1", "not-ready", 0, 1, capabilities, [],
            "synchronized", "version", ManagedDiagnostics: observations));
        var project = new CentralProject("project", "Project", "owner/repo", "main", "", [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var worker = Assert.IsType<WorkerRegistrationResponse>(await store.GetWorkerAsync(workerId));
        var diagnostics = WorkerDiagnosticsDerivation.Derive(worker, [project], [], "version");

        var readiness = Assert.Single(diagnostics.Projects);
        Assert.True(readiness.IsEligible);
        Assert.Equal("failed", readiness.MaterializationState);
        Assert.Equal("project-preparation-failed", readiness.DiagnosticCode);
        Assert.Equal("worker-reported-current-revision", readiness.ObservationStatus);
        Assert.Equal(observations.Projects, diagnostics.WorkerReportedManagedDiagnostics?.Projects);
        Assert.Contains(diagnostics.Reasons, reason => reason.Contains("project-preparation-failed", StringComparison.Ordinal));
        Assert.Equal("stale-revision", Assert.Single(WorkerDiagnosticsDerivation.Derive(worker,
            [project with { Revision = 2 }], [], "version").Projects).ObservationStatus);
        clock.Advance(TimeSpan.FromSeconds(31));
        var stale = Assert.IsType<WorkerRegistrationResponse>(await store.GetWorkerAsync(workerId));
        Assert.Equal("stale-heartbeat", Assert.Single(WorkerDiagnosticsDerivation.Derive(stale, [project], [], "version").Projects).ObservationStatus);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ManagedDiagnosticsRejectRawOutputAndUnboundedPayloads()
    {
        var diagnostics = new ManagedWorkerDiagnostics("retrieved", "synchronized", "server-retrieved", "v", "v", null, null,
            [new("project", 1, "ready")]);
        Assert.True(ManagedWorkerDiagnostics.Valid(diagnostics));
        Assert.False(ManagedWorkerDiagnostics.Valid(diagnostics with { DiagnosticCode = "raw command output" }));
        Assert.False(ManagedWorkerDiagnostics.Valid(diagnostics with { Projects = [new("project", 1, "failed", "token=private-value")] }));
        Assert.False(ManagedWorkerDiagnostics.Valid(diagnostics with { Projects = Enumerable.Range(0, 1001)
            .Select(index => new ManagedProjectDiagnostic(index.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, "ready")).ToArray() }));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task MissingLocalAuthenticationBlocksAssignmentDespiteRunningHeartbeat()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "readiness.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Readiness", "team/readiness", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(2, workerId, "worker", "1.0", "test", 1, capabilities));
        var inventory = CapabilityCatalog.Definitions.Where(item => item.RequiredForExecution).Select(definition =>
            CapabilityCatalog.Unknown(definition) with
            {
                Installation = InstallationState.Installed, Health = CapabilityHealth.Healthy,
                Authentication = definition.RequiresAuthentication ? RequirementState.Satisfied : null,
                Configuration = definition.RequiresConfiguration ? RequirementState.Satisfied : null,
                DetectedAtUtc = DateTimeOffset.UtcNow, DiagnosticCode = null
            }).ToArray();
        var missingAuth = inventory.Select(state => state.Id == "github-cli"
            ? state with { Authentication = RequirementState.Required, DiagnosticCode = "authentication-required" } : state).ToArray();
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(2, workerId, "1.0", "running", 0, 1,
            capabilities, [], CapabilityInventory: missingAuth));
        await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var request = new WorkerAssignmentRequest(workerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        var worker = await store.GetWorkerAsync(workerId);
        Assert.NotNull(worker);
        var node = NodeProvisioning.Describe(worker, []);
        Assert.Equal("healthy", node.Health);
        Assert.Equal("connected", node.Connectivity);
        Assert.Equal("not-ready", node.ExecutionReadiness);
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(2, workerId, "1.0", "running", 0, 1,
            capabilities, [], CapabilityInventory: inventory));
        Assert.True((await store.RequestAssignmentAsync(request)).HasWork);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Fact]
    public async Task TypedProvisioningApiRejectsShellFieldsAndRunsKnownLocalAction()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "test-management-token");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "registry.db")),
                capabilityDiscovery: TestCapabilityDiscovery.Create());
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var request = new CodexProvisioning.ProvisioningCommandRequest("server", "git", CodexProvisioning.ProvisioningCommandAction.Detect);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/provisioning/commands", request)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
            using var arbitrary = new StringContent("""
                {"nodeId":"server","capabilityId":"git","action":"Detect","command":"echo secret"}
                """, System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/provisioning/commands", arbitrary)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/provisioning/commands",
                request with { CapabilityId = "codex-cli", Action = CodexProvisioning.ProvisioningCommandAction.CheckConfiguration })).StatusCode);
            var installResponse = await client.PostAsJsonAsync("/api/v1/provisioning/commands",
                request with { CapabilityId = "codex-cli", Action = CodexProvisioning.ProvisioningCommandAction.Install, AllowElevation = true });
            Assert.Equal(HttpStatusCode.Created, installResponse.StatusCode);
            var install = await installResponse.Content.ReadFromJsonAsync<CodexProvisioning.ProvisioningCommand>();
            Assert.NotNull(install);
            var response = await client.PostAsJsonAsync("/api/v1/provisioning/commands", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var operation = await response.Content.ReadFromJsonAsync<CodexProvisioning.ProvisioningCommand>();
            Assert.NotNull(operation);
            var boundedHistory = await client.GetFromJsonAsync<CodexProvisioning.ProvisioningCommand[]>("/api/v1/provisioning/commands?limit=1");
            Assert.Single(Assert.IsType<CodexProvisioning.ProvisioningCommand[]>(boundedHistory));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/provisioning/commands?limit=101")).StatusCode);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                operation = await client.GetFromJsonAsync<CodexProvisioning.ProvisioningCommand>($"/api/v1/provisioning/commands/{operation.Id}");
                Assert.NotNull(operation);
                if (CodexProvisioning.ProvisioningCommandProtocol.Terminal(operation.Status)) break;
                await Task.Delay(30);
            }
            Assert.Equal(CodexProvisioning.ProvisioningCommandStatus.Succeeded, operation.Status);
            Assert.NotNull(operation.StartedAtUtc);
            Assert.NotNull(operation.CompletedAtUtc);
            Assert.Equal(CodexProvisioning.ProvisioningDiagnostic.Completed, operation.Diagnostic);
            install = await client.GetFromJsonAsync<CodexProvisioning.ProvisioningCommand>($"/api/v1/provisioning/commands/{install.Id}");
            Assert.NotNull(install);
            Assert.Equal(CodexProvisioning.ProvisioningCommandStatus.Failed, install.Status);
            Assert.Equal(CodexProvisioning.ProvisioningDiagnostic.Denied, install.Diagnostic);
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var workerId = Guid.NewGuid().ToString("N");
            var workerToken = new string('t', 40);
            await registry.RegisterWorkerAsync(new(2, workerId, "Worker", "1.0", "linux", 1, []));
            await registry.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
            var planHistory = await client.GetFromJsonAsync<ProvisioningPlan[]>("/api/v1/provisioning?limit=1");
            Assert.Single(Assert.IsType<ProvisioningPlan[]>(planHistory));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/provisioning?offset=10001")).StatusCode);
            var bootstrap = await registry.CreateWorkerAuthorizationAsync(workerId, "rotate", TimeSpan.FromMinutes(1));
            Assert.True(await EnrollWorkerAsync(registry, bootstrap, workerId, workerToken));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/provisioning/commands", request with { NodeId = workerId })).StatusCode);
            await registry.HeartbeatWorkerAsync(new(2, workerId, "1.0", "running", 0, 1, [], []));
            var queuedResponse = await client.PostAsJsonAsync("/api/v1/provisioning/commands",
                request with { NodeId = workerId, CapabilityId = "codex-cli" });
            var queued = await queuedResponse.Content.ReadFromJsonAsync<CodexProvisioning.ProvisioningCommand>();
            Assert.NotNull(queued);
            var cancelledResponse = await client.PostAsync($"/api/v1/provisioning/commands/{queued.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);
            var cancelled = await cancelledResponse.Content.ReadFromJsonAsync<CodexProvisioning.ProvisioningCommand>();
            Assert.Equal(CodexProvisioning.ProvisioningCommandStatus.Cancelled, cancelled?.Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(
                $"/api/v1/provisioning/commands/{queued.Id}/reconcile?nodeQuiescent=false", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(
                $"/api/v1/provisioning/commands/{queued.Id}/reconcile?nodeQuiescent=true", null)).StatusCode);
            var workerResponse = await client.PostAsJsonAsync("/api/v1/provisioning/commands", request with { NodeId = workerId });
            Assert.Equal(HttpStatusCode.Created, workerResponse.StatusCode);
            using var workerClient = new HttpClient { BaseAddress = new Uri(url) };
            workerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
            var dispatch = await workerClient.PostAsync($"/api/v1/workers/{workerId}/provisioning/commands/request", null);
            Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
            var dispatched = await dispatch.Content.ReadFromJsonAsync<CodexProvisioning.ProvisioningCommand>();
            Assert.NotNull(dispatched);
            Assert.Equal(CodexProvisioning.ProvisioningCommandStatus.Running, dispatched.Status);
            Assert.Equal(workerId, dispatched.Request.NodeId);
            Assert.Equal(HttpStatusCode.NoContent, (await workerClient.PostAsync($"/api/v1/workers/{workerId}/provisioning/commands/request", null)).StatusCode);
            var report = new CodexProvisioning.ProvisioningCommandReport(CodexProvisioning.ProvisioningCommandStatus.Succeeded, CodexProvisioning.ProvisioningDiagnostic.Completed);
            Assert.Equal(HttpStatusCode.Conflict, (await workerClient.PostAsJsonAsync($"/api/v1/workers/{workerId}/provisioning/commands/{operation.Id}/report", report)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await workerClient.PostAsJsonAsync($"/api/v1/workers/server/provisioning/commands/{operation.Id}/report", report)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await workerClient.PostAsJsonAsync($"/api/v1/workers/{workerId}/provisioning/commands/{dispatched.Id}/report", report)).StatusCode);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task NodeApiUsesCommonInventoryAndRequiresManagementAuthentication()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "test-management-token");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "registry.db")),
                capabilityDiscovery: TestCapabilityDiscovery.Create());
            var store = app.Services.GetRequiredService<IRegistryStore>();
            await store.RegisterWorkerAsync(new(2, new string('a', 32), "Worker", "1.0", "linux", 1, []));
            var commands = app.Services.GetRequiredService<ProvisioningCommandStore>();
            var login = await commands.CreateAsync(new("server", "github-cli", ProvisioningCommandAction.Login, 600));
            await commands.ClaimAsync("server");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/nodes")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/nodes/server/github-connection")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/nodes/server/capabilities/refresh", null)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
            using var nodes = JsonDocument.Parse(await client.GetStringAsync("/api/v1/nodes"));
            using var connection = JsonDocument.Parse(await client.GetStringAsync("/api/v1/nodes/server/github-connection"));
            Assert.False(connection.RootElement.GetProperty("provisioningEnabled").GetBoolean());
            Assert.False(connection.RootElement.GetProperty("elevationAllowed").GetBoolean());
            Assert.Equal(1, connection.RootElement.GetProperty("commands").GetArrayLength());
            Assert.Equal(2, nodes.RootElement.GetArrayLength());
            var server = nodes.RootElement[0];
            Assert.Equal("server", server.GetProperty("id").GetString());
            Assert.Equal("not-applicable", server.GetProperty("executionReadiness").GetString());
            Assert.Equal(CapabilityCatalog.Definitions.Count, server.GetProperty("capabilities").GetArrayLength());
            Assert.Equal("Missing", server.GetProperty("capabilities")[0].GetProperty("state").GetProperty("installation").GetString());
            Assert.Equal("disconnected", nodes.RootElement[1].GetProperty("connectivity").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/nodes/server/capabilities/refresh", null)).StatusCode);
            await commands.ReportAsync(login.Id, "server", new(ProvisioningCommandStatus.Running,
                ProvisioningDiagnostic.Executing, LoginInstructions: new("https://github.com/login/device", "ABCD-1234")));
            using var activeConnection = JsonDocument.Parse(await client.GetStringAsync("/api/v1/nodes/server/github-connection"));
            var activeLogin = activeConnection.RootElement.GetProperty("commands")[0];
            Assert.Equal(login.Id, activeLogin.GetProperty("id").GetString());
            Assert.Equal("Running", activeLogin.GetProperty("status").GetString());
            Assert.Equal("ABCD-1234", activeLogin.GetProperty("loginInstructions").GetProperty("userCode").GetString());
            await commands.ReportAsync(login.Id, "server", new(ProvisioningCommandStatus.Cancelled, ProvisioningDiagnostic.Cancelled));
            Assert.DoesNotContain("ABCD-1234", await client.GetStringAsync("/api/v1/nodes/server/github-connection"), StringComparison.Ordinal);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CredentialAdministrationApiProjectsSecretFreeMetadataAndKeepsDeliveryAssignedOnly()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        var priorEncryptionKey = Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY");
        const string managementToken = "credential-admin-management-token";
        const string originalSecret = "provider-credential-secret-original";
        const string replacementSecret = "provider-credential-secret-replacement";
        const string deliveryToken = "credential-delivery-token-with-sufficient-entropy";
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", managementToken);
        Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY",
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "credentials-api.db")));
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var workerId = Guid.NewGuid().ToString("N");
            var otherWorkerId = Guid.NewGuid().ToString("N");
            await registry.RegisterWorkerAsync(new(2, workerId, "credential worker", "1.0", "test", 1, []));
            await registry.RegisterWorkerAsync(new(2, otherWorkerId, "other credential worker", "1.0", "test", 1, []));
            var credentials = app.Services.GetRequiredService<ICredentialStore>();
            await credentials.SetWorkerDeliveryTokenAsync(workerId, new CredentialSecretInput(deliveryToken));
            await app.StartAsync();

            static void AssertNonStorage(HttpResponseMessage response)
            {
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
                Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));
                Assert.Null(response.Headers.ETag);
                Assert.Null(response.Content.Headers.LastModified);
                Assert.NotEqual(HttpStatusCode.NotModified, response.StatusCode);
            }
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await management.GetAsync("/api/v1/credentials")).StatusCode);
            management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", managementToken);
            using var createdResponse = await management.PostAsJsonAsync("/api/v1/credentials", new
            {
                provider = "test-provider", type = "api-token", secret = new { value = originalSecret }
            });
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
            AssertNonStorage(createdResponse);
            var created = await createdResponse.Content.ReadFromJsonAsync<CredentialMetadata>();
            Assert.NotNull(created);
            var createBody = await createdResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(originalSecret, createBody, StringComparison.Ordinal);
            Assert.Equal("Ready", created.Status);

            using var listResponse = await management.GetAsync("/api/v1/credentials");
            var listed = await listResponse.Content.ReadFromJsonAsync<IReadOnlyList<CredentialMetadata>>();
            Assert.NotNull(listed);
            Assert.Equal(created, Assert.Single(listed));
            using var showResponse = await management.GetAsync($"/api/v1/credentials/{created.Id}");
            Assert.Equal(created, await showResponse.Content.ReadFromJsonAsync<CredentialMetadata>());

            using var malformed = await management.PostAsync("/api/v1/credentials", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            AssertNonStorage(malformed);
            using var assignment = await management.PutAsJsonAsync($"/api/v1/credentials/{created.Id}/assignment",
                new CredentialAssignmentRequest(workerId));
            var assigned = await assignment.Content.ReadFromJsonAsync<CredentialMetadata>();
            Assert.Equal(workerId, assigned?.AssignedWorkerId);
            using var delivery = new HttpClient { BaseAddress = new Uri(url) };
            delivery.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", "*");
            delivery.DefaultRequestHeaders.IfModifiedSince = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var unauthorized = await delivery.GetAsync($"/api/v1/workers/{workerId}/credentials/{created.Id}");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            AssertNonStorage(unauthorized);
            Assert.DoesNotContain(originalSecret, await unauthorized.Content.ReadAsStringAsync());
            delivery.DefaultRequestHeaders.Add("X-Worker-Credential-Token", deliveryToken);
            using var reassignment = await management.PutAsJsonAsync($"/api/v1/credentials/{created.Id}/assignment",
                new CredentialAssignmentRequest(otherWorkerId));
            Assert.Equal(HttpStatusCode.OK, reassignment.StatusCode);
            using (var denied = await delivery.GetAsync($"/api/v1/workers/{workerId}/credentials/{created.Id}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                AssertNonStorage(denied);
                Assert.DoesNotContain(originalSecret, await denied.Content.ReadAsStringAsync());
                Assert.DoesNotContain(replacementSecret, await denied.Content.ReadAsStringAsync());
            }
            using var assignBack = await management.PutAsJsonAsync($"/api/v1/credentials/{created.Id}/assignment",
                new CredentialAssignmentRequest(workerId));
            using var delivered = await delivery.GetAsync($"/api/v1/workers/{workerId}/credentials/{created.Id}");
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            AssertNonStorage(delivered);
            Assert.Equal(originalSecret, (await delivered.Content.ReadFromJsonAsync<CredentialDeliveryResponse>())?.Secret);

            using var replace = await management.PutAsJsonAsync($"/api/v1/credentials/{created.Id}/secret",
                new CredentialSecretInput(replacementSecret));
            var replaced = await replace.Content.ReadFromJsonAsync<CredentialMetadata>();
            Assert.Equal(5, replaced?.Version);
            Assert.DoesNotContain(replacementSecret, await management.GetStringAsync("/api/v1/credentials"), StringComparison.Ordinal);
            Assert.Equal(replacementSecret, (await delivery.GetFromJsonAsync<CredentialDeliveryResponse>(
                $"/api/v1/workers/{workerId}/credentials/{created.Id}"))?.Secret);

            using var revoke = await management.PostAsync($"/api/v1/credentials/{created.Id}/revoke", null);
            var revoked = await revoke.Content.ReadFromJsonAsync<CredentialMetadata>();
            Assert.Equal("Revoked", revoked?.Status);
            Assert.Null(revoked?.AssignedWorkerId);
            using (var denied = await delivery.GetAsync($"/api/v1/workers/{workerId}/credentials/{created.Id}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
                AssertNonStorage(denied);
                Assert.DoesNotContain(originalSecret, await denied.Content.ReadAsStringAsync());
                Assert.DoesNotContain(replacementSecret, await denied.Content.ReadAsStringAsync());
            }
            await app.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY", priorEncryptionKey);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task BootstrapAndTransientLoginResponsesProhibitStorageIncludingRejections()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        const string managementToken = "SENTINEL-management-secret";
        const string apiToken = "SENTINEL-worker-api-secret-with-sufficient-entropy";
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", managementToken);
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "transient.db")));
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var workerId = Guid.NewGuid().ToString("N");
            var registration = new WorkerRegistrationRequest(2, workerId, "worker", "1.0", "test", 1, []);
            using var denied = await client.PostAsJsonAsync("/api/v1/workers/register", registration);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl?.NoStore);
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bootstrap);
            client.DefaultRequestHeaders.Add("X-Codex-Worker-Token", apiToken);
            using var enrolled = await client.PostAsJsonAsync("/api/v1/workers/register", registration);
            Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
            Assert.True(enrolled.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain(apiToken, await enrolled.Content.ReadAsStringAsync());
            Assert.DoesNotContain(bootstrap, await enrolled.Content.ReadAsStringAsync());
            using var reused = await client.PostAsJsonAsync("/api/v1/workers/register", registration);
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
            Assert.True(reused.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain(apiToken, await reused.Content.ReadAsStringAsync());
            Assert.DoesNotContain(bootstrap, await reused.Content.ReadAsStringAsync());

            var commands = app.Services.GetRequiredService<ProvisioningCommandStore>();
            var command = await commands.CreateAsync(new(workerId, "github-cli", ProvisioningCommandAction.Login));
            await commands.ClaimAsync(workerId);
            await commands.ReportAsync(command.Id, workerId, new(ProvisioningCommandStatus.Running,
                ProvisioningDiagnostic.Executing, LoginInstructions: new("https://github.com/login/device", "ABCD-1234")));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", managementToken);
            client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", "*");
            using var challenge = await client.GetAsync($"/api/v1/provisioning/commands/{command.Id}");
            Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
            Assert.True(challenge.Headers.CacheControl?.NoStore);
            Assert.Null(challenge.Headers.ETag);
            Assert.Contains("ABCD-1234", await challenge.Content.ReadAsStringAsync());
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", prior); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerAdministrationApiProjectsPolicyAndSeparatesApiAndDeliveryRevocation()
    {
        using var temporary = new TemporaryDirectory();
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        var priorRegistration = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "worker-admin-management-token");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "shared-worker-registration-token");
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "worker-admin-api.db")));
            await app.StartAsync();
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "worker-admin-management-token");
            var workerId = Guid.NewGuid().ToString("N");
            const string workerToken = "worker-api-token-with-sufficient-entropy";
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(2, workerId, "admin worker", "1.0", "test", 1, []));
            var bootstrap = await registry.CreateWorkerAuthorizationAsync(workerId, "rotate", TimeSpan.FromMinutes(1));
            Assert.True(await EnrollWorkerAsync(registry, bootstrap, workerId, workerToken));
            var credentials = app.Services.GetRequiredService<ICredentialStore>();
            const string deliveryToken = "worker-delivery-token-with-sufficient-entropy";
            await credentials.SetWorkerDeliveryTokenAsync(workerId, new CredentialSecretInput(deliveryToken));

            using var policyResponse = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/scheduling-policy",
                new WorkerSchedulingPolicyRequest(WorkerSchedulingPolicy.Draining));
            Assert.Equal(HttpStatusCode.OK, policyResponse.StatusCode);
            var projected = await policyResponse.Content.ReadFromJsonAsync<WorkerRegistrationResponse>();
            Assert.NotNull(projected);
            Assert.Equal(WorkerSchedulingPolicy.Draining, projected.SchedulingPolicy);
            Assert.Equal("unknown", projected.LifecycleState);

            using var prematureActivation = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/scheduling-policy",
                new WorkerSchedulingPolicyRequest(WorkerSchedulingPolicy.Enabled));
            Assert.Equal(HttpStatusCode.Conflict, prematureActivation.StatusCode);
            Assert.Equal(WorkerSchedulingPolicy.Draining, (await registry.GetWorkerAsync(workerId))?.SchedulingPolicy);

            var project = await registry.CreateProjectAsync(new("Prepared project", "owner/repo", "main", ""));
            var version = ServerApplication.ManagedConfigurationVersion([project]);
            var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            WorkerCapability[] readyCapabilities = [new("authentication", "github-api", Scope: project.Repository),
                new("authentication", "git-repository", Scope: project.Repository), new("agent-provider", "codex")];
            await registry.HeartbeatWorkerAsync(new(2, workerId, "1.0", "running", 0, 1, readyCapabilities, [],
                "synchronized", version, WorkerPreparationTests.ReadyInventory(now),
                new("retrieved", "synchronized", "server-retrieved", version, version, null, null,
                    [new(project.Id, project.Revision, "not-materialized")])));
            using var activation = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/scheduling-policy",
                new WorkerSchedulingPolicyRequest(WorkerSchedulingPolicy.Enabled));
            Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
            Assert.Equal(WorkerSchedulingPolicy.Enabled, (await registry.GetWorkerAsync(workerId))?.SchedulingPolicy);

            using var revokeApi = await management.PostAsync($"/api/v1/workers/{workerId}/authentication/revoke", null);
            Assert.Equal(HttpStatusCode.OK, revokeApi.StatusCode);
            var revoked = await revokeApi.Content.ReadFromJsonAsync<WorkerRegistrationResponse>();
            Assert.NotNull(revoked);
            Assert.Equal("revoked", revoked.AuthenticationCredentialStatus);
            Assert.NotNull(revoked.AuthenticationCredentialRevokedAtUtc);
            Assert.True(await credentials.IsWorkerDeliveryTokenValidAsync(workerId, deliveryToken));

            using var workerClient = new HttpClient { BaseAddress = new Uri(url) };
            workerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await workerClient.GetAsync($"/api/v1/workers/{workerId}/configuration")).StatusCode);
            workerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "shared-worker-registration-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await workerClient.GetAsync($"/api/v1/workers/{workerId}/configuration")).StatusCode);

            using var deliveryStatus = await management.GetAsync($"/api/v1/workers/{workerId}/credential-access");
            Assert.Equal("active", (await deliveryStatus.Content.ReadFromJsonAsync<WorkerDeliveryAuthorizationStatus>())!.Status);
            using var revokeDelivery = await management.PostAsync($"/api/v1/workers/{workerId}/credential-access/revoke", null);
            Assert.Equal(HttpStatusCode.OK, revokeDelivery.StatusCode);
            Assert.Equal("revoked", (await revokeDelivery.Content.ReadFromJsonAsync<WorkerDeliveryAuthorizationStatus>())!.Status);
            Assert.Equal("revoked", (await registry.GetWorkerAsync(workerId))!.AuthenticationCredentialStatus);
            await app.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", priorRegistration);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerTelegramApiReturnsOnlyMetadataAndUsesManagedSnapshotAndWorkerOnlyDelivery()
    {
        using var temporary = new TemporaryDirectory();
        var previousManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        var previousKey = Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "telegram-management-test");
        Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY",
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        const string botToken = "123456:abcdefghijklmnopqrstuvwxyz_123456789";
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "telegram-api.db")));
            await app.StartAsync();
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "telegram-management-test");
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var credentials = app.Services.GetRequiredService<ICredentialStore>();
            var id = Guid.NewGuid().ToString("N");
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(5));
            Assert.True(await registry.BootstrapWorkerAsync(bootstrap, new(2, id, "Worker", "1.0", "test", 1, []), "telegram-worker-authentication-test"));
            using var workerClient = new HttpClient { BaseAddress = new Uri(url) };
            workerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "telegram-worker-authentication-test");
            using var invalid = await management.PutAsJsonAsync($"/api/v1/workers/{id}/settings",
                new WorkerConfigurationUpdateRequest("Worker", 1, 1, Telegram: new(true, "123")));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var saved = await management.PutAsJsonAsync($"/api/v1/workers/{id}/settings",
                new WorkerConfigurationUpdateRequest("Worker", 1, 1, Telegram: new(true, "-123", new(botToken))));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.DoesNotContain(botToken, await saved.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var worker = await saved.Content.ReadFromJsonAsync<WorkerRegistrationResponse>();
            var telegram = Assert.IsType<WorkerTelegramConfiguration>(worker?.Telegram);
            var reference = Assert.IsType<string>(telegram.CredentialId);
            foreach (var path in new[] { $"/api/v1/workers/{id}", "/api/v1/workers", $"/api/v1/credentials/{reference}" })
            {
                using var read = await management.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.DoesNotContain(botToken, await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            using var snapshotResponse = await workerClient.GetAsync($"/api/v1/workers/{id}/configuration");
            var snapshot = await snapshotResponse.Content.ReadFromJsonAsync<ServerManagedConfigurationContract>();
            Assert.NotNull(snapshot);
            Assert.Equal(4, snapshot.ContractVersion);
            ManagedConfigurationSynchronizer.ValidateSnapshot(snapshot);
            using var denied = await management.GetAsync($"/api/v1/workers/{id}/credentials/{reference}");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            await credentials.SetWorkerDeliveryTokenAsync(id, new("telegram-delivery-test-with-sufficient-entropy"));
            using var delivery = new HttpClient { BaseAddress = new Uri(url) };
            delivery.DefaultRequestHeaders.Add("X-Worker-Credential-Token", "telegram-delivery-test-with-sufficient-entropy");
            var secret = await delivery.GetFromJsonAsync<CredentialDeliveryResponse>($"/api/v1/workers/{id}/credentials/{reference}");
            Assert.Equal(botToken, secret?.Secret);
            using var conflict = await management.PutAsJsonAsync($"/api/v1/workers/{id}/settings",
                new WorkerConfigurationUpdateRequest("Worker", 1, 1, Telegram: new(false, null, ClearToken: true)));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            await registry.HeartbeatWorkerAsync(new(2, id, "1.0", "running", 0, 1, [], [],
                "synchronized", snapshot.Version));
            var applied = await management.GetFromJsonAsync<CodexServer.WorkerDiagnostics>($"/api/v1/workers/{id}/diagnostics");
            Assert.Equal("synchronized", applied?.ConfigurationSynchronization);
            Assert.Equal(2, applied?.ConfigurationRevision);
            using var cleared = await management.PutAsJsonAsync($"/api/v1/workers/{id}/settings",
                new WorkerConfigurationUpdateRequest("Worker", 1, 2, Telegram: new(false, null, ClearToken: true)));
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            var pending = await management.GetFromJsonAsync<CodexServer.WorkerDiagnostics>($"/api/v1/workers/{id}/diagnostics");
            Assert.Equal("out-of-sync", pending?.ConfigurationSynchronization);
            Assert.Equal(3, pending?.ConfigurationRevision);
            using var revoked = await delivery.GetAsync($"/api/v1/workers/{id}/credentials/{reference}");
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
            await app.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previousManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY", previousKey);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task WorkerConfigurationApiSavesRevisionCheckedSettingsAndPreservesReportedCapacity()
    {
        using var temporary = new TemporaryDirectory();
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "worker-editor-management-token");
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            await using var app = await ServerApplication.BuildAsync([
                .. Args(url, Path.Combine(temporary.Path, "worker-editor-api.db")),
                $"--Server:AdministrationOrigin={url}"]);
            await app.StartAsync();
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "worker-editor-management-token");
            var workerId = Guid.NewGuid().ToString("N");
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            await registry.RegisterWorkerAsync(new(2, workerId, "Original name", "1.0.0", "linux-x64", 2, []));
            await registry.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 1, 3, [], []));

            using var anonymous = new HttpClient { BaseAddress = new Uri(url) };
            using var denied = await anonymous.PutAsJsonAsync($"/api/v1/workers/{workerId}/settings",
                new WorkerConfigurationUpdateRequest("Unauthorized", 2, 1));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            // Exercise the same cookie session and CSRF boundary as the editor.
            management.DefaultRequestHeaders.Add("Origin", url);
            using var login = await management.PostAsync("/api/v1/administration/session", null);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var session = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            management.DefaultRequestHeaders.Authorization = null;
            management.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
            using var missingCsrf = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/settings",
                new WorkerConfigurationUpdateRequest("No CSRF", 2, 1));
            Assert.Equal(HttpStatusCode.Unauthorized, missingCsrf.StatusCode);
            management.DefaultRequestHeaders.Add("X-Codex-CSRF", session.RootElement.GetProperty("csrfToken").GetString());

            using var savedResponse = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/settings",
                new WorkerConfigurationUpdateRequest("Operations worker", 1, 1, 4));
            Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
            var saved = await savedResponse.Content.ReadFromJsonAsync<WorkerRegistrationResponse>();
            Assert.NotNull(saved);
            Assert.Equal("Operations worker", saved.DisplayName);
            Assert.Equal(1, saved.ConfiguredMaximumCapacity);
            Assert.Equal(3, saved.MaximumCapacity);
            Assert.Equal(1, saved.ActiveExecutions);
            Assert.Equal(2, saved.ConfigurationRevision);

            var readBack = await management.GetFromJsonAsync<WorkerRegistrationResponse>($"/api/v1/workers/{workerId}");
            Assert.NotNull(readBack);
            Assert.Equal(saved.DisplayName, readBack.DisplayName);
            Assert.Equal(saved.ConfiguredMaximumCapacity, readBack.ConfiguredMaximumCapacity);
            Assert.Equal(saved.ConfigurationRevision, readBack.ConfigurationRevision);
            Assert.Equal(4, readBack.WorkerExecutionSlots);
            var persisted = await new SqliteRegistryStore(Path.Combine(temporary.Path, "worker-editor-api.db")).GetWorkerAsync(workerId);
            Assert.NotNull(persisted);
            Assert.Equal(saved.DisplayName, persisted.DisplayName);
            Assert.Equal(saved.ConfiguredMaximumCapacity, persisted.ConfiguredMaximumCapacity);
            Assert.Equal(saved.ConfigurationRevision, persisted.ConfigurationRevision);
            Assert.Equal(4, persisted.WorkerExecutionSlots);

            await registry.HeartbeatWorkerAsync(new(2, workerId, "1.0.0", "running", 0, 3, [], []));
            var afterHeartbeat = await registry.GetWorkerAsync(workerId);
            Assert.NotNull(afterHeartbeat);
            Assert.Equal("Operations worker", afterHeartbeat.DisplayName);
            Assert.Equal(1, afterHeartbeat.ConfiguredMaximumCapacity);
            Assert.Equal(3, afterHeartbeat.MaximumCapacity);

            using var staleResponse = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/settings",
                new WorkerConfigurationUpdateRequest("Stale name", 2, 1));
            Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
            using var invalidResponse = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/settings",
                new WorkerConfigurationUpdateRequest("Operations worker", 9, 2));
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            Assert.Equal("Operations worker", (await registry.GetWorkerAsync(workerId))?.DisplayName);
            using var invalidSlotsResponse = await management.PutAsJsonAsync($"/api/v1/workers/{workerId}/settings",
                new WorkerConfigurationUpdateRequest("Operations worker", 1, 2, 0));
            Assert.Equal(HttpStatusCode.BadRequest, invalidSlotsResponse.StatusCode);
            using var missingResponse = await management.PutAsJsonAsync($"/api/v1/workers/{Guid.NewGuid():N}/settings",
                new WorkerConfigurationUpdateRequest("Missing", 1, 1));
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
            var unchanged = await management.GetFromJsonAsync<WorkerRegistrationResponse>($"/api/v1/workers/{workerId}");
            Assert.NotNull(unchanged);
            Assert.Equal(1, unchanged.ConfiguredMaximumCapacity);
            Assert.Equal(2, unchanged.ConfigurationRevision);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }
}
