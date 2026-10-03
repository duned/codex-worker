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

[CollectionDefinition("ServerTokenEnvironment", DisableParallelization = true)]
public sealed class ServerTokenEnvironmentCollection { }

[Collection("ServerTokenEnvironment")]
public sealed class CodexServerTests
{
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
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            Assert.True(await registry.RedeemWorkerBootstrapTokenAsync(bootstrap, workerId, workerToken));
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
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/nodes")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/nodes/server/capabilities/refresh", null)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
            using var nodes = JsonDocument.Parse(await client.GetStringAsync("/api/v1/nodes"));
            Assert.Equal(2, nodes.RootElement.GetArrayLength());
            var server = nodes.RootElement[0];
            Assert.Equal("server", server.GetProperty("id").GetString());
            Assert.Equal("not-applicable", server.GetProperty("executionReadiness").GetString());
            Assert.Equal(CapabilityCatalog.Definitions.Count, server.GetProperty("capabilities").GetArrayLength());
            Assert.Equal("Missing", server.GetProperty("capabilities")[0].GetProperty("state").GetProperty("installation").GetString());
            Assert.Equal("disconnected", nodes.RootElement[1].GetProperty("connectivity").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/nodes/server/capabilities/refresh", null)).StatusCode);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

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

            using var management = new HttpClient { BaseAddress = new Uri(url) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await management.GetAsync("/api/v1/credentials")).StatusCode);
            management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", managementToken);
            using var createdResponse = await management.PostAsJsonAsync("/api/v1/credentials", new
            {
                provider = "test-provider", type = "api-token", secret = new { value = originalSecret }
            });
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
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

            using var assignment = await management.PutAsJsonAsync($"/api/v1/credentials/{created.Id}/assignment",
                new CredentialAssignmentRequest(workerId));
            var assigned = await assignment.Content.ReadFromJsonAsync<CredentialMetadata>();
            Assert.Equal(workerId, assigned?.AssignedWorkerId);
            using var delivery = new HttpClient { BaseAddress = new Uri(url) };
            delivery.DefaultRequestHeaders.Add("X-Worker-Credential-Token", deliveryToken);
            using var reassignment = await management.PutAsJsonAsync($"/api/v1/credentials/{created.Id}/assignment",
                new CredentialAssignmentRequest(otherWorkerId));
            Assert.Equal(HttpStatusCode.OK, reassignment.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await delivery.GetAsync($"/api/v1/workers/{workerId}/credentials/{created.Id}")).StatusCode);
            using var assignBack = await management.PutAsJsonAsync($"/api/v1/credentials/{created.Id}/assignment",
                new CredentialAssignmentRequest(workerId));
            using var delivered = await delivery.GetAsync($"/api/v1/workers/{workerId}/credentials/{created.Id}");
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
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
            Assert.Equal(HttpStatusCode.NotFound,
                (await delivery.GetAsync($"/api/v1/workers/{workerId}/credentials/{created.Id}")).StatusCode);
            await app.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY", priorEncryptionKey);
        }
    }

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
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            Assert.True(await registry.RedeemWorkerBootstrapTokenAsync(bootstrap, workerId, workerToken));
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
            Assert.Equal(HttpStatusCode.OK, (await workerClient.GetAsync($"/api/v1/workers/{workerId}/configuration")).StatusCode);

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

    [Fact]
    public async Task DirectTokenCommandRejectsImplicitHomeState()
    {
        var priorDirectory = Environment.GetEnvironmentVariable("Server__DataDirectory");
        var priorDatabase = Environment.GetEnvironmentVariable("Server__DatabasePath");
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", null);
            Environment.SetEnvironmentVariable("Server__DatabasePath", null);
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(1, await CodexServer.Program.Main(["worker-token", "create"]));
            Assert.Empty(output.ToString());
            Assert.Contains("service state configuration", error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("Server__DataDirectory", priorDirectory);
            Environment.SetEnvironmentVariable("Server__DatabasePath", priorDatabase);
        }
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("-h", 0)]
    [InlineData("--version", 0)]
    [InlineData("unknown-private-secret", 2)]
    [InlineData("--unknown", 2)]
    [InlineData("worker-token", 2)]
    [InlineData("backup", 2)]
    public async Task CliDispatchDoesNotStartHostOrCreateStateWhenDefaultPortIsOccupied(string argument, int expectedExit)
    {
        using var temporary = new TemporaryDirectory();
        var state = Path.Combine(temporary.Path, "must-not-exist");
        var priorDirectory = Environment.GetEnvironmentVariable("Server__DataDirectory");
        var priorUrl = Environment.GetEnvironmentVariable("Server__ListenUrl");
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var listener = new TcpListener(IPAddress.Loopback, 5090);
        // If a real Server already occupies the port, it supplies the same condition.
        try { listener.Start(); }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse) { }
        try
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", state);
            Environment.SetEnvironmentVariable("Server__ListenUrl", "http://127.0.0.1:5090");
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(expectedExit, await CodexServer.Program.Main([argument]));
            Assert.False(Directory.Exists(state));
            if (argument is "--help" or "-h")
            {
                Assert.Contains("Usage:", output.ToString());
                Assert.Contains("sudo codex-server worker-token create", output.ToString());
            }
            else if (argument == "--version") Assert.Equal($"Codex Server {ServerApplication.DisplayVersion}", output.ToString().Trim());
            else Assert.Contains("--help", error.ToString());
            Assert.DoesNotContain("unknown-private-secret", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("initialized", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("Server__DataDirectory", priorDirectory);
            Environment.SetEnvironmentVariable("Server__ListenUrl", priorUrl);
        }
    }

    [Fact]
    public void CapabilityContractSupportsLegacyStringsAndUnknownFutureTypes()
    {
        Assert.Equal(new ServerWorkerCapability("tool", "git"), JsonSerializer.Deserialize<ServerWorkerCapability>("\"git\""));
        Assert.Equal(new ServerWorkerCapability("future-runtime", "specialized", "4.2"),
            JsonSerializer.Deserialize<ServerWorkerCapability>("{\"type\":\"future-runtime\",\"name\":\"specialized\",\"version\":\"4.2\"}"));
    }

    [Theory]
    [InlineData("{\"type\":\"tool\",\"name\":\"git\",\"extra\":true}")]
    [InlineData("{\"type\":\"tool\",\"name\":7}")]
    [InlineData("{\"type\":\"tool\"}")]
    public void MalformedCapabilityDataIsRejected(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ServerWorkerCapability>(json));

    [Fact]
    public void ServerLeaseTimingDefaultsAreConservativeAndRenewalMustPrecedeExpiry()
    {
        var configuration = new ServerConfiguration();
        configuration.Validate();
        Assert.Equal(900, configuration.ExecutionLeaseDurationSeconds);
        Assert.Equal(60, configuration.ExecutionLeaseRenewalIntervalSeconds);
        configuration.ExecutionLeaseDurationSeconds = 120;
        configuration.ExecutionLeaseRenewalIntervalSeconds = 40;
        Assert.Throws<InvalidDataException>(configuration.Validate);
    }

    [Fact]
    public async Task BootstrapTokensAreShortLivedOneTimeAndCreatePerWorkerCredentials()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), timeProvider: clock);
        await store.InitializeAsync();
        var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        var workerId = Guid.NewGuid().ToString("N");
        Assert.False(await store.RedeemWorkerBootstrapTokenAsync("invalid", workerId, "worker-secret"));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(await store.RedeemWorkerBootstrapTokenAsync(bootstrap, workerId, "worker-secret"));

        var validBootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        Assert.True(await store.RedeemWorkerBootstrapTokenAsync(validBootstrap, workerId, "worker-secret"));
        Assert.False(await store.RedeemWorkerBootstrapTokenAsync(validBootstrap, workerId, "other-secret"));
        Assert.True(await store.IsWorkerTokenValidAsync(workerId, "worker-secret"));
        Assert.False(await store.IsWorkerTokenValidAsync(workerId, "other-secret"));
        Assert.True(await store.RevokeWorkerTokenAsync(workerId));
        Assert.False(await store.IsWorkerTokenValidAsync(workerId, "worker-secret"));
    }

    [Fact]
    public void DataDirectoryAndRelativeDatabasePathResolveOutsideTheWorkingDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var configuration = new ServerConfiguration { DataDirectory = Path.Combine(temporary.Path, "state") };
        Assert.Equal(Path.Combine(temporary.Path, "state"), configuration.ResolveDataDirectory());
        Assert.Equal(Path.Combine(temporary.Path, "state", "codex-server.db"), configuration.ResolveDatabasePath());
        configuration.DatabasePath = "custom/server.db";
        Assert.Equal(Path.Combine(temporary.Path, "state", "custom", "server.db"), configuration.ResolveDatabasePath());
    }

    [Fact]
    public async Task BackupRoundTripPreservesControlPlaneMetadataAndExcludesSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var restoredDatabase = Path.Combine(temporary.Path, "restored.db");
        var archivePath = Path.Combine(temporary.Path, "backup.zip");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(new CentralProjectDefinition("Example", "owner/repo", "main", "description"));
        var encryptionKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var credentials = new SqliteCredentialStore(database, encryptionKey);
        await credentials.InitializeAsync();
        const string secret = "credential-secret-value";
        const string deliveryToken = "worker-delivery-token-value-that-is-long-enough";
        var workerId = Guid.NewGuid().ToString("N");
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "backup worker", "1.0.0", "test", 1, [new WorkerCapability("tool", "git")]));
        var legacyPlan = await registry.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
        var typedCommands = new ProvisioningCommandStore(database);
        await typedCommands.InitializeAsync();
        var typedCommand = await typedCommands.CreateAsync(new(workerId, "git", ProvisioningCommandAction.Detect));
        var workerBootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
        Assert.True(await registry.RedeemWorkerBootstrapTokenAsync(workerBootstrap, workerId, "backup-worker-api-token"));
        await registry.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Draining);
        var credential = await credentials.CreateAsync(new CreateCredentialRequest("github", "api", new CredentialSecretInput(secret)));
        await credentials.AssignAsync(credential.Id, workerId);
        var credentialForRevocation = await credentials.CreateAsync(new CreateCredentialRequest("github", "api",
            new CredentialSecretInput("second-backup-credential-secret")));
        await credentials.SetWorkerDeliveryTokenAsync(workerId, new CredentialSecretInput(deliveryToken));

        await new ServerBackup(database).ExportAsync(archivePath);
        await new ServerBackup(database).ValidateAsync(archivePath);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
        {
            var manifestEntry = Assert.IsType<System.IO.Compression.ZipArchiveEntry>(archive.GetEntry("manifest.json"));
            await using (var manifestStream = manifestEntry.Open())
            using (var manifest = await JsonDocument.ParseAsync(manifestStream))
            {
                Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());
                Assert.Equal(SqliteRegistryStore.CurrentSchemaVersion, manifest.RootElement.GetProperty("registrySchemaVersion").GetInt32());
                Assert.Contains("legacy provisioning plans", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
                Assert.Contains("typed provisioning command history when present", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
                Assert.Contains("credential metadata; ready credentials require re-provisioning", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
                Assert.Contains("revoked Worker API-token metadata", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
            }
            var entry = Assert.IsType<System.IO.Compression.ZipArchiveEntry>(archive.GetEntry("state.sqlite"));
            await using var stream = entry.Open();
            using var contents = new MemoryStream();
            await stream.CopyToAsync(contents);
            var databaseContents = System.Text.Encoding.Latin1.GetString(contents.ToArray());
            Assert.DoesNotContain(secret, databaseContents, StringComparison.Ordinal);
            Assert.DoesNotContain("second-backup-credential-secret", databaseContents, StringComparison.Ordinal);
            Assert.DoesNotContain(deliveryToken, databaseContents, StringComparison.Ordinal);
        }

        await new ServerBackup(restoredDatabase).RestoreOfflineAsync(archivePath);
        var restoredRegistry = new SqliteRegistryStore(restoredDatabase);
        await restoredRegistry.InitializeAsync();
        var restoredProject = Assert.IsType<CentralProject>(await restoredRegistry.GetProjectAsync(project.Id));
        Assert.Equal(project.Id, restoredProject.Id);
        Assert.Equal(project.Name, restoredProject.Name);
        Assert.Equal(project.Repository, restoredProject.Repository);
        var restoredWorker = await restoredRegistry.GetWorkerAsync(workerId);
        Assert.Equal("backup worker", restoredWorker?.DisplayName);
        Assert.Equal(WorkerSchedulingPolicy.Draining, restoredWorker?.SchedulingPolicy);
        Assert.Equal("Pending", (await restoredRegistry.GetProvisioningPlanAsync(legacyPlan.Id))?.State);
        var restoredCommands = new ProvisioningCommandStore(restoredDatabase);
        await restoredCommands.InitializeAsync();
        var restoredTypedCommand = Assert.Single(await restoredCommands.ListAsync());
        Assert.Equal(typedCommand.Id, restoredTypedCommand.Id);
        Assert.Equal(ProvisioningCommandStatus.Pending, restoredTypedCommand.Status);
        Assert.Equal("revoked", restoredWorker?.AuthenticationCredentialStatus);

        await using (var metadataConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restoredDatabase, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await metadataConnection.OpenAsync();
            await using var metadataCommand = metadataConnection.CreateCommand();
            metadataCommand.CommandText = "SELECT COUNT(*) FROM worker_auth_tokens WHERE revoked_at_utc IS NOT NULL AND token_hash=zeroblob(length(token_hash));";
            Assert.Equal(1L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
            metadataCommand.CommandText = "SELECT COUNT(*) FROM worker_bootstrap_tokens;";
            Assert.Equal(0L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
            metadataCommand.CommandText = "SELECT COUNT(*) FROM worker_credential_auth;";
            Assert.Equal(0L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
            metadataCommand.CommandText = "SELECT COUNT(*) FROM credentials WHERE status='NeedsReprovision' AND assigned_worker_id IS NULL AND length(nonce)=0 AND length(ciphertext)=0 AND length(tag)=0;";
            Assert.Equal(2L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
        }

        var restoredCredentials = new SqliteCredentialStore(restoredDatabase, encryptionKey);
        await restoredCredentials.InitializeAsync();
        var restoredCredentialMetadata = await restoredCredentials.ListAsync();
        var metadata = Assert.Single(restoredCredentialMetadata, item => item.Id == credential.Id);
        var metadataForRevocation = Assert.Single(restoredCredentialMetadata, item => item.Id == credentialForRevocation.Id);
        Assert.Equal("NeedsReprovision", metadata.Status);
        Assert.Equal("NeedsReprovision", metadataForRevocation.Status);
        Assert.False(await restoredCredentials.IsWorkerDeliveryTokenValidAsync(workerId, deliveryToken));
        Assert.Null(await restoredCredentials.RetrieveForWorkerAsync(credential.Id, workerId));
        var revokedAfterRestore = await restoredCredentials.RevokeAsync(credentialForRevocation.Id);
        Assert.Equal("Revoked", revokedAfterRestore?.Status);
        Assert.Null(revokedAfterRestore?.AssignedWorkerId);
        Assert.Equal("Ready", (await restoredCredentials.ReplaceSecretAsync(credential.Id, new CredentialSecretInput("re-entered-test-secret")))?.Status);
        await restoredCredentials.AssignAsync(credential.Id, workerId);
        Assert.Equal("re-entered-test-secret", await restoredCredentials.RetrieveForWorkerAsync(credential.Id, workerId));
    }

    [Fact]
    public async Task BackupRejectsTypedProvisioningHistoryThatDoesNotMatchItsCurrentSchema()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var archivePath = Path.Combine(temporary.Path, "backup.zip");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var commands = new ProvisioningCommandStore(database);
        await commands.InitializeAsync();
        await commands.CreateAsync(new("server", "git", ProvisioningCommandAction.Detect));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE provisioning_commands SET body='{}';";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerBackup(database).ExportAsync(archivePath));
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task BackupRestoreRequiresExclusiveOfflineServerDatabaseAccess()
    {
        using var temporary = new TemporaryDirectory();
        var sourceDatabase = Path.Combine(temporary.Path, "source.db");
        var targetDatabase = Path.Combine(temporary.Path, "target.db");
        var archivePath = Path.Combine(temporary.Path, "backup.zip");
        var sourceRegistry = new SqliteRegistryStore(sourceDatabase);
        await sourceRegistry.InitializeAsync();
        var project = await sourceRegistry.CreateProjectAsync(new CentralProjectDefinition("Backup", "owner/repo", "main", "snapshot"));
        await new SqliteCredentialStore(sourceDatabase, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))).InitializeAsync();
        await new ProvisioningCommandStore(sourceDatabase).InitializeAsync();
        await new ServerBackup(sourceDatabase).ExportAsync(archivePath);

        var targetRegistry = new SqliteRegistryStore(targetDatabase);
        await targetRegistry.InitializeAsync();
        var retained = await targetRegistry.CreateProjectAsync(new CentralProjectDefinition("Retained", "owner/retained", "main", "keep"));
        await using (var app = await ServerApplication.BuildAsync(Args($"http://127.0.0.1:{ReservePort()}", targetDatabase)))
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => new ServerBackup(targetDatabase).RestoreOfflineAsync(archivePath));
            Assert.Contains("Stop the Server", exception.Message, StringComparison.Ordinal);
            Assert.Equal(retained.Id, (await targetRegistry.GetProjectAsync(retained.Id))?.Id);
        }

        await new ServerBackup(targetDatabase).RestoreOfflineAsync(archivePath);
        var restored = Assert.IsType<CentralProject>(await new SqliteRegistryStore(targetDatabase).GetProjectAsync(project.Id));
        Assert.Equal(project.Name, restored.Name);
    }

    [Fact]
    public async Task InvalidBackupIsRejectedBeforeRestoreChangesExistingDatabase()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var archivePath = Path.Combine(temporary.Path, "invalid.zip");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(new CentralProjectDefinition("Existing", "owner/existing", "main", "keep"));
        await using (var file = File.Create(archivePath))
        using (var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using var stream = manifest.Open();
            await JsonSerializer.SerializeAsync(stream, new { formatVersion = 999, registrySchemaVersion = SqliteRegistryStore.CurrentSchemaVersion });
        }

        var backup = new ServerBackup(database);
        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreOfflineAsync(archivePath));
        var existingProject = Assert.IsType<CentralProject>(await registry.GetProjectAsync(project.Id));
        Assert.Equal(project.Name, existingProject.Name);
    }

    [Fact]
    public async Task CleanWorkerBootstrapRegistersVisibleWorkerAndEstablishesHeartbeatAuthentication()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "bootstrap.db");
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "bootstrap-management-secret");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, database));
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
            var identityPath = Path.Combine(temporary.Path, "worker-id");
            var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = identityPath };
            using var client = new HttpClient();

            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, bootstrap, CancellationToken.None);
            // Replaying after a lost success response recovers through the durable Worker credential.
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, bootstrap, CancellationToken.None);

            var identity = await WorkerIdentity.LoadOrCreateAsync(identityPath);
            Assert.Single(await store.GetWorkersAsync());
            Assert.True(await store.IsWorkerTokenValidAsync(identity, WorkerAuthentication.GetToken(settings)));
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).HeartbeatAsync(settings, 1, 0, [], "running", CancellationToken.None);
            Assert.False(await store.RedeemWorkerBootstrapTokenAsync(bootstrap, identity, WorkerAuthentication.GetToken(settings)));
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "bootstrap-management-secret");
            using var workers = JsonDocument.Parse(await management.GetStringAsync("/api/v1/workers"));
            Assert.Single(workers.RootElement.EnumerateArray());
            Assert.Equal(identity, workers.RootElement[0].GetProperty("workerId").GetString());
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

    [Fact]
    public async Task ManagedNotReadyHeartbeatIsAcceptedAndWorkerRemainsOnlineButCannotReceiveWork()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "not-ready-management-token");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "not-ready.db")));
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var project = await store.CreateProjectAsync(new CentralProjectDefinition("Agent project", "team/project", "main", "",
                [new("agent-provider", "codex")]));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "130")));
            var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
            var identityPath = Path.Combine(temporary.Path, "worker-id");
            var settings = new WorkerServerSettings
            {
                Enabled = true,
                Url = url,
                IdentityFile = identityPath
            };
            using var workerClient = new HttpClient { BaseAddress = new Uri(url) };
            var registration = new WorkerRegistrationClient(workerClient, TestCapabilityDiscovery.Create());
            await registration.BootstrapAsync(settings, 1, bootstrap, CancellationToken.None);
            var workerId = await WorkerIdentity.LoadOrCreateAsync(identityPath);

            await registration.HeartbeatAsync(settings, 1, 0, [], WorkerLifecycleStates.NotReady, CancellationToken.None, []);
            await registration.HeartbeatAsync(settings, 1, 0, [], WorkerLifecycleStates.NotReady, CancellationToken.None, []);

            var worker = Assert.IsType<WorkerRegistrationResponse>(await store.GetWorkerAsync(workerId));
            Assert.Equal("online", worker.Availability);
            Assert.Equal(WorkerLifecycleStates.NotReady, worker.LifecycleState);
            using var managementClient = new HttpClient { BaseAddress = new Uri(url) };
            managementClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", "not-ready-management-token");
            var diagnostics = await managementClient.GetFromJsonAsync<WorkerDiagnostics>($"/api/v1/workers/{workerId}/diagnostics");
            Assert.NotNull(diagnostics);
            Assert.Equal("online", diagnostics.Availability);
            Assert.False(Assert.Single(diagnostics.Projects).IsEligible);
            Assert.Contains(diagnostics.Reasons, reason => reason.Contains("AI agent unavailable", StringComparison.Ordinal));

            workerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", WorkerAuthentication.GetToken(settings));
            var assignmentResponse = await workerClient.PostAsJsonAsync($"/api/v1/workers/{workerId}/assignments/request",
                new WorkerAssignmentRequest(workerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 }));
            Assert.Equal(HttpStatusCode.OK, assignmentResponse.StatusCode);
            var assignment = await assignmentResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>();
            Assert.NotNull(assignment);
            Assert.False(assignment.HasWork);
            Assert.Equal("Queued", Assert.Single(await store.GetExecutionsAsync()).State);

            var invalidHeartbeat = new
            {
                contractVersion = 2,
                workerId,
                workerVersion = "1.0",
                lifecycleState = "unknown-state",
                activeExecutions = 0,
                maximumCapacity = 1,
                capabilities = Array.Empty<object>(),
                activeProjects = Array.Empty<string>()
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await workerClient.PostAsJsonAsync(
                $"/api/v1/workers/{workerId}/heartbeat", invalidHeartbeat)).StatusCode);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("custom/registry.db")]
    public async Task OperatorCommandUsesServiceDatabaseAndFreshWorkerCliCanRetry(string? databaseOverride)
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorDirectory = Environment.GetEnvironmentVariable("Server__DataDirectory");
        var priorDatabase = Environment.GetEnvironmentVariable("Server__DatabasePath");
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var originalInput = Console.In;
        using var tokenOutput = new StringWriter();
        using var diagnostics = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", temporary.Path);
            Environment.SetEnvironmentVariable("Server__DatabasePath", databaseOverride);
            await using var app = await ServerApplication.BuildAsync(
                [$"--Server:ListenUrl={url}", "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            // The installed helper supplies the same environment to this entry point
            // as systemd supplies to the service. No database argument is needed.
            Console.SetOut(tokenOutput);
            Console.SetError(diagnostics);
            Assert.Equal(0, await CodexServer.Program.Main(["worker-token", "create"]));
            var token = tokenOutput.ToString().Trim();
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
            Assert.DoesNotContain(token, diagnostics.ToString(), StringComparison.Ordinal);
            var database = new ServerConfiguration { DataDirectory = temporary.Path, DatabasePath = databaseOverride }.ResolveDatabasePath();
            Assert.True(File.Exists(database));
            Assert.Contains(database, diagnostics.ToString());

            Console.SetOut(originalOutput);
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var identityPath = Path.Combine(temporary.Path, "worker-id");
            async Task<int> Register(string value, string path)
            {
                using var input = new StringReader(value + "\r\n");
                return await CodexWorker.Program.RunAsync(["register", "--server", url, "--token-stdin",
                    "--capacity", "2", "--identity-file", path], CancellationToken.None, input);
            }

            // Reproduce the old documented command's separate database: its fresh
            // token is rejected, but staging does not prevent a valid clean retry.
            tokenOutput.GetStringBuilder().Clear();
            Console.SetOut(tokenOutput);
            await CodexServer.Program.Main(["worker-token", "create", Path.Combine(temporary.Path, "wrong.db")]);
            var wrongDatabaseToken = tokenOutput.ToString().Trim();
            Console.SetOut(originalOutput);
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register(wrongDatabaseToken, identityPath));
            Assert.Empty(await store.GetWorkersAsync());
            Assert.Equal(ProcessExitCodes.Success, await Register("  " + token + "  ", identityPath));
            Assert.Single(await store.GetWorkersAsync());
            Assert.Equal(ProcessExitCodes.Success, await Register(token, identityPath)); // lost-response recovery
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register(token, Path.Combine(temporary.Path, "reuse-id")));
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register("invalid", Path.Combine(temporary.Path, "invalid-id")));

            tokenOutput.GetStringBuilder().Clear();
            Console.SetOut(tokenOutput);
            await CodexServer.Program.Main(["worker-token", "create"]);
            var expired = tokenOutput.ToString().Trim();
            Console.SetOut(originalOutput);
            // Advance persisted expiry without waiting 15 minutes. Generation and
            // HTTP validation still use the production paths and actual SQLite DB.
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE worker_bootstrap_tokens SET expires_at_utc='2000-01-01T00:00:00.0000000+00:00';";
                await command.ExecuteNonQueryAsync();
            }
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register(expired, Path.Combine(temporary.Path, "expired-id")));
            Assert.Single(await store.GetWorkersAsync());
            Assert.DoesNotContain(token, diagnostics.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(expired, diagnostics.ToString(), StringComparison.Ordinal);
            await app.StopAsync();
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Console.SetIn(originalInput);
            Environment.SetEnvironmentVariable("Server__DataDirectory", priorDirectory);
            Environment.SetEnvironmentVariable("Server__DatabasePath", priorDatabase);
        }
    }

    [Fact]
    public async Task RegistrationValidationReturnsSafeActionableContractAndCorrelation()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "validation.db")));
        await app.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/api/v1/workers/register");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-bootstrap-token");
        request.Headers.Add("X-Codex-Worker-Token", "private-worker-token");
        request.Content = JsonContent.Create(new WorkerRegistrationRequest(2, Guid.NewGuid().ToString("N"),
            "private-display-name", "1.0", "test", 9, []));
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_worker_registration", json.RootElement.GetProperty("code").GetString());
        Assert.Contains("capacity (1..8)", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(response.Headers.GetValues("X-Codex-Request-Id").Single(), json.RootElement.GetProperty("requestId").GetString());
        Assert.DoesNotContain("private-", body, StringComparison.Ordinal);
        Assert.Empty(await app.Services.GetRequiredService<IRegistryStore>().GetWorkersAsync());

        var settings = new CodexWorker.WorkerServerSettings
        {
            Enabled = true, Url = url, IdentityFile = Path.Combine(temporary.Path, "worker-id")
        };
        var failure = await Assert.ThrowsAsync<CodexWorker.WorkerStartupException>(() =>
            new CodexWorker.WorkerRegistrationClient(client).BootstrapAsync(settings, 9, "private-bootstrap-token", CancellationToken.None));
        Assert.Contains("HTTP 400", failure.Message);
        Assert.Contains("capacity (1..8)", failure.Message);
        Assert.Contains("Request ID:", failure.Message);
        Assert.DoesNotContain("private-bootstrap-token", failure.Message, StringComparison.Ordinal);

        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var originalName = Environment.GetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME");
        using var capture = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME", new string('x', 201));
            Console.SetOut(capture);
            Console.SetError(capture);
            var exitCode = await CodexWorker.Program.Main(["register", "--server", url, "--token", "private-bootstrap-token",
                "--identity-file", Path.Combine(temporary.Path, "cli-worker-id")]);
            Assert.Equal(CodexWorker.ProcessExitCodes.StartupFailure, exitCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME", originalName);
        }
        Assert.Contains("HTTP 400", capture.ToString());
        Assert.Contains("displayName (1..200)", capture.ToString());
        Assert.Contains("Request ID:", capture.ToString());
        Assert.DoesNotContain("private-bootstrap-token", capture.ToString(), StringComparison.Ordinal);
        await app.StopAsync();
    }

    [Fact]
    public async Task InvalidBootstrapCredentialIsActionableAndDoesNotCreateWorker()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        var priorRegistration = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "bootstrap-management-secret");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", null);
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "bootstrap.db")));
            await app.StartAsync();
            using var client = new HttpClient();
            var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = Path.Combine(temporary.Path, "worker-id") };
            var failure = await Assert.ThrowsAsync<WorkerStartupException>(() =>
                new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, "invalid-bootstrap-token", CancellationToken.None));
            Assert.Contains("invalid, expired, or already used", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("invalid-bootstrap-token", failure.Message, StringComparison.Ordinal);
            Assert.Empty(await app.Services.GetRequiredService<IRegistryStore>().GetWorkersAsync());
            var freshBootstrap = await app.Services.GetRequiredService<IRegistryStore>()
                .CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, freshBootstrap, CancellationToken.None);
            Assert.Single(await app.Services.GetRequiredService<IRegistryStore>().GetWorkersAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", priorRegistration);
        }
    }

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
            await using var app = await ServerApplication.BuildAsync(Args(url, database));
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
            await using var restarted = await ServerApplication.BuildAsync(Args(url, database));
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
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 2, [new("tool", "git")]));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 2, 2,
            [new("tool", "git")], ["one", "two"], "synchronized", "configuration-v1"));
        var initial = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("online", initial.Availability);
        Assert.Equal(0, initial.AvailableCapacity);
        Assert.Equal("synchronized", initial.ConfigurationSynchronization);
        Assert.Equal("configuration-v1", initial.ConfigurationVersion);
        Assert.NotNull(initial.LastHeartbeatAtUtc);
        clock.Advance(TimeSpan.FromSeconds(31));
        var stale = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("stale", stale.Availability);
        Assert.Equal(0, stale.ActiveExecutions);
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.1", "running", 1, 3, [new("tool", "git"), new("runtime", "new-capability")], ["three"]));
        var reconnected = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("online", reconnected.Availability);
        Assert.Equal(2, reconnected.AvailableCapacity);
        Assert.Contains(reconnected.Capabilities, capability => capability.Name == "new-capability");
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
            var dashboard = await dashboardResponse.Content.ReadAsStringAsync();
            Assert.Contains("Node provisioning", dashboard);
            Assert.Contains("/api/v1/nodes", dashboard);
            Assert.Contains("/api/v1/provisioning/commands", dashboard);
            Assert.Contains("data-lifecycle", dashboard);
            Assert.Contains("/lifecycle", dashboard);
            Assert.Contains("active executions keep their lease and finish", dashboard, StringComparison.OrdinalIgnoreCase);

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
            Assert.True(health.RootElement.GetProperty("controlPlaneInitialized").GetBoolean());

            using var liveResponse = await client.GetAsync("/livez");
            Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
            using var readyResponse = await client.GetAsync("/readyz");
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
            using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            Assert.Equal("ready", ready.RootElement.GetProperty("status").GetString());
            Assert.True(ready.RootElement.GetProperty("controlPlaneInitialized").GetBoolean());

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
    }

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

    [Fact]
    public async Task ProvisioningPlansPersistLifecycleRecoverInterruptedWorkAndSanitizeFailure()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "provisioning.db");
        var workerId = Guid.NewGuid().ToString("N");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, [new("tool", "git")]));

        var noOp = await store.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
        Assert.Equal("Pending", noOp.State);
        Assert.Empty(noOp.Actions);
        var noOpClaim = Assert.IsType<ProvisioningPlan>(await store.AcceptProvisioningPlanAsync(workerId));
        var noOpCompleted = await store.ReportProvisioningPlanAsync(noOp.Id,
            new ProvisioningWorkerReport(workerId, "Completed", Result: "No provisioning actions were required."));
        Assert.Equal("Completed", noOpCompleted!.State);
        Assert.NotNull(noOpCompleted.StartedAtUtc);
        Assert.NotNull(noOpCompleted.CompletedAtUtc);
        Assert.Equal(noOpClaim.Id, noOpCompleted.Id);

        var actions = new[] { new ProvisioningAction("runtime-dotnet", "runtime", "dotnet", "10.0"), new ProvisioningAction("refresh", "refresh-capabilities", "worker", Operation: "refresh") };
        var plan = await store.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, actions));
        Assert.Equal(actions, plan.Actions);
        var claimed = Assert.IsType<ProvisioningPlan>(await store.AcceptProvisioningPlanAsync(workerId));
        Assert.Equal("Accepted", claimed.State);
        var restartedStore = new SqliteRegistryStore(database);
        await restartedStore.InitializeAsync();
        var interrupted = await restartedStore.ReportProvisioningPlanAsync(plan.Id,
            new ProvisioningWorkerReport(workerId, "Running", "runtime-dotnet"));
        Assert.Equal("Running", interrupted!.State);
        Assert.Equal("runtime-dotnet", interrupted.CurrentActionId);
        var recovered = await new SqliteRegistryStore(database).AcceptProvisioningPlanAsync(workerId);
        Assert.Equal("Accepted", recovered!.State);
        Assert.Equal(plan.Id, recovered.Id);
        var failed = await restartedStore.ReportProvisioningPlanAsync(plan.Id,
            new ProvisioningWorkerReport(workerId, "Failed", "runtime-dotnet", Failure: "runtime install token=secret-value failed"));
        Assert.Equal("Failed", failed!.State);
        Assert.Contains("[redacted]", failed.Failure);
        Assert.DoesNotContain("secret-value", JsonSerializer.Serialize(failed), StringComparison.Ordinal);
        Assert.NotNull(failed.StartedAtUtc);
        Assert.NotNull(failed.CompletedAtUtc);

        var cancelled = await restartedStore.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
        var transition = await restartedStore.TransitionProvisioningPlanAsync(cancelled.Id, new ProvisioningStateTransition("Cancelled"));
        Assert.Equal("Cancelled", transition!.State);
        Assert.NotNull(transition.CompletedAtUtc);
        Assert.Equal("Completed", (await restartedStore.GetProvisioningPlanAsync(noOp.Id))!.State);
        Assert.Equal(3, (await restartedStore.GetProvisioningPlansAsync()).Count);
    }

    [Fact]
    public void ProvisioningActionsRequireExplicitSafeKindsAndRejectEmbeddedCredentialValues()
    {
        var workerId = Guid.NewGuid().ToString("N");
        Assert.Null(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("dotnet", "runtime", "dotnet", "10.0", "install"),
            new("refresh", "refresh-capabilities", "worker", Operation: "refresh")])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("secret", "tool", "token=private-value")])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("refresh", "refresh-capabilities", "worker")])));
        Assert.Null(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("auth", "authentication", "github-api", Operation: "provision", CredentialId: Guid.NewGuid().ToString("N"), Scope: "team/repo")])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("auth", "authentication", "github-api", Operation: "provision", CredentialId: Guid.NewGuid().ToString("N"))])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("auth", "authentication", "github-api", Operation: "provision", Scope: "team/repo")] )));
    }

    [Fact]
    public async Task StaleWorkerTurnsInterruptedProvisioningIntoAnExplicitFailure()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "provisioning.db"), 10, clock);
        await store.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, [new("tool", "git")]));
        var plan = await store.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, [new("git", "tool", "git")]));
        await store.AcceptProvisioningPlanAsync(workerId);
        await store.ReportProvisioningPlanAsync(plan.Id, new ProvisioningWorkerReport(workerId, "Running", "git"));

        clock.Advance(TimeSpan.FromSeconds(11));
        await store.ExpireLeasesAsync();

        var failed = await store.GetProvisioningPlanAsync(plan.Id);
        Assert.Equal("Failed", failed!.State);
        Assert.Equal("Worker heartbeat expired while provisioning was active.", failed.Failure);
        Assert.NotNull(failed.CompletedAtUtc);
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
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var workerId = Guid.NewGuid().ToString("N");
            const string workerToken = "project-snapshot-worker-token";
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "snapshot worker", "1.0", "test", 1, []));
            var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            Assert.True(await store.RedeemWorkerBootstrapTokenAsync(bootstrap, workerId, workerToken));
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

    [Fact]
    public async Task CentralProjectsWithoutRequirementsRemainValidAndPersistAsAnEmptyList()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "empty-requirements.db"));
        await store.InitializeAsync();
        var definition = new CentralProjectDefinition("Minimal", "team/minimal", "main", "", null);
        Assert.Null(CentralProjectValidation.Error(definition));
        var created = await store.CreateProjectAsync(definition);
        Assert.Empty(created.Requirements);
        Assert.Empty((await store.GetProjectAsync(created.Id))!.Requirements);
    }

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

    [Fact]
    public void WorkerEligibilityMatchesAllRequirementsAndExplainsMissingOrIncompatibleCapabilities()
    {
        ProjectRequirement[] requirements =
        [
            new("runtime", ".NET", ">=10"),
            new("tool", "Docker"),
            new("service", "PostgreSQL", "18.0")
        ];
        Assert.True(WorkerEligibility.Evaluate(null, null).IsEligible);
        Assert.True(WorkerEligibility.Evaluate(requirements,
        [
            new("runtime", "dotnet", "10.0.0"),
            new("tool", "docker"),
            new("service", "postgresql", "18")
        ]).IsEligible);

        var result = WorkerEligibility.Evaluate(requirements,
        [
            new("runtime", "dotnet", "9"),
            new("service", "postgresql", "17.9")
        ]);
        Assert.False(result.IsEligible);
        Assert.Equal(3, result.MissingRequirements.Count);
        Assert.Contains("requires .NET >=10; worker reports .NET 9", result.MissingRequirements);
        Assert.Contains("requires Docker; capability unavailable", result.MissingRequirements);
        Assert.Contains("requires PostgreSQL 18.0; worker reports PostgreSQL 17.9", result.MissingRequirements);
    }

    [Fact]
    public void WorkerDiagnosticsExplainHealthyAndUnhealthyReadiness()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var project = new CentralProject("project", "Project", "team/repository", "main", "", [], 1, now, now);
        WorkerCapability[] readyCapabilities =
        [
            new("authentication", "github-api", Scope: "team/repository"),
            new("authentication", "git-repository", Scope: "team/repository"),
            new("agent-provider", "codex")
        ];
        var healthy = new WorkerRegistrationResponse(2, "worker", "worker", "1.2.3", "linux", 2,
            readyCapabilities, now, now, "online", 0, 2, 2, "running", [], now, "synchronized", "config-v1");

        var healthyDiagnostics = WorkerDiagnosticsDerivation.Derive(healthy, [project], [], "config-v1");

        Assert.Empty(healthyDiagnostics.Reasons);
        Assert.True(healthyDiagnostics.GitHubReady);
        Assert.True(healthyDiagnostics.GitReady);
        Assert.True(healthyDiagnostics.AiAgentReady);
        Assert.True(Assert.Single(healthyDiagnostics.Projects).IsEligible);
        Assert.Equal(now, healthyDiagnostics.LastHeartbeatAtUtc);
        Assert.Equal("synchronized", healthyDiagnostics.ConfigurationSynchronization);

        var unhealthy = healthy with
        {
            Availability = "stale",
            LifecycleState = "draining",
            ActiveExecutions = 2,
            AvailableCapacity = 0,
            Capabilities = [new("tool", "git")]
        };
        var unhealthyDiagnostics = WorkerDiagnosticsDerivation.Derive(unhealthy, [project], [], "config-v1");

        Assert.Contains("Offline", unhealthyDiagnostics.Reasons);
        Assert.Contains("GitHub unavailable", unhealthyDiagnostics.Reasons);
        Assert.Contains("Git unavailable", unhealthyDiagnostics.Reasons);
        Assert.Contains("AI agent unavailable", unhealthyDiagnostics.Reasons);
        Assert.False(Assert.Single(unhealthyDiagnostics.Projects).IsEligible);
        Assert.Contains("requires github-api for team/repository; capability unavailable",
            Assert.Single(unhealthyDiagnostics.Projects).MissingRequirements);

        var draining = healthy with { Availability = "draining", LifecycleState = "draining" };
        Assert.Contains("Draining", WorkerDiagnosticsDerivation.Derive(draining, [project], [], "config-v1").Reasons);
        var full = healthy with { ActiveExecutions = 2, AvailableCapacity = 0 };
        Assert.Contains("At capacity", WorkerDiagnosticsDerivation.Derive(full, [project], [], "config-v1").Reasons);
        var outOfSync = healthy with { ConfigurationVersion = "older-config" };
        var syncDiagnostics = WorkerDiagnosticsDerivation.Derive(outOfSync, [project], [], "config-v1");
        Assert.Equal("out-of-sync", syncDiagnostics.ConfigurationSynchronization);
        Assert.Contains("Configuration synchronization required", syncDiagnostics.Reasons);
    }

    [Fact]
    public void AuthenticationEligibilityRequiresBothApiAndGitReadinessForTheExactRepository()
    {
        var required = WorkerAuthenticationRequirements.ForRepository("team/compiler");
        WorkerCapability[] ready =
        [
            new("authentication", "github-api", Scope: "team/compiler"),
            new("authentication", "git-repository", Scope: "team/compiler")
        ];

        Assert.True(WorkerEligibility.Evaluate(required, ready).IsEligible);
        Assert.False(WorkerEligibility.Evaluate(required, [ready[0]]).IsEligible);
        Assert.False(WorkerEligibility.Evaluate(required, [ready[1]]).IsEligible);
        Assert.False(WorkerEligibility.Evaluate(required, ready.Select(capability => capability with { Scope = "team/other" })).IsEligible);
    }

    [Fact]
    public void ProjectEligibilityRequiresCodexAgentReadiness()
    {
        var project = new CentralProject("project-id", "Project", "team/compiler", "main", "", [], 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var requirements = WorkerAuthenticationRequirements.ForProject(project);
        var repositoryReady = WorkerAuthenticationRequirements.ForRepository(project.Repository)
            .Select(requirement => new WorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope)).ToArray();

        var missingAgent = WorkerEligibility.Evaluate(requirements, repositoryReady);
        Assert.False(missingAgent.IsEligible);
        Assert.Contains("requires codex; capability unavailable", missingAgent.MissingRequirements);
        Assert.True(WorkerEligibility.Evaluate(requirements, [.. repositoryReady, new WorkerCapability("agent-provider", "codex")]).IsEligible);
    }

    [Fact]
    public async Task AssignmentSkipsIncompatibleWorkersKeepsWorkQueuedAndReevaluatesUpdatedCapabilities()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "eligibility.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Capability project", "team/project", "main", "",
            [new("runtime", ".NET", ">=10"), new("tool", "docker")]));
        var execution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "49")));
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "old worker", "1.0", "test", 1,
            [new("runtime", ".NET", "9")]));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1,
            [new("runtime", ".NET", "9")], []));

        var request = new WorkerAssignmentRequest(workerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        var pending = Assert.Single(await store.GetExecutionsAsync());
        Assert.Equal("Queued", pending.State);
        Assert.Equal("no compatible worker", pending.PendingReason);
        Assert.Contains("requires .NET >=10; worker reports .NET 9", pending.MissingRequirements!);
        Assert.Contains("requires Docker; capability unavailable", pending.MissingRequirements!);

        var laterWorkerId = Guid.NewGuid().ToString("N");
        WorkerCapability[] matchingCapabilities = [new("runtime", ".NET", "10.0"), new("tool", "Docker"),
            .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, laterWorkerId, "later worker", "1.0", "test", 1,
            matchingCapabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, laterWorkerId, "1.0", "running", 1, 1,
            matchingCapabilities, [project.Id]));
        var laterRequest = request with { WorkerId = laterWorkerId };
        Assert.False((await store.RequestAssignmentAsync(laterRequest)).HasWork);
        pending = Assert.Single(await store.GetExecutionsAsync());
        Assert.Equal("compatible workers currently at capacity", pending.PendingReason);

        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, laterWorkerId, "1.0", "not-ready", 0, 1,
            matchingCapabilities, []));
        Assert.False((await store.RequestAssignmentAsync(laterRequest)).HasWork);
        Assert.Equal("Queued", Assert.Single(await store.GetExecutionsAsync()).State);

        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, laterWorkerId, "1.0", "running", 0, 1,
            matchingCapabilities, []));
        var assignment = (await store.RequestAssignmentAsync(laterRequest)).Assignment;
        Assert.NotNull(assignment);
        Assert.Equal(execution.Id, assignment.ServerExecutionId);
        Assert.Equal(laterWorkerId, assignment.WorkerId);
        Assert.Equal("Assigned", Assert.Single(await store.GetExecutionsAsync()).State);

        var nextExecution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "50")));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1,
            matchingCapabilities, []));
        var updatedWorkerAssignment = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(updatedWorkerAssignment);
        Assert.Equal(nextExecution.Id, updatedWorkerAssignment.ServerExecutionId);
        Assert.Equal(workerId, updatedWorkerAssignment.WorkerId);
    }

    [Fact]
    public async Task RecoveryAssignmentsAreNodeBoundIdempotentAndExhaustedBaseDoesNotRearmAfterRestart()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-15T00:00:00Z"));
        var path = Path.Combine(temporary.Path, "integration-recovery.db");
        var store = new SqliteRegistryStore(path, timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery project", "team/recovery", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var competitor = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        foreach (var workerId in new[] { owner, competitor })
        {
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 2, capabilities));
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 2, capabilities, []));
        }
        var queued = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17")));
        var request = new WorkerAssignmentRequest(owner, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var initial = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(initial);
        var localId = Guid.NewGuid().ToString();
        await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(owner, initial.AssignmentId, localId,
            "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: initial.Lease!.Generation));
        request = request with { IntegrationRecoveries = [new(project.Id, queued.Id, localId, new string('a', 40))] };
        var rejected = await store.RequestAssignmentAsync(request with { WorkerId = competitor });
        Assert.False(rejected.HasWork);
        Assert.Contains("local execution identity", rejected.IntegrationRecoveryRejections?[localId]);
        var recovery = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(recovery);
        Assert.Equal(localId, recovery.Metadata["integrationRecoveryExecutionId"]);
        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17"))));
        await store.ReportExecutionAsync(recovery.ServerExecutionId, new WorkerExecutionReport(owner, recovery.AssignmentId,
            Guid.NewGuid().ToString(), "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: recovery.Lease!.Generation));
        var restarted = new SqliteRegistryStore(path, timeProvider: clock);
        await restarted.InitializeAsync();
        Assert.False((await restarted.RequestAssignmentAsync(request)).HasWork);
        var changed = request with { IntegrationRecoveries = [new(project.Id, queued.Id, localId, new string('b', 40))] };
        var rearmed = (await restarted.RequestAssignmentAsync(changed)).Assignment;
        Assert.NotNull(rearmed);
        Assert.NotEqual(recovery.ServerExecutionId, rearmed.ServerExecutionId);
        Assert.Equal(3, (await restarted.GetExecutionAsync(rearmed.ServerExecutionId))?.AttemptNumber);
        // A restart in recovery cannot enqueue a fresh implementation after lease expiry.
        await restarted.ReportExecutionAsync(rearmed.ServerExecutionId, new WorkerExecutionReport(owner, rearmed.AssignmentId,
            Guid.NewGuid().ToString(), "Running", Stage: "Preparing", Generation: rearmed.Lease!.Generation));
        clock.Advance(TimeSpan.FromMinutes(16));
        var rows = await restarted.GetExecutionsAsync();
        Assert.DoesNotContain(rows, entry => entry.State == "Queued");
        Assert.Equal("LeaseExpiredUncertain", rows.Single(entry => entry.Id == rearmed.ServerExecutionId).RecoveryState);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrCorruptRecoveryAssignmentMetadataPreservesReservationAndSchedulesOtherIssues(bool missing)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "corrupt-recovery.db");
        var store = new SqliteRegistryStore(path);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery project", "team/recovery", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, owner, "worker", "1.0", "test", 2, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, owner, "1.0", "running", 0, 2, capabilities, []));
        var original = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17")));
        var request = new WorkerAssignmentRequest(owner, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var initial = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(initial);
        var localId = Guid.NewGuid().ToString();
        await store.ReportExecutionAsync(original.Id, new WorkerExecutionReport(owner, initial.AssignmentId, localId,
            "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: initial.Lease!.Generation));
        request = request with { IntegrationRecoveries = [new(project.Id, original.Id, localId, new string('a', 40))] };
        var recovery = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(recovery);
        await store.RejectManagedAssignmentAsync(recovery.ServerExecutionId, recovery.AssignmentId, owner,
            new("unavailable", ["Simulated read interruption"], DateTimeOffset.UtcNow));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = missing ? "DELETE FROM execution_metadata WHERE execution_id=$id" :
                "UPDATE execution_metadata SET metadata_json='{invalid' WHERE execution_id=$id";
            command.Parameters.AddWithValue("$id", recovery.ServerExecutionId);
            await command.ExecuteNonQueryAsync();
        }
        var other = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "18")));
        var next = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.Equal(other.Id, next?.ServerExecutionId);
        var retained = await store.GetExecutionAsync(recovery.ServerExecutionId);
        Assert.NotNull(retained);
        Assert.Equal("Queued", retained.State);
        Assert.Equal("blocked", retained.ManagedEligibilityState);
        Assert.Contains("ownership metadata", Assert.Single(retained.ManagedEligibilityReasons!));
    }

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

    [Fact]
    public async Task WorkerApiTokenRevocationIsPersistentAndSeparateFromCredentialDeliveryAuthorization()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "worker-auth.db");
        var workerId = Guid.NewGuid().ToString("N");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "auth worker", "1.0", "test", 1, []));
        var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
        Assert.True(await store.RedeemWorkerBootstrapTokenAsync(bootstrap, workerId, new string('w', 40)));
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

    [Fact]
    public async Task LocalWorkerAdministrationCliPersistsSchedulingChanges()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "worker-admin-cli.db");
        var workerId = Guid.NewGuid().ToString("N");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "cli worker", "1.0", "test", 1, []));
        var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
        Assert.True(await registry.RedeemWorkerBootstrapTokenAsync(bootstrap, workerId, "cli-worker-api-token"));
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
            command.CommandText = "ALTER TABLE workers DROP COLUMN scheduling_policy; ALTER TABLE execution_requests DROP COLUMN managed_eligibility_checked_at_utc; ALTER TABLE execution_requests DROP COLUMN managed_eligibility_reasons_json; ALTER TABLE execution_requests DROP COLUMN managed_eligibility_state; UPDATE schema_metadata SET schema_version=11 WHERE singleton=1;";
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = new SqliteRegistryStore(database);
        await upgraded.InitializeAsync();
        var worker = await upgraded.GetWorkerAsync(workerId);
        Assert.Equal(WorkerSchedulingPolicy.Enabled, worker!.SchedulingPolicy);
        Assert.Equal("eligible", (await upgraded.GetExecutionAsync(execution.Id))!.ManagedEligibilityState);
    }

    [Fact]
    public async Task ExecutionQueuePersistsFifoWorkerReportsAndAllowsLaterAttemptsAcrossProjects()
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
        var sameIssueOtherProject = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectB.Id, new WorkReference("issue", "42")));
        Assert.Equal(new[] { first.Id, second.Id, sameIssueOtherProject.Id }, (await store.GetExecutionsAsync()).Select(x => x.Id));

        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(projectA.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "queue worker", "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var assigned = (await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [projectA.Id] = 1 }))).Assignment;
        Assert.NotNull(assigned);
        Assert.Equal(new WorkReference("github-issue", "42", "https://github.com/team/alpha/issues/42"), assigned.Work);
        var assignment = await store.GetExecutionAsync(first.Id);
        Assert.NotNull(assignment);
        Assert.Equal(assigned.AssignmentId, assignment.AssignmentId);
        Assert.Equal(first.Id, assigned.ServerExecutionId);
        var leased = assignment;
        Assert.Equal("Assigned", leased!.State);
        Assert.Equal(workerId, leased.AssignedWorkerId);
        Assert.NotNull(leased.AssignedAtUtc);
        var lease = Assert.IsType<ExecutionLease>(leased.Lease);
        Assert.Equal(new ExecutionLease(first.Id, workerId, 1, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(15), "Active"), lease);
        var started = DateTimeOffset.Parse("2026-02-01T00:00:03Z");
        var running = await store.ReportExecutionAsync(first.Id, new WorkerExecutionReport(workerId, assigned.AssignmentId, "run-a", "Running", "Implementing", started, Generation: lease.Generation));
        Assert.Equal("Running", running!.State);
        Assert.Equal("run-a", running.ExecutionId);
        var finalReport = new WorkerExecutionReport(workerId, assigned.AssignmentId, "run-a", "Completed", null,
            started, started.AddMinutes(2), 120000, "passed", "integrated", null, false, "Implemented Issue #42.", lease.Generation);
        var completed = await store.ReportExecutionAsync(first.Id, finalReport);
        var duplicate = await store.ReportExecutionAsync(first.Id, finalReport);
        Assert.Equal("Completed", completed!.State);
        Assert.Equal(completed, duplicate);
        Assert.Equal("Implementing", completed.CurrentStage);
        Assert.Equal("run-a", completed.WorkerExecutionId);
        Assert.Equal("passed", completed.ValidationResult);
        Assert.Equal("integrated", completed.IntegrationResult);
        Assert.Equal("Implemented Issue #42.", completed.CompletionSummary);
        Assert.Equal("Released", completed.Lease!.State);
        Assert.Equal(1L, completed.Lease.Generation);
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
    public async Task ManagedWorkReferencesAreCanonicalPositiveGitHubIssuesAndRemainUniqueAcrossAliasesAndRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "canonical-issues.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Canonical", "Team/Compiler", "main", "", []));

        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id,
            new WorkReference("issue", "00042", "https://github.com/team/compiler/issues/42")));

        Assert.Equal(new WorkReference("github-issue", "42", "https://github.com/Team/Compiler/issues/42"), first.WorkReference);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("GITHUB-ISSUE", "42"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("pull-request", "42"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "0"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "42abc"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "43", "https://github.com/other/repo/issues/43"))));

        var concurrentRequests = new[]
        {
            new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "99")),
            new EnqueueExecutionRequest(project.Id, new WorkReference("github-issue", "099"))
        };
        var results = await Task.WhenAll(concurrentRequests.Select(async request =>
        {
            try
            {
                await store.EnqueueExecutionAsync(request);
                return true;
            }
            catch (ExecutionRequestConflictException) { return false; }
        }));
        Assert.Single(results, created => created);
        Assert.Single(results, created => !created);

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        var persisted = Assert.Single(await restarted.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, WorkType: "issue", WorkId: "42", Limit: 10)));
        Assert.Equal(first.WorkReference, persisted.WorkReference);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => restarted.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "42"))));
    }

    [Fact]
    public async Task ExecutionAdministrationFiltersDetailsCancelsOnlyQueuedWorkAndPersistsAcrossRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "execution-administration.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Administration", "team/admin", "main", "", []));
        var assignedRequest = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var cancelled = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var filtered = await store.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, WorkType: "issue", WorkId: "0002", Limit: 1));
        Assert.Equal(assignedRequest.Id, Assert.Single(filtered).Id);
        Assert.Equal(assignedRequest.Id, (await store.GetExecutionAsync(assignedRequest.Id))!.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListExecutionsAsync(new ExecutionQuery(Limit: 101)));

        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "admin worker", "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var assignment = (await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }))).Assignment!;
        Assert.Equal(assignedRequest.Id, assignment.ServerExecutionId);
        await Assert.ThrowsAsync<ExecutionRequestCancellationException>(() => store.CancelQueuedExecutionAsync(assignedRequest.Id));
        var cancelledRequest = await store.CancelQueuedExecutionAsync(cancelled.Id);
        Assert.Equal("Cancelled", cancelledRequest!.State);
        Assert.Null(cancelledRequest.Lease);
        await Assert.ThrowsAsync<ExecutionRequestTransitionException>(() => store.TransitionExecutionAsync(assignedRequest.Id,
            new ExecutionStateTransition("Completed")));

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        Assert.Equal("Cancelled", (await restarted.GetExecutionAsync(cancelled.Id))!.State);
        Assert.Equal("Assigned", (await restarted.GetExecutionAsync(assignedRequest.Id))!.State);
        Assert.Equal(2, (await restarted.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, Limit: 100))).Count);
    }

    [Fact]
    public async Task AssignmentLeaseIsExclusiveDurableAndReleasedOnTerminalReport()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "lease-race.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Lease", "team/lease", "main", "", []));
        var firstWorker = Guid.NewGuid().ToString("N");
        var secondWorker = Guid.NewGuid().ToString("N");
        foreach (var worker in new[] { firstWorker, secondWorker })
        {
            WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, worker, worker, "1.0", "test", 1, capabilities));
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, worker, "1.0", "running", 0, 1, capabilities, []));
        }
        var execution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        WorkerAssignmentRequest Request(string worker) => new(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        var attempts = await Task.WhenAll(store.RequestAssignmentAsync(Request(firstWorker)), store.RequestAssignmentAsync(Request(secondWorker)));
        var acquired = Assert.Single(attempts, x => x.HasWork).Assignment!;
        Assert.Equal(execution.Id, acquired.ServerExecutionId);
        Assert.Equal(1L, acquired.Lease!.Generation);
        Assert.Equal(clock.GetUtcNow(), acquired.Lease.AcquiredAtUtc);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), acquired.Lease.ExpiresAtUtc);
        Assert.False((await store.RequestAssignmentAsync(Request(firstWorker))).HasWork);

        var restarted = new SqliteRegistryStore(database, timeProvider: clock);
        await restarted.InitializeAsync();
        var owned = Assert.Single(await restarted.GetExecutionsAsync());
        Assert.Equal(acquired.WorkerId, owned.Lease!.WorkerId);
        Assert.Equal(1L, owned.Lease.Generation);
        Assert.Equal("Active", owned.Lease.State);

        var staleReport = new WorkerExecutionReport("stale-worker", acquired.AssignmentId, "stale-run", "Completed",
            CompletedAtUtc: clock.GetUtcNow(), Generation: acquired.Lease!.Generation);
        await Assert.ThrowsAsync<ExecutionRequestOwnershipException>(() => restarted.ReportExecutionAsync(execution.Id, staleReport));
        var unchanged = await restarted.GetExecutionsAsync();
        Assert.Equal("Assigned", Assert.Single(unchanged).State);
        Assert.Equal("Active", Assert.Single(unchanged).Lease!.State);
        await Assert.ThrowsAsync<ExecutionRequestOwnershipException>(() => restarted.ReportExecutionAsync(execution.Id, new WorkerExecutionReport(
            acquired.WorkerId, acquired.AssignmentId, "old-run", "Running", "Codex", Generation: acquired.Lease.Generation + 1)));

        var report = new WorkerExecutionReport(acquired.WorkerId, acquired.AssignmentId, "lease-run", "Completed",
            CompletedAtUtc: clock.GetUtcNow(), Generation: acquired.Lease!.Generation);
        var completed = await restarted.ReportExecutionAsync(execution.Id, report);
        Assert.Equal("Released", completed!.Lease!.State);
        Assert.Equal(acquired.WorkerId, completed.Lease.WorkerId);
        Assert.Equal(1L, completed.Lease.Generation);
        Assert.Equal(1L, await LeaseCountAsync(database, execution.Id));
    }

    [Fact]
    public async Task LeaseRenewalIsGenerationAndOwnerBoundAndExpiryCreatesSafeRetry()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "lease-renewal.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-04-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 120, leaseRenewalIntervalSeconds: 20);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Renewal", "team/renewal", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        foreach (var worker in new[] { owner, other })
        {
            WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, worker, worker, "1.0", "test", 2, capabilities));
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, worker, "1.0", "running", 0, 2, capabilities, []));
        }
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var second = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3")));
        WorkerAssignmentRequest Request(string worker) => new(worker, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var firstAssignment = (await store.RequestAssignmentAsync(Request(owner))).Assignment!;
        var secondAssignment = (await store.RequestAssignmentAsync(Request(owner))).Assignment!;
        Assert.Equal(3, (await store.GetExecutionsAsync()).Count); // both leases and the queued request persist across reads
        var lease = firstAssignment.Lease!;
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), lease.ExpiresAtUtc);
        Assert.Null(await store.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(other, lease.Generation)));
        Assert.Null(await store.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, lease.Generation + 1)));
        clock.Advance(TimeSpan.FromSeconds(30)); // a missed scheduled renewal still leaves ample safety margin
        var renewed = await store.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, lease.Generation));
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), renewed!.ExpiresAtUtc);

        var restarted = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 120, leaseRenewalIntervalSeconds: 20);
        await restarted.InitializeAsync();
        var afterRestart = await restarted.GetExecutionsAsync();
        Assert.Equal(renewed.ExpiresAtUtc, afterRestart.Single(x => x.Id == first.Id).Lease!.ExpiresAtUtc);
        Assert.Equal("Active", afterRestart.Single(x => x.Id == second.Id).Lease!.State);

        clock.Advance(TimeSpan.FromSeconds(91));
        var expired = await restarted.GetExecutionsAsync();
        Assert.Equal("Expired", expired.Single(x => x.Id == second.Id).Lease!.State);
        var expiredAttempt = expired.Single(x => x.Id == second.Id);
        Assert.Equal("Failed", expiredAttempt.State);
        Assert.Equal("LeaseExpiredRequeued", expiredAttempt.RecoveryState);
        var retry = Assert.Single(expired, x => x.RetryOfExecutionId == second.Id);
        Assert.Equal("Queued", retry.State);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.Null(await restarted.RenewExecutionLeaseAsync(second.Id,
            new ExecutionLeaseRenewal(owner, secondAssignment.Lease!.Generation)));
        Assert.Null(await restarted.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, 0)));
        await restarted.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, other, "1.0", "running", 0, 2,
            [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)], []));
        var laterAssignment = (await restarted.RequestAssignmentAsync(Request(other))).Assignment;
        Assert.Equal(queued.Id, laterAssignment!.ServerExecutionId);
        Assert.Equal("Failed", (await restarted.GetExecutionsAsync()).Single(x => x.Id == second.Id).State);

        var final = await restarted.ReportExecutionAsync(first.Id, new WorkerExecutionReport(owner,
            firstAssignment.AssignmentId, "run-renew-1", "Completed", CompletedAtUtc: clock.GetUtcNow(), Generation: firstAssignment.Lease!.Generation));
        Assert.Equal("Released", final!.Lease!.State);
        Assert.Null(await restarted.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, lease.Generation)));
    }

    [Fact]
    public async Task ExpiredLeaseReconciliationCreatesLinkedAttemptOnlyBeforeIntegrationAndIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "reconciliation.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 10, leaseRenewalIntervalSeconds: 2);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery", "team/recovery", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 5, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 5, capabilities, []));
        var safe = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var implementing = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var validating = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3")));
        var claiming = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "4")));
        var uncertain = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "5")));
        WorkerAssignmentRequest request = new(workerId, true, 5, new Dictionary<string, int> { [project.Id] = 5 });
        var safeAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var implementingAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var validatingAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var claimingAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var uncertainAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        await store.ReportExecutionAsync(implementing.Id, new WorkerExecutionReport(workerId, implementingAssignment.AssignmentId,
            "worker-implementation", "Running", Stage: "Codex", StartedAtUtc: clock.GetUtcNow(), Generation: implementingAssignment.Lease!.Generation));
        await store.ReportExecutionAsync(validating.Id, new WorkerExecutionReport(workerId, validatingAssignment.AssignmentId,
            "worker-validation", "Running", Stage: "Validation", StartedAtUtc: clock.GetUtcNow(), Generation: validatingAssignment.Lease!.Generation));
        await store.ReportExecutionAsync(uncertain.Id, new WorkerExecutionReport(workerId, uncertainAssignment.AssignmentId,
            "worker-execution", "Running", Stage: "Integration", StartedAtUtc: clock.GetUtcNow(), Generation: uncertainAssignment.Lease!.Generation));
        await store.ReportExecutionAsync(claiming.Id, new WorkerExecutionReport(workerId, claimingAssignment.AssignmentId,
            "worker-claiming", "Running", Stage: "Claiming", StartedAtUtc: clock.GetUtcNow(), Generation: claimingAssignment.Lease!.Generation));

        clock.Advance(TimeSpan.FromSeconds(11));
        var afterRestart = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 10, leaseRenewalIntervalSeconds: 2);
        await afterRestart.InitializeAsync(); // reconciliation also runs after a Server restart
        var firstRead = await afterRestart.GetExecutionsAsync();
        var safeOriginal = firstRead.Single(x => x.Id == safe.Id);
        var safeRetry = Assert.Single(firstRead, x => x.RetryOfExecutionId == safe.Id);
        Assert.Equal("LeaseExpiredRequeued", safeOriginal.RecoveryState);
        Assert.Equal("Failed", safeOriginal.State);
        Assert.Equal("Queued", safeRetry.State);
        Assert.Equal(2, safeRetry.AttemptNumber);
        Assert.Equal(safe.Id, safeRetry.RetryOfExecutionId);
        Assert.Equal(workerId, safeOriginal.Lease!.WorkerId);
        Assert.Equal("PreviousWorkerLocalStateUnknown", safeOriginal.WorkspaceRecovery);
        Assert.Equal("FreshWorkspaceRequired", safeRetry.WorkspaceRecovery);
        Assert.Equal("LeaseExpiredRequeued", firstRead.Single(x => x.Id == implementing.Id).RecoveryState);
        Assert.Single(firstRead, x => x.RetryOfExecutionId == implementing.Id);
        var validatingOriginal = firstRead.Single(x => x.Id == validating.Id);
        Assert.Equal("LeaseExpiredRequeued", validatingOriginal.RecoveryState);
        Assert.Single(firstRead, x => x.RetryOfExecutionId == validating.Id);

        var uncertainResult = firstRead.Single(x => x.Id == uncertain.Id);
        Assert.Equal("LeaseExpiredUncertain", uncertainResult.RecoveryState);
        Assert.Contains("Integration", uncertainResult.RecoveryReason);
        Assert.DoesNotContain(firstRead, x => x.RetryOfExecutionId == uncertain.Id);
        Assert.Equal("LeaseExpiredUncertain", firstRead.Single(x => x.Id == claiming.Id).RecoveryState);
        Assert.DoesNotContain(firstRead, x => x.RetryOfExecutionId == claiming.Id);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => afterRestart.EnqueueExecutionAsync(
            new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "5"))));

        var verifiedRetry = await afterRestart.ReconcileUncertainExecutionAsync(uncertain.Id,
            new ExecutionReconciliationRequest("NotIntegrated", "Checked authoritative base branch and confirmed the execution commit is absent."));
        Assert.NotNull(verifiedRetry);
        Assert.Equal("Failed", verifiedRetry.Execution.State);
        Assert.Equal("OperatorRetryQueued", verifiedRetry.Execution.RecoveryState);
        Assert.Equal(uncertain.Id, verifiedRetry.Retry!.RetryOfExecutionId);
        Assert.Equal(2, verifiedRetry.Retry.AttemptNumber);
        Assert.Equal("Queued", verifiedRetry.Retry.State);
        await Assert.ThrowsAsync<ExecutionRequestReconciliationException>(() => afterRestart.ReconcileUncertainExecutionAsync(uncertain.Id,
            new ExecutionReconciliationRequest("NotIntegrated", "Duplicate operator request.")));

        var verifiedIntegrated = await afterRestart.ReconcileUncertainExecutionAsync(claiming.Id,
            new ExecutionReconciliationRequest("Integrated", "Confirmed the commit on the protected base branch.", new string('a', 40)));
        Assert.NotNull(verifiedIntegrated);
        Assert.Null(verifiedIntegrated.Retry);
        Assert.Equal("Failed", verifiedIntegrated.Execution.State);
        Assert.Equal("OperatorVerifiedIntegrated", verifiedIntegrated.Execution.RecoveryState);
        Assert.Contains(new string('a', 40), Assert.IsType<string>(verifiedIntegrated.Execution.IntegrationResult));
        await Assert.ThrowsAsync<ExecutionRequestOwnershipException>(() => afterRestart.ReportExecutionAsync(uncertain.Id,
            new WorkerExecutionReport(workerId, uncertainAssignment.AssignmentId, "late-worker-report", "Completed",
                CompletedAtUtc: clock.GetUtcNow(), Generation: uncertainAssignment.Lease!.Generation)));

        var secondRead = await afterRestart.GetExecutionsAsync();
        Assert.Equal(9, secondRead.Count);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == safe.Id);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == implementing.Id);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == validating.Id);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == uncertain.Id);
        Assert.Single(secondRead, x => x.Id == uncertain.Id && x.RecoveryState == "OperatorRetryQueued");
        Assert.Single(secondRead, x => x.Id == claiming.Id && x.RecoveryState == "OperatorVerifiedIntegrated");
    }

    private static async Task<long> LeaseCountAsync(string database, string executionId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM execution_leases WHERE execution_id=$id;";
        command.Parameters.AddWithValue("$id", executionId);
        return (long)(await command.ExecuteScalarAsync())!;
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
            await using var app = await ServerApplication.BuildAsync(Args(url, database), githubReadService: new AlwaysEligibleServerGitHubReadService());
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var alpha = await store.CreateProjectAsync(new CentralProjectDefinition("Alpha", "team/alpha", "main", "", []));
            var beta = await store.CreateProjectAsync(new CentralProjectDefinition("Beta", "team/beta", "main", "", []));
            WorkerCapability[] Capabilities(string id) => id == workerA
                ? [new("tool", "git"), .. AuthenticationCapabilities("team/alpha"), .. AuthenticationCapabilities("team/beta")]
                : [new("tool", "git"), .. AuthenticationCapabilities("team/alpha")];
            alphaId = alpha.Id;
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(alpha.Id, new WorkReference("issue", "1")));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(alpha.Id, new WorkReference("issue", "2")));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(beta.Id, new WorkReference("issue", "3")));
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "assignment-test-token");
                foreach (var id in new[] { workerA, workerB })
                {
                    var registration = new WorkerRegistrationRequest(1, id, id, "1.0", "test", 2, Capabilities(id));
                    Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{id}", registration)).StatusCode);
                }
                async Task Heartbeat(string id, string state) => Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/v1/workers/{id}/heartbeat",
                        new WorkerHeartbeatRequest(1, id, "1.0", state, 0, 2, Capabilities(id), []))).StatusCode);
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
                Assert.Equal(workerA, first.Assignment.Lease!.WorkerId);
                Assert.Equal(first.Assignment.ServerExecutionId, first.Assignment.Lease.ExecutionId);
                Assert.Equal(1L, first.Assignment.Lease.Generation);
                Assert.NotEqual(first.Assignment.AssignmentId, first.Assignment.ServerExecutionId);
                firstAssignmentId = first.Assignment.AssignmentId;

                // A project with no remaining declared slot is skipped while other projects remain eligible.
                using var secondResponse = await Request(workerA, true, 2, new() { [alpha.Id] = 1, [beta.Id] = 1 });
                var second = (await secondResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!;
                Assert.True(second.HasWork);
                Assert.Equal(beta.Id, second.Assignment!.Project.Id);
                var report = new WorkerExecutionReport(workerA, second.Assignment.AssignmentId, "worker-run-2", "Running", "Validation",
                    DateTimeOffset.UtcNow, Generation: second.Assignment.Lease!.Generation);
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
                var owned = Assert.Single(await store.GetExecutionsAsync(), x => x.AssignmentId == first.Assignment.AssignmentId);
                Assert.Equal("Assigned", owned.State);
                Assert.Equal(workerA, owned.AssignedWorkerId);
                Assert.Equal(first.Assignment.ServerExecutionId, owned.Id);
                await app.StopAsync();
                await app.DisposeAsync();
            }

            await using var restarted = await ServerApplication.BuildAsync(Args(url, database));
            await restarted.StartAsync();
            var restartedStore = restarted.Services.GetRequiredService<IRegistryStore>();
            var retained = Assert.Single(await restartedStore.GetExecutionsAsync(), x => x.AssignmentId == firstAssignmentId);
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
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "server.db")),
                githubReadService: new AlwaysEligibleServerGitHubReadService());
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var projectDefinition = new CentralProjectDefinition("Queue project", "team/queue", "main", "", [new("tool", "docker")]);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "execution-test-token");
            var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", projectDefinition);
            var project = (await projectResponse.Content.ReadFromJsonAsync<CentralProject>())!;
            client.DefaultRequestHeaders.Authorization = null;
            var request = new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "7", "https://github.com/team/queue/issues/7"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/executions", request)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "execution-test-token");
            var created = await client.PostAsJsonAsync("/api/v1/executions", request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var representation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.Equal(project.Id, representation.RootElement.GetProperty("projectId").GetString());
            Assert.Equal("Queued", representation.RootElement.GetProperty("state").GetString());
            Assert.Equal("github-issue", representation.RootElement.GetProperty("workReference").GetProperty("type").GetString());
            Assert.Equal("7", representation.RootElement.GetProperty("workReference").GetProperty("id").GetString());
            Assert.True(representation.RootElement.TryGetProperty("createdAtUtc", out _));
            var executionId = representation.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/executions", request)).StatusCode);
            var list = await client.GetFromJsonAsync<ExecutionRequest[]>("/api/v1/executions");
            var queued = Assert.Single(list!);
            Assert.Equal("Queued", queued.State);
            Assert.Equal("no compatible worker", queued.PendingReason);
            Assert.Contains("requires Docker; capability unavailable", queued.MissingRequirements!);
            Assert.Equal(executionId, (await client.GetFromJsonAsync<ExecutionRequest>($"/api/v1/executions/{executionId}"))!.Id);
            Assert.Single((await client.GetFromJsonAsync<ExecutionRequest[]>($"/api/v1/executions?projectId={project.Id}&state=Queued&limit=1"))!);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/executions?limit=101")).StatusCode);
            Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync($"/api/v1/executions/{executionId}/state",
                new ExecutionStateTransition("Completed"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/executions",
                new EnqueueExecutionRequest(project.Id, new WorkReference("pull-request", "8")))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/executions",
                new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "8", "https://github.com/other/repo/issues/8")))).StatusCode);
            using var cancelled = await client.PostAsync($"/api/v1/executions/{executionId}/cancel", content: null);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<ExecutionRequest>())!.State);
            Assert.Contains("Execution administration", await (await client.GetAsync("/")).Content.ReadAsStringAsync());
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

    [Fact]
    public async Task EmptyDataDirectoryIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(
            ["--Server:ListenUrl=http://127.0.0.1:5090", "--Server:DataDirectory="]));
    }

    private static string[] Args(string url, string databasePath)
    {
        // Keep successful ASP.NET Core lifetime and request logs out of normal test output.
        // Setting CODEXSERVER_TEST_LOG_LEVEL to Information or Debug restores verbose diagnostics.
        var logLevel = Environment.GetEnvironmentVariable("CODEXSERVER_TEST_LOG_LEVEL") ?? "Warning";
        return
        [
            $"--Server:ListenUrl={url}",
            $"--Server:DatabasePath={databasePath}",
            $"--Logging:LogLevel:Default={logLevel}",
            $"--Logging:LogLevel:Microsoft.AspNetCore={logLevel}"
        ];
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ServerWorkerCapability[] AuthenticationCapabilities(string repository) =>
        [.. WorkerAuthenticationRequirements.ForRepository(repository).Select(requirement =>
            new ServerWorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope)), new ServerWorkerCapability("agent-provider", "codex")];

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
