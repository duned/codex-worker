namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

public static partial class ServerApplication
{
    private static void MapObserverReports(WebApplication app)
    {
        app.MapPost("/api/v1/observers/{observerId}/snapshots", (string observerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) => IngestObserverHttpAsync(observerId, context, settings, store, snapshot: true));
        app.MapPost("/api/v1/observers/{observerId}/events", (string observerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) => IngestObserverHttpAsync(observerId, context, settings, store, snapshot: false));
        app.MapGet("/api/v1/observers/{observerId}", async (string observerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var observer = await store.GetObserverInspectionAsync(observerId, context.RequestAborted);
            return observer is null ? Results.NotFound() : Results.Ok(observer);
        });
        app.MapGet("/api/v1/observers/{observerId}/history", async (string observerId, int? limit, HttpContext context,
            ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!ObserverProtocol.ValidIdentity(observerId) || limit is < 1 or > SqliteRegistryStore.ObserverHistoryLimit)
                return Results.BadRequest();
            if (await store.GetObserverInspectionAsync(observerId, context.RequestAborted) is null) return Results.NotFound();
            return Results.Ok(await store.GetObserverHistoryAsync(observerId, limit ?? 50, context.RequestAborted));
        });
    }

    private static async Task<IResult> IngestObserverHttpAsync(string observerId, HttpContext context,
        ServerConfiguration settings, IRegistryStore store, bool snapshot)
    {
        context.Response.Headers.CacheControl = "no-store";
        var credential = ObserverBearer(context);
        if (settings.AdministrationOrigin is not { } server ||
            await store.AuthenticateObserverAsync(observerId, server, credential, context.RequestAborted) is null)
            return Results.Unauthorized();
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > ObserverProtocol.MaximumPayloadBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        // Read one extra byte to detect oversize chunked bodies without buffering unbounded input.
        var buffer = new byte[ObserverProtocol.MaximumPayloadBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted);
            if (count == 0) break;
            length += count;
        }
        if (length > ObserverProtocol.MaximumPayloadBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        object report;
        try
        {
            report = snapshot ? ObserverProtocol.ReadSnapshot(buffer.AsSpan(0, length)) : ObserverProtocol.ReadEvent(buffer.AsSpan(0, length));
        }
        catch (JsonException) { return Results.BadRequest(new { error = "invalid_observer_report" }); }
        var result = report switch
        {
            ObserverStatusSnapshot status when status.ObserverId == observerId =>
                await store.IngestObserverSnapshotAsync(server, credential, status, context.RequestAborted),
            ObserverExecutionEvent execution when execution.ObserverId == observerId =>
                await store.IngestObserverEventAsync(server, credential, execution, context.RequestAborted),
            _ => ObserverIngestionResult.Invalid
        };
        return result switch
        {
            ObserverIngestionResult.Accepted => Results.Ok(new { state = "accepted" }),
            ObserverIngestionResult.Duplicate => Results.Ok(new { state = "duplicate" }),
            ObserverIngestionResult.Unauthorized => Results.Unauthorized(),
            ObserverIngestionResult.Conflict => Results.Conflict(new { error = "observer_report_identity_conflict" }),
            ObserverIngestionResult.Expired => Results.Conflict(new { error = "observer_report_expired" }),
            _ => Results.BadRequest(new { error = "invalid_observer_report" })
        };
    }
}
