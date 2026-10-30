namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class ServerApplication
{
    private static void MapWorkerUpdates(WebApplication app)
    {
        app.MapGet("/api/v1/workers/{workerId}/update/recovery", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try { return Results.Ok(await store.InspectWorkerUpdateAsync(workerId, context.RequestAborted)); }
            catch (InvalidDataException) { return Results.BadRequest(new { reason = "invalid-worker-update" }); }
        });
        app.MapGet("/api/v1/workers/{workerId}/update", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try { return Results.Ok(await store.GetWorkerUpdateAsync(workerId, context.RequestAborted)); }
            catch (InvalidDataException) { return Results.BadRequest(new { reason = "invalid-worker-update" }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/update", async (string workerId, WorkerUpdateRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            // Explicit mutation authorization (including browser CSRF), independent of reads/settings.
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            try { return Results.Ok(await store.RequestWorkerUpdateAsync(workerId, request.OperationId, context.RequestAborted, request.Restart)); }
            catch (InvalidDataException) { return Results.BadRequest(new { reason = "invalid-worker-update" }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { reason = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/update/reconcile", async (string workerId, bool nodeQuiescent,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!nodeQuiescent) return Results.Conflict(new { reason = "inspect-quiescent-node-before-recovery" });
            try { return Results.Ok(await store.ReconcileWorkerUpdateAsync(workerId, context.RequestAborted)); }
            catch (InvalidDataException) { return Results.BadRequest(new { reason = "invalid-worker-update" }); }
            catch (WorkerUpdateRecoveryException ex) { return Results.Conflict(new { reason = ex.Message, recovery = ex.Recovery }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { reason = ex.Message }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/update/request", async (string workerId, HttpContext context, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            var operation = await store.AdvanceWorkerUpdateAsync(workerId, cancellationToken: context.RequestAborted);
            return operation is null ? Results.NoContent() : Results.Ok(operation);
        });
        app.MapPost("/api/v1/workers/{workerId}/update/{operationId}/start", async (string workerId, string operationId,
            WorkerUpdatePreparation preparation, HttpContext context, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            if (!WorkerUpdateProtocol.ValidVersion(preparation.Version)) return Results.BadRequest(new { reason = "invalid-worker-update" });
            try
            {
                var operation = await store.AdvanceWorkerUpdateAsync(workerId, operationId, preparation.Version, cancellationToken: context.RequestAborted);
                return operation is null ? Results.NotFound() : Results.Ok(operation);
            }
            catch (InvalidDataException) { return Results.BadRequest(new { reason = "invalid-worker-update" }); }
            catch (InvalidOperationException) { return Results.Conflict(new { reason = "worker-update-not-authorized" }); }
        });
        app.MapPost("/api/v1/workers/{workerId}/update/{operationId}/verified", async (string workerId, string operationId,
            HttpContext context, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            return Results.Ok(await store.AdvanceWorkerUpdateAsync(workerId, operationId, verified: true, cancellationToken: context.RequestAborted));
        });
        app.MapPost("/api/v1/workers/{workerId}/update/{operationId}/fail", async (string workerId, string operationId,
            HttpContext context, IRegistryStore store) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            return Results.Ok(await store.AdvanceWorkerUpdateAsync(workerId, operationId, fail: true, cancellationToken: context.RequestAborted));
        });
    }
}
