namespace CodexServer;

using System.Text.Json;
using CodexProvisioning;
using Microsoft.Data.Sqlite;

public sealed class WorkerUpdateRecoveryException(WorkerUpdateRecovery recovery) : InvalidOperationException("worker-update-recovery-readiness-required")
{
    public WorkerUpdateRecovery Recovery { get; } = recovery;
}

public sealed partial class SqliteRegistryStore
{
    public Task<WorkerUpdateOperation?> ReleaseWorkerRestartDrainAsync(string workerId, string operationId, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, operationId, "release-drain", null, cancellationToken);

    public Task<WorkerUpdateOperation?> RetryWorkerUpdateAsync(string workerId, string operationId, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, operationId, "retry", null, cancellationToken);

    public Task<WorkerUpdateOperation?> ReconcileWorkerUpdateAsync(string workerId, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, null, "reconcile", null, cancellationToken);

    public Task<WorkerUpdateOperation?> InspectWorkerUpdateAsync(string workerId, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, null, "inspect", null, cancellationToken);

    public Task<WorkerUpdateOperation?> GetWorkerUpdateAsync(string workerId, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, null, "observe", null, cancellationToken);

    public async Task<WorkerUpdateOperation> RequestWorkerUpdateAsync(string workerId, string operationId,
        CancellationToken cancellationToken = default, bool restart = false) =>
        await ChangeWorkerUpdateAsync(workerId, operationId, restart ? "restart" : "request", null, cancellationToken)
        ?? throw new InvalidOperationException("worker-unavailable");

    public Task<WorkerUpdateOperation?> AdvanceWorkerUpdateAsync(string workerId, string? operationId = null,
        string? version = null, bool fail = false, bool verified = false, CancellationToken cancellationToken = default) =>
        ChangeWorkerUpdateAsync(workerId, operationId, fail ? "fail" : verified ? "verified" : version is null ? "poll" : "start", version, cancellationToken);

