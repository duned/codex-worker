namespace CodexServer;

using System.Text.Json;
using CodexProvisioning;
using Microsoft.Data.Sqlite;

public sealed partial class SqliteRegistryStore
{
    public Task<WorkerUpdateOperation?> ReconcileWorkerUpdateAsync(string workerId, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, null, "reconcile", null, cancellationToken);

    public Task<WorkerUpdateOperation?> GetWorkerUpdateAsync(string workerId, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, null, "observe", null, cancellationToken);

    public async Task<WorkerUpdateOperation> RequestWorkerUpdateAsync(string workerId, string operationId,
        CancellationToken cancellationToken = default) =>
        await ChangeWorkerUpdateAsync(workerId, operationId, "request", null, cancellationToken)
        ?? throw new InvalidOperationException("worker-unavailable");

    public Task<WorkerUpdateOperation?> AdvanceWorkerUpdateAsync(string workerId, string? operationId = null,
        string? version = null, bool fail = false, bool verified = false, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, operationId, fail ? "fail" : verified ? "verified" : version is null ? "poll" : "start", version, cancellationToken);

    private async Task<WorkerUpdateOperation?> ChangeWorkerUpdateAsync(string workerId, string? operationId,
        string action, string? version, CancellationToken ct)
    {
        if (!WorkerUpdateProtocol.ValidId(workerId) ||
            action is "request" or "start" or "fail" or "verified" && !WorkerUpdateProtocol.ValidId(operationId ?? string.Empty) ||
            operationId is not null && !WorkerUpdateProtocol.ValidId(operationId) ||
            version is not null && !WorkerUpdateProtocol.ValidVersion(version))
            throw new InvalidDataException("invalid-worker-update");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$worker", workerId);
        // Same database write gate as assignment admission and scheduling policy changes.
        command.CommandText = "UPDATE workers SET worker_id=worker_id WHERE worker_id=$worker;";
        await command.ExecuteNonQueryAsync(ct);
        if (action == "request")
        {
            command.CommandText = "SELECT operation_json FROM worker_updates WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operationId);
            var retained = await command.ExecuteScalarAsync(ct) as string;
            command.Parameters.RemoveAt("$operation");
            if (retained is not null)
            {
                var prior = JsonSerializer.Deserialize<WorkerUpdateOperation>(retained) ?? throw new InvalidDataException("invalid-retained-worker-update");
                if (prior.WorkerId != workerId) throw new InvalidOperationException("operation-id-conflict");
                await transaction.CommitAsync(ct);
                return prior;
            }
        }
        command.CommandText = "SELECT operation_json FROM worker_updates WHERE worker_id=$worker ORDER BY rowid DESC LIMIT 1;";
        var json = await command.ExecuteScalarAsync(ct) as string;
        var operation = json is null ? null : JsonSerializer.Deserialize<WorkerUpdateOperation>(json)
            ?? throw new InvalidDataException("invalid-retained-worker-update");
        command.CommandText = "SELECT heartbeat_json,last_seen_at_utc,scheduling_policy FROM workers WHERE worker_id=$worker AND registration_json IS NOT NULL;";
        WorkerHeartbeatRequest? heartbeat;
        DateTimeOffset seen;
        string policy;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            heartbeat = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<WorkerHeartbeatRequest>(reader.GetString(0));
            seen = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
            policy = reader.GetString(2);
        }
        command.CommandText = "SELECT COUNT(*) FROM execution_requests WHERE assigned_worker_id=$worker AND state IN ('Assigned','Running');";
        var assignments = Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "SELECT COUNT(*) FROM execution_leases WHERE worker_id=$worker AND state='Active';";
        assignments += Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        var now = _timeProvider.GetUtcNow();
        var fresh = heartbeat is not null && now - seen <= _staleAfter && heartbeat.LifecycleState != WorkerLifecycleStates.Stopped;
        var idle = fresh && assignments == 0 && heartbeat?.ActiveExecutions == 0;
        if (operation is { State: not ("succeeded" or "failed") } && now >= operation.DeadlineUtc)
            operation = operation with { State = "failed", Diagnostic = "timeout-inspect-service-and-journal-before-retry" };
        if (action == "reconcile")
        {
            if (operation?.State != "failed" || !idle || heartbeat?.WorkerVersion != (operation.ExpectedVersion ?? operation.OriginalVersion) ||
                heartbeat.LifecycleState != WorkerLifecycleStates.Running || heartbeat.ConfigurationSynchronization != "synchronized")
                throw new InvalidOperationException("worker-update-recovery-readiness-required");
            // Explicit operator reconciliation only, after inspecting the quiescent local updater.
            // This never dispatches or repeats installation and retains the prior failure diagnostic.
            operation = operation with { State = "updating", InstallerVerified = true,
                ExpectedVersion = heartbeat.WorkerVersion, StartedAtUtc = operation.StartedAtUtc ?? operation.RequestedAtUtc.AddTicks(-1), DeadlineUtc = now.AddMinutes(20) };
        }
        if (operation is { State: "updating", StartedAtUtc: not null } && heartbeat is not null && seen > operation.StartedAtUtc &&
            (heartbeat.WorkerVersion == operation.ExpectedVersion || heartbeat.LifecycleState == WorkerLifecycleStates.Stopped))
            operation = operation with { State = "reconnecting" };
        if (operation is { State: "updating" or "reconnecting", ExpectedVersion: not null, StartedAtUtc: not null, InstallerVerified: true } && fresh && seen > operation.StartedAtUtc &&
            heartbeat?.WorkerVersion == operation.ExpectedVersion && heartbeat.LifecycleState == WorkerLifecycleStates.Running &&
            heartbeat.ConfigurationSynchronization == "synchronized" &&
            (operation.RequiredCapabilities ?? []).All(required => heartbeat.Capabilities.Any(current =>
                 $"{current.Type}/{current.Name}/{current.Scope}" == required)))
        {
            operation = operation with { State = "succeeded", Diagnostic = action == "reconcile" ? $"reconciled:{operation.Diagnostic}" : "ready" };
            if (operation.OwnsDrain && policy == WorkerSchedulingPolicy.Draining)
            {
                command.CommandText = "UPDATE workers SET scheduling_policy=$policy WHERE worker_id=$worker;";
                command.Parameters.AddWithValue("$policy", operation.PriorPolicy);
                await command.ExecuteNonQueryAsync(ct);
                command.Parameters.RemoveAt("$policy");
            }
        }
        if (action == "request" && (operation is null || operation.State == "succeeded" && operation.OperationId != operationId))
        {
            if (!fresh || heartbeat is null || heartbeat.LifecycleState is not (WorkerLifecycleStates.Running or WorkerLifecycleStates.NotReady or WorkerLifecycleStates.Ready or WorkerLifecycleStates.Draining or WorkerLifecycleStates.DrainRequested or WorkerLifecycleStates.Drained) || !WorkerUpdateProtocol.ValidVersion(heartbeat.WorkerVersion) ||
                !heartbeat.Capabilities.Any(c => c.Type == "protocol" && c.Name == WorkerUpdateProtocol.Capability))
                throw new InvalidOperationException("worker-update-unavailable");
            var originallyDraining = heartbeat.LifecycleState is WorkerLifecycleStates.Draining or WorkerLifecycleStates.DrainRequested or WorkerLifecycleStates.Drained;
            var prior = originallyDraining && policy == WorkerSchedulingPolicy.Enabled ? WorkerSchedulingPolicy.Draining : policy;
            operation = new(operationId ?? throw new InvalidDataException("operation-id-required"), workerId,
                "waiting-for-idle", now, now.AddHours(24), prior, policy == WorkerSchedulingPolicy.Enabled, heartbeat.WorkerVersion,
                RequiredCapabilities: heartbeat.Capabilities.Select(c => $"{c.Type}/{c.Name}/{c.Scope}").ToArray());
            if (policy == WorkerSchedulingPolicy.Enabled)
            {
                command.CommandText = "UPDATE workers SET scheduling_policy='Draining' WHERE worker_id=$worker;";
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        if (operation is not null && action == "start")
        {
            if (operation.OperationId != operationId || operation.State != "waiting-for-idle" || !idle)
                throw new InvalidOperationException("worker-update-not-authorized");
            var update = WorkerUpdateProtocol.NeedsUpdate(heartbeat?.WorkerVersion ?? operation.OriginalVersion, version ?? throw new InvalidDataException("version-required"));
            operation = operation with { ExpectedVersion = version, State = update ? "updating" : "succeeded",
                StartedAtUtc = now, DeadlineUtc = now.AddMinutes(20), Diagnostic = update ? null : "already-current" };
            if (!update && operation.PriorPolicy == WorkerSchedulingPolicy.Enabled &&
                (heartbeat?.LifecycleState != WorkerLifecycleStates.Running || heartbeat.ConfigurationSynchronization != "synchronized"))
                operation = operation with { State = "reconnecting", InstallerVerified = true,
                    ExpectedVersion = heartbeat?.WorkerVersion ?? operation.OriginalVersion, Diagnostic = "already-current-waiting-for-readiness" };
            if (operation.State == "succeeded" && operation.OwnsDrain && policy == WorkerSchedulingPolicy.Draining)
            {
                command.CommandText = "UPDATE workers SET scheduling_policy=$policy WHERE worker_id=$worker;";
                command.Parameters.AddWithValue("$policy", operation.PriorPolicy);
                await command.ExecuteNonQueryAsync(ct);
                command.Parameters.RemoveAt("$policy");
            }
        }
        if (operation is { State: "updating" or "reconnecting" } && action == "verified" && operation.OperationId == operationId)
            operation = operation with { InstallerVerified = true };
        if (operation is not null && action == "fail" && operation.OperationId == operationId && operation.State != "succeeded")
            operation = operation with { State = "failed", Diagnostic = "update-failed-inspect-service-and-journal-before-retry" };
        if (operation is not null)
        {
            command.CommandText = "INSERT INTO worker_updates(operation_id,worker_id,operation_json) VALUES($operation,$worker,$json) ON CONFLICT(operation_id) DO UPDATE SET operation_json=excluded.operation_json;";
            command.Parameters.AddWithValue("$operation", operation.OperationId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(operation));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return action == "poll" && (operation?.State is not ("waiting-for-idle" or "updating" or "reconnecting") || !idle) ? null : operation;
    }
}
