namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public sealed record BranchMaintenanceTarget(string WorkerId, string DisplayName, string Mode,
    bool Eligible, string? Reason, bool CanApply, string? ApplyReason);
public sealed record BranchMaintenanceTargets(string ProjectId, long Revision, string Project, string Repository,
    IReadOnlyList<BranchMaintenanceTarget> Targets);

public static partial class ServerApplication
{
    private static void MapBranchMaintenanceTargets(WebApplication app)
    {
        app.MapGet("/api/v1/projects/{projectId}/branch-maintenance-targets", async (string projectId,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ExecutionMaintenanceStore commands, TimeProvider clock) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            context.Response.Headers.CacheControl = "no-store";
            var project = await registry.GetProjectAsync(projectId, context.RequestAborted);
            return project is null ? Results.NotFound() : Results.Ok(await ReadBranchMaintenanceTargetsAsync(
                project, registry, commands, clock, settings.WorkerStaleAfterSeconds, context.RequestAborted));
        });
    }

    // Project-scoped display projection of the same authority checks used at dispatch and inside the repository gate.
    internal static async Task<BranchMaintenanceTargets> ReadBranchMaintenanceTargetsAsync(CentralProject project,
        IRegistryStore registry, ExecutionMaintenanceStore commands, TimeProvider clock, int staleAfterSeconds, CancellationToken ct)
    {
        var targets = new List<BranchMaintenanceTarget>();
        var workers = await registry.GetWorkersAsync(ct);
        var observers = await registry.ListObserversAsync(100, 0, ct);
        foreach (var identity in workers.Take(100).Select(w => (Id: w.WorkerId, Name: w.DisplayName, Mode: "managed"))
            .Concat(observers.Select(o => (Id: o.ObserverId, Name: o.Name ?? o.ObserverId, Mode: ObserverProtocol.Mode))))
        {
            var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), identity.Id, null, null, null, null,
                Action: "archived-branches", Project: project.Name, Repository: project.Repository, OlderThanDays: 30, ProjectId: project.Id, ProjectRevision: project.Revision);
            var reason = await MaintenanceAuthorityReasonAsync(
                request, registry, ct, clock, commands, staleAfterSeconds);
            var applyReason = reason ?? await MaintenanceAuthorityReasonAsync(request with { Apply = true },
                registry, ct, clock, commands, staleAfterSeconds);
            targets.Add(new(identity.Id, identity.Name, identity.Mode, reason is null, reason, applyReason is null, applyReason));
        }
        return new(project.Id, project.Revision, project.Name, project.Repository, targets);
    }
}
