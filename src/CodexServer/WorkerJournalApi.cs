namespace CodexServer;

using CodexProvisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// Ephemeral read requests: no journal content is persisted in the registry.
public sealed class WorkerJournalRequests
{
    private readonly object gate = new();
    private readonly Dictionary<string, (WorkerJournalRequest Request, DateTimeOffset Expires, WorkerJournalResult? Result)> requests = new();
    public WorkerJournalResult Read(string worker, int lines)
    {
        if (lines is < 1 or > WorkerJournalProtocol.MaximumLines) throw new ArgumentOutOfRangeException(nameof(lines));
        lock (gate)
        {
            foreach (var key in requests.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) requests.Remove(key);
            if (requests.TryGetValue(worker, out var current) && current.Request.Lines == lines)
                return current.Result ?? new("pending", []);
            if (requests.Count >= 64) return new("busy", []);
            requests[worker] = (new(Guid.NewGuid(), lines), DateTimeOffset.UtcNow.AddSeconds(90), null);
            return new("pending", []);
        }
    }
    public WorkerJournalRequest? Pending(string worker)
    {
        lock (gate) return requests.TryGetValue(worker, out var value) && value.Expires > DateTimeOffset.UtcNow && value.Result is null ? value.Request : null;
    }
    public bool Complete(string worker, Guid id, WorkerJournalResult result)
    {
        lock (gate)
        {
            if (!WorkerJournalProtocol.Valid(result) || !requests.TryGetValue(worker, out var value) || value.Request.Id != id || value.Expires <= DateTimeOffset.UtcNow || result.Lines.Count > value.Request.Lines) return false;
            requests[worker] = (value.Request, value.Expires, result with { Lines = result.Lines.Select(line => SecretSanitizer.Sanitize(line, 1000)).ToArray() });
            return true;
        }
    }
}

public static partial class ServerApplication
{
    private static void MapWorkerJournal(WebApplication app)
    {
        app.MapGet("/api/v1/workers/{workerId}/journal", async (string workerId, int? lines, HttpContext context, ServerConfiguration settings, IRegistryStore store, WorkerJournalRequests journal) =>
        {
            if (!AuthorizedManagement(context, settings)) return Results.Unauthorized();
            if (lines is < 1 or > WorkerJournalProtocol.MaximumLines) return Results.BadRequest();
            var worker = await store.GetWorkerAsync(workerId, context.RequestAborted);
            if (worker is null) return Results.NotFound();
            return Results.Ok(worker.Availability == "online" ? journal.Read(workerId, lines ?? 100) : new WorkerJournalResult("unavailable", []));
        });
        app.MapGet("/api/v1/workers/{workerId}/journal/request", async (string workerId, HttpContext context, IRegistryStore store, WorkerJournalRequests journal) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            return journal.Pending(workerId) is { } request ? Results.Ok(request) : Results.NoContent();
        });
        app.MapPost("/api/v1/workers/{workerId}/journal/{id:guid}", async (string workerId, Guid id, WorkerJournalResult result, HttpContext context, IRegistryStore store, WorkerJournalRequests journal) =>
        {
            if (!await AuthorizedWorkerAsync(context, store, workerId)) return WorkerUnauthorized();
            return journal.Complete(workerId, id, result) ? Results.Ok() : Results.BadRequest();
        });
    }
}
