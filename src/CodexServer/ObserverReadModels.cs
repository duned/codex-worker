namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Text.Json;

/// <summary>Receipt freshness describes reporting contact, never execution readiness or process liveness.</summary>
public sealed record ObserverSummary(string ObserverId, string Mode, string RegistrationState,
    string? Name, string? Version, DateTimeOffset? LastReceivedAtUtc, DateTimeOffset? SnapshotReceivedAtUtc,
    DateTimeOffset? ObservedAtUtc, string Freshness, string FreshnessMeaning, int StaleAfterSeconds,
    string? ReportedLifecycle, string? ReportedReadiness, int? MaximumCapacity, int? ActiveExecutions, DateTimeOffset? PairingExpiresAtUtc = null);
public sealed record ObserverInventory(IReadOnlyList<ObserverSummary> Observers, IReadOnlyList<ObserverInventoryExecution> Executions, int Total, int Active, int? TotalCapacity);
public sealed record ObserverInventoryExecution(string ObserverId, ObserverExecutionObservation Observation);
public sealed record ObserverDetail(ObserverSummary Observer, IReadOnlyList<ObserverExecutionObservation> CurrentExecutions);

public sealed partial class SqliteRegistryStore
{
    internal static ObserverSummary ObserverSummaryFor(ObserverInspection inspection, DateTimeOffset now, int staleAfterSeconds)
    {
        var snapshot = inspection.Snapshot;
        // Events establish receipt contact but cannot establish a fresh lifecycle/capacity snapshot.
        var freshness = inspection.SnapshotReceivedAtUtc is not { } received ? "unknown" :
            now - received > TimeSpan.FromSeconds(staleAfterSeconds) ? "stale" : "fresh";
        return new(inspection.Registration.ObserverId, ObserverProtocol.Mode, inspection.Registration.State,
            snapshot?.Name, snapshot?.Version, inspection.LastReceivedAtUtc, inspection.SnapshotReceivedAtUtc,
            snapshot?.ObservedAtUtc, freshness,
            "Freshness uses Server snapshot receipt time. Stale means no recent snapshot; it does not prove the observer is offline or change its reported lifecycle, readiness or execution outcomes.",
            staleAfterSeconds, snapshot?.Lifecycle, snapshot?.Readiness, snapshot?.MaximumCapacity, snapshot?.ActiveExecutions);
    }

    public async Task<IReadOnlyList<ObserverSummary>> ListObserversAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || offset is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return await ReadObserversAsync(connection, null, limit, offset, false, _timeProvider.GetUtcNow(), ct);
    }

    private async Task<IReadOnlyList<ObserverSummary>> ReadObserversAsync(SqliteConnection connection,
        SqliteTransaction? transaction, int limit, int offset, bool acceptedOnly, DateTimeOffset now, CancellationToken ct)
    {
        await using var command = ObserverCommand(connection, transaction, """
            SELECT o.observer_id,o.server_origin,o.accepted_at_utc,o.revoked_at_utc,
                s.snapshot_json,s.snapshot_received_at_utc,s.last_received_at_utc,NULL AS expires_at_utc
            FROM observers o LEFT JOIN observer_status s ON s.observer_id=o.observer_id
            WHERE $accepted=0 OR o.revoked_at_utc IS NULL
            UNION ALL
            SELECT p.observer_id,p.server_origin,NULL,p.revoked_at_utc,NULL,NULL,NULL,p.expires_at_utc
            FROM observer_pairings p WHERE $accepted=0 AND p.consumed_at_utc IS NULL
                AND NOT EXISTS (SELECT 1 FROM observers o WHERE o.observer_id=p.observer_id)
            ORDER BY observer_id LIMIT $limit OFFSET $offset;
            """, ("$limit", limit), ("$offset", offset), ("$accepted", acceptedOnly ? 1 : 0));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ObserverSummary>();
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(2))
            {
                var expires = ObserverDate(reader.GetString(7));
                var state = !reader.IsDBNull(3) ? "revoked" : expires <= now ? "pairing-expired" : "pairing-pending";
                items.Add(new(reader.GetString(0), ObserverProtocol.Mode, state, null, null, null, null, null,
                    "unknown", "No accepted snapshot received.", (int)_staleAfter.TotalSeconds,
                    null, null, null, null, expires));
                continue;
            }
            var snapshot = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<ObserverStatusSnapshot>(reader.GetString(4), ProjectJson);
            var registration = new ObserverRegistration(ObserverProtocol.Version, reader.GetString(0), ObserverProtocol.Mode,
                reader.GetString(1), ObserverDate(reader.GetString(2)), !reader.IsDBNull(3) ? "revoked" : snapshot is null ? "awaiting-first-report" : "reporting");
            items.Add(ObserverSummaryFor(new(registration, snapshot,
                reader.IsDBNull(5) ? null : ObserverDate(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ObserverDate(reader.GetString(6)), []), now, (int)_staleAfter.TotalSeconds));
        }
        return items;
    }
    // A fixed inventory window and a fixed latest-projection window; no per-observer reads.
    public async Task<ObserverInventory> GetObserverInventoryAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var observers = await ReadObserversAsync(connection, transaction, 100, 0, true, now, ct);
        await using var command = ObserverCommand(connection, transaction, """
            SELECT e.observer_id,e.body,e.observed_ticks,e.received_at_utc
            FROM observer_executions e
            JOIN (SELECT observer_id FROM observers WHERE revoked_at_utc IS NULL ORDER BY observer_id LIMIT 100) o
                ON o.observer_id=e.observer_id
            ORDER BY e.observed_ticks DESC,e.observer_id,e.execution_id LIMIT 100;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var executions = new List<ObserverInventoryExecution>();
        while (await reader.ReadAsync(ct))
        {
            var execution = JsonSerializer.Deserialize<ObserverExecution>(reader.GetString(1), ProjectJson)
                ?? throw new InvalidDataException("Observer execution metadata is missing.");
            executions.Add(new(reader.GetString(0), new(execution, new(reader.GetInt64(2), TimeSpan.Zero),
                ObserverDate(reader.GetString(3)))));
        }
        await reader.DisposeAsync();
        await using var totalsCommand = ObserverCommand(connection, transaction, """
            SELECT COUNT(*),
                COALESCE(SUM(CASE WHEN s.snapshot_received_at_utc IS NOT NULL
                    AND julianday(s.snapshot_received_at_utc) >= julianday($cutoff)
                    AND COALESCE(json_extract(s.snapshot_json,'$.lifecycle'),'') <> 'stopped' THEN 1 ELSE 0 END),0),
                CASE WHEN COUNT(*)=COUNT(json_extract(s.snapshot_json,'$.maximumCapacity'))
                    THEN COALESCE(SUM(json_extract(s.snapshot_json,'$.maximumCapacity')),0) ELSE NULL END
            FROM observers o LEFT JOIN observer_status s ON s.observer_id=o.observer_id
            WHERE o.revoked_at_utc IS NULL;
            """, ("$cutoff", (now - _staleAfter).ToString("O")));
        await using var totals = await totalsCommand.ExecuteReaderAsync(ct);
        await totals.ReadAsync(ct);
        var result = new ObserverInventory(observers, executions, totals.GetInt32(0), totals.GetInt32(1),
            totals.IsDBNull(2) ? null : totals.GetInt32(2));
        await totals.DisposeAsync();
        await transaction.CommitAsync(ct);
        return result;
    }

}
