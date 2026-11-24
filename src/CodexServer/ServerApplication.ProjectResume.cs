namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class ServerApplication
{
    /// <summary>Whether the Worker's last heartbeat reports this project paused in its memory.</summary>
    internal static bool ProjectPausedOnWorker(WorkerRegistrationResponse worker, string projectId) =>
        worker.Availability is "online" or "draining" && worker.ManagedDiagnostics?.Projects.Any(project =>
            project.ProjectId == projectId && project.State == "blocked" && project.DiagnosticCode == ProjectResumeProtocol.PausedCode) == true;

    private static void MapProjectResume(WebApplication app)
    {
        app.MapGet("/api/v1/workers/{workerId}/projects/resume", async (string workerId, HttpContext context,
            ServerConfiguration settings, ProjectResumeStore resumes) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Ok(await resumes.GetAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/projects/{projectId}/resume", async (string workerId, string projectId,
            HttpContext context, ServerConfiguration settings, IRegistryStore registry, ProjectResumeStore resumes) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var worker = await registry.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null) return Results.NotFound();
            if (!ProjectPausedOnWorker(worker, projectId))
                return Results.Conflict(new { error = "The Worker does not report this project as paused. Wait for a current heartbeat." });
            try { return Results.Ok(await resumes.CreateAsync(workerId, projectId, context.RequestAborted)); }
            catch (InvalidOperationException) { return Results.Conflict(new { error = "A resume request for this Worker is already pending." }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/projects/resume/request", async (string workerId, HttpContext context,
            IRegistryStore registry, ProjectResumeStore resumes) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            var operation = await resumes.ClaimAsync(workerId, context.RequestAborted);
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        app.MapPost("/api/v1/workers/{workerId}/projects/resume/report", async (string workerId, ProjectResumeReport report,
            HttpContext context, IRegistryStore registry, ProjectResumeStore resumes) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            try { return Results.Ok(await resumes.ReportAsync(workerId, report, context.RequestAborted)); }
            catch (InvalidOperationException) { return Results.Conflict(); }
        });
    }
}
