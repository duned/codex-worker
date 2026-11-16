namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>What the dashboard needs to offer a Worker restart or update. The Worker still chooses
/// and verifies the release it installs; this is only the latest published version for display.</summary>
public sealed record WorkerReleaseStatus(string? CurrentVersion, string? LatestVersion, bool UpdateAvailable,
    bool SelfUpdateSupported, bool RestartSupported, DateTimeOffset? CheckedAtUtc, string? Diagnostic = null);

/// <summary>Caches the latest published Worker release so dashboard reads never fan out to GitHub.</summary>
public sealed class WorkerReleaseCatalog(Func<CancellationToken, Task<string>> findLatest, TimeProvider clock)
{
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string? Version, string? Diagnostic, DateTimeOffset CheckedAtUtc, DateTimeOffset ExpiresAtUtc)? _cached;

    public static WorkerReleaseCatalog CreateDefault(TimeProvider clock)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var releases = new PackagedSelfUpdate(http);
        return new(async ct => (await releases.FindReleaseAsync(
            new("worker", "Codex Worker", "", "/opt/codex-worker/CodexWorker"), ct)).Version, clock);
    }

    public async Task<(string? Version, string? Diagnostic, DateTimeOffset CheckedAtUtc)> GetLatestAsync(CancellationToken ct)
    {
        if (Fresh() is { } fresh) return fresh;
        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh() is { } current) return current;
            var now = clock.GetUtcNow();
            try
            {
                var version = await findLatest(ct);
                if (!WorkerUpdateProtocol.ValidVersion(version)) throw new InvalidDataException("Invalid release version.");
                _cached = (version, null, now, now + SuccessLifetime);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException ||
                ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // A failed lookup is shown as unknown and retried later; it never blocks restart.
                _cached = (null, "release-catalog-unavailable", now, now + FailureLifetime);
            }
            var stored = _cached.Value;
            return (stored.Version, stored.Diagnostic, stored.CheckedAtUtc);
        }
        finally { _gate.Release(); }
    }

    private (string? Version, string? Diagnostic, DateTimeOffset CheckedAtUtc)? Fresh() =>
        _cached is { } cached && clock.GetUtcNow() < cached.ExpiresAtUtc ? (cached.Version, cached.Diagnostic, cached.CheckedAtUtc) : null;

    public static WorkerReleaseStatus Describe(WorkerRegistrationResponse worker, string? latest, string? diagnostic, DateTimeOffset checkedAtUtc)
    {
        bool Supports(string capability) => worker.Capabilities.Any(item => item.Type == "protocol" && item.Name == capability);
        var current = WorkerUpdateProtocol.ValidVersion(worker.WorkerVersion) ? worker.WorkerVersion : null;
        var available = current is not null && latest is not null && WorkerUpdateProtocol.NeedsUpdate(current, latest);
        return new(worker.WorkerVersion, latest, available, Supports(WorkerUpdateProtocol.Capability),
            Supports(WorkerUpdateProtocol.RestartCapability), checkedAtUtc, diagnostic);
    }
}

public static partial class ServerApplication
{
    private static void MapWorkerRelease(WebApplication app) =>
        app.MapGet("/api/v1/workers/{workerId}/release", async (string workerId, HttpContext context,
            ServerConfiguration settings, IRegistryStore store, WorkerReleaseCatalog releases) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null) return Results.NotFound();
            var (latest, diagnostic, checkedAtUtc) = await releases.GetLatestAsync(context.RequestAborted);
            return Results.Ok(WorkerReleaseCatalog.Describe(worker, latest, diagnostic, checkedAtUtc));
        });
}
