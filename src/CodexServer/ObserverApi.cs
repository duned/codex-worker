namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class ServerApplication
{
    internal static void MapObserverPairing(WebApplication app)
    {
        app.MapPost("/api/v1/observers/pairing/authorize", async (ObserverPairingRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (!request.IsValid() || request.Server != settings.AdministrationOrigin) return Results.BadRequest();
            var authorization = await store.CreateObserverAuthorizationAsync(request, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return authorization is null ? Results.Conflict() : Results.Ok(authorization);
        });
        app.MapPost("/api/v1/observers/pairing/redeem", async (ObserverPairingRequest request,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!request.IsValid() || request.Server != settings.AdministrationOrigin) return Results.BadRequest();
            var registration = await store.RedeemObserverAuthorizationAsync(request, ObserverBearer(context),
                context.Request.Headers["X-Codex-Observer-Token"].ToString(), context.RequestAborted);
            return registration is null ? Results.Unauthorized() : Results.Ok(registration);
        });
        app.MapGet("/api/v1/observers/{observerId}/registration", async (string observerId,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (settings.AdministrationOrigin is not { } server) return Results.Unauthorized();
            var registration = await store.AuthenticateObserverAsync(observerId, server,
                ObserverBearer(context), context.RequestAborted);
            return registration is null ? Results.Unauthorized() : Results.Ok(registration);
        });
        app.MapPost("/api/v1/observers/{observerId}/revoke", async (string observerId,
            HttpContext context, ServerConfiguration settings, IRegistryStore store) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            return await store.RevokeObserverAsync(observerId, context.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });
    }

    private static string ObserverBearer(HttpContext context)
    {
        var value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : "";
    }
}
