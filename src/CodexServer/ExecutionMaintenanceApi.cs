namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class ServerApplication
{
    private static void MapExecutionMaintenance(WebApplication app)
    {
        MapBranchMaintenanceTargets(app);
        app.MapPut("/api/v1/observers/{observerId}/branch-maintenance", async (string observerId,
            ObserverBranchMaintenanceBinding binding, HttpContext context, ServerConfiguration settings,
            IRegistryStore registry, ExecutionMaintenanceStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!ObserverProtocol.ValidIdentity(observerId) || !binding.IsValid()) return Results.BadRequest();
            var observer = await registry.GetObserverInspectionAsync(observerId, context.RequestAborted);
            if (observer is null || observer.Registration.State == "revoked") return Results.NotFound();
            await commands.SetObserverBranchGrantAsync(observerId, binding, context.RequestAborted);
            return Results.NoContent();
        });
        app.MapDelete("/api/v1/observers/{observerId}/branch-maintenance", async (string observerId,
            HttpContext context, ServerConfiguration settings, ExecutionMaintenanceStore commands) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!ObserverProtocol.ValidIdentity(observerId)) return Results.BadRequest();
            await commands.SetObserverBranchGrantAsync(observerId, null, context.RequestAborted);
            return Results.NoContent();
        });
        app.MapPost("/api/v1/maintenance/executions", async (ExecutionMaintenanceRequest request, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, ExecutionMaintenanceStore commands, TimeProvider clock) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!ExecutionMaintenanceProtocol.Valid(request)) return Results.BadRequest(new { reason = "invalid-maintenance-scope" });
            // Idempotent requests return the retained audit even after the Worker disconnects.
            var prior = await commands.GetAsync(request.OperationId, context.RequestAborted);
            if (prior is null)
            {
                var reason = await MaintenanceAuthorityReasonAsync(request, registry, context.RequestAborted, clock, commands, settings.WorkerStaleAfterSeconds);
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
            ServerConfiguration settings, IRegistryStore registry, ExecutionMaintenanceStore commands, TimeProvider clock) =>
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
            var observer = ObserverProtocol.ValidIdentity(operation.Request.WorkerId)
                ? await registry.GetObserverInspectionAsync(operation.Request.WorkerId, context.RequestAborted) : null;
            var availability = worker?.Availability ?? (observer?.Registration.State == "reporting" && observer.SnapshotReceivedAtUtc is { } received &&
                clock.GetUtcNow() - received <= TimeSpan.FromSeconds(settings.WorkerStaleAfterSeconds) ? "online" : "offline");
            var canonical = operation.Request.ServerExecutionId is null ? null :
                await registry.GetExecutionAsync(operation.Request.ServerExecutionId, context.RequestAborted);
            return Results.Ok(new { operation, execution = canonical,
                workerStatus = availability, observations,
                status = availability is not ("online" or "draining") ? "worker-offline" : operation.Status });
        });
        app.MapPost("/api/v1/workers/{workerId}/maintenance/executions/request", async (string workerId,
            HttpContext context, IRegistryStore registry, ExecutionMaintenanceStore commands, ServerConfiguration settings) =>
        {
            if (!await AuthorizedMaintenanceWorkerAsync(context, registry, settings, workerId)) return WorkerUnauthorized();
            var operation = await commands.ClaimAsync(workerId, context.RequestAborted);
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        // Fresh confirmation is required inside the Worker's repository/drain gate before a mutation.
        app.MapPost("/api/v1/workers/{workerId}/maintenance/executions/{id}/authorize", async (string workerId, string id,
            HttpContext context, IRegistryStore registry, ExecutionMaintenanceStore commands, TimeProvider clock, ServerConfiguration settings) =>
        {
            if (!await AuthorizedMaintenanceWorkerAsync(context, registry, settings, workerId)) return WorkerUnauthorized();
            var operation = await commands.GetAsync(id, context.RequestAborted);
            if (operation is null) return Results.NotFound(new { reason = "maintenance-record-missing" });
            if (operation.Request.WorkerId != workerId || operation.Status != "running")
                return Results.Conflict(new { reason = "maintenance-not-current" });
            var reason = await MaintenanceAuthorityReasonAsync(operation.Request, registry, context.RequestAborted, clock, commands, settings.WorkerStaleAfterSeconds);
            return reason is null ? Results.Ok(operation) : Results.Conflict(new { reason });
        });
        app.MapPost("/api/v1/workers/{workerId}/maintenance/executions/{id}/report", async (string workerId, string id,
            ExecutionMaintenanceReport report, HttpContext context, IRegistryStore registry, ExecutionMaintenanceStore commands, ServerConfiguration settings) =>
        {
            if (!await AuthorizedMaintenanceWorkerAsync(context, registry, settings, workerId)) return WorkerUnauthorized();
            try
            {
                var result = await commands.ReportAsync(id, workerId, report, context.RequestAborted);
                if (result is not null) await registry.RestoreCompletionProvenanceAsync(result, context.RequestAborted);
                return result is null ? Results.NotFound(new { reason = "maintenance-record-missing" }) : Results.Ok(result);
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { reason = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { reason = ex.Message }); }
        });
    }

    private static async Task<bool> AuthorizedMaintenanceWorkerAsync(HttpContext context, IRegistryStore registry,
        ServerConfiguration settings, string workerId) => ObserverProtocol.ValidIdentity(workerId)
        ? settings.AdministrationOrigin is { } server &&
            await registry.AuthenticateObserverAsync(workerId, server, ObserverBearer(context), context.RequestAborted) is not null
        : await AuthorizedWorkerAsync(context, registry, workerId);

    internal static async Task<string?> MaintenanceAuthorityReasonAsync(ExecutionMaintenanceRequest request,
        IRegistryStore registry, CancellationToken ct, TimeProvider? clock = null,
        ExecutionMaintenanceStore? commands = null, int staleAfterSeconds = 90)
    {
        // Branch maintenance uses configured scope and explicit authority, independent of Issue scheduling.
        if (request.ProjectId is { } projectId)
        {
            var project = await registry.GetProjectAsync(projectId, ct);
            if (project is null || (request.Action != "archived-branches" && !project.Enabled) || project.Revision != request.ProjectRevision ||
                project.Name != request.Project || project.Repository != request.Repository)
                return "project-binding-changed";
        }
        if (ObserverProtocol.ValidIdentity(request.WorkerId))
        {
            if (request.Action != "archived-branches" || commands is null || !await commands.HasObserverBranchGrantAsync(request, ct))
                return "observer-maintenance-not-authorized";
            var observer = await registry.GetObserverInspectionAsync(request.WorkerId, ct);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            if (observer?.Registration.State != "reporting" || observer.SnapshotReceivedAtUtc is not { } received ||
                now - received > TimeSpan.FromSeconds(staleAfterSeconds) || observer.Snapshot is not { } snapshot ||
                now - snapshot.ObservedAtUtc > TimeSpan.FromSeconds(staleAfterSeconds) || snapshot.Lifecycle == "stopped")
                return "worker-offline";
            if (snapshot.BranchMaintenance?.Contains(new(request.Project ?? "", request.Repository ?? "")) != true)
                return "worker-branch-protocol-unavailable";
            if (request.Apply && (snapshot.Lifecycle != "drained" || snapshot.ActiveExecutions != 0))
                return "worker-drain-required";
            return null;
        }
        var worker = await registry.GetWorkerAsync(request.WorkerId, ct);
        if (worker is null) return "worker-missing";
        if (worker.Availability is not ("online" or "draining")) return "worker-offline";
        if (!worker.Capabilities.Any(c => c.Type == "protocol" && c.Name == ExecutionMaintenanceProtocol.Capability))
            return "worker-maintenance-protocol-unavailable";
        if (request.Apply && (worker.SchedulingPolicy != WorkerSchedulingPolicy.Draining ||
            request.Action != "retry-report" && worker.ActiveAssignments != 0))
            return "worker-drain-required";
        if (request.Action == "archived-branches")
        {
            if (!worker.Capabilities.Any(c => c.Type == "protocol" && c.Name == ExecutionMaintenanceProtocol.BranchCapability))
                return "worker-branch-protocol-unavailable";
            var projects = await registry.GetProjectsAsync(ct);
            return projects.Count(p => p.Name == request.Project && p.Repository == request.Repository) == 1 ? null : "project-unavailable";
        }
        if (request.Action == "inventory") return null;
        var execution = await registry.GetExecutionAsync(request.ServerExecutionId ?? "", ct);
        if (execution is null) return "server-record-missing";
        if (execution.AssignedWorkerId != request.WorkerId || execution.AssignmentId != request.AssignmentId ||
            execution.Lease?.WorkerId != request.WorkerId || execution.Lease.Generation != request.Generation ||
            execution.WorkerExecutionId is not null && !string.Equals(execution.WorkerExecutionId, request.WorkerExecutionId.ToString(), StringComparison.OrdinalIgnoreCase))
            return "stale-ownership";
        if (request.Action == "inspect") return null;
        if (request.Action == "reconcile-completion")
        {
            if (!worker.Capabilities.Any(c => c.Type == "protocol" && c.Name == ExecutionMaintenanceProtocol.CompletionCapability))
                return "worker-completion-protocol-unavailable";
            if (worker.SchedulingPolicy != WorkerSchedulingPolicy.Draining || worker.ActiveAssignments != 0)
                return "worker-drain-required";
            if (execution.State is not ("Failed" or "Completed") || execution.Lease.State is not ("Released" or "Expired") ||
                execution.WorkerExecutionId is null || !SqliteRegistryStore.CompletionMaintenanceEligible(execution) ||
                execution.State == "Completed" && execution.RecoveryState != "CompletionReconciled")
                return "quarantined-completion-terminal-proof-required";
            if (execution.WorkReference.Type is not ("issue" or "github-issue") ||
                !int.TryParse(execution.WorkReference.Id, out var issueNumber) || issueNumber <= 0)
                return "completion-issue-identity-missing";
            var others = await registry.ListExecutionsAsync(new(ProjectId: execution.ProjectId, WorkType: "github-issue",
                WorkId: execution.WorkReference.Id, Limit: 100), ct);
            if (others.Count == 100) return "completion-ownership-review-bound-exceeded";
            return others.Any(other => other.Id != execution.Id &&
                (other.CreatedAtUtc >= execution.CreatedAtUtc || other.State is "Queued" or "Assigned" or "Running" or "Completed"))
                ? "issue-execution-ownership-changed" : null;
        }

        if (request.Action == "retry-report")
            return execution.State is "Completed" or "Failed" && execution.WorkerExecutionId is not null && execution.Lease.State == "Released" ||
                execution.State is "Assigned" or "Running" && execution.Lease is { State: "Active" } lease && lease.ExpiresAtUtc > (clock ?? TimeProvider.System).GetUtcNow()
                ? null : "stale-lease-report-reconciliation-required";
        if (execution.WorkerExecutionId is null) return "worker-execution-proof-missing";
        return execution.State is "Completed" or "Failed" && execution.Lease.State == "Released"
            ? null : "terminal-server-proof-required";
    }
}
