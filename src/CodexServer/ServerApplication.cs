namespace CodexServer;

using CodexProvisioning;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record ServerStatus(string State, string Version, DateTimeOffset StartedAtUtc);
public sealed record ServerVersion(string Version, string Product);
public sealed record ServerHealth(string Status, bool PersistenceAvailable, bool ControlPlaneInitialized = true);
public sealed record ProjectUpdateRequest(CentralProjectDefinition Definition, long ExpectedRevision);

public interface IServerHealthService
{
    Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken);
}

public sealed class ServerHealthService(IRegistryStore registryStore) : IServerHealthService
{
    public async Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var available = await registryStore.IsAvailableAsync(cancellationToken);
        return new ServerHealth(available ? "healthy" : "unhealthy", available, ControlPlaneInitialized: true);
    }
}

public static class ServerApplication
{
    internal static WebApplicationBuilder CreateBuilder(string[] args) =>
        WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // Match configuration for both the service and operator commands.
            ContentRootPath = AppContext.BaseDirectory
        });

    public static async Task<WebApplication> BuildAsync(string[] args, CancellationToken cancellationToken = default,
        NodeCapabilityDiscovery? capabilityDiscovery = null)
    {
        var builder = CreateBuilder(args);
        var configuration = new ServerConfiguration();
        builder.Configuration.GetSection("Server").Bind(configuration);
        configuration.Validate();
        builder.WebHost.UseUrls(configuration.ListenUrl);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(capabilityDiscovery ?? new NodeCapabilityDiscovery());
        builder.Services.AddSingleton<IRegistryStore>(_ => new SqliteRegistryStore(configuration.ResolveDatabasePath(), configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds));
        builder.Services.AddSingleton<ICredentialStore>(_ => new SqliteCredentialStore(configuration.ResolveDatabasePath()));
        builder.Services.AddSingleton<IServerHealthService, ServerHealthService>();
        builder.Services.AddSingleton(_ => new ProvisioningCommandStore(configuration.ResolveDatabasePath()));
        builder.Services.AddSingleton<NodeProvisioningCommandExecutor>();
        builder.Services.AddHostedService<LocalProvisioningCommandService>();
        builder.Services.AddHostedService<ExecutionLeaseExpirationService>();
        builder.Services.AddSingleton(new ServerStatus("ready", DisplayVersion, DateTimeOffset.UtcNow));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Codex-Request-Id"] = context.TraceIdentifier;
            await next(context);
        });
        var persistence = app.Services.GetRequiredService<IRegistryStore>();
        await persistence.InitializeAsync(cancellationToken);
        await app.Services.GetRequiredService<ProvisioningCommandStore>().InitializeAsync(cancellationToken);
        await app.Services.GetRequiredService<ICredentialStore>().InitializeAsync(cancellationToken);
        app.Logger.LogInformation("Codex Server {Version} initialized in {RuntimeMode} mode; listening on {ListenUrl}; persistent data directory: {DataDirectory}",
            DisplayVersion, app.Environment.EnvironmentName, configuration.ListenUrl, configuration.ResolveDataDirectory());
        app.MapGet("/livez", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/readyz", async (IServerHealthService healthService, HttpContext context) =>
        {
            try
            {
                var health = await healthService.GetHealthAsync(context.RequestAborted);
                return health.PersistenceAvailable && health.ControlPlaneInitialized
                    ? Results.Ok(new { status = "ready", health.PersistenceAvailable, health.ControlPlaneInitialized })
                    : Results.Json(new { status = "not-ready", health.PersistenceAvailable, health.ControlPlaneInitialized }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { status = "not-ready", persistenceAvailable = false, controlPlaneInitialized = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapGet("/api/v1/nodes", async (HttpContext context, ServerConfiguration settings, IRegistryStore store,
            NodeCapabilityDiscovery discovery, IServerHealthService health, ProvisioningCommandStore commands) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var nodes = new List<ProvisionableNode>();
            var local = await discovery.GetAsync(cancellationToken: context.RequestAborted);
            var serverHealth = await health.GetHealthAsync(context.RequestAborted);
            nodes.Add(new("server", "server", "Codex Server", "connected",
                "not-applicable", local.Any(state => state.Operation.State == CapabilityOperationState.Running) ? "busy" : "ready", false,
                CapabilityCatalog.Definitions.Select(definition => CapabilityCatalog.Describe(definition,
                    local.Single(state => state.Id == definition.Id), true)).ToArray(), serverHealth.Status));
            var plans = await store.GetProvisioningPlansAsync(context.RequestAborted);
            foreach (var worker in await store.GetWorkersAsync(context.RequestAborted))
                nodes.Add(NodeProvisioning.Describe(worker, plans));
            var operations = await commands.ListAsync(context.RequestAborted);
            return Results.Ok(nodes.Select(node => NodeProvisioning.WithCommands(node, operations)));
        });
        app.MapPost("/api/v1/nodes/server/capabilities/refresh", async (HttpContext context, ServerConfiguration settings,
            NodeCapabilityDiscovery discovery) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            return Results.Ok(await discovery.GetAsync(refresh: true, cancellationToken: context.RequestAborted));
        });
        app.MapPost("/api/v1/provisioning/commands", async (ProvisioningCommandRequest request, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ProvisioningCommandStore commands) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            if (!ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request))
                return Results.BadRequest(new { error = "Unsupported or invalid provisioning action." });
            if (request.NodeId != "server")
            {
                var worker = await registry.GetWorkerAsync(request.NodeId, context.RequestAborted);
                if (worker is null) return Results.NotFound();
                if (worker.Availability == "stale") return Results.Conflict(new { error = "Worker is offline." });
            }
            try
            {
                var operation = await commands.CreateAsync(request, context.RequestAborted);
                return Results.Created($"/api/v1/provisioning/commands/{operation.Id}", operation);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapGet("/api/v1/provisioning/commands", async (HttpContext context, ServerConfiguration settings, ProvisioningCommandStore commands) =>
            Authorized(context, settings, management: true) ? Results.Ok(await commands.ListAsync(context.RequestAborted)) : Results.Unauthorized());
        app.MapGet("/api/v1/provisioning/commands/{id}", async (string id, HttpContext context, ServerConfiguration settings, ProvisioningCommandStore commands) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var operation = (await commands.ListAsync(context.RequestAborted)).FirstOrDefault(item => item.Id == id);
            return operation is null ? Results.NotFound() : Results.Ok(operation);
        });
        app.MapPost("/api/v1/provisioning/commands/{id}/cancel", async (string id, HttpContext context, ServerConfiguration settings, ProvisioningCommandStore commands) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            try
            {
                var operation = await commands.CancelAsync(id, context.RequestAborted);
                return operation is null ? Results.NotFound() : Results.Ok(operation);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/provisioning/commands/{id}/reconcile", async (string id, bool nodeQuiescent, HttpContext context, ServerConfiguration settings, ProvisioningCommandStore commands) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            if (!nodeQuiescent) return Results.BadRequest(new { error = "Verify that the node operation has stopped before reconciliation." });
            try
            {
                var operation = await commands.ReconcileAsync(id, context.RequestAborted);
                return operation is null ? Results.NotFound() : Results.Ok(operation);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/provisioning/commands/request", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ProvisioningCommandStore commands) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, registry, workerId)) return Results.Unauthorized();
            var worker = await registry.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null || worker.Availability == "stale") return Results.NoContent();
            var operation = await commands.ClaimAsync(workerId, context.RequestAborted);
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        app.MapPost("/api/v1/workers/{workerId}/provisioning/commands/{id}/report", async (string workerId, string id,
            ProvisioningCommandReport report, HttpContext context, ServerConfiguration settings, IRegistryStore registry, ProvisioningCommandStore commands) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, registry, workerId)) return Results.Unauthorized();
            if (!Guid.TryParseExact(workerId, "N", out _) || await registry.GetWorkerAsync(workerId, context.RequestAborted) is null)
                return Results.NotFound();
            try
            {
                var operation = await commands.ReportAsync(id, workerId, report, context.RequestAborted);
                return operation is null ? Results.NotFound() : Results.Ok(operation);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapGet("/api/status", (ServerStatus status) => Results.Ok(status));
        app.MapGet("/api/version", () => Results.Ok(new ServerVersion(DisplayVersion, "Codex Server")));
        app.MapPost("/api/v1/workers/register", async (WorkerRegistrationRequest request, HttpContext context, IRegistryStore store) =>
        {
            if (request is null || !Valid(request))
            {
                return RegistrationError(app, context, StatusCodes.Status400BadRequest, "invalid_worker_registration",
                    "Invalid worker registration contract. Check contractVersion (1 or 2), workerId (32-digit GUID), displayName (1..200), workerVersion (1..100), platform (1..300), capacity (1..8), and capabilities (up to 32 valid entries).");
            }
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return RegistrationError(app, context, StatusCodes.Status401Unauthorized, "missing_bootstrap_token",
                    "A Bearer registration token is required. Create a fresh registration token and retry.");
            var workerToken = context.Request.Headers["X-Codex-Worker-Token"].ToString();
            var accepted = await store.BootstrapWorkerAsync(authorization[7..], request, workerToken, context.RequestAborted);
            if (!accepted)
            {
                return RegistrationError(app, context, StatusCodes.Status401Unauthorized, "invalid_bootstrap_token",
                    "Worker bootstrap token is invalid, expired, or has already been used. Create a fresh registration token and retry.");
            }
            return Results.Ok(new { workerId = request.WorkerId });
        });
        app.MapPost("/api/v1/credentials", async (CreateCredentialRequest request, HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            if (request is null || request.Secret is null) return Results.BadRequest(new { error = "Credential metadata and secret are required." });
            try
            {
                var metadata = await store.CreateAsync(request, context.RequestAborted);
                return Results.Created($"/api/v1/credentials/{metadata.Id}", metadata);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException) { return Results.Json(new { error = "Credential encryption is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapGet("/api/v1/credentials", async (HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            return Results.Ok(await store.ListAsync(context.RequestAborted));
        });
        app.MapGet("/api/v1/credentials/{credentialId}", async (string credentialId, HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var credential = await store.GetAsync(credentialId, context.RequestAborted);
            return credential is null ? Results.NotFound() : Results.Ok(credential);
        });
        app.MapPut("/api/v1/credentials/{credentialId}/assignment", async (string credentialId, CredentialAssignmentRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ICredentialStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            if (request is null || !Guid.TryParseExact(request.WorkerId, "N", out _)) return Results.BadRequest(new { error = "Worker identity is invalid." });
            if (await registry.GetWorkerAsync(request.WorkerId, context.RequestAborted) is null) return Results.NotFound();
            var credential = await store.AssignAsync(credentialId, request.WorkerId, context.RequestAborted);
            return credential is null ? Results.NotFound() : Results.Ok(credential);
        });
        app.MapPut("/api/v1/workers/{workerId}/credential-access", async (string workerId, CredentialSecretInput token,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ICredentialStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            if (token is null || await registry.GetWorkerAsync(workerId, context.RequestAborted) is null) return Results.NotFound();
            try { await store.SetWorkerDeliveryTokenAsync(workerId, token, context.RequestAborted); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.NoContent();
        });
        app.MapGet("/api/v1/workers/{workerId}/credentials/{credentialId}", async (string workerId, string credentialId,
            HttpContext context, ICredentialStore store) =>
        {
            if (!await store.IsWorkerDeliveryTokenValidAsync(workerId, context.Request.Headers["X-Worker-Credential-Token"].ToString(), context.RequestAborted))
                return Results.Unauthorized();
            var metadata = await store.GetAsync(credentialId, context.RequestAborted);
            if (metadata is null || metadata.AssignedWorkerId != workerId || metadata.Status != "Ready") return Results.NotFound();
            var secret = await store.RetrieveForWorkerAsync(credentialId, workerId, context.RequestAborted);
            return secret is null ? Results.NotFound() : Results.Ok(new CredentialDeliveryResponse(metadata.Id, metadata.Provider, metadata.Type, metadata.Version, secret));
        });
        app.MapPost("/api/v1/credentials/{credentialId}/revoke", async (string credentialId, HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var credential = await store.RevokeAsync(credentialId, context.RequestAborted);
            return credential is null ? Results.NotFound() : Results.Ok(credential);
        });
        app.MapPut("/api/v1/credentials/{credentialId}/secret", async (string credentialId, CredentialSecretInput secret,
            HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            try
            {
                var credential = await store.ReplaceSecretAsync(credentialId, secret, context.RequestAborted);
                return credential is null ? Results.NotFound() : Results.Ok(credential);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException) { return Results.Json(new { error = "Credential encryption is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapGet("/", () => Results.Content(ReadDashboard(), "text/html; charset=utf-8"));
        app.MapGet("/health", async (IServerHealthService healthService, HttpContext context) =>
        {
            try
            {
                var health = await healthService.GetHealthAsync(context.RequestAborted);
                return health.PersistenceAvailable && health.ControlPlaneInitialized ? Results.Ok(health) : Results.Json(health, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new ServerHealth("unhealthy", false), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapPut("/api/v1/workers/{workerId}", async (string workerId, WorkerRegistrationRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) || !Valid(request))
                return RegistrationError(app, context, StatusCodes.Status400BadRequest, "invalid_worker_registration",
                    "Invalid worker registration contract. Check that workerId matches the URL, capacity is 1..8, contractVersion is 1 or 2, and metadata and capabilities satisfy the registration limits.");
            await store.RegisterWorkerAsync(request, context.RequestAborted);
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/heartbeat", async (string workerId, WorkerHeartbeatRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) || !Valid(request))
                return Results.BadRequest(new { error = "Invalid worker heartbeat contract." });
            try { await store.HeartbeatWorkerAsync(request, context.RequestAborted); }
            catch (InvalidOperationException) { return Results.NotFound(); }
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/assignments/request", async (string workerId, WorkerAssignmentRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (request is null || !string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) ||
                request.AvailableCapacity is < 0 or > 8 || request.ProjectCapacities is null ||
                request.ProjectCapacities.Count > 128 || request.ProjectCapacities.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 80 || p.Value is < 0 or > 8))
                return Results.BadRequest(new { error = "Invalid Worker assignment request contract." });
            try { return Results.Ok(await store.RequestAssignmentAsync(request, context.RequestAborted)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/provisioning/request", async (string workerId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (!Guid.TryParseExact(workerId, "N", out _)) return Results.BadRequest(new { error = "Worker identity is invalid." });
            var plan = await store.AcceptProvisioningPlanAsync(workerId, context.RequestAborted);
            return plan is null ? Results.NoContent() : Results.Ok(plan);
        });
        app.MapPost("/api/v1/workers/{workerId}/provisioning/{planId}/report", async (string workerId, string planId, ProvisioningWorkerReport report,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (report is null || !string.Equals(workerId, report.WorkerId, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Worker provisioning report identity is invalid." });
            try
            {
                var updated = await store.ReportProvisioningPlanAsync(planId, report, context.RequestAborted);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapGet("/api/v1/provisioning", async (HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            return Results.Ok(await store.GetProvisioningPlansAsync(context.RequestAborted));
        });
        app.MapGet("/api/v1/provisioning/{planId}", async (string planId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var plan = await store.GetProvisioningPlanAsync(planId, context.RequestAborted);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });
        app.MapPost("/api/v1/provisioning/{planId}/state", async (string planId, ProvisioningStateTransition transition,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            try
            {
                var updated = await store.TransitionProvisioningPlanAsync(planId, transition, context.RequestAborted);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/provisioning", async (CreateProvisioningPlanRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var error = ProvisioningPlanValidation.Error(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var plan = await store.CreateProvisioningPlanAsync(request, context.RequestAborted);
                return Results.Created($"/api/v1/provisioning/{plan.Id}", plan);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/executions/{executionRequestId}/report", async (string workerId, string executionRequestId,
            WorkerExecutionReport report, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (report is null || !string.Equals(workerId, report.WorkerId, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Worker execution report identity is invalid." });
            try
            {
                var updated = await store.ReportExecutionAsync(executionRequestId, report, context.RequestAborted);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ExecutionRequestOwnershipException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/executions/{executionId}/lease/renew", async (string workerId, string executionId,
            ExecutionLeaseRenewal renewal, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (renewal is null || !string.Equals(workerId, renewal.WorkerId, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Execution lease renewal identity is invalid." });
            try
            {
                var lease = await store.RenewExecutionLeaseAsync(executionId, renewal, context.RequestAborted);
                return lease is null ? Results.Conflict(new { error = "Execution lease is stale or no longer owned by this Worker." }) : Results.Ok(lease);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/api/v1/workers", async (HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            return Results.Ok(await store.GetWorkersAsync(context.RequestAborted));
        });
        app.MapGet("/api/v1/workers/{workerId}", async (string workerId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
            return worker is null ? Results.NotFound() : Results.Ok(worker);
        });
        app.MapGet("/api/v1/workers/{workerId}/diagnostics", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null) return Results.NotFound();
            var projects = await store.GetProjectsAsync(context.RequestAborted);
            var plans = await store.GetProvisioningPlansAsync(context.RequestAborted);
            return Results.Ok(WorkerDiagnosticsDerivation.Derive(worker, projects, plans, ManagedConfigurationVersion(projects)));
        });
        app.MapGet("/api/v1/workers/{workerId}/configuration", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, settings, store, workerId)) return Results.Unauthorized();
            if (await store.GetWorkerAsync(workerId, context.RequestAborted) is null) return Results.NotFound();
            var projects = await store.GetProjectsAsync(context.RequestAborted);
            var version = ManagedConfigurationVersion(projects);
            return Results.Ok(new ServerManagedConfigurationResponse(1, version, projects));
        });
        app.MapGet("/api/v1/events/stream", async (HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            await StreamWorkerUpdatesAsync(context, store);
            return Results.Empty;
        });
        app.MapGet("/api/v1/projects", async (HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            return Results.Ok(await store.GetProjectsAsync(context.RequestAborted));
        });
        app.MapGet("/api/v1/projects/{projectId}", async (string projectId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var project = await store.GetProjectAsync(projectId, context.RequestAborted);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });
        app.MapPost("/api/v1/projects", async (CentralProjectDefinition definition, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var error = CentralProjectValidation.Error(definition);
            if (error is not null) return Results.BadRequest(new { error });
            try { return Results.Created($"/api/v1/projects/{CentralProjectValidation.IdFor(definition.Name)}", await store.CreateProjectAsync(definition, context.RequestAborted)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPut("/api/v1/projects/{projectId}", async (string projectId, ProjectUpdateRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var error = CentralProjectValidation.Error(request.Definition);
            if (error is not null || request.ExpectedRevision < 1) return Results.BadRequest(new { error = error ?? "expectedRevision must be positive." });
            try
            {
                var updated = await store.UpdateProjectAsync(projectId, request.Definition, request.ExpectedRevision, context.RequestAborted);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (ProjectRevisionConflictException ex) { return Results.Conflict(new { error = ex.Message, currentRevision = ex.CurrentRevision }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapDelete("/api/v1/projects/{projectId}", async (string projectId, long expectedRevision, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            if (expectedRevision < 1) return Results.BadRequest(new { error = "expectedRevision must be positive." });
            try { return await store.RemoveProjectAsync(projectId, expectedRevision, context.RequestAborted) ? Results.NoContent() : Results.NotFound(); }
            catch (ProjectRevisionConflictException ex) { return Results.Conflict(new { error = ex.Message, currentRevision = ex.CurrentRevision }); }
        });
        app.MapGet("/api/v1/executions", async (HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            return Results.Ok(await store.GetExecutionsAsync(context.RequestAborted));
        });
        app.MapPost("/api/v1/executions", async (EnqueueExecutionRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            var error = ExecutionRequestValidation.Error(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var created = await store.EnqueueExecutionAsync(request, context.RequestAborted);
                return Results.Created($"/api/v1/executions/{created.Id}", created);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (ExecutionRequestConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/executions/{executionRequestId}/state", async (string executionRequestId, ExecutionStateTransition transition, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings, management: true)) return Results.Unauthorized();
            try
            {
                var updated = await store.TransitionExecutionAsync(executionRequestId, transition, context.RequestAborted);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ExecutionRequestTransitionException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        return app;
    }

    private static string ReadDashboard()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexServer.dashboard.html")
            ?? throw new InvalidOperationException("The Server dashboard resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task StreamWorkerUpdatesAsync(HttpContext context, IRegistryStore store)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                var workers = await store.GetWorkersAsync(context.RequestAborted);
                await context.Response.WriteAsync("event: workers\ndata: ", context.RequestAborted);
                await context.Response.WriteAsync(JsonSerializer.Serialize(workers), context.RequestAborted);
                await context.Response.WriteAsync("\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            } while (await timer.WaitForNextTickAsync(context.RequestAborted));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static async Task<bool> AuthorizedWorkerAsync(HttpContext context, ServerConfiguration configuration, IRegistryStore store, string workerId)
    {
        if (Authorized(context, configuration)) return true;
        var supplied = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            await store.IsWorkerTokenValidAsync(workerId, supplied[prefix.Length..], context.RequestAborted);
    }

    private static bool Authorized(HttpContext context, ServerConfiguration configuration, bool management = false)
    {
        var expected = management ? configuration.ManagementToken : configuration.RegistrationToken;
        var supplied = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(expected) || !supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var actualBytes = Encoding.UTF8.GetBytes(supplied[prefix.Length..]);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static IResult RegistrationError(WebApplication app, HttpContext context, int statusCode, string code, string error)
    {
        app.Logger.LogWarning("Worker registration rejected with code {ErrorCode}; trace {TraceId}", code, context.TraceIdentifier);
        return Results.Json(new { error, code, requestId = context.TraceIdentifier }, statusCode: statusCode);
    }

    private static bool Valid(WorkerRegistrationRequest request) => request.ContractVersion is 1 or 2 &&
        Guid.TryParseExact(request.WorkerId, "N", out _) && !string.IsNullOrWhiteSpace(request.DisplayName) &&
        request.DisplayName.Length <= 200 && !string.IsNullOrWhiteSpace(request.WorkerVersion) && request.WorkerVersion.Length <= 100 &&
        !string.IsNullOrWhiteSpace(request.Platform) && request.Platform.Length <= 300 && request.Capacity is >= 1 and <= 8 &&
        request.Capabilities is not null && request.Capabilities.Count <= 32 &&
        request.Capabilities.All(ValidCapability) && CapabilityCatalog.ValidInventory(request.CapabilityInventory);

    private static bool Valid(WorkerHeartbeatRequest request) => request.ContractVersion is 1 or 2 &&
        Guid.TryParseExact(request.WorkerId, "N", out _) && !string.IsNullOrWhiteSpace(request.WorkerVersion) &&
        request.WorkerVersion.Length <= 100 && WorkerLifecycleStates.IsValid(request.LifecycleState) &&
        request.ActiveExecutions is >= 0 and <= 8 && request.MaximumCapacity is >= 1 and <= 8 &&
        request.ActiveExecutions <= request.MaximumCapacity && request.Capabilities is not null && request.Capabilities.Count <= 32 &&
        request.Capabilities.All(ValidCapability) &&
        CapabilityCatalog.ValidInventory(request.CapabilityInventory) && request.ActiveProjects is not null && request.ActiveProjects.Count <= 32 &&
        (request.ConfigurationSynchronization is null or "synchronized" or "cached" or "unavailable" or "error" or "not-synchronized") &&
        (request.ConfigurationVersion is null || (request.ConfigurationVersion.Length <= 128 && !request.ConfigurationVersion.Any(char.IsControl))) &&
        request.ActiveProjects.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 200);

    private static bool ValidCapability(WorkerCapability value) => value is not null &&
        !string.IsNullOrWhiteSpace(value.Type) && value.Type.Length <= 40 &&
        !string.IsNullOrWhiteSpace(value.Name) && value.Name.Length <= 100 &&
        !value.Type.Any(char.IsControl) && !value.Name.Any(char.IsControl) &&
        (value.Version is null || (value.Version.Length <= 100 && !value.Version.Any(char.IsControl)));

    internal static string ManagedConfigurationVersion(IReadOnlyList<CentralProject> projects)
    {
        var material = new StringBuilder();
        foreach (var project in projects.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Append(material, "project");
            Append(material, project.Id);
            Append(material, project.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(material, project.Name);
            Append(material, project.Repository);
            Append(material, project.DefaultBranch);
            Append(material, project.Description);
            Append(material, project.Requirements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var requirement in project.Requirements.OrderBy(item => item.Type, StringComparer.Ordinal)
                         .ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal)
                         .ThenBy(item => item.Scope, StringComparer.Ordinal))
            {
                Append(material, requirement.Type);
                Append(material, requirement.Name);
                Append(material, requirement.Version);
                Append(material, requirement.Scope);
            }
            Append(material, "end-project");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string? value)
    {
        if (value is null) target.Append("-1:");
        else target.Append(value.Length).Append(':').Append(value);
    }

    private sealed record ServerManagedConfigurationResponse(int ContractVersion, string Version,
        IReadOnlyList<CentralProject> Projects);

    public static string DisplayVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}
