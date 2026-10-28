namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class ServerApplication
{
    internal static bool TelegramTestReady(WorkerRegistrationResponse worker, string version) =>
        worker.Telegram is { Enabled: true, TokenConfigured: true } && worker.Availability is ("online" or "draining") &&
        worker.ConfigurationSynchronization == "synchronized" && worker.ConfigurationVersion == version;

    private static void MapTelegramTests(WebApplication app)
    {
        app.MapGet("/api/v1/workers/{workerId}/telegram/test", async (string workerId, HttpContext context,
            ServerConfiguration settings, TelegramTestStore tests) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return Results.Ok(await tests.GetAsync(workerId, context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/telegram/test", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore registry, TelegramTestStore tests) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var worker = await registry.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null) return Results.NotFound();
            var version = ManagedConfigurationVersion(await registry.GetProjectsAsync(context.RequestAborted), worker.WorkerExecutionSlots, worker.Telegram);
            if (!TelegramTestReady(worker, version)) return Results.Conflict(new { error = "Enable managed Telegram and wait for a current online Worker acknowledgement. Check credential delivery if synchronization failed." });
            try { return Results.Ok(await tests.CreateAsync(workerId, version, context.RequestAborted)); }
            catch (InvalidOperationException) { return Results.Conflict(); }
        });
        app.MapPost("/api/v1/workers/{workerId}/telegram/test/request", async (string workerId, HttpContext context,
            IRegistryStore registry, TelegramTestStore tests) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            var operation = await tests.ClaimAsync(workerId, context.RequestAborted);
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        app.MapPost("/api/v1/workers/{workerId}/telegram/test/report", async (string workerId, TelegramTestReport report,
            HttpContext context, IRegistryStore registry, TelegramTestStore tests) =>
        {
            if (!await AuthorizedWorkerAsync(context, registry, workerId)) return WorkerUnauthorized();
            try { return Results.Ok(await tests.ReportAsync(workerId, report, context.RequestAborted)); }
            catch (InvalidOperationException) { return Results.Conflict(); }
        });
    }
}
