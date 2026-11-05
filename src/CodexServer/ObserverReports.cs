namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

public enum ObserverIngestionResult { Accepted, Duplicate, Invalid, Unauthorized, Conflict, Expired }
public sealed record ObserverExecutionObservation(ObserverExecution Execution, DateTimeOffset ObservedAtUtc, DateTimeOffset ReceivedAtUtc);
public sealed record ObserverReportReceipt(string Kind, string ReportId, DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc, ObserverStatusSnapshot? Snapshot, ObserverExecutionEvent? Event);
public sealed record ObserverInspection(ObserverRegistration Registration, ObserverStatusSnapshot? Snapshot,
    DateTimeOffset? SnapshotReceivedAtUtc, DateTimeOffset? LastReceivedAtUtc, IReadOnlyList<ObserverExecutionObservation> Executions);

public sealed partial class SqliteRegistryStore
{
    public const int ObserverHistoryLimit = 512;
    public const int ObserverExecutionLimit = 256;
    public static readonly TimeSpan ObserverReportMaximumAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan ObserverReportFutureTolerance = TimeSpan.FromMinutes(5);

    public Task<ObserverIngestionResult> IngestObserverSnapshotAsync(string server, string credential,
        ObserverStatusSnapshot snapshot, CancellationToken ct = default) => snapshot.IsValid()
        ? IngestObserverReportAsync(server, credential, snapshot.ObserverId, "snapshot", snapshot.SnapshotId,
            snapshot.ObservedAtUtc, JsonSerializer.Serialize(snapshot, ProjectJson), snapshot.Executions, snapshot, ct)
        : Task.FromResult(ObserverIngestionResult.Invalid);

    public Task<ObserverIngestionResult> IngestObserverEventAsync(string server, string credential,
        ObserverExecutionEvent report, CancellationToken ct = default) => report.IsValid()
        ? IngestObserverReportAsync(server, credential, report.ObserverId, "event", report.EventId,
            report.ObservedAtUtc, JsonSerializer.Serialize(report, ProjectJson), [report.Execution], null, ct)
        : Task.FromResult(ObserverIngestionResult.Invalid);

