namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Ephemeral read requests: no journal or event content is persisted in the registry.
public sealed class WorkerJournalRequests(TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Source, string Worker), (WorkerJournalRequest Request, DateTimeOffset Expires, WorkerJournalResult? Result)> requests = new();
    public WorkerJournalResult Read(string worker, int lines, bool refresh = false, string source = WorkerJournalProtocol.Journal)
    {
        if (lines is < 1 or > WorkerJournalProtocol.MaximumLines) throw new ArgumentOutOfRangeException(nameof(lines));
        lock (gate)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            foreach (var expired in requests.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) requests.Remove(expired);
            var key = (source, worker);
            if (requests.TryGetValue(key, out var current) && current.Request.Lines == lines &&
                (current.Result is null || !refresh))
                return current.Result ?? new("pending", []);
            if (requests.Count >= 64 && !requests.ContainsKey(key)) return new("busy", []);
            requests[key] = (new(Guid.NewGuid(), lines), now.AddSeconds(90), null);
            return new("pending", []);
        }
    }
    public WorkerJournalRequest? Pending(string worker, string source = WorkerJournalProtocol.Journal)
    {
        lock (gate) return requests.TryGetValue((source, worker), out var value) && value.Expires > (clock ?? TimeProvider.System).GetUtcNow() && value.Result is null ? value.Request : null;
    }
    public bool Complete(string worker, Guid id, WorkerJournalResult result, string source = WorkerJournalProtocol.Journal)
    {
        lock (gate)
        {
            if (!WorkerJournalProtocol.Valid(result) || !requests.TryGetValue((source, worker), out var value) || value.Request.Id != id || value.Expires <= (clock ?? TimeProvider.System).GetUtcNow() || result.Lines.Count > value.Request.Lines) return false;
            requests[(source, worker)] = (value.Request, value.Expires, result with { Lines = result.Lines.Select(line => SecretSanitizer.Sanitize(line, 1000)).ToArray() });
            return true;
        }
    }
}

public static partial class ServerApplication
{
    private static void MapWorkerJournal(WebApplication app)
    {
        // The journal and the runtime events share one bounded, ephemeral request flow under their own paths.
        foreach (var source in new[] { WorkerJournalProtocol.Journal, WorkerJournalProtocol.Events })
        {
            app.MapGet($"/api/v1/workers/{{workerId}}/{source}", async (string workerId, int? lines, bool? refresh, HttpContext context, ServerConfiguration settings, IRegistryStore store, WorkerJournalRequests journal) =>
            {
                if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
                if (lines is < 1 or > WorkerJournalProtocol.MaximumLines) return Results.BadRequest();
                var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
                if (worker is null) return Results.NotFound();
                return Results.Ok(worker.Availability == "online" ? journal.Read(workerId, lines ?? 100, refresh ?? false, source) : new WorkerJournalResult("unavailable", []));
            });
            app.MapGet($"/api/v1/workers/{{workerId}}/{source}/request", async (string workerId, HttpContext context, IRegistryStore store, WorkerJournalRequests journal) =>
            {
                if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
                return journal.Pending(workerId, source) is { } request ? Results.Ok(request) : Results.NoContent();
            });
            app.MapPost($"/api/v1/workers/{{workerId}}/{source}/{{id:guid}}", async (string workerId, Guid id, WorkerJournalResult result, HttpContext context, IRegistryStore store, WorkerJournalRequests journal) =>
            {
                if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
                return journal.Complete(workerId, id, result, source) ? Results.Ok() : Results.BadRequest();
            });
        }
    }
}