    private async Task<WorkerUpdateOperation?> ChangeWorkerUpdateAsync(string workerId, string? operationId,
        string action, string? version, CancellationToken ct)
    {
        if (!WorkerUpdateProtocol.ValidId(workerId) ||
            action is "release-drain" or "request" or "restart" or "retry" or "start" or "fail" or "verified" && !WorkerUpdateProtocol.ValidId(operationId ?? string.Empty) ||
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
        if (action != "inspect") await command.ExecuteNonQueryAsync(ct);
        if (action is "request" or "restart" or "retry")
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
        var leases = Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        var now = _timeProvider.GetUtcNow();
        var heartbeatFresh = heartbeat is not null && now - seen <= _staleAfter;
        var fresh = heartbeatFresh && heartbeat?.LifecycleState != WorkerLifecycleStates.Stopped;
        var idle = fresh && assignments == 0 && leases == 0 && heartbeat?.ActiveExecutions == 0;
        if (operation is { State: not ("succeeded" or "failed") } && now >= operation.DeadlineUtc)
            operation = operation with { State = "failed", Diagnostic = "timeout-inspect-service-and-journal-before-retry" };
        var recovery = RecoveryReadiness(operation, heartbeat, seen, heartbeatFresh, assignments, leases, now);
        if (action == "inspect")
        {
            // Project an expired outcome without advancing or persisting lifecycle state.
            await transaction.CommitAsync(ct);
            return operation is null ? null : operation with { Recovery = recovery };
        }
        if (action == "release-drain")
        {
            if (operation?.OperationId != operationId) throw new InvalidOperationException("worker-operation-changed");
            if (operation is { Restart: true, State: "failed", DrainReleasedAtUtc: not null })
            {
                await transaction.CommitAsync(ct);
                return operation;
            }
            if (!recovery.ReleaseDrainEligible) throw new WorkerUpdateRecoveryException(recovery);
            if (operation is null) throw new InvalidOperationException("worker-update-unavailable");
            if (operation.OwnsDrain && policy == WorkerSchedulingPolicy.Draining)
            {
                command.CommandText = "UPDATE workers SET scheduling_policy=$prior WHERE worker_id=$worker;";
                command.Parameters.AddWithValue("$prior", operation.PriorPolicy);
                await command.ExecuteNonQueryAsync(ct);
            }
            // Retain the failed outcome and diagnostics; this changes no Worker observation.
            operation = operation with { DrainReleasedAtUtc = now, OwnsDrain = false };
        }
        if (action == "retry")
        {
            if (!recovery.RetryEligible) throw new WorkerUpdateRecoveryException(recovery);
            if (operation is null) throw new InvalidOperationException("worker-update-unavailable");
            // A fresh operation identity fences late installer reports from the failed attempt.
            // Retain target, capability requirements and drain ownership across the retry.
            operation = operation with { OperationId = operationId ?? throw new InvalidDataException("operation-id-required"),
                RetryOfOperationId = operation.OperationId, State = "waiting-for-idle", RequestedAtUtc = now, DeadlineUtc = now.AddHours(24),
                StartedAtUtc = null, InstallerVerified = false, Recovery = null, Diagnostic = "retry-managed-update" };
        }
        if (action == "reconcile" && operation?.State == "succeeded")
        {
            await transaction.CommitAsync(ct);
            return operation;
        }
        if (action == "reconcile")
        {
            if (operation?.DrainReleasedAtUtc is not null || !recovery.Eligible) throw new WorkerUpdateRecoveryException(recovery);
            if (operation is null || heartbeat is null) throw new InvalidOperationException("worker-update-unavailable");
            // Explicit operator reconciliation only, after inspecting the quiescent local updater.
            // This never dispatches or repeats installation and retains the prior failure diagnostic.
            operation = operation with { State = "updating", InstallerVerified = true, ExpectedVersion = operation.ExpectedVersion ?? operation.OriginalVersion,
                StartedAtUtc = operation.StartedAtUtc ?? operation.RequestedAtUtc.AddTicks(-1), DeadlineUtc = now.AddMinutes(20) };
        }
        if (operation is { State: "updating", StartedAtUtc: not null } && heartbeat is not null && seen > operation.StartedAtUtc &&
            (heartbeat.WorkerVersion == operation.ExpectedVersion || heartbeat.LifecycleState == WorkerLifecycleStates.Stopped))
            operation = operation with { State = "reconnecting" };
        if (operation is { State: "updating" or "reconnecting", ExpectedVersion: not null, StartedAtUtc: not null, InstallerVerified: true } && (!operation.Restart || !string.IsNullOrEmpty(heartbeat?.InstanceId) && heartbeat.InstanceId != operation.OriginalInstanceId) && idle && seen > operation.StartedAtUtc &&
            heartbeat?.WorkerVersion == operation.ExpectedVersion && heartbeat?.LifecycleState == WorkerLifecycleStates.Running &&
            (heartbeat.CapabilityInventory is null || CapabilityCatalog.ExecutionReadiness(heartbeat.CapabilityInventory).Available) &&
            heartbeat.ConfigurationSynchronization == "synchronized" && heartbeat.ContractVersion is 1 or 2 &&
            (operation.RequiredCapabilities ?? []).All(required => heartbeat.Capabilities.Any(current =>
                 $"{current.Type}/{current.Name}/{current.Scope}" == required)))
        {
            operation = operation with { State = "succeeded", Diagnostic = action == "reconcile" ? $"reconciled:{heartbeat.WorkerVersion}:{operation.Diagnostic}" : "ready" };
            if (operation.OwnsDrain && policy == WorkerSchedulingPolicy.Draining)
            {
                command.CommandText = "UPDATE workers SET scheduling_policy=$policy WHERE worker_id=$worker;";
                command.Parameters.AddWithValue("$policy", operation.PriorPolicy);
                await command.ExecuteNonQueryAsync(ct);
                command.Parameters.RemoveAt("$policy");
            }
        }
        if ((action is "request" or "restart") && (operation is null || (operation.State == "succeeded" || operation is { Restart: true, State: "failed", DrainReleasedAtUtc: not null }) && operation.OperationId != operationId))
        {
            if (!fresh || heartbeat is null || heartbeat.LifecycleState is not (WorkerLifecycleStates.Running or WorkerLifecycleStates.NotReady or WorkerLifecycleStates.Ready or WorkerLifecycleStates.Draining or WorkerLifecycleStates.DrainRequested or WorkerLifecycleStates.Drained) || !WorkerUpdateProtocol.ValidVersion(heartbeat.WorkerVersion) ||
                !heartbeat.Capabilities.Any(c => c.Type == "protocol" && c.Name == (action == "restart" ? WorkerUpdateProtocol.RestartCapability : WorkerUpdateProtocol.Capability)))
                throw new InvalidOperationException("worker-update-unavailable");
            if (action == "restart" && (heartbeat.ConfigurationSynchronization != "synchronized" ||
                heartbeat.LifecycleState != WorkerLifecycleStates.Running ||
                heartbeat.CapabilityInventory is { } restartInventory && !CapabilityCatalog.ExecutionReadiness(restartInventory).Available))
                throw new InvalidOperationException("worker-restart-unavailable");
            var originallyDraining = heartbeat.LifecycleState is WorkerLifecycleStates.Draining or WorkerLifecycleStates.DrainRequested or WorkerLifecycleStates.Drained;
            var prior = originallyDraining && policy == WorkerSchedulingPolicy.Enabled ? WorkerSchedulingPolicy.Draining : policy;
            operation = new(operationId ?? throw new InvalidDataException("operation-id-required"), workerId,
                "waiting-for-idle", now, now.AddHours(24), prior, policy == WorkerSchedulingPolicy.Enabled, heartbeat.WorkerVersion,
                RequiredCapabilities: heartbeat.Capabilities.Select(c => $"{c.Type}/{c.Name}/{c.Scope}").ToArray(),
                Restart: action == "restart", OriginalInstanceId: heartbeat.InstanceId);
            if (operation.Restart && string.IsNullOrEmpty(operation.OriginalInstanceId)) throw new InvalidOperationException("worker-restart-unavailable");
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
            if (!operation.Restart && operation.ExpectedVersion is not null &&
                (!WorkerUpdateProtocol.ValidVersion(heartbeat?.WorkerVersion) || version != operation.ExpectedVersion || heartbeat?.WorkerVersion != version &&
                    !WorkerUpdateProtocol.NeedsUpdate(heartbeat?.WorkerVersion ?? operation.OriginalVersion, version ?? throw new InvalidDataException("version-required"))))
                throw new InvalidOperationException("worker-update-target-changed-use-versioned-installer");
            var update = operation.Restart || WorkerUpdateProtocol.NeedsUpdate(heartbeat?.WorkerVersion ?? operation.OriginalVersion, version ?? throw new InvalidDataException("version-required"));
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
        if (operation is not null && action == "fail" && operation.OperationId == operationId && operation.State != "succeeded" && operation.DrainReleasedAtUtc is null)
            operation = operation with { State = "failed", Diagnostic = operation.Restart ? "restart-failed-inspect-service-and-journal-before-retry" : "update-failed-inspect-service-and-journal-before-retry" };
        if (operation is not null)
        {
            command.CommandText = "INSERT INTO worker_updates(operation_id,worker_id,operation_json) VALUES($operation,$worker,$json) ON CONFLICT(operation_id) DO UPDATE SET operation_json=excluded.operation_json;";
            command.Parameters.AddWithValue("$operation", operation.OperationId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(operation));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return action == "poll" && operation is not { Restart: true, State: "failed", DrainReleasedAtUtc: not null } && (operation?.State is not ("waiting-for-idle" or "updating" or "reconnecting") || !idle) ? null : operation;
    }
    private static WorkerUpdateRecovery RecoveryReadiness(WorkerUpdateOperation? operation,
        WorkerHeartbeatRequest? heartbeat, DateTimeOffset seen, bool fresh, int assignments, int leases, DateTimeOffset now)
    {
        var recovery = new WorkerUpdateRecovery(operation is { State: not "succeeded", DrainReleasedAtUtc: null }, now,
        [
            new("failed-operation", operation?.State == "failed", "Operation has failed or timed out.", "Wait for the operation to finish or its deadline; inspect the service and journal."),
            new("fresh-heartbeat", fresh, "Worker heartbeat is fresh.", "Restore the Worker connection and wait for a new heartbeat."),
            new("running-lifecycle", heartbeat?.LifecycleState == WorkerLifecycleStates.Running, "Worker lifecycle is running.", "Inspect the Worker service and resolve its lifecycle state."),
            new("execution-readiness", heartbeat is not null && (heartbeat.CapabilityInventory is null || CapabilityCatalog.ExecutionReadiness(heartbeat.CapabilityInventory).Available), "Execution capabilities are ready.", "Resolve execution capability checks before reconciling this operation."),
            new("configuration-synchronized", heartbeat?.ConfigurationSynchronization == "synchronized", "Managed configuration is synchronized.", "Resolve configuration errors and wait for synchronization."),
            new("restart-instance", operation?.Restart != true || !string.IsNullOrEmpty(heartbeat?.InstanceId) && heartbeat.InstanceId != operation.OriginalInstanceId, "A replacement Worker instance has reconnected.", "Use Release restart drain to retain the failed restart outcome when no replacement instance was verified."),
            new("expected-version", heartbeat is not null && heartbeat.WorkerVersion == (operation?.ExpectedVersion ?? operation?.OriginalVersion),
                "Worker reports the expected version.",
                $"Expected {operation?.ExpectedVersion ?? "unavailable"}; original {operation?.OriginalVersion ?? "unavailable"}; reported {heartbeat?.WorkerVersion ?? "unavailable"}. " +
                (operation?.Restart == true
                    ? "Use Release restart drain to retain the failed outcome if the expected restart version cannot be verified."
                    : "Use Retry managed update when available. Otherwise, after confirming the updater is quiescent, use the documented versioned Worker installer for the expected release, then refresh and reconcile.")),
            new("supported-protocol", heartbeat?.ContractVersion is 1 or 2, "Worker protocol is supported.", "Install a Worker release supporting the Server protocol, then refresh recovery checks."),
            new("no-active-assignments", assignments == 0, "No active Server assignments.", "Allow assigned work to finish or reconcile it through execution recovery."),
            new("no-active-leases", leases == 0, "No active Server leases.", "Wait for active leases to finish or reconcile uncertain executions."),
            new("no-active-executions", heartbeat?.ActiveExecutions == 0, "Worker reports no active executions.", "Wait for executions to finish and a fresh idle heartbeat."),
            new("required-capabilities", heartbeat is not null && heartbeat.Capabilities.Any(c => c.Type == "protocol" && c.Name == (operation?.Restart == true ? WorkerUpdateProtocol.RestartCapability : WorkerUpdateProtocol.Capability)) && (operation?.RequiredCapabilities ?? []).All(required => heartbeat.Capabilities.Any(current => $"{current.Type}/{current.Name}/{current.Scope}" == required)), "Required capabilities are present.", "Restore capabilities reported before the operation."),
            new("post-operation-heartbeat", operation is not null && seen > (operation.StartedAtUtc ?? (operation.Restart ? operation.RequestedAtUtc : operation.RequestedAtUtc.AddTicks(-1))), "Heartbeat was received after the operation started.", "Wait for a new Worker heartbeat.")
        ], heartbeat?.WorkerVersion);
        return recovery with { ReleaseDrainEligible = operation is { Restart: true, State: "failed", DrainReleasedAtUtc: null } &&
            recovery.Checks.Where(check => check.Code is "fresh-heartbeat" or "no-active-assignments" or "no-active-leases" or "no-active-executions" or "post-operation-heartbeat").All(check => check.Satisfied),
            RetryEligible = operation is { State: "failed", Restart: false, ExpectedVersion: not null } &&
            WorkerUpdateProtocol.ValidVersion(operation.ExpectedVersion) && WorkerUpdateProtocol.ValidVersion(heartbeat?.WorkerVersion) &&
            WorkerUpdateProtocol.NeedsUpdate(heartbeat?.WorkerVersion ?? string.Empty, operation.ExpectedVersion) &&
            recovery.Checks.Where(check => check.Code != "expected-version").All(check => check.Satisfied) };
    }

}
