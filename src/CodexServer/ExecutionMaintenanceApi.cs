namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class ServerApplication
{
    private static void MapExecutionMaintenance(WebApplication app)
    {
        app.MapPost("/api/v1/maintenance/executions", async (ExecutionMaintenanceRequest request, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ExecutionMaintenanceStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!ExecutionMaintenanceProtocol.Valid(request)) return Results.BadRequest(new { reason = "invalid-maintenance-scope" });
            // Idempotent requests return the retained audit even after the Worker disconnects.
            var prior = await commands.GetAsync(request.OperationId, context.RequestAborted);
            if (prior is null)
            {
                var reason = await MaintenanceAuthorityReasonAsync(request, registry, context.RequestAborted);
                if (reason is not null) return Results.Conflict(new { reason });
            }
            try
            {
                var operation = await commands.CreateAsync(request, $"server-management:{context.TraceIdentifier}", context.RequestAborted);
                return Results.Created($"/api/v1/maintenance/executions/{request.OperationId}", operation);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { reason = ex.Message }); }
        });
        app.MapGet("/api/v1/maintenance/executions", async (string? workerId, int? limit, int? offset,
            HttpContext context, ServerConfiguration settings, ExecutionMaintenanceStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try { return Results.Ok(await commands.ListAsync(workerId, limit ?? 50, offset ?? 0, context.RequestAborted)); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { reason = ex.Message }); }
        });
        app.MapPost("/api/v1/maintenance/executions/{id}/cancel", async (string id, HttpContext context,
            ServerConfiguration settings, ExecutionMaintenanceStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try
            {
                var operation = await commands.CancelAsync(id, context.RequestAborted);
                return operation is null ? Results.NotFound(new { reason = "maintenance-record-missing" }) : Results.Ok(operation);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { reason = ex.Message }); }
        });
        app.MapGet("/api/v1/maintenance/executions/{id}", async (string id, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ExecutionMaintenanceStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var operation = await commands.GetAsync(id, context.RequestAborted);
            if (operation is null) return Results.NotFound(new { reason = "maintenance-record-missing" });
            var worker = await registry.GetWorkerAsync(operation.Request.WorkerId, context.RequestAborted);
            var observations = new List<object>();
            foreach (var observation in operation.Report?.Observations ?? [])
            {
                var execution = observation.ServerExecutionId is null ? null :
                    await registry.GetExecutionAsync(observation.ServerExecutionId, context.RequestAborted);
                var status = execution is null ? "orphan-worker-record" :
                    execution.AssignedWorkerId != operation.Request.WorkerId || execution.AssignmentId != observation.AssignmentId ||
                    execution.Lease?.Generation != observation.Generation ? "stale-ownership" :
                    execution.Lease is { State: "Expired" } ? "stale-lease" :
                    observation.ReportingStatus != "acknowledged" && observation.ReportingStatus != "none" ||
                    observation.State is "Completed" or "Blocked" or "Failed" or "IntegrationConflict" or "InfrastructureFailure" or "Cancelled" &&
                    execution.State is not ("Completed" or "Failed") ? "reporting-pending" :
                    execution.Recoverable || observation.RecoveryState is "recoverable" or "integration-conflict" or "uncertain" or "codex-interrupted"
                        ? "potentially-recoverable" : "confirmed";
                observations.Add(new { observation, execution, status });
            }
            var canonical = operation.Request.ServerExecutionId is null ? null :
                await registry.GetExecutionAsync(operation.Request.ServerExecutionId, context.RequestAborted);
            return Results.Ok(new { operation, execution = canonical,
                workerStatus = worker?.Availability ?? "missing", observations,
                status = worker?.Availability is not ("online" or "draining") ? "worker-offline" : operation.Status });
        });
        app.MapPost("/api/v1/workers/{workerId}/maintenance/executions/request", async (string workerId,
            HttpContext context, IRegistryStore registry, ExecutionMaintenanceStore commands) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            var operation = await commands.ClaimAsync(workerId, context.RequestAborted);
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        // Fresh confirmation is required inside the Worker's repository/drain gate before a mutation.
        app.MapPost("/api/v1/workers/{workerId}/maintenance/executions/{id}/authorize", async (string workerId, string id,
            HttpContext context, IRegistryStore registry, ExecutionMaintenanceStore commands) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            var operation = await commands.GetAsync(id, context.RequestAborted);
            if (operation is null) return Results.NotFound(new { reason = "maintenance-record-missing" });
            if (operation.Request.WorkerId != workerId || operation.Status != "running")
                return Results.Conflict(new { reason = "maintenance-not-current" });
            var reason = await MaintenanceAuthorityReasonAsync(operation.Request, registry, context.RequestAborted);
            return reason is null ? Results.Ok(operation) : Results.Conflict(new { reason });
        });
        app.MapPost("/api/v1/workers/{workerId}/maintenance/executions/{id}/report", async (string workerId, string id,
            ExecutionMaintenanceReport report, HttpContext context, IRegistryStore registry, ExecutionMaintenanceStore commands) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            try
            {
                var result = await commands.ReportAsync(id, workerId, report, context.RequestAborted);
                return result is null ? Results.NotFound(new { reason = "maintenance-record-missing" }) : Results.Ok(result);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { reason = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { reason = ex.Message }); }
        });
    }

    internal static async Task<string?> MaintenanceAuthorityReasonAsync(ExecutionMaintenanceRequest request,
        IRegistryStore registry, CancellationToken ct)
    {
        var worker = await registry.GetWorkerAsync(request.WorkerId, ct);
        if (worker is null) return "worker-missing";
        if (worker.Availability is not ("online" or "draining")) return "worker-offline";
        if (!worker.Capabilities.Any(c => c.Type == "protocol" && c.Name == ExecutionMaintenanceProtocol.Capability))
            return "worker-maintenance-protocol-unavailable";
        if (request.Apply && (worker.SchedulingPolicy != WorkerSchedulingPolicy.Draining ||
            request.Action != "retry-report" && worker.ActiveAssignments != 0))
            return "worker-drain-required";
        if (request.Action == "inventory") return null;
        var execution = await registry.GetExecutionAsync(request.ServerExecutionId ?? "", ct);
        if (execution is null) return "server-record-missing";
        if (execution.AssignedWorkerId != request.WorkerId || execution.AssignmentId != request.AssignmentId ||
            execution.Lease?.WorkerId != request.WorkerId || execution.Lease.Generation != request.Generation ||
            execution.WorkerExecutionId is not null && !string.Equals(execution.WorkerExecutionId, request.WorkerExecutionId.ToString(), StringComparison.OrdinalIgnoreCase))
            return "stale-ownership";
        if (request.Action == "inspect") return null;
        if (request.Action == "retry-report")
            return execution.State is "Completed" or "Failed" && execution.WorkerExecutionId is not null && execution.Lease.State == "Released" ||
                execution.State is "Assigned" or "Running" && execution.Lease is { State: "Active" } lease && lease.ExpiresAtUtc > DateTimeOffset.UtcNow
                ? null : "stale-lease-report-reconciliation-required";
        if (execution.WorkerExecutionId is null) return "worker-execution-proof-missing";
        return execution.State is "Completed" or "Failed" && execution.Lease.State == "Released"
            ? null : "terminal-server-proof-required";
    }
}
