namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Text.Json;

public sealed record ManagedConflictPreview(string ExecutionId, string? WorkerId,
    IntegrationRecoveryCandidate? Candidate, bool Eligible, string Reason, string? ReasonCode = null);
public sealed record ManagedConflictRetry(string OperationId, string WorkerId, IntegrationRecoveryCandidate Candidate);

public sealed partial class SqliteRegistryStore
{
    private static bool ValidRecoveryCommit(string? commit) => commit is { Length: 40 or 64 } &&
        commit.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Short-lived node observations are previews, never mutation authority. After a Server restart
    // the original Worker must advertise fresh evidence again. Requests/leases use the durable registry.
    private readonly object _managedConflictGate = new();
    private readonly Dictionary<(string Worker, string Source), (IntegrationRecoveryCandidate Candidate, DateTimeOffset Seen)> _managedConflicts = [];

    private void ObserveManagedConflicts(WorkerAssignmentRequest request)
    {
        lock (_managedConflictGate)
        {
            foreach (var key in _managedConflicts.Keys.Where(key => key.Worker == request.WorkerId).ToArray())
                _managedConflicts.Remove(key);
            foreach (var candidate in request.IntegrationRecoveries ?? [])
            {
                if (candidate.Kind != "Integration") continue;
                if (_managedConflicts.Count >= 1024)
                    _managedConflicts.Remove(_managedConflicts.MinBy(pair => pair.Value.Seen).Key);
                _managedConflicts[(request.WorkerId, candidate.ServerExecutionId)] = (candidate, _timeProvider.GetUtcNow());
            }
        }
    }