    private async Task<ObserverIngestionResult> IngestObserverReportAsync(string server, string credential, string observerId,
        string kind, string reportId, DateTimeOffset observed, string body, IReadOnlyList<ObserverExecution> executions,
        ObserverStatusSnapshot? snapshot, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(body) > ObserverProtocol.MaximumPayloadBytes ||
            executions.Any(execution => execution.StartedAtUtc > observed || execution.CompletedAtUtc > observed))
            return ObserverIngestionResult.Invalid;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        // Verify inside the write transaction: revocation and ingestion cannot interleave.
        if (await AuthenticateObserverAsync(connection, transaction, observerId, server, credential, ct) is null)
            return ObserverIngestionResult.Unauthorized;
        var now = _timeProvider.GetUtcNow();
        if (observed <= now - ObserverReportMaximumAge || observed > now + ObserverReportFutureTolerance)
            return ObserverIngestionResult.Expired;
        var hash = HashToken(body);
        await using (var duplicate = ObserverCommand(connection, transaction,
            "SELECT payload_hash FROM observer_reports WHERE observer_id=$id AND kind=$kind AND report_id=$report;",
            ("$id", observerId), ("$kind", kind), ("$report", reportId)))
        {
            if (await duplicate.ExecuteScalarAsync(ct) is string existingHash)
                return existingHash == hash ? ObserverIngestionResult.Duplicate : ObserverIngestionResult.Conflict;
        }
        long replayFloor = 0, executionFloor = 0;
        long? currentSnapshotTicks = null;
        ObserverStatusSnapshot? currentSnapshot = null;
        await using (var status = ObserverCommand(connection, transaction,
            "SELECT replay_before_ticks,execution_before_ticks,snapshot_observed_ticks,snapshot_json FROM observer_status WHERE observer_id=$id;", ("$id", observerId)))
        await using (var reader = await status.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                replayFloor = reader.GetInt64(0);
                executionFloor = reader.GetInt64(1);
                if (!reader.IsDBNull(2)) currentSnapshotTicks = reader.GetInt64(2);
                if (!reader.IsDBNull(3)) currentSnapshot = JsonSerializer.Deserialize<ObserverStatusSnapshot>(reader.GetString(3), ProjectJson);
            }
        }
        if (observed.UtcTicks <= replayFloor) return ObserverIngestionResult.Expired;
        var currentExecutions = await ReadObserverExecutionsAsync(connection, transaction, observerId, ct);
        foreach (var execution in executions)
        {
            var existing = currentExecutions.FirstOrDefault(item => item.Execution.LocalExecutionId == execution.LocalExecutionId);
            var previous = existing?.Execution ?? currentSnapshot?.Executions.FirstOrDefault(item => item.LocalExecutionId == execution.LocalExecutionId);
            if (previous is not null && (previous.Repository != execution.Repository ||
                previous.IssueNumber != execution.IssueNumber || previous.StartedAtUtc != execution.StartedAtUtc))
                return ObserverIngestionResult.Conflict;
            if (previous is not null && ObserverTerminal(previous)) continue;
            // Only terminal summaries remain authoritative after their per-execution ordering metadata
            // is pruned. Do not recreate an evicted nonterminal execution from a delayed observation.
            if (existing is null && execution.StartedAtUtc.UtcTicks <= executionFloor) return ObserverIngestionResult.Expired;
            var previousObserved = existing?.ObservedAtUtc ?? currentSnapshot?.ObservedAtUtc;
            if (previous is not null && (previousObserved > observed ||
                previousObserved == observed && !ObserverTerminal(execution))) continue;
            await using var update = ObserverCommand(connection, transaction, """
                INSERT INTO observer_executions(observer_id,execution_id,observed_ticks,started_ticks,received_at_utc,body)
                VALUES($id,$execution,$observed,$started,$received,$body)
                ON CONFLICT(observer_id,execution_id) DO UPDATE SET observed_ticks=$observed,received_at_utc=$received,body=$body;
                """, ("$id", observerId), ("$execution", execution.LocalExecutionId), ("$observed", observed.UtcTicks),
                ("$started", execution.StartedAtUtc.UtcTicks), ("$received", now.ToString("O")), ("$body", JsonSerializer.Serialize(execution, ProjectJson)));
            await update.ExecuteNonQueryAsync(ct);
        }
        await using (var insert = ObserverCommand(connection, transaction, """
            INSERT INTO observer_reports(observer_id,kind,report_id,payload_hash,observed_ticks,received_at_utc,body)
            VALUES($id,$kind,$report,$hash,$observed,$received,$body);
            INSERT INTO observer_status(observer_id,last_received_at_utc) VALUES($id,$received)
            ON CONFLICT(observer_id) DO UPDATE SET last_received_at_utc=$received;
            """, ("$id", observerId), ("$kind", kind), ("$report", reportId), ("$hash", hash),
            ("$observed", observed.UtcTicks), ("$received", now.ToString("O")), ("$body", body)))
            await insert.ExecuteNonQueryAsync(ct);
        if (snapshot is not null && (currentSnapshotTicks is null || observed.UtcTicks > currentSnapshotTicks))
        {
            // Normalize against execution projections so even a newer snapshot cannot undo a terminal outcome.
            var projections = await ReadObserverExecutionsAsync(connection, transaction, observerId, ct);
            snapshot = NormalizeObserverSnapshot(snapshot, projections, currentSnapshot);
            await using var update = ObserverCommand(connection, transaction, """
                UPDATE observer_status SET snapshot_json=$body,snapshot_observed_ticks=$observed,snapshot_received_at_utc=$received WHERE observer_id=$id;
                """, ("$body", JsonSerializer.Serialize(snapshot, ProjectJson)), ("$observed", observed.UtcTicks),
                ("$received", now.ToString("O")), ("$id", observerId));
            await update.ExecuteNonQueryAsync(ct);
        }
        else if (currentSnapshot is not null)
        {
            // Persist event-driven enrichment before pruning. Otherwise evicting a terminal execution
            // could expose an older running summary still embedded in the latest snapshot.
            var projections = await ReadObserverExecutionsAsync(connection, transaction, observerId, ct);
            var normalized = NormalizeObserverSnapshot(currentSnapshot, projections);
            await using var update = ObserverCommand(connection, transaction,
                "UPDATE observer_status SET snapshot_json=$body WHERE observer_id=$id;",
                ("$body", JsonSerializer.Serialize(normalized, ProjectJson)), ("$id", observerId));
            await update.ExecuteNonQueryAsync(ct);
        }
        await PruneObserverReportsAsync(connection, transaction, observerId, ct);
        await transaction.CommitAsync(ct);
        return ObserverIngestionResult.Accepted;
    }

    internal static bool ObserverTerminal(ObserverExecution execution) => execution.Outcome is not null || execution.CompletedAtUtc is not null ||
        execution.Stage is "Completed" or "Blocked" or "Failed" or "IntegrationConflict" or "InfrastructureFailure" or "Cancelled" or "Superseded";

    private static ObserverStatusSnapshot NormalizeObserverSnapshot(ObserverStatusSnapshot snapshot, IReadOnlyList<ObserverExecutionObservation> executions,
        ObserverStatusSnapshot? previous = null) =>
        snapshot with { Executions = snapshot.Executions.Select(item => executions.FirstOrDefault(current =>
            current.Execution.LocalExecutionId == item.LocalExecutionId)?.Execution ??
            previous?.Executions.FirstOrDefault(current => current.LocalExecutionId == item.LocalExecutionId) ?? item).ToArray() };

    private static async Task PruneObserverReportsAsync(SqliteConnection connection, SqliteTransaction transaction, string observerId, CancellationToken ct)
    {
        long replayFloor = 0, executionFloor = 0;
        await using (var prune = ObserverCommand(connection, transaction, """
            DELETE FROM observer_reports WHERE rowid IN (SELECT rowid FROM observer_reports WHERE observer_id=$id ORDER BY rowid DESC LIMIT -1 OFFSET $limit)
            RETURNING observed_ticks;
            """, ("$id", observerId), ("$limit", ObserverHistoryLimit)))
        await using (var reader = await prune.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) replayFloor = Math.Max(replayFloor, reader.GetInt64(0));
        await using (var prune = ObserverCommand(connection, transaction, """
            DELETE FROM observer_executions WHERE rowid IN (SELECT rowid FROM observer_executions WHERE observer_id=$id
                ORDER BY observed_ticks DESC,execution_id DESC LIMIT -1 OFFSET $limit) RETURNING started_ticks;
            """, ("$id", observerId), ("$limit", ObserverExecutionLimit)))
        await using (var reader = await prune.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) executionFloor = Math.Max(executionFloor, reader.GetInt64(0));
        await using var update = ObserverCommand(connection, transaction, """
            UPDATE observer_status SET replay_before_ticks=MAX(replay_before_ticks,$replay),execution_before_ticks=MAX(execution_before_ticks,$execution) WHERE observer_id=$id;
            """, ("$replay", replayFloor), ("$execution", executionFloor), ("$id", observerId));
        await update.ExecuteNonQueryAsync(ct);
    }

    public async Task<ObserverInspection?> GetObserverInspectionAsync(string observerId, CancellationToken ct = default)
    {
        if (!ObserverProtocol.ValidIdentity(observerId)) return null;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        ObserverRegistration registration;
        ObserverStatusSnapshot? snapshot = null;
        DateTimeOffset? snapshotReceived = null, lastReceived = null;
        await using (var command = ObserverCommand(connection, transaction, """
            SELECT o.server_origin,o.accepted_at_utc,o.revoked_at_utc,s.snapshot_json,s.snapshot_received_at_utc,s.last_received_at_utc
            FROM observers o LEFT JOIN observer_status s ON s.observer_id=o.observer_id WHERE o.observer_id=$id;
            """, ("$id", observerId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            if (!reader.IsDBNull(3)) snapshot = JsonSerializer.Deserialize<ObserverStatusSnapshot>(reader.GetString(3), ProjectJson);
            registration = new(ObserverProtocol.Version, observerId, ObserverProtocol.Mode, reader.GetString(0),
                ObserverDate(reader.GetString(1)), !reader.IsDBNull(2) ? "revoked" : snapshot is null ? "awaiting-first-report" : "reporting");
            if (!reader.IsDBNull(4)) snapshotReceived = ObserverDate(reader.GetString(4));
            if (!reader.IsDBNull(5)) lastReceived = ObserverDate(reader.GetString(5));
        }
        var executions = await ReadObserverExecutionsAsync(connection, transaction, observerId, ct);
        await transaction.CommitAsync(ct);
        return new(registration, snapshot is null ? null : NormalizeObserverSnapshot(snapshot, executions), snapshotReceived, lastReceived, executions);
    }

    public async Task<IReadOnlyList<ObserverReportReceipt>> GetObserverHistoryAsync(string observerId, int limit = 50, CancellationToken ct = default)
    {
        if (!ObserverProtocol.ValidIdentity(observerId) || limit is < 1 or > ObserverHistoryLimit)
            throw new ArgumentException("Invalid observer history query.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = ObserverCommand(connection, null,
            "SELECT kind,report_id,observed_ticks,received_at_utc,body FROM observer_reports WHERE observer_id=$id ORDER BY rowid DESC LIMIT $limit;",
            ("$id", observerId), ("$limit", limit));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var reports = new List<ObserverReportReceipt>();
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(0);
            var body = reader.GetString(4);
            reports.Add(new(kind, reader.GetString(1), new(reader.GetInt64(2), TimeSpan.Zero), ObserverDate(reader.GetString(3)),
                kind == "snapshot" ? JsonSerializer.Deserialize<ObserverStatusSnapshot>(body, ProjectJson) : null,
                kind == "event" ? JsonSerializer.Deserialize<ObserverExecutionEvent>(body, ProjectJson) : null));
        }
        return reports;
    }

    private static async Task<IReadOnlyList<ObserverExecutionObservation>> ReadObserverExecutionsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string observerId, CancellationToken ct)
    {
        await using var command = ObserverCommand(connection, transaction,
            "SELECT body,observed_ticks,received_at_utc FROM observer_executions WHERE observer_id=$id ORDER BY observed_ticks DESC,execution_id DESC;", ("$id", observerId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var executions = new List<ObserverExecutionObservation>();
        while (await reader.ReadAsync(ct))
        {
            var execution = JsonSerializer.Deserialize<ObserverExecution>(reader.GetString(0), ProjectJson)
                ?? throw new InvalidDataException("Observer execution metadata is missing.");
            executions.Add(new(execution, new(reader.GetInt64(1), TimeSpan.Zero), ObserverDate(reader.GetString(2))));
        }
        return executions;
    }

    private static DateTimeOffset ObserverDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static SqliteCommand ObserverCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
