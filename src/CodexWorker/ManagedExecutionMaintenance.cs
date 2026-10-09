namespace CodexWorker;

using CodexProvisioning;

internal sealed class ManagedExecutionMaintenance(ExecutionHistoryStore history, WorkerRuntimeReadModel runtime)
{
    internal async Task<ExecutionMaintenanceReport> ExecuteAsync(ExecutionMaintenanceCommand command,
        Func<CancellationToken, Task<bool>> authorize, Func<ExecutionHistoryEntry, CancellationToken, Task> retryReport, CancellationToken ct)
    {
        if (!ExecutionMaintenanceProtocol.Valid(command.Request) || command.Status != "running" || command.DeadlineUtc is null)
            throw new InvalidDataException("Invalid managed maintenance command.");
        var request = command.Request;
        if (request.Action == "inventory")
        {
            var entries = await history.ReadInventoryAsync(new(Project: request.Project, IssueNumber: request.IssueNumber,
                Outcome: request.Outcome, Origin: request.Origin, Limit: request.Limit, Offset: request.Offset, IncludeArchived: true), DateTimeOffset.UtcNow, ct);
            var observations = new List<ExecutionMaintenanceObservation>();
            foreach (var entry in entries) observations.Add(await ObserveAsync(entry, ct));
            return new("succeeded", "inventory-observed", observations);
        }
        var current = (await history.ReadAllAsync(ct)).SingleOrDefault(e => e.ExecutionId == request.WorkerExecutionId);
        if (current is null) return new("refused", "worker-record-missing", []);
        if (current.ServerExecutionId != request.ServerExecutionId || current.AssignmentId != request.AssignmentId || current.OwnershipGeneration != request.Generation)
            return new("refused", "worker-identity-mismatch", []);
        if (request.Action == "retry-report")
        {
            if (current.CompletedAtUtc is null || current.State is not ("Completed" or "Blocked" or "Failed" or "IntegrationConflict" or "InfrastructureFailure" or "Cancelled"))
                return new("refused", "terminal-worker-proof-required", [await ObserveAsync(current, ct)]);
            if (!request.Apply) return new("succeeded", "completion-report-preview", [await ObserveAsync(current, ct)]);
            using var maintenance = runtime.Registry.TryBeginServerMaintenance();
            if (maintenance is null) return new("refused", "worker-drain-required", [await ObserveAsync(current, ct)]);
            if (!await authorize(ct)) return new("refused", "server-authority-rejected", [await ObserveAsync(current, ct)]);
            try
            {
                // Reporting only. This never invokes completion reconciliation or GitHub notification.
                await retryReport(current, ct);
                await history.RecordMaintenanceReportDispositionAsync(current.ExecutionId, "acknowledged", CancellationToken.None);
                await history.UpdateReportingFailureAsync(current.ExecutionId, null, CancellationToken.None);
                return new("succeeded", "completion-report-acknowledged", [await ObserveAsync(current with { ReportingFailure = null }, ct)]);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
            {
                var reason = ex.StatusCode == System.Net.HttpStatusCode.NotFound ? "server-record-missing" : "stale-lease-report-reconciliation-required";
                await history.RecordMaintenanceReportDispositionAsync(current.ExecutionId, reason, CancellationToken.None);
                return new("refused", reason, [await ObserveAsync(current, ct)]);
            }
        }
        var results = await runtime.ExecutionCleanup.RunManagedAsync(command, authorize, ct);
        var result = results.SingleOrDefault();
        var updated = (await history.ReadAllAsync(ct)).Single(e => e.ExecutionId == current.ExecutionId);
        return new(result?.Outcome is "refused" ? "refused" : result?.Outcome is "failed" ? "failed" : "succeeded",
            result?.Inspection.ReasonCode ?? "worker-record-missing", [await ObserveAsync(updated, ct)]);
    }

    private async Task<ExecutionMaintenanceObservation> ObserveAsync(ExecutionHistoryEntry entry, CancellationToken ct)
    {
        var disposition = await history.ReadServerReportDispositionAsync(entry.ExecutionId, ct);
        var reporting = disposition == "acknowledged" ? "acknowledged" : disposition is not null ? "reconciliation-required" :
            entry.ReportingFailure is null ? "none" : "pending";
        return new(entry.ExecutionId, entry.ServerExecutionId, entry.AssignmentId, entry.OwnershipGeneration, entry.State,
            entry.RecoveryState ?? "none", reporting, entry.Project, entry.IssueNumber, await history.ReadArchiveAuditAsync(entry.ExecutionId, ct) is not null);
    }
}