    public async Task<ManagedConflictPreview> InspectManagedConflictAsync(string executionId, CancellationToken ct = default)
    {
        var source = await GetExecutionAsync(executionId, ct);
        ManagedConflictPreview Refuse(string reason, IntegrationRecoveryCandidate? candidate = null, string? reasonCode = null) =>
            new(executionId, source?.AssignedWorkerId, candidate, false, reason, reasonCode);
        if (source is null) return Refuse("Original Server execution is missing.");
        if (source.State != "Failed" || source.FailureClassification != "IntegrationConflict" || !source.Recoverable || source.RecoveryState is not null)
            return Refuse("A settled recoverable integration conflict is required; reconcile uncertain outcomes first.");
        var worker = source.AssignedWorkerId is null ? null : await GetWorkerAsync(source.AssignedWorkerId, ct);
        if (worker is null || worker.Availability != "online" || worker.SchedulingPolicy != WorkerSchedulingPolicy.Enabled)
            return Refuse("Original Worker must be online with scheduling enabled.");
        if (worker.SchedulingBlocker is { } blocker) return Refuse($"Worker scheduling is blocked: {blocker}. Inspect Worker readiness.");
        var project = await GetProjectAsync(source.ProjectId, ct);
        if (project is null || !project.Enabled) return Refuse("Enable the original project before requesting recovery.");
        if (!WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(project, source.WorkReference.Selection.Engine),
                worker.Capabilities, worker.CapabilityInventory).IsEligible)
            return Refuse("Original Worker does not meet current project execution requirements.");
        IntegrationRecoveryCandidate? evidence;
        lock (_managedConflictGate)
            evidence = _managedConflicts.TryGetValue((worker.WorkerId, executionId), out var observation) &&
                _timeProvider.GetUtcNow() - observation.Seen <= _staleAfter ? observation.Candidate : null;
        if (evidence is null) return Refuse("No fresh preserved-workspace preview. Wait for the original Worker's capacity/discovery poll, then refresh; inspect its recovery diagnostics if absent.");
        if (evidence.ProjectId != source.ProjectId || evidence.WorkerExecutionId != source.WorkerExecutionId || evidence.PreservedCommit is null)
            return Refuse("Original execution identity or exact preserved commit is unavailable; inspect Worker history.", evidence);
        if (evidence.RefusalReason is { } reason) return Refuse(reason, evidence, evidence.RefusalCode);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM execution_requests WHERE project_id=$project AND work_id=$issue AND id!=$source
                AND (state IN ('Queued','Assigned','Running','Completed') OR recovery_state='LeaseExpiredUncertain'));
            """;
        command.Parameters.AddWithValue("$project", source.ProjectId);
        command.Parameters.AddWithValue("$issue", source.WorkReference.Id);
        command.Parameters.AddWithValue("$source", source.Id);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 0)
            return Refuse("Another queued, active, completed or uncertain execution owns this Issue; inspect it first.", evidence);
        return new(executionId, worker.WorkerId, evidence, true,
            "Eligible for one bounded resolution attempt. The Worker rechecks Issue intent, exact workspace identity and current lease before mutation.");
    }

    public async Task<ExecutionRequest> RetryManagedConflictAsync(string executionId, ManagedConflictRetry request, CancellationToken ct = default)
    {
        if (request is null || !Guid.TryParseExact(request.OperationId, "N", out _) ||
            !Guid.TryParseExact(request.WorkerId, "N", out _) || request.Candidate is null ||
            request.Candidate.ServerExecutionId != executionId || request.Candidate.Kind != "Integration" ||
            !Printable(request.Candidate.ProjectId, 80) || !Guid.TryParse(request.Candidate.WorkerExecutionId, out _) ||
            !ValidRecoveryCommit(request.Candidate.IntegrationBase) || !ValidRecoveryCommit(request.Candidate.PreservedCommit) ||
            request.Candidate.RefusalReason is not null || request.Candidate.RefusalCode is not null)
            throw new InvalidDataException("Invalid explicit conflict recovery scope.");
        // Stable operation ID is also the retry execution ID. Lost replies and restart cannot
        // enqueue another attempt; returning a prior result does not require a live Worker.
        var prior = await ExistingManagedConflictRetryAsync(executionId, request, ct);
        if (prior is not null) return prior;
        var preview = await InspectManagedConflictAsync(executionId, ct);
        if (!preview.Eligible || preview.WorkerId != request.WorkerId || preview.Candidate != request.Candidate)
        {
            prior = await ExistingManagedConflictRetryAsync(executionId, request, ct);
            if (prior is not null) return prior;
            throw new InvalidOperationException(preview.Eligible ? "Preview changed; inspect again before requesting recovery." : preview.Reason);
        }
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Same admission serialization and reservation checks as automatic recovery.
        command.CommandText = "UPDATE workers SET worker_id=worker_id WHERE worker_id=$worker;";
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        await command.ExecuteNonQueryAsync(ct);
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM execution_requests WHERE id=$id;";
        command.Parameters.AddWithValue("$id", request.OperationId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            await transaction.CommitAsync(ct);
            return await ExistingManagedConflictRetryAsync(executionId, request, ct)
                ?? throw new InvalidOperationException("Recovery request disappeared.");
        }
        var projects = await ReadProjectsByIdAsync(command, ct);
        if (!projects.TryGetValue(request.Candidate.ProjectId, out var project))
            throw new InvalidOperationException("Project is unavailable.");
        await QueueIntegrationRecoveriesAsync(command,
            new(request.WorkerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 }, [request.Candidate]),
            new Dictionary<string, CentralProject> { [project.Id] = project }, ct, request.OperationId);
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM execution_requests WHERE id=$id;";
        command.Parameters.AddWithValue("$id", request.OperationId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Recovery ownership changed or another attempt exists; refresh execution history.");
        await transaction.CommitAsync(ct);
        return await GetExecutionAsync(request.OperationId, ct) ?? throw new InvalidOperationException("Queued recovery is missing.");
    }

    private async Task<ExecutionRequest?> ExistingManagedConflictRetryAsync(string executionId, ManagedConflictRetry request, CancellationToken ct)
    {
        var prior = await GetExecutionAsync(request.OperationId, ct);
        if (prior is null) return null;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT metadata_json FROM execution_metadata WHERE execution_id=$id AND worker_id=$worker AND state='IntegrationRecovery';";
        command.Parameters.AddWithValue("$id", request.OperationId);
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        var json = await command.ExecuteScalarAsync(ct) as string;
        var metadata = json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (prior.RetryOfExecutionId != executionId || metadata?.GetValueOrDefault("explicitIntegrationRecovery") != request.OperationId ||
            metadata.GetValueOrDefault("explicitRecoveryCandidate") != JsonSerializer.Serialize(request.Candidate))
            throw new InvalidOperationException("Operation ID already belongs to another request.");
        return prior;
    }
}
