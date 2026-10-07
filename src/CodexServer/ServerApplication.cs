namespace CodexServer;

using CodexProvisioning;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        NodeCapabilityDiscovery? capabilityDiscovery = null, IServerGitHubReadService? githubReadService = null,
        IServerGitHubIssueWriteService? githubIssueWriteService = null)
    {
        // Verify product assets before opening persistent services.
        var preview = new EmbeddedDashboardAssets();
        var builder = CreateBuilder(args);
        var configuration = new ServerConfiguration();
        builder.Configuration.GetSection("Server").Bind(configuration);
        configuration.Validate();
        builder.WebHost.UseUrls(configuration.ListenUrl);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AdministrationSessions>();
        var databasePath = configuration.ResolveDatabasePath();
        builder.Services.AddSingleton(_ => new ServerDatabaseAccessLock(databasePath, forRestore: false));
        // Server readiness uses the same managed authentication context as Server GitHub reads/writes.
        // Shared defaults retain standalone Worker service-account authentication.
        builder.Services.AddSingleton(capabilityDiscovery ?? new NodeCapabilityDiscovery(requireManagedGitHubAuthentication: true));
        builder.Services.AddSingleton<IServerGitHubReadService>(githubReadService ?? new ServerGitHubReadService());

        builder.Services.AddSingleton<ServerGitHubAdministrationService>(services => new ServerGitHubAdministrationService(
            services.GetRequiredService<IRegistryStore>(), services.GetRequiredService<IServerGitHubReadService>(),
            issueWriter: githubIssueWriteService, cacheDatabasePath: databasePath,
            logger: services.GetRequiredService<ILogger<ServerGitHubAdministrationService>>()));
        builder.Services.AddSingleton<IServerGitHubAdministrationService>(services => services.GetRequiredService<ServerGitHubAdministrationService>());
        builder.Services.AddHostedService<AutomaticIssueDiscoveryService>();
        builder.Services.AddSingleton<IRegistryStore>(services => new SqliteRegistryStore(databasePath, configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds,
            logger: services.GetRequiredService<ILogger<SqliteRegistryStore>>()));
        builder.Services.AddSingleton<ICredentialStore>(_ => new SqliteCredentialStore(databasePath, Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY")));
        builder.Services.AddSingleton<IServerHealthService, ServerHealthService>();
        builder.Services.AddSingleton(_ => new ProvisioningCommandStore(databasePath));
        builder.Services.AddSingleton(services => new NodeProvisioningCommandExecutor(
            services.GetRequiredService<NodeCapabilityDiscovery>(), requireManagedGitHubAuthentication: true));
        builder.Services.AddHostedService<LocalProvisioningCommandService>();
        builder.Services.AddHostedService<ExecutionLeaseExpirationService>();
        builder.Services.AddSingleton(new ServerStatus("ready", DisplayVersion, DateTimeOffset.UtcNow));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Codex-Request-Id"] = context.TraceIdentifier;
            // All control-plane API responses can include private state or transient login
            // challenges. Set this before binding/authentication so errors are covered too.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers.Pragma = "no-cache";
                    context.Response.Headers.Expires = "0";
                    context.Response.Headers.Remove("ETag");
                    context.Response.Headers.Remove("Last-Modified");
                    return Task.CompletedTask;
                });
                // The API has no conditional representation: never reuse a cached secret.
                context.Request.Headers.Remove("If-None-Match");
                context.Request.Headers.Remove("If-Modified-Since");
            }
            await next(context);
        });
        try
        {
            _ = app.Services.GetRequiredService<ServerDatabaseAccessLock>();
            var persistence = app.Services.GetRequiredService<IRegistryStore>();
            await persistence.InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<ProvisioningCommandStore>().InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<ICredentialStore>().InitializeAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        app.Logger.LogInformation("Codex Server {Version} initialized in {RuntimeMode} mode; listening on {ListenUrl}; persistent data directory: {DataDirectory}",
            DisplayVersion, app.Environment.EnvironmentName, configuration.ListenUrl, configuration.ResolveDataDirectory());
        if (configuration.AdministrationOrigin is null)
            app.Logger.LogWarning("{Guidance}", AdministrationSessions.MissingOriginGuidance);
        app.MapPost("/api/v1/administration/session", (HttpContext context, AdministrationSessions sessions) =>
        {
            if (sessions.LoginFailure(context) is { } failure)
            {
                context.Response.Headers[AdministrationSessions.ErrorHeader] = failure;
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (!sessions.BearerAuthorized(context))
            {
                context.Response.Headers[AdministrationSessions.ErrorHeader] = "administration-token-invalid";
                return Results.Unauthorized();
            }
            var session = sessions.Create(context);
            return session is null ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                : Results.Ok(new { session.CsrfToken, session.ExpiresAtUtc });
        });
        app.MapGet("/api/v1/administration/session", (HttpContext context, AdministrationSessions sessions) =>
        {
            if (sessions.ConfigurationFailure(context) is { } failure)
            {
                context.Response.Headers[AdministrationSessions.ErrorHeader] = failure;
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            var session = sessions.Validate(context);
            if (session is null) context.Response.Headers[AdministrationSessions.ErrorHeader] = "administration-session-invalid";
            return session is null ? Results.Unauthorized() : Results.Ok(new { session.CsrfToken, session.ExpiresAtUtc });
        });
        app.MapDelete("/api/v1/administration/session", (HttpContext context, AdministrationSessions sessions) =>
        {
            if (sessions.Validate(context, mutation: true) is null) return Results.Unauthorized();
            sessions.Logout(context);
            return Results.NoContent();
        });
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
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var nodes = new List<ProvisionableNode>();
            var local = await discovery.GetAsync(cancellationToken: context.RequestAborted);
            var serverHealth = await health.GetHealthAsync(context.RequestAborted);
            nodes.Add(new("server", "server", settings.EffectiveDisplayName, "connected",
                "not-applicable", local.Any(state => state.Operation.State == CapabilityOperationState.Running) ? "busy" : "ready", false,
                CapabilityCatalog.Definitions.Select(definition => CapabilityCatalog.Describe(definition,
                    local.Single(state => state.Id == definition.Id), true)).ToArray(), serverHealth.Status));
            var plans = await store.GetProvisioningPlansAsync(context.RequestAborted);
            foreach (var worker in await store.GetWorkersAsync(context.RequestAborted))
                nodes.Add(NodeProvisioning.Describe(worker, plans));
            var described = new List<ProvisionableNode>();
            foreach (var node in nodes)
                described.Add(NodeProvisioning.WithCommands(node, await commands.ListNodeAsync(node.Id, context.RequestAborted), plans));
            return Results.Ok(described);
        });
        app.MapGet("/api/v1/nodes/{nodeId}/commands", async (string nodeId, HttpContext context,
            ServerConfiguration settings, ProvisioningCommandStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (nodeId != "server" && !Guid.TryParseExact(nodeId, "N", out _)) return Results.BadRequest(new { error = "Invalid node identity." });
            return Results.Ok(await commands.ListNodeAsync(nodeId, context.RequestAborted));
        });
        app.MapGet("/api/v1/nodes/server/github-connection", async (HttpContext context, ServerConfiguration settings,
            ProvisioningCommandStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Ok(new { commands = await commands.ListServerGitHubAsync(context.RequestAborted),
                provisioningEnabled = settings.EnableLocalProvisioning,
                elevationAllowed = settings.AllowLocalProvisioningElevation });
        });
        app.MapPost("/api/v1/nodes/server/capabilities/refresh", async (HttpContext context, ServerConfiguration settings,
            NodeCapabilityDiscovery discovery) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Ok(await discovery.GetAsync(refresh: true, cancellationToken: context.RequestAborted));
        });
        app.MapPost("/api/v1/provisioning/commands", async (ProvisioningCommandRequest request, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ProvisioningCommandStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request))
                return Results.BadRequest(new { error = "Unsupported or invalid provisioning action." });
            if (request.NodeId != "server")
            {
                var worker = await registry.GetWorkerAsync(request.NodeId, context.RequestAborted);
                if (worker is null) return Results.NotFound();
                if (worker.Availability is not ("online" or "draining")) return Results.Conflict(new { error = "Worker is offline." });
            }
            try
            {
                var operation = await commands.CreateAsync(request, context.RequestAborted);
                return Results.Created($"/api/v1/provisioning/commands/{operation.Id}", operation);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapGet("/api/v1/provisioning/commands", async (HttpContext context, ServerConfiguration settings,
            ProvisioningCommandStore commands, int? limit, int? offset) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var pageLimit = limit ?? 100;
            var pageOffset = offset ?? 0;
            if (pageLimit is < 1 or > 100 || pageOffset is < 0 or > 10_000)
                return Results.BadRequest(new { error = "Provisioning history limit must be 1..100 and offset must be 0..10000." });
            return Results.Ok(await commands.ListAsync(context.RequestAborted, pageLimit, pageOffset));
        });
        app.MapGet("/api/v1/provisioning/commands/{id}", async (string id, HttpContext context, ServerConfiguration settings, ProvisioningCommandStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var operation = await commands.GetAsync(id, context.RequestAborted);
            return operation is null ? Results.NotFound() : Results.Ok(operation);
        });
        app.MapPost("/api/v1/provisioning/commands/{id}/cancel", async (string id, HttpContext context, ServerConfiguration settings, ProvisioningCommandStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try
            {
                var operation = await commands.CancelAsync(id, context.RequestAborted);
                return operation is null ? Results.NotFound() : Results.Ok(operation);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/provisioning/commands/{id}/reconcile", async (string id, bool nodeQuiescent, HttpContext context, ServerConfiguration settings, ProvisioningCommandStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
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
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            var worker = await registry.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null || worker.Availability == "stale") return Results.NoContent();
            var operation = await commands.ClaimAsync(workerId, context.RequestAborted);
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        app.MapPost("/api/v1/workers/{workerId}/provisioning/commands/{id}/report", async (string workerId, string id,
            ProvisioningCommandReport report, HttpContext context, ServerConfiguration settings, IRegistryStore registry, ProvisioningCommandStore commands) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
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
        // The public handoff is produced on the node; authorization uses the existing registry.
        app.MapPost("/api/v1/workers/onboarding/authorize", async (WorkerPairingRequest request, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (request is null || !request.IsValid() || request.Server != settings.AdministrationOrigin)
                return Results.BadRequest(new { error = "Pairing request must match this HTTPS Server and enroll or associate operation. Return to the node; do not edit the request." });
            var worker = await store.GetWorkerAsync(request.WorkerId, context.RequestAborted);
            if (request.Operation == "enroll" && worker is not null)
                return Results.Conflict(new { error = "This identity is already registered. Resume on the node without a new authorization; use existing recovery operations if credentials are unavailable." });
            if (worker?.ActiveAssignments > 0)
                return Results.Conflict(new { error = "Active assignments must finish or be reconciled before association. Drain the Worker first." });
            var token = await store.CreateWorkerAuthorizationAsync(request.WorkerId, request.Operation,
                TimeSpan.FromMinutes(15), context.RequestAborted);
            return Results.Ok(new { authorization = token, lifetimeSeconds = 900 });
        });
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
            var operation = context.Request.Headers["X-Codex-Worker-Operation"].ToString();
            if (operation.Length == 0) operation = "enroll";
            var accepted = await store.BootstrapWorkerAsync(authorization[7..], request, workerToken, context.RequestAborted, operation);
            if (!accepted)
            {
                return RegistrationError(app, context, StatusCodes.Status401Unauthorized, "invalid_bootstrap_token",
                    "Worker authorization was rejected: it may be invalid, expired, used, bound to another identity/operation, or association may have active assignments. Retain pending state and reconcile the same Server and operation first; drain active assignments before requesting a fresh authorization.");
            }
            return Results.Ok(new CodexProvisioning.WorkerEnrollmentAcknowledgement(CodexProvisioning.WorkerEnrollmentProtocol.AcknowledgementVersion, request.WorkerId));
        });
        app.MapPost("/api/v1/credentials", async (CreateCredentialRequest request, HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
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
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Ok(await store.ListAsync(context.RequestAborted));
        });
        app.MapGet("/api/v1/credentials/{credentialId}", async (string credentialId, HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var credential = await store.GetAsync(credentialId, context.RequestAborted);
            return credential is null ? Results.NotFound() : Results.Ok(credential);
        });
        app.MapPut("/api/v1/credentials/{credentialId}/assignment", async (string credentialId, CredentialAssignmentRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ICredentialStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (request is null || !Guid.TryParseExact(request.WorkerId, "N", out _)) return Results.BadRequest(new { error = "Worker identity is invalid." });
            if (await registry.GetWorkerAsync(request.WorkerId, context.RequestAborted) is null) return Results.NotFound();
            var credential = await store.AssignAsync(credentialId, request.WorkerId, context.RequestAborted);
            return credential is null ? Results.NotFound() : Results.Ok(credential);
        });
        app.MapPut("/api/v1/workers/{workerId}/credential-access", async (string workerId, CredentialSecretInput token,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ICredentialStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (token is null || await registry.GetWorkerAsync(workerId, context.RequestAborted) is null) return Results.NotFound();
            try { await store.SetWorkerDeliveryTokenAsync(workerId, token, context.RequestAborted); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.NoContent();
        });
        app.MapGet("/api/v1/workers/{workerId}/credential-access", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ICredentialStore credentials) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await registry.GetWorkerAsync(workerId, context.RequestAborted) is null) return Results.NotFound();
            return Results.Ok(await credentials.GetWorkerDeliveryAuthorizationStatusAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/credential-access/revoke", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ICredentialStore credentials) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await registry.GetWorkerAsync(workerId, context.RequestAborted) is null) return Results.NotFound();
            await credentials.RevokeWorkerDeliveryTokenAsync(workerId, context.RequestAborted);
            return Results.Ok(await credentials.GetWorkerDeliveryAuthorizationStatusAsync(workerId, context.RequestAborted));
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
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var credential = await store.RevokeAsync(credentialId, context.RequestAborted);
            return credential is null ? Results.NotFound() : Results.Ok(credential);
        });
        app.MapPut("/api/v1/credentials/{credentialId}/secret", async (string credentialId, CredentialSecretInput secret,
            HttpContext context, ServerConfiguration settings, ICredentialStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try
            {
                var credential = await store.ReplaceSecretAsync(credentialId, secret, context.RequestAborted);
                return credential is null ? Results.NotFound() : Results.Ok(credential);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException) { return Results.Json(new { error = "Credential encryption is not configured." }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        // Explicit UI routes only: protocol, API and health failures never become HTML.
        foreach (var path in new[] { "/", "/home", "/projects", "/projects/{resourceId}",
            "/workers", "/workers/{resourceId}", "/executions", "/executions/{resourceId}",
            "/settings", "/settings/{resourceId}" })
        {
            app.MapGet(path, () => Results.Content(ReadDashboard(), "text/html; charset=utf-8"));
        }
        // Temporary migration entry. Canonical/legacy routes remain unchanged until cutover.
        foreach (var path in new[] { "/dashboard-preview", "/dashboard-preview/home",
            "/dashboard-preview/projects", "/dashboard-preview/projects/{resourceId}",
            "/dashboard-preview/workers", "/dashboard-preview/workers/{resourceId}",
            "/dashboard-preview/executions", "/dashboard-preview/executions/{resourceId}",
            "/dashboard-preview/settings", "/dashboard-preview/settings/{resourceId}" })
            app.MapGet(path, () => Results.Content(preview.Shell, "text/html; charset=utf-8"));
        foreach (var (path, bytes) in preview.Assets)
            app.MapGet("/dashboard-assets/preview/" + path, () => Results.Bytes(bytes, EmbeddedDashboardAssets.ContentType(path)));
        app.MapGet("/workers/{resourceId}/poc", () => Results.Content(ReadDashboard(workerPoc: true), "text/html; charset=utf-8"));
        // Fixed embedded assets only; no filesystem/static-file or HTML fallback.
        app.MapGet("/dashboard-assets/worker-poc.js", () => Results.Content(ReadWorkerPocAsset("js"), "text/javascript; charset=utf-8"));
        app.MapGet("/dashboard-assets/worker-poc.css", () => Results.Content(ReadWorkerPocAsset("css"), "text/css; charset=utf-8"));
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
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) || !Valid(request))
                return RegistrationError(app, context, StatusCodes.Status400BadRequest, "invalid_worker_registration",
                    "Invalid worker registration contract. Check that workerId matches the URL, capacity is 1..8, contractVersion is 1 or 2, and metadata and capabilities satisfy the registration limits.");
            await store.RegisterWorkerAsync(request, context.RequestAborted);
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/heartbeat", async (string workerId, WorkerHeartbeatRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) || !Valid(request))
                return Results.BadRequest(new { error = "Invalid worker heartbeat contract." });
            try { await store.HeartbeatWorkerAsync(request, context.RequestAborted); }
            catch (InvalidOperationException) { return Results.NotFound(); }
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapPut("/api/v1/workers/{workerId}/scheduling-policy", async (string workerId, WorkerSchedulingPolicyRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store, ProvisioningCommandStore commands, TimeProvider clock) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (request is null || !WorkerSchedulingPolicy.IsValid(request.Policy))
                return Results.BadRequest(new { error = "Policy must be Enabled, Draining, or Disabled." });
            try
            {
                if (request.Policy == WorkerSchedulingPolicy.Enabled)
                {
                    var current = await store.GetWorkerAsync(workerId, context.RequestAborted);
                    if (current is null) return Results.NotFound();
                    var projects = await store.GetProjectsAsync(context.RequestAborted);
                    var diagnostics = WorkerDiagnosticsDerivation.Derive(current, projects,
                        await store.GetProvisioningPlansAsync(context.RequestAborted), ManagedConfigurationVersion(projects),
                        await commands.ListNodeAsync(workerId, context.RequestAborted), clock.GetUtcNow());
                    if (!diagnostics.CanActivate)
                        return Results.Conflict(new { error = "Worker activation is blocked by current preparation evidence.", reasons = diagnostics.ActivationBlockingReasons });
                }
                var worker = await store.SetWorkerSchedulingPolicyAsync(workerId, request.Policy, context.RequestAborted);
                return worker is null ? Results.NotFound() : Results.Ok(worker);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/authentication/revoke", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null) return Results.NotFound();
            await store.RevokeWorkerTokenAsync(workerId, context.RequestAborted);
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/assignments/request", async (string workerId, WorkerAssignmentRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            if (request is null || !string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) ||
                request.AvailableCapacity is < 0 or > 8 || request.ProjectCapacities is null ||
                request.ProjectCapacities.Count > 128 || request.ProjectCapacities.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 80 || p.Value is < 0 or > 8))
                return Results.BadRequest(new { error = "Invalid Worker assignment request contract." });
            try { return Results.Ok(await github.RequestAssignmentAsync(request, context.RequestAborted)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPost("/api/v1/workers/{workerId}/provisioning/request", async (string workerId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            if (!Guid.TryParseExact(workerId, "N", out _)) return Results.BadRequest(new { error = "Worker identity is invalid." });
            var plan = await store.AcceptProvisioningPlanAsync(workerId, context.RequestAborted);
            return plan is null ? Results.NoContent() : Results.Ok(plan);
        });
        app.MapPost("/api/v1/workers/{workerId}/provisioning/{planId}/report", async (string workerId, string planId, ProvisioningWorkerReport report,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
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
        app.MapGet("/api/v1/provisioning", async (HttpContext context, ServerConfiguration settings, IRegistryStore store,
            int? limit, int? offset) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var pageLimit = limit ?? 100;
            var pageOffset = offset ?? 0;
            if (pageLimit is < 1 or > 100 || pageOffset is < 0 or > 10_000)
                return Results.BadRequest(new { error = "Provisioning history limit must be 1..100 and offset must be 0..10000." });
            return Results.Ok(await store.GetProvisioningPlansAsync(context.RequestAborted, pageLimit, pageOffset));
        });
        app.MapGet("/api/v1/provisioning/{planId}", async (string planId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var plan = await store.GetProvisioningPlanAsync(planId, context.RequestAborted);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });
        app.MapPost("/api/v1/provisioning/{planId}/state", async (string planId, ProvisioningStateTransition transition,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
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
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
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
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            if (report is null || !string.Equals(workerId, report.WorkerId, StringComparison.Ordinal))
            {
                ServerOperationalDiagnostics.Write(app.Logger, LogLevel.Information, "result-report", "identity-rejected",
                    executionId: executionRequestId, workerId: workerId, assignmentId: report?.AssignmentId,
                    leaseGeneration: report?.Generation, workerExecutionId: report?.WorkerExecutionId);
                return Results.BadRequest(new { error = "Worker execution report identity is invalid." });
            }
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
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
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
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Ok(await store.GetWorkersAsync(context.RequestAborted));
        });
        app.MapGet("/api/v1/workers/{workerId}", async (string workerId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
            return worker is null ? Results.NotFound() : Results.Ok(worker);
        });
        app.MapGet("/api/v1/workers/{workerId}/diagnostics", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store, ProvisioningCommandStore commands, TimeProvider clock) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null) return Results.NotFound();
            var projects = await store.GetProjectsAsync(context.RequestAborted);
            var plans = await store.GetProvisioningPlansAsync(context.RequestAborted);
            var typedCommands = await commands.ListNodeAsync(workerId, context.RequestAborted);
            return Results.Ok(WorkerDiagnosticsDerivation.Derive(worker, projects, plans,
                ManagedConfigurationVersion(projects), typedCommands, clock.GetUtcNow()));
        });
        app.MapGet("/api/v1/workers/{workerId}/configuration", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            if (await store.GetWorkerAsync(workerId, context.RequestAborted) is null) return Results.NotFound();
            var projects = await store.GetProjectsAsync(context.RequestAborted);
            var version = ManagedConfigurationVersion(projects);
            return Results.Ok(new ServerManagedConfigurationResponse(1, version, projects));
        });
        app.MapGet("/api/v1/events/stream", async (HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            await StreamWorkerUpdatesAsync(context, store, app.Lifetime.ApplicationStopping);
            return Results.Empty;
        });
        app.MapGet("/api/v1/github/repositories", async (int? page, HttpContext context,
            ServerConfiguration settings, IServerGitHubReadService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if ((page ?? 1) is < 1 or > 1000) return Results.BadRequest(new { error = "page must be between 1 and 1000." });
            try { return Results.Ok(await github.ListRepositoriesAsync(page ?? 1, context.RequestAborted)); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: 503); }
        });
        app.MapPost("/api/v1/projects/verify", async (CentralProjectDefinition definition, HttpContext context,
            ServerConfiguration settings, IServerGitHubReadService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (CentralProjectValidation.Error(definition) is { } error) return Results.BadRequest(new { error });
            try { return Results.Ok(await github.VerifyRepositoryAsync(definition, context.RequestAborted)); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: 503); }
        });
        app.MapGet("/api/v1/projects", async (HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Ok(await store.GetProjectsAsync(context.RequestAborted));
        });
        app.MapGet("/api/v1/projects/{projectId}", async (string projectId, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var project = await store.GetProjectAsync(projectId, context.RequestAborted);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });
        app.MapGet("/api/v1/projects/{projectId}/github/access", async (string projectId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            try { return Results.Ok(await github.CheckAccessAsync(projectId, context.RequestAborted)); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapGet("/api/v1/projects/{projectId}/github/issues", async (string projectId, string? state, int? limit,
            string? label, HttpContext context, ServerConfiguration settings, IRegistryStore store,
            IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            var query = new GitHubIssueQuery(state ?? "open", limit ?? 50, label);
            if (GitHubIssueQueryValidation.Error(query) is { } queryError) return Results.BadRequest(new { error = queryError });
            try { return Results.Ok(await github.ListIssuesAsync(projectId, query, context.RequestAborted)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapGet("/api/v1/projects/{projectId}/github/discovery", async (string projectId, int? limit, string? after,
            HttpContext context, ServerConfiguration settings, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var query = new GitHubIssueDiscoveryQuery(limit ?? 50, after);
            if (GitHubIssueDiscoveryValidation.Error(query) is { } error) return Results.BadRequest(new { error });
            try
            {
                var result = await github.DiscoverIssuesAsync(projectId, query, context.RequestAborted);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapGet("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}", async (string projectId, int issueNumber,
            HttpContext context, ServerConfiguration settings, IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (issueNumber <= 0) return Results.BadRequest(new { error = "Issue number must be positive." });
            try
            {
                var issue = await github.GetIssueAsync(projectId, issueNumber, context.RequestAborted);
                return issue is null ? Results.NotFound() : Results.Ok(issue);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapGet("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/relationships", async (string projectId,
            int issueNumber, HttpContext context, ServerConfiguration settings, IRegistryStore store,
            IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (issueNumber <= 0) return Results.BadRequest(new { error = "Issue number must be positive." });
            try
            {
                var relationships = await github.GetIssueRelationshipsAsync(projectId, issueNumber, context.RequestAborted);
                return relationships is null ? Results.NotFound() : Results.Ok(relationships);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPost("/api/v1/projects/{projectId}/github/issues", async (string projectId, GitHubIssueCreateRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (GitHubIssueMutationValidation.CreateError(request) is { } error) return Results.BadRequest(new { error });
            try
            {
                var result = await github.CreateIssueAsync(projectId, request, context.RequestAborted);
                return request.PreviewOnly ? Results.Ok(result) : Results.Created(result.Url, result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubIssueWriteUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPatch("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}", async (string projectId, int issueNumber,
            GitHubIssueUpdateRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store,
            IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (issueNumber <= 0) return Results.BadRequest(new { error = "Issue number must be positive." });
            if (GitHubIssueMutationValidation.UpdateError(request) is { } error) return Results.BadRequest(new { error });
            try { return Results.Ok(await github.UpdateIssueAsync(projectId, issueNumber, request, context.RequestAborted)); }
            catch (GitHubIssueNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (GitHubIssueWriteUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPut("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/labels/configured", async (string projectId,
            int issueNumber, GitHubIssueLabelRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store,
            IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (issueNumber <= 0) return Results.BadRequest(new { error = "Issue number must be positive." });
            try { return Results.Ok(await github.SetIssueLabelAsync(projectId, issueNumber, request, context.RequestAborted)); }
            catch (GitHubIssueNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (GitHubIssueWriteUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPut("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/dependencies/blocked-by", async (string projectId,
            int issueNumber, GitHubIssueDependencyRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store,
            IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            try { return Results.Ok(await github.SetIssueBlockedByAsync(projectId, issueNumber, request, context.RequestAborted)); }
            catch (GitHubIssueNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (GitHubIssueWriteUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPut("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/parent", async (string projectId,
            int issueNumber, GitHubIssueParentRequest request, HttpContext context, ServerConfiguration settings,
            IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (request is null) return Results.BadRequest(new { error = "Issue parent request is required." });
            if (GitHubIssueMutationValidation.ParentError(issueNumber, request.ParentIssueNumber) is { } error)
                return Results.BadRequest(new { error });
            try { return Results.Ok(await github.SetIssueParentAsync(projectId, issueNumber, request, context.RequestAborted)); }
            catch (GitHubIssueNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (GitHubIssueWriteUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPut("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/sub-issues", async (string projectId,
            int issueNumber, GitHubIssueSubIssueBatchRequest request, HttpContext context, ServerConfiguration settings,
            IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (request is null) return Results.BadRequest(new { error = "Sub-issue batch request is required." });
            if (GitHubIssueMutationValidation.BatchIssueNumbersError(issueNumber, request.ChildIssueNumbers, "sub-issue") is { } error)
                return Results.BadRequest(new { error });
            try { return Results.Ok(await github.SetIssueParentForChildrenAsync(projectId, issueNumber, request, context.RequestAborted)); }
            catch (GitHubIssueNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (GitHubIssueWriteUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPut("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/dependencies/blocked-by/batch", async (string projectId,
            int issueNumber, GitHubIssueDependencyBatchRequest request, HttpContext context, ServerConfiguration settings,
            IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (await store.GetProjectAsync(projectId, context.RequestAborted) is null) return Results.NotFound();
            if (request is null) return Results.BadRequest(new { error = "Issue dependency batch request is required." });
            if (GitHubIssueMutationValidation.BatchIssueNumbersError(issueNumber, request.BlockerIssueNumbers, "dependency") is { } error)
                return Results.BadRequest(new { error });
            try { return Results.Ok(await github.SetIssueBlockedByBatchAsync(projectId, issueNumber, request, context.RequestAborted)); }
            catch (GitHubIssueNotFoundException) { return Results.NotFound(); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (GitHubIssueWriteUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPost("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/enqueue", async (string projectId,
            int issueNumber, HttpContext context, ServerConfiguration settings, IRegistryStore store,
            IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (issueNumber <= 0) return Results.BadRequest(new { error = "Issue number must be positive." });
            try
            {
                var created = await github.EnqueueIssueAsync(projectId, new WorkReference("github-issue", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), context.RequestAborted);
                return Results.Created($"/api/v1/executions/{created.Id}", created);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ProjectDisabledException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ProjectRevisionConflictException ex) { return Results.Conflict(new { error = ex.Message, currentRevision = ex.CurrentRevision }); }
            catch (ExecutionRequestConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ManagedIssueIneligibleException ex) { return Results.Conflict(new { error = ex.Message, reasons = ex.Issue.EligibilityReasons }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPost("/api/v1/projects/{projectId}/github/issues/{issueNumber:int}/eligibility/refresh", async (string projectId,
            int issueNumber, HttpContext context, ServerConfiguration settings, IRegistryStore store,
            IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (issueNumber <= 0) return Results.BadRequest(new { error = "Issue number must be positive." });
            try
            {
                var refreshed = await github.RefreshQueuedEligibilityAsync(projectId, issueNumber, context.RequestAborted);
                return refreshed is null ? Results.NotFound() : Results.Ok(refreshed);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPost("/api/v1/projects", async (CentralProjectDefinition definition, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var error = CentralProjectValidation.Error(definition);
            if (error is not null) return Results.BadRequest(new { error });
            try { return Results.Created($"/api/v1/projects/{CentralProjectValidation.IdFor(definition.Name)}", await store.CreateProjectAsync(definition, context.RequestAborted)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPut("/api/v1/projects/{projectId}", async (string projectId, ProjectUpdateRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
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
        app.MapPut("/api/v1/projects/{projectId}/lifecycle", async (string projectId, ProjectLifecycleUpdateRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (request.ExpectedRevision < 1) return Results.BadRequest(new { error = "expectedRevision must be positive." });
            try
            {
                var updated = await store.UpdateProjectLifecycleAsync(projectId, request.Enabled, request.ExpectedRevision, context.RequestAborted);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (ProjectRevisionConflictException ex) { return Results.Conflict(new { error = ex.Message, currentRevision = ex.CurrentRevision }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapDelete("/api/v1/projects/{projectId}", async (string projectId, long expectedRevision, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (expectedRevision < 1) return Results.BadRequest(new { error = "expectedRevision must be positive." });
            try { return await store.RemoveProjectAsync(projectId, expectedRevision, context.RequestAborted) ? Results.NoContent() : Results.NotFound(); }
            catch (ProjectRevisionConflictException ex) { return Results.Conflict(new { error = ex.Message, currentRevision = ex.CurrentRevision }); }
            catch (ProjectInUseException ex) { return Results.Conflict(new { error = ex.Message, queued = ex.Queued, assigned = ex.Assigned, running = ex.Running }); }
        });
        app.MapGet("/api/v1/executions", async (string? projectId, string? state, string? workType, string? workId,
            int? limit, int? offset, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var query = new ExecutionQuery(projectId, state, workType, workId, limit ?? 50, offset ?? 0);
            var error = ExecutionAdministrationValidation.QueryError(query);
            if (error is not null) return Results.BadRequest(new { error });
            return Results.Ok(await store.ListExecutionsAsync(query, context.RequestAborted));
        });
        app.MapGet("/api/v1/executions/{executionRequestId}", async (string executionRequestId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var execution = await store.GetExecutionAsync(executionRequestId, context.RequestAborted);
            return execution is null ? Results.NotFound() : Results.Ok(execution);
        });
        app.MapPost("/api/v1/executions", async (EnqueueExecutionRequest request, HttpContext context, ServerConfiguration settings,
            IRegistryStore store, IServerGitHubAdministrationService github) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var error = ExecutionRequestValidation.Error(request);
            if (error is not null)
            {
                ServerOperationalDiagnostics.Write(app.Logger, LogLevel.Information, "enqueue", "invalid-request", request?.ProjectId, request?.WorkReference);
                return Results.BadRequest(new { error });
            }
            try
            {
                var created = await github.EnqueueIssueAsync(request.ProjectId, request.WorkReference, context.RequestAborted);
                return Results.Created($"/api/v1/executions/{created.Id}", created);
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ProjectDisabledException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ProjectRevisionConflictException ex) { return Results.Conflict(new { error = ex.Message, currentRevision = ex.CurrentRevision }); }
            catch (ExecutionRequestConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (ManagedIssueIneligibleException ex) { return Results.Conflict(new { error = ex.Message, reasons = ex.Issue.EligibilityReasons }); }
            catch (GitHubReadUnavailableException ex) { return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPost("/api/v1/executions/{executionRequestId}/state", async (string executionRequestId, HttpContext context, ServerConfiguration settings) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Json(new { error = "Execution states are owned by Worker assignment and report contracts. Use queued cancellation or uncertain execution reconciliation." },
                statusCode: StatusCodes.Status410Gone);
        });
        app.MapPost("/api/v1/executions/{executionRequestId}/cancel", async (string executionRequestId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try
            {
                var execution = await store.CancelQueuedExecutionAsync(executionRequestId, context.RequestAborted);
                return execution is null ? Results.NotFound() : Results.Ok(execution);
            }
            catch (ExecutionRequestCancellationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/executions/{executionRequestId}/reconcile", async (string executionRequestId,
            ExecutionReconciliationRequest request, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try
            {
                var result = await store.ReconcileUncertainExecutionAsync(executionRequestId, request, context.RequestAborted);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ExecutionRequestReconciliationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        return app;
    }

    private static string ReadWorkerPocAsset(string extension)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CodexServer.worker-poc.{extension}")
            ?? throw new InvalidOperationException("A Server Worker PoC asset resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadDashboard(bool workerPoc = false)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexServer.dashboard.html")
            ?? throw new InvalidOperationException("The Server dashboard resource is missing.");
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        html = html.Replace("<!-- worker-poc -->", "", StringComparison.Ordinal);
        if (workerPoc)
        {
            return "<!DOCTYPE html><html lang=\"en\" class=\"dark-mode\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Codex Server · Worker</title>"
                + "<link rel=\"stylesheet\" href=\"/dashboard-assets/worker-poc.css\"></head>"
                + "<body><div id=\"worker-poc\"></div><script src=\"/dashboard-assets/worker-poc.js\"></script></body></html>";
        }
        var scripts = new StringBuilder();
        foreach (var name in new[] { "dashboard-navigation.js", "dashboard-admin.js" })
        {
            using var scriptStream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CodexServer.{name}")
                ?? throw new InvalidOperationException("A Server dashboard script resource is missing.");
            using var scriptReader = new StreamReader(scriptStream);
            var source = scriptReader.ReadToEnd();
            if (name == "dashboard-admin.js")
            {
                foreach (var module in new[] { "session", "nodes", "stream", "executions", "issues", "onboarding", "projects" })
                {
                    using var moduleStream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CodexServer.dashboard-{module}.js")
                        ?? throw new InvalidOperationException("A Server dashboard module resource is missing.");
                    using var moduleReader = new StreamReader(moduleStream);
                    source = source.Replace($"/* dashboard-{module} */", moduleReader.ReadToEnd(), StringComparison.Ordinal);
                }
            }
            scripts.AppendLine(source);
        }
        return html.Replace("<!-- dashboard-scripts -->", $"<script>{scripts}</script>", StringComparison.Ordinal);
    }

    internal static async Task StreamWorkerUpdatesAsync(HttpContext context, IRegistryStore store,
        CancellationToken applicationStopping)
    {
        // Kestrel waits for active requests during graceful shutdown; RequestAborted
        // alone does not end a connected stream when the host begins stopping.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, applicationStopping);
        var sessions = context.RequestServices.GetService<AdministrationSessions>();
        var session = context.Request.Headers.ContainsKey("Authorization") ? null : sessions?.Validate(context);
        if (sessions is not null && !context.Request.Headers.ContainsKey("Authorization") && session is null) return;
        using var sessionLifetime = session is null ? null : CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, session.CancellationToken);
        var cancellationToken = sessionLifetime?.Token ?? lifetime.Token;
        var jsonOptions = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                if (session is not null && sessions?.Validate(context) is null) break;
                var workers = await store.GetWorkersAsync(cancellationToken);
                await context.Response.WriteAsync("event: workers\ndata: ", cancellationToken);
                await context.Response.WriteAsync(JsonSerializer.Serialize(workers, jsonOptions), cancellationToken);
                await context.Response.WriteAsync("\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static IResult WorkerUnauthorized() => Results.Json(new
    {
        error = "Worker API credential is missing, invalid, revoked, or belongs to another Worker. Verify the enrolled identity and durable credential with the Server operator."
    }, statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<bool> AuthorizedWorkerAsync(HttpContext context, IRegistryStore store, string workerId)
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return supplied.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            await store.IsWorkerTokenValidAsync(workerId, supplied[prefix.Length..], context.RequestAborted);
    }

    private static bool AuthorizedManagement(HttpContext context, ServerConfiguration configuration)
    {
        return context.RequestServices.GetRequiredService<AdministrationSessions>().Authorized(context);
    }

    private static IResult RegistrationError(WebApplication app, HttpContext context, int statusCode, string code, string error)
    {
        app.Logger.LogWarning("Worker registration rejected with code {ErrorCode}; trace {TraceId}", code, context.TraceIdentifier);
        return Results.Json(new { error, code, requestId = context.TraceIdentifier }, statusCode: statusCode);
    }

    private static bool Valid(WorkerRegistrationRequest request) => request.IsValid();

    private static bool Valid(WorkerHeartbeatRequest request) => request.ContractVersion is 1 or 2 &&
        Guid.TryParseExact(request.WorkerId, "N", out _) && !string.IsNullOrWhiteSpace(request.WorkerVersion) &&
        request.WorkerVersion.Length <= 100 && WorkerLifecycleStates.IsValid(request.LifecycleState) &&
        request.ActiveExecutions is >= 0 and <= 8 && request.MaximumCapacity is >= 1 and <= 8 &&
        request.ActiveExecutions <= request.MaximumCapacity && request.Capabilities is not null && request.Capabilities.Count <= 32 &&
        request.Capabilities.All(ValidCapability) &&
        ManagedWorkerDiagnostics.Valid(request.ManagedDiagnostics) &&
        CapabilityCatalog.ValidInventory(request.CapabilityInventory) && request.ActiveProjects is not null && request.ActiveProjects.Count <= 32 &&
        (request.ConfigurationSynchronization is null or "synchronized" or "cached" or "unavailable" or "error" or "not-synchronized") &&
        (request.ConfigurationVersion is null || (request.ConfigurationVersion.Length <= 128 && !request.ConfigurationVersion.Any(char.IsControl))) &&
        request.ActiveProjects.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 200);

    private static bool ValidCapability(WorkerCapability value) => WorkerRegistrationRequest.ValidCapability(value);

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

    public static string DisplayVersion => CodexProvisioning.ProductVersion.Display(Assembly.GetExecutingAssembly());
}
