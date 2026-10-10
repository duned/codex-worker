namespace CodexWorker;

public sealed record StandaloneExecutionMaintenanceRequest(bool Apply = false, int Limit = 20, int Offset = 0);
public sealed record StandaloneExecutionMaintenanceResult(int Scanned, int AlreadyResolved, int ResourcesCleaned,
    int Archived, int NeedsReview, IReadOnlyList<ExecutionCleanupResult> Items, bool Interrupted = false);

internal sealed class StandaloneExecutionMaintenance(ExecutionHistoryStore history, ProjectRuntimeRegistry registry,
    ExecutionCleanupService cleanup, bool managed)
{
    private readonly SemaphoreSlim _operation = new(1, 1);

    internal async Task<StandaloneExecutionMaintenanceResult> RunAsync(StandaloneExecutionMaintenanceRequest request, CancellationToken ct)
    {
        if (managed) throw new InvalidOperationException("Managed Worker maintenance requires Server authority.");
        if (request.Limit is < 1 or > 100 || request.Offset is < 0 or > 10000)
            throw new ArgumentException("Limit must be 1–100 and offset 0–10000.");
        if (!await _operation.WaitAsync(0, ct)) throw new InvalidOperationException("Execution maintenance is already running.");
        IDisposable? reservation = null;
        var items = new List<ExecutionCleanupResult>();
        var cleaned = 0;
        var archived = 0;
        var resolved = 0;
        ExecutionHistoryEntry[] entries = [];
        var finished = new HashSet<Guid>();
        try
        {
            if (request.Apply)
            {
                reservation = registry.TryBeginDrainingMaintenance() ?? throw new InvalidOperationException("Concurrent maintenance prevents reservation.");
                while (!registry.WorkerDrainComplete) await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
            entries = (await history.ReadMaintenancePageAsync(request.Limit, request.Offset, ct)).ToArray();
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.ServerExecutionId is not null || entry.AssignmentId is not null || entry.OwnershipGeneration is not null)
                {
                    items.Add(new(new(entry.ExecutionId, "review", "server-authority-required", "Managed execution requires Server maintenance."), "refused"));
                    finished.Add(entry.ExecutionId);
                    continue;
                }
                if (await history.ReadArchiveAuditAsync(entry.ExecutionId, ct) is not null)
                {
                    resolved++;
                    finished.Add(entry.ExecutionId);
                    continue;
                }
                if (entry.ReportingFailure is not null || entry.CompletionJson is not null && !ExecutionCompletion.IsSettled(entry) || entry.CodexRecovery is not null)
                {
                    items.Add(new(new(entry.ExecutionId, "review", "recovery-or-reporting-pending", "Retained Codex session, completion or reporting evidence requires its owning recovery protocol."), "refused"));
                    finished.Add(entry.ExecutionId);
                    continue;
                }
                var result = (await cleanup.RunReservedAsync(new(ExecutionId: entry.ExecutionId, Apply: request.Apply), ct)).Single();
                if (result.Inspection.Decision == "safe")
                {
                    if (result.Outcome == "cleaned") cleaned++;
                    if (result.Inspection.ReasonCode == "already-clean") resolved++;
                    if (!request.Apply && result.Inspection.ReasonCode != "already-clean")
                    {
                        items.Add(result with { Inspection = result.Inspection with
                        { Message = result.Inspection.Message + " Apply will clean verified resources, then reassess archive eligibility." } });
                        finished.Add(entry.ExecutionId);
                        continue;
                    }
                    var archive = (await cleanup.RunReservedAsync(new(ExecutionId: entry.ExecutionId, Apply: request.Apply, Action: "archive"), ct)).Single();
                    if (archive.Outcome == "archived") archived++;
                    result = archive;
                }
                items.Add(result);
                finished.Add(entry.ExecutionId);
            }
            return new(entries.Length, resolved, cleaned, archived, items.Count(i => i.Inspection.Decision != "safe"), items);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var entry in entries.Where(e => !finished.Contains(e.ExecutionId)))
                items.Add(new(new(entry.ExecutionId, "review", "maintenance-interrupted", "Deadline or cancellation interrupted this pass; inspect durable history before retry."), "refused"));
            return new(entries.Length, resolved, cleaned, archived, items.Count(i => i.Inspection.Decision != "safe"), items, true);
        }
        finally
        {
            reservation?.Dispose();
            _operation.Release();
        }
    }
}
