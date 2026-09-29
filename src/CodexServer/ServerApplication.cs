namespace CodexServer;

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
public sealed record ServerHealth(string Status, bool PersistenceAvailable);
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
        return new ServerHealth(available ? "healthy" : "unhealthy", available);
    }
}

public static class ServerApplication
{
    public static async Task<WebApplication> BuildAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args);
        var configuration = new ServerConfiguration();
        builder.Configuration.GetSection("Server").Bind(configuration);
        configuration.Validate();
        builder.WebHost.UseUrls(configuration.ListenUrl);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IRegistryStore>(_ => new SqliteRegistryStore(configuration.ResolveDatabasePath(), configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds));
        builder.Services.AddSingleton<IServerHealthService, ServerHealthService>();
        builder.Services.AddHostedService<ExecutionLeaseExpirationService>();
        builder.Services.AddSingleton(new ServerStatus("ready", DisplayVersion, DateTimeOffset.UtcNow));

        var app = builder.Build();
        var persistence = app.Services.GetRequiredService<IRegistryStore>();
        await persistence.InitializeAsync(cancellationToken);
        app.MapGet("/api/status", (ServerStatus status) => Results.Ok(status));
        app.MapGet("/api/version", () => Results.Ok(new ServerVersion(DisplayVersion, "Codex Server")));
        app.MapGet("/", () => Results.Content(ReadDashboard(), "text/html; charset=utf-8"));
        app.MapGet("/health", async (IServerHealthService healthService, HttpContext context) =>
        {
            try
            {
                var health = await healthService.GetHealthAsync(context.RequestAborted);
                return health.PersistenceAvailable ? Results.Ok(health) : Results.Json(health, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new ServerHealth("unhealthy", false), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapPut("/api/v1/workers/{workerId}", async (string workerId, WorkerRegistrationRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings)) return Results.Unauthorized();
            if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) || !Valid(request))
                return Results.BadRequest(new { error = "Invalid worker registration contract." });
            await store.RegisterWorkerAsync(request, context.RequestAborted);
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/heartbeat", async (string workerId, WorkerHeartbeatRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings)) return Results.Unauthorized();
            if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) || !Valid(request))
                return Results.BadRequest(new { error = "Invalid worker heartbeat contract." });
            try { await store.HeartbeatWorkerAsync(request, context.RequestAborted); }
            catch (InvalidOperationException) { return Results.NotFound(); }
            return Results.Ok(await store.GetWorkerAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/assignments/request", async (string workerId, WorkerAssignmentRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings)) return Results.Unauthorized();
            if (request is null || !string.Equals(workerId, request.WorkerId, StringComparison.Ordinal) ||
                request.AvailableCapacity is < 0 or > 8 || request.ProjectCapacities is null ||
                request.ProjectCapacities.Count > 128 || request.ProjectCapacities.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 80 || p.Value is < 0 or > 8))
                return Results.BadRequest(new { error = "Invalid Worker assignment request contract." });
            try { return Results.Ok(await store.RequestAssignmentAsync(request, context.RequestAborted)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/executions/{executionRequestId}/report", async (string workerId, string executionRequestId,
            WorkerExecutionReport report, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings)) return Results.Unauthorized();
            if (report is null || !string.Equals(workerId, report.WorkerId, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Worker execution report identity is invalid." });
            try
            {
                var updated = await store.ReportExecutionAsync(executionRequestId, report, context.RequestAborted);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/executions/{executionId}/lease/renew", async (string workerId, string executionId,
            ExecutionLeaseRenewal renewal, HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!Authorized(context, settings)) return Results.Unauthorized();
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

    private static bool Valid(WorkerRegistrationRequest request) => request.ContractVersion == 1 &&
        Guid.TryParseExact(request.WorkerId, "N", out _) && !string.IsNullOrWhiteSpace(request.DisplayName) &&
        request.DisplayName.Length <= 200 && !string.IsNullOrWhiteSpace(request.WorkerVersion) && request.WorkerVersion.Length <= 100 &&
        !string.IsNullOrWhiteSpace(request.Platform) && request.Platform.Length <= 300 && request.Capacity is >= 1 and <= 8 &&
        request.Capabilities is not null && request.Capabilities.Count <= 32 &&
        request.Capabilities.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 100);

    private static bool Valid(WorkerHeartbeatRequest request) => request.ContractVersion == 1 &&
        Guid.TryParseExact(request.WorkerId, "N", out _) && !string.IsNullOrWhiteSpace(request.WorkerVersion) &&
        request.WorkerVersion.Length <= 100 && (request.LifecycleState is "starting" or "running" or "draining" or "stopped") &&
        request.ActiveExecutions is >= 0 and <= 8 && request.MaximumCapacity is >= 1 and <= 8 &&
        request.ActiveExecutions <= request.MaximumCapacity && request.Capabilities is not null && request.Capabilities.Count <= 32 &&
        request.Capabilities.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 100) &&
        request.ActiveProjects is not null && request.ActiveProjects.Count <= 32 &&
        request.ActiveProjects.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 200);

    public static string DisplayVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}
