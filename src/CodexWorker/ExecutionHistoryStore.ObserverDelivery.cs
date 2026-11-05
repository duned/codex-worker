namespace CodexWorker;

using CodexProvisioning;
using System.Text.Json;

public sealed partial class ExecutionHistoryStore
{
    private const string ObserverHistorySelection = " WHERE server_execution_id IS NULL AND assignment_id IS NULL AND ownership_generation IS NULL " +
        "ORDER BY (completed_at_utc IS NULL) DESC, COALESCE(completed_at_utc,started_at_utc) DESC, execution_id LIMIT 256";

    // A bounded latest-summary cursor, not a second execution log. History remains authoritative.
    // Include archived attempts: archiving local resources must not discard reporting lineage.
    internal Task<IReadOnlyList<ExecutionHistoryEntry>> ReadObserverHistoryAsync(CancellationToken ct) =>
        ReadAsync(ObserverHistorySelection, null, null, ct);

    internal async Task<IReadOnlyList<ObserverExecutionEvent>> PrepareObserverDeliveryAsync(string observerId, DateTimeOffset now, CancellationToken ct)
    {
        var entries = await ReadObserverHistoryAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The local credential lock serializes reporters and pairing changes. Retain at most 256 cursors.
        var retainedIds = entries.Select((_, index) => "$retained" + index).ToArray();
        command.CommandText = "DELETE FROM observer_delivery WHERE observer_id!=$observer OR execution_id NOT IN (" +
            string.Join(',', retainedIds) + ")";
        command.Parameters.AddWithValue("$observer", observerId);
        for (var index = 0; index < entries.Count; index++)
            command.Parameters.AddWithValue(retainedIds[index], entries[index].ExecutionId.ToString());
        await command.ExecuteNonQueryAsync(ct);
        var pending = new List<ObserverExecutionEvent>();
        foreach (var entry in entries)
        {
            var execution = ObserverSummary(entry);
            if (!execution.IsValid() || execution.StartedAtUtc > now || execution.CompletedAtUtc > now) continue;
            command.Parameters.Clear();
            command.CommandText = "SELECT body,disposition FROM observer_delivery WHERE execution_id=$id";
            command.Parameters.AddWithValue("$id", entry.ExecutionId.ToString());
            ObserverExecutionEvent? previous = null;
            var settled = false;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    previous = JsonSerializer.Deserialize<ObserverExecutionEvent>(reader.GetString(0));
                    settled = reader.GetInt32(1) != 0;
                }
            }
            if (previous?.Execution == execution && settled) continue;
            // Refresh an unacknowledged report outside the Server's seven-day replay window.
            var report = previous is not null && previous.Execution == execution && previous.ObservedAtUtc > now.AddDays(-7)
                ? previous : new ObserverExecutionEvent(1, observerId, Guid.NewGuid().ToString("N"),
                    previous is not null && previous.ObservedAtUtc >= now ? previous.ObservedAtUtc.AddTicks(1) : now, execution);
            command.CommandText = "INSERT INTO observer_delivery(execution_id,observer_id,body,disposition) VALUES($id,$observer,$body,0) " +
                "ON CONFLICT(execution_id) DO UPDATE SET body=$body,disposition=0";
            command.Parameters.AddWithValue("$observer", observerId);
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(report));
            await command.ExecuteNonQueryAsync(ct);
            if (pending.Count < ObserverProtocol.MaximumExecutions) pending.Add(report);
        }
        await transaction.CommitAsync(ct);
        return pending;
    }

    internal Task AcknowledgeObserverDeliveryAsync(ObserverExecutionEvent report, CancellationToken ct) =>
        SettleObserverDeliveryAsync(report, false, ct);

    internal async Task SettleObserverDeliveryAsync(ObserverExecutionEvent report, bool refused, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE observer_delivery SET disposition=$disposition WHERE execution_id=$id AND observer_id=$observer AND body=$body";
        command.Parameters.AddWithValue("$disposition", refused ? 2 : 1);
        command.Parameters.AddWithValue("$id", Guid.ParseExact(report.Execution.LocalExecutionId, "N").ToString());
        command.Parameters.AddWithValue("$observer", report.ObserverId);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(report));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static ObserverExecution ObserverSummary(ExecutionHistoryEntry entry) => new(
        entry.ExecutionId.ToString("N"), entry.Project, entry.Repository, entry.IssueNumber, entry.IssueTitle, entry.State,
        entry.State switch
        {
            "Completed" => "success", "Blocked" => "blocked", "Cancelled" => "cancelled", "Superseded" => "superseded",
            "Failed" or "InfrastructureFailure" or "IntegrationConflict" => "failed", _ => null
        },
        entry.ValidationOutcome switch
        {
            "passed" => "passed",
            { } value when value.StartsWith("failed", StringComparison.Ordinal) => "failed",
            { } value when value.StartsWith("pending:", StringComparison.Ordinal) => "pending",
            null => null, _ => "unavailable"
        }, entry.StartedAtUtc.ToUniversalTime(), entry.CompletedAtUtc?.ToUniversalTime(), entry.DurationMilliseconds,
        entry.AttemptNumber, entry.RetryOfExecutionId?.ToString("N"), entry.Resumed);
}
