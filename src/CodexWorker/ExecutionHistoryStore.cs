using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexWorker;

/// <summary>A durable snapshot of one Issue execution. Raw process output is intentionally excluded.</summary>
public sealed record ExecutionHistoryEntry(
    Guid ExecutionId,
    string Project,
    string Repository,
    int IssueNumber,
    string IssueTitle,
    string FeatureBranch,
    string BaseBranch,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string State,
    long? DurationMilliseconds,
    string? ImplementationSummary,
    string? ValidationOutcome,
    int RepairCount,
    IReadOnlyList<ValidationRepairRecord> Repairs,
    string? CommitSha,
    string? IntegrationBranch,
    string? CompletedBranch,
    string? FailureReason,
    string? RecoveryState = null,
    string? RecoveryBaseCommit = null,
    string? RecoveryStatus = null,
    Guid? RetryOfExecutionId = null,
    int AttemptNumber = 1,
    bool Resumed = false,
    DateTimeOffset? RecoveryExpiresAtUtc = null,
    string? ServerExecutionId = null,
    string? AssignmentId = null,
    long? OwnershipGeneration = null,
    string? EffectiveModel = null,
    string? EffectiveEffort = null,
    string? ReportingFailure = null,
    string? IntegrationRecoveryAttemptBase = null,
    Guid? IntegrationRecoveryClaim = null,
    string? OriginalIssueBody = null,
    bool ModelSelectedByCli = false,
    CodexInterruptionRecovery? CodexRecovery = null,
    string? CompletionJson = null);

/// <summary>Local, single-worker SQLite history with an SQLite user_version migration sequence.</summary>
public sealed class ExecutionHistoryStore : IDisposable
{
    private const int CurrentSchemaVersion = 14;
    private readonly string _connectionString;

    public ExecutionHistoryStore(string? databasePath = null)
    {
        databasePath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-worker", "codex-worker.db");
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        Initialize();
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public async Task CreateAsync(ExecutionHistoryEntry entry, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await InsertAsync(connection, null, entry, ct);
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction? transaction, ExecutionHistoryEntry entry, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO executions (execution_id, project, repository, issue_number, issue_title, feature_branch, base_branch,
                started_at_utc, completed_at_utc, state, duration_ms, implementation_summary, validation_outcome,
                repair_count, repairs_json, commit_sha, integration_branch, completed_branch, failure_reason,
                recovery_state, recovery_base_commit, recovery_status, retry_of_execution_id, attempt_number, resumed, recovery_expires_at_utc,
                server_execution_id, assignment_id, ownership_generation, effective_model, effective_effort, reporting_failure, integration_recovery_attempt_base, integration_recovery_claim, original_issue_body, model_selected_by_cli, codex_recovery_json, completion_json)
            VALUES ($id,$project,$repository,$number,$title,$feature,$base,$started,$completed,$state,$duration,$summary,$validation,
                $repairCount,$repairs,$sha,$integration,$completedBranch,$failure,$recoveryState,$recoveryBase,$recoveryStatus,
                $retryOf,$attempt,$resumed,$recoveryExpires,$serverExecutionId,$assignmentId,$ownershipGeneration,$effectiveModel,$effectiveEffort,$reportingFailure,$recoveryAttemptBase,$recoveryClaim,$originalIssueBody,$modelSelectedByCli,$codexRecovery,$completion)
            """;
        Bind(command, entry);
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (SqliteException ex) { throw PersistenceFailure("create execution history", ex); }
    }

    public async Task UpdateAsync(ExecutionHistoryEntry entry, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE executions SET completed_at_utc=COALESCE($completed,completed_at_utc), state=$state,
                duration_ms=COALESCE($duration,duration_ms), implementation_summary=COALESCE($summary,implementation_summary),
                validation_outcome=COALESCE($validation,validation_outcome),
                repair_count=MAX($repairCount,repair_count), repairs_json=CASE WHEN $repairCount > 0 THEN $repairs ELSE repairs_json END,
                commit_sha=COALESCE($sha,commit_sha), integration_branch=COALESCE($integration,integration_branch),
                completed_branch=COALESCE($completedBranch,completed_branch), failure_reason=COALESCE($failure,failure_reason),
                recovery_state=COALESCE($recoveryState,recovery_state), recovery_base_commit=COALESCE($recoveryBase,recovery_base_commit),
                recovery_status=COALESCE($recoveryStatus,recovery_status),
                recovery_expires_at_utc=COALESCE($recoveryExpires,recovery_expires_at_utc),
                retry_of_execution_id=COALESCE($retryOf,retry_of_execution_id), attempt_number=MAX($attempt,attempt_number),
                resumed=MAX($resumed,resumed), server_execution_id=COALESCE($serverExecutionId,server_execution_id),
                assignment_id=COALESCE($assignmentId,assignment_id), ownership_generation=COALESCE($ownershipGeneration,ownership_generation),
                effective_model=CASE WHEN model_selected_by_cli=1 THEN $effectiveModel ELSE COALESCE(effective_model,$effectiveModel) END,
                model_selected_by_cli=CASE WHEN effective_effort IS NULL THEN $modelSelectedByCli ELSE model_selected_by_cli END,
                effective_effort=COALESCE(effective_effort,$effectiveEffort),
                codex_recovery_json=COALESCE($codexRecovery,codex_recovery_json),
                reporting_failure=COALESCE($reportingFailure,reporting_failure)
                WHERE execution_id=$id AND completed_at_utc IS NULL
                    AND state NOT IN ('Completed','Blocked','Failed','IntegrationConflict','InfrastructureFailure','Cancelled')
            """;
        Bind(command, entry);
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new WorkerInfrastructureException($"Execution history row was not found for {entry.ExecutionId}.");
        }
        catch (SqliteException ex) { throw PersistenceFailure("update execution history", ex); }
    }

    /// <summary>Claims preserved implementation and creates its attempt in one durable transaction.
    /// The base fingerprint survives completion and restart; only a changed base or explicit operator action re-arms it.</summary>
    public async Task<bool> TryClaimIntegrationRecoveryAsync(ExecutionHistoryEntry source, ExecutionHistoryEntry attempt,
        string integrationBase, bool explicitRecovery, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE executions SET integration_recovery_attempt_base=$base, integration_recovery_claim=$claim
            WHERE execution_id=$id AND state='IntegrationConflict'
                AND (recovery_state IS NULL OR recovery_state='integration-conflict')
                AND integration_recovery_claim IS NULL
                AND ($explicit=1 OR integration_recovery_attempt_base IS NULL OR integration_recovery_attempt_base!=$base)
                AND NOT EXISTS (SELECT 1 FROM executions other WHERE other.project=executions.project
                    AND other.repository=executions.repository AND other.issue_number=executions.issue_number
                    AND other.execution_id!=executions.execution_id
                    AND (other.completed_at_utc IS NULL OR other.state='Completed'))
            """;
        command.Parameters.AddWithValue("$id", source.ExecutionId.ToString());
        command.Parameters.AddWithValue("$base", integrationBase);
        command.Parameters.AddWithValue("$claim", attempt.ExecutionId.ToString());
        command.Parameters.AddWithValue("$explicit", explicitRecovery);
        if (await command.ExecuteNonQueryAsync(ct) != 1) return false;
        await InsertAsync(connection, transaction, attempt, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Publishes verified legacy metadata once without changing the original outcome or lineage.</summary>
    internal async Task<bool> TryReconstructCodexRecoveryAsync(ExecutionHistoryEntry reconstructed, CancellationToken ct)
    {
        if (reconstructed.CodexRecovery is not { ResumeCount: 0 } snapshot ||
            snapshot.WorkspaceExecutionId != reconstructed.ExecutionId ||
            snapshot.WorkspaceAttemptNumber != reconstructed.AttemptNumber ||
            reconstructed.OriginalIssueBody is null || reconstructed.RecoveryBaseCommit is null || snapshot.SessionId is null)
            return false;
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE executions SET codex_recovery_json=$codexRecovery, recovery_base_commit=$recoveryBase,
                recovery_state='codex-interrupted'
            WHERE execution_id=$id AND codex_recovery_json IS NULL AND recovery_state='uncertain'
                AND state IN ('InfrastructureFailure','Cancelled') AND completed_at_utc IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM executions other WHERE other.project=executions.project
                    AND other.repository=executions.repository AND other.issue_number=executions.issue_number
                    AND other.execution_id!=executions.execution_id
                    AND (other.completed_at_utc IS NULL OR other.attempt_number>executions.attempt_number OR other.state='Completed'))
            """;
        Bind(command, reconstructed);
        try { return await command.ExecuteNonQueryAsync(ct) == 1; }
        catch (SqliteException ex) { throw PersistenceFailure("reconstruct Codex recovery", ex); }
    }

    /// <summary>Consumes one resume durably and prevents duplicate ownership across restart.</summary>
    public async Task<bool> TryClaimCodexRecoveryAsync(ExecutionHistoryEntry source, ExecutionHistoryEntry attempt, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE executions SET recovery_state='codex-resuming'
            WHERE execution_id=$id AND recovery_state='codex-interrupted' AND state IN ('InfrastructureFailure','Cancelled') AND completed_at_utc IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM executions other WHERE other.project=executions.project
                    AND other.repository=executions.repository AND other.issue_number=executions.issue_number
                    AND other.execution_id!=executions.execution_id
                    AND (other.completed_at_utc IS NULL OR other.attempt_number>executions.attempt_number OR other.state='Completed'))
            """;
        command.Parameters.AddWithValue("$id", source.ExecutionId.ToString());
        if (source.CodexRecovery is null || source.CodexRecovery.ResumeCount < 0 ||
            source.CodexRecovery.ResumeCount >= CodexInterruptionRecovery.MaximumResumes ||
            attempt.CodexRecovery != source.CodexRecovery with { ResumeCount = source.CodexRecovery.ResumeCount + 1 } ||
            attempt.RetryOfExecutionId != source.ExecutionId || attempt.AttemptNumber != source.AttemptNumber + 1 ||
            await command.ExecuteNonQueryAsync(ct) != 1) return false;
        await InsertAsync(connection, transaction, attempt, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Releases only this attempt's ownership; its consumed base remains recorded.</summary>
    public async Task FinishIntegrationRecoveryAsync(Guid sourceId, Guid attemptId, string? finalBase, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE executions SET integration_recovery_claim=NULL,
                integration_recovery_attempt_base=COALESCE($base,integration_recovery_attempt_base)
            WHERE execution_id=$id AND integration_recovery_claim=$claim
            """;
        command.Parameters.AddWithValue("$id", sourceId.ToString());
        command.Parameters.AddWithValue("$claim", attemptId.ToString());
        Add(command, "$base", finalBase);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Updates recovery metadata without changing the durable execution outcome.</summary>
    public async Task UpdateRecoveryAsync(Guid executionId, string state, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE executions SET recovery_state=$state WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", executionId.ToString());
        command.Parameters.AddWithValue("$state", state);
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new WorkerInfrastructureException($"Execution history row was not found for {executionId}.");
        }
        catch (SqliteException ex) { throw PersistenceFailure("update execution recovery metadata", ex); }
    }

    public async Task QuarantineCompletionAsync(Guid executionId, string reason, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE executions SET recovery_state='managed-completion-quarantined', reporting_failure=CASE WHEN recovery_state='managed-completion-quarantined' THEN reporting_failure WHEN reporting_failure IS NULL THEN $reason ELSE reporting_failure || char(10) || $reason END WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", executionId.ToString());
        command.Parameters.AddWithValue("$reason", reason);
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new WorkerInfrastructureException($"Execution history row was not found for {executionId}.");
        }
        catch (SqliteException ex) { throw PersistenceFailure("quarantine managed completion", ex); }
    }

    public async Task<string?> ReadServerReportDispositionAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT disposition FROM execution_server_report_dispositions WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", executionId.ToString());
        try { return await command.ExecuteScalarAsync(ct) as string; }
        catch (SqliteException ex) { throw PersistenceFailure("read Server report disposition", ex); }
    }

    public async Task SaveServerReportDispositionAsync(Guid executionId, string disposition, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO execution_server_report_dispositions (execution_id,disposition) VALUES ($id,$disposition) ON CONFLICT(execution_id) DO NOTHING";
        command.Parameters.AddWithValue("$id", executionId.ToString());
        command.Parameters.AddWithValue("$disposition", disposition);
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (SqliteException ex) { throw PersistenceFailure("record Server report disposition", ex); }
    }

    /// <summary>Records a secondary reporting failure without replacing the execution's primary outcome.</summary>
    public async Task UpdateReportingFailureAsync(Guid executionId, string failure, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE executions SET reporting_failure=$failure WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", executionId.ToString());
        command.Parameters.AddWithValue("$failure", failure);
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new WorkerInfrastructureException($"Execution history row was not found for {executionId}.");
        }
        catch (SqliteException ex) { throw PersistenceFailure("update secondary execution reporting failure", ex); }
    }

    public async Task UpdateIntegrationRecoverySnapshotAsync(Guid executionId, string commit, string status,
        DateTimeOffset expiresAtUtc, CancellationToken ct = default, string? integrationBase = null)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE executions SET recovery_state='integration-conflict', recovery_base_commit=$commit, recovery_status=$status, recovery_expires_at_utc=$expires, integration_recovery_attempt_base=COALESCE($base,integration_recovery_attempt_base) WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", executionId.ToString());
        command.Parameters.AddWithValue("$commit", commit);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$expires", expiresAtUtc.ToString("O"));
        Add(command, "$base", integrationBase);
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new WorkerInfrastructureException($"Execution history row was not found for {executionId}.");
        }
        catch (SqliteException ex) { throw PersistenceFailure("update integration recovery snapshot", ex); }
    }

    // Completion checkpoints deliberately survive terminal infrastructure/reporting outcomes.
    internal async Task SaveCompletionAsync(Guid executionId, ExecutionCompletion completion, bool finished, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE executions SET completion_json=$completion,
                commit_sha=$sha, integration_branch=$branch,
                recovery_state=CASE WHEN $finished THEN 'completion-reconciled' ELSE recovery_state END,
                state=CASE WHEN $finished THEN 'Completed' ELSE state END,
                completed_at_utc=CASE WHEN $finished THEN COALESCE(completed_at_utc,$now) ELSE completed_at_utc END
            WHERE execution_id=$id
            """;
        command.Parameters.AddWithValue("$id", executionId.ToString());
        command.Parameters.AddWithValue("$completion", JsonSerializer.Serialize(completion));
        Add(command, "$sha", completion.Report.Integration?.CommitSha);
        Add(command, "$branch", completion.Report.Integration?.IntegrationBranch);
        command.Parameters.AddWithValue("$finished", finished);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new WorkerInfrastructureException($"Completion history is missing for execution {executionId}.");
    }

    public Task<IReadOnlyList<ExecutionHistoryEntry>> ReadAllAsync(CancellationToken ct = default) =>
        ReadAsync(" ORDER BY started_at_utc", null, null, ct);

    public Task<IReadOnlyList<ExecutionHistoryEntry>> ReadRecentAsync(int limit, CancellationToken ct = default) =>
        ReadAsync(" ORDER BY started_at_utc DESC, execution_id LIMIT $value", "$value", Math.Clamp(limit, 1, 500), ct);

    public async Task<ExecutionHistoryEntry?> ReadExecutionAsync(Guid executionId, CancellationToken ct = default) =>
        (await ReadAsync(" WHERE execution_id=$value", "$value", executionId.ToString(), ct)).SingleOrDefault();

    public Task<IReadOnlyList<ExecutionHistoryEntry>> ReadIssueAsync(int issueNumber, CancellationToken ct = default) =>
        ReadAsync(" WHERE issue_number=$value ORDER BY project, repository, started_at_utc, attempt_number, execution_id",
            "$value", issueNumber, ct);

    private async Task<IReadOnlyList<ExecutionHistoryEntry>> ReadAsync(string suffix, string? parameter, object? value,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT execution_id, project, repository, issue_number, issue_title, feature_branch, base_branch, started_at_utc, completed_at_utc, state, duration_ms, implementation_summary, validation_outcome, repair_count, repairs_json, commit_sha, integration_branch, completed_branch, failure_reason, recovery_state, recovery_base_commit, recovery_status, retry_of_execution_id, attempt_number, resumed, recovery_expires_at_utc, server_execution_id, assignment_id, ownership_generation, effective_model, effective_effort, reporting_failure, integration_recovery_attempt_base, integration_recovery_claim, original_issue_body, model_selected_by_cli, codex_recovery_json, completion_json FROM executions" + suffix;
        if (parameter is not null) command.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        var entries = new List<ExecutionHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new ExecutionHistoryEntry(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7)), NullableDate(reader, 8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10), NullableString(reader, 11), NullableString(reader, 12),
                reader.GetInt32(13), JsonSerializer.Deserialize<List<ValidationRepairRecord>>(reader.GetString(14)) ?? [],
                NullableString(reader, 15), NullableString(reader, 16), NullableString(reader, 17), NullableString(reader, 18),
                NullableString(reader, 19), NullableString(reader, 20), NullableString(reader, 21),
                reader.IsDBNull(22) ? null : Guid.Parse(reader.GetString(22)), reader.GetInt32(23), reader.GetBoolean(24), NullableDate(reader, 25),
                NullableString(reader, 26), NullableString(reader, 27), reader.IsDBNull(28) ? null : reader.GetInt64(28),
                NullableString(reader, 29), NullableString(reader, 30), NullableString(reader, 31),
                NullableString(reader, 32), reader.IsDBNull(33) ? null : Guid.Parse(reader.GetString(33)), NullableString(reader, 34), reader.GetBoolean(35), reader.IsDBNull(36) ? null : JsonSerializer.Deserialize<CodexInterruptionRecovery>(reader.GetString(36)), NullableString(reader, 37)));
        }
        return entries;
    }

    /// <summary>Returns a detached snapshot of executions that have not reached a terminal state.</summary>
    public async Task<IReadOnlyList<ExecutionHistoryEntry>> ReadActiveAsync(CancellationToken ct = default)
    {
        var entries = await ReadAllAsync(ct);
        return entries.Where(entry => entry.CompletedAtUtc is null && entry.State is not
            ("Completed" or "Blocked" or "Failed" or "InfrastructureFailure" or "Cancelled")).ToArray();
    }

    public async Task<string?> ReadAcknowledgementAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT acknowledged_at_utc || ' · ' || proof || ' · prompt pruned=' || pruned FROM execution_acknowledgements WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteScalarAsync(ct) as string;
    }

    // Caller holds the checkout lock. Retain outcome, lineage, commit and completion provenance forever.
    internal async Task AcknowledgeAsync(ExecutionHistoryEntry entry, string proof, bool prune, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE executions SET recovery_state='operator-acknowledged',
                original_issue_body=CASE WHEN $prune THEN NULL ELSE original_issue_body END
            WHERE execution_id=$id AND completed_at_utc IS NOT NULL
                AND state IN ('Completed','Failed','Blocked','InfrastructureFailure','Cancelled','IntegrationConflict')
                AND NOT EXISTS (SELECT 1 FROM executions other WHERE other.execution_id!=executions.execution_id
                    AND other.repository=executions.repository AND other.issue_number=executions.issue_number
                    AND other.completed_at_utc IS NULL)
                AND server_execution_id IS NULL AND assignment_id IS NULL AND ownership_generation IS NULL
                AND integration_recovery_claim IS NULL
                AND (NOT $prune OR (codex_recovery_json IS NULL AND recovery_base_commit IS NULL AND recovery_status IS NULL));
            """;
        command.Parameters.AddWithValue("$id", entry.ExecutionId.ToString());
        command.Parameters.AddWithValue("$prune", prune);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new WorkerInfrastructureException("Execution ownership changed; acknowledgment rejected.");
        command.CommandText = """
            INSERT INTO execution_acknowledgements(execution_id,acknowledged_at_utc,proof,pruned)
            VALUES ($id,$now,$proof,$prune)
            ON CONFLICT(execution_id) DO UPDATE SET pruned=MAX(pruned,excluded.pruned);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$proof", proof);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public void Dispose() { }

    private void Initialize()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version";
            var schemaVersion = Convert.ToInt32(version.ExecuteScalar());
            if (schemaVersion > CurrentSchemaVersion)
                throw new WorkerInfrastructureException($"Execution history database schema {schemaVersion} is newer than this worker supports ({CurrentSchemaVersion}).");
            if (schemaVersion < 1)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                    CREATE TABLE executions (
                        execution_id TEXT PRIMARY KEY, project TEXT NOT NULL, repository TEXT NOT NULL,
                        issue_number INTEGER NOT NULL, issue_title TEXT NOT NULL, feature_branch TEXT NOT NULL,
                        base_branch TEXT NOT NULL, started_at_utc TEXT NOT NULL, completed_at_utc TEXT NULL,
                        state TEXT NOT NULL, duration_ms INTEGER NULL, implementation_summary TEXT NULL,
                        validation_outcome TEXT NULL, repair_count INTEGER NOT NULL, repairs_json TEXT NOT NULL,
                        commit_sha TEXT NULL, integration_branch TEXT NULL, completed_branch TEXT NULL, failure_reason TEXT NULL
                    );
                    CREATE INDEX idx_executions_started_at ON executions(started_at_utc);
                    PRAGMA user_version = 1;
                    """;
                migration.ExecuteNonQuery();
                schemaVersion = 1;
            }
            if (schemaVersion < 2)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN recovery_state TEXT NULL; ALTER TABLE executions ADD COLUMN recovery_base_commit TEXT NULL; ALTER TABLE executions ADD COLUMN recovery_status TEXT NULL; PRAGMA user_version = 2;";
                migration.ExecuteNonQuery();
                schemaVersion = 2;
            }
            if (schemaVersion < 3)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN retry_of_execution_id TEXT NULL; ALTER TABLE executions ADD COLUMN attempt_number INTEGER NOT NULL DEFAULT 1; ALTER TABLE executions ADD COLUMN resumed INTEGER NOT NULL DEFAULT 0; PRAGMA user_version = 3;";
                migration.ExecuteNonQuery();
                schemaVersion = 3;
            }
            if (schemaVersion < 4)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN recovery_expires_at_utc TEXT NULL; PRAGMA user_version = 4;";
                migration.ExecuteNonQuery();
                schemaVersion = 4;
            }
            if (schemaVersion < 5)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN server_execution_id TEXT NULL; ALTER TABLE executions ADD COLUMN assignment_id TEXT NULL; PRAGMA user_version = 5;";
                migration.ExecuteNonQuery();
                schemaVersion = 5;
            }
            if (schemaVersion < 6)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN ownership_generation INTEGER NULL; PRAGMA user_version = 6;";
                migration.ExecuteNonQuery();
            }
            if (schemaVersion < 7)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN effective_model TEXT NULL; ALTER TABLE executions ADD COLUMN effective_effort TEXT NULL; PRAGMA user_version = 7;";
                migration.ExecuteNonQuery();
                schemaVersion = 7;
            }
            if (schemaVersion < 8)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN reporting_failure TEXT NULL; PRAGMA user_version = 8;";
                migration.ExecuteNonQuery();
            }
            if (schemaVersion < 9)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN integration_recovery_attempt_base TEXT NULL; ALTER TABLE executions ADD COLUMN integration_recovery_claim TEXT NULL; ALTER TABLE executions ADD COLUMN original_issue_body TEXT NULL; PRAGMA user_version = 9;";
                migration.ExecuteNonQuery();
            }
            if (schemaVersion < 10)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN model_selected_by_cli INTEGER NOT NULL DEFAULT 0; UPDATE executions SET model_selected_by_cli=1 WHERE effective_effort IS NOT NULL AND effective_model IS NULL; PRAGMA user_version = 10;";
                migration.ExecuteNonQuery();
            }
            if (schemaVersion < 11)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN codex_recovery_json TEXT NULL; PRAGMA user_version = 11;";
                migration.ExecuteNonQuery();
            }
            if (schemaVersion < 12)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "ALTER TABLE executions ADD COLUMN completion_json TEXT NULL; PRAGMA user_version = 12;";
                migration.ExecuteNonQuery();
            }
            if (schemaVersion < 13)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "CREATE TABLE execution_acknowledgements (execution_id TEXT PRIMARY KEY REFERENCES executions(execution_id), acknowledged_at_utc TEXT NOT NULL, proof TEXT NOT NULL, pruned INTEGER NOT NULL DEFAULT 0); PRAGMA user_version = 13;";
                migration.ExecuteNonQuery();
            }
            if (schemaVersion < 14)
            {
                using var migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = "CREATE TABLE execution_server_report_dispositions (execution_id TEXT PRIMARY KEY REFERENCES executions(execution_id), disposition TEXT NOT NULL); PRAGMA user_version = 14;";
                migration.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw PersistenceFailure("initialize execution history database", ex); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch (Exception ex) { await connection.DisposeAsync(); throw PersistenceFailure("open execution history database", ex); }
    }

    private static void Bind(SqliteCommand command, ExecutionHistoryEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.ExecutionId.ToString());
        command.Parameters.AddWithValue("$project", entry.Project);
        command.Parameters.AddWithValue("$repository", entry.Repository);
        command.Parameters.AddWithValue("$number", entry.IssueNumber);
        command.Parameters.AddWithValue("$title", entry.IssueTitle);
        command.Parameters.AddWithValue("$feature", entry.FeatureBranch);
        command.Parameters.AddWithValue("$base", entry.BaseBranch);
        command.Parameters.AddWithValue("$started", entry.StartedAtUtc.ToString("O"));
        Add(command, "$completed", entry.CompletedAtUtc?.ToString("O"));
        command.Parameters.AddWithValue("$state", entry.State);
        Add(command, "$duration", entry.DurationMilliseconds);
        Add(command, "$summary", entry.ImplementationSummary);
        Add(command, "$validation", entry.ValidationOutcome);
        command.Parameters.AddWithValue("$repairCount", entry.RepairCount);
        command.Parameters.AddWithValue("$repairs", JsonSerializer.Serialize(entry.Repairs));
        Add(command, "$completion", entry.CompletionJson);
        Add(command, "$sha", entry.CommitSha);
        Add(command, "$integration", entry.IntegrationBranch);
        Add(command, "$completedBranch", entry.CompletedBranch);
        Add(command, "$failure", entry.FailureReason);
        Add(command, "$recoveryState", entry.RecoveryState);
        Add(command, "$recoveryBase", entry.RecoveryBaseCommit);
        Add(command, "$recoveryStatus", entry.RecoveryStatus);
        Add(command, "$recoveryExpires", entry.RecoveryExpiresAtUtc?.ToString("O"));
        Add(command, "$retryOf", entry.RetryOfExecutionId?.ToString());
        Add(command, "$serverExecutionId", entry.ServerExecutionId);
        Add(command, "$assignmentId", entry.AssignmentId);
        Add(command, "$ownershipGeneration", entry.OwnershipGeneration);
        Add(command, "$effectiveModel", entry.EffectiveModel);
        command.Parameters.AddWithValue("$modelSelectedByCli", entry.ModelSelectedByCli ? 1 : 0);
        Add(command, "$effectiveEffort", entry.EffectiveEffort);
        Add(command, "$reportingFailure", entry.ReportingFailure);
        Add(command, "$recoveryAttemptBase", entry.IntegrationRecoveryAttemptBase);
        Add(command, "$recoveryClaim", entry.IntegrationRecoveryClaim?.ToString());
        Add(command, "$originalIssueBody", entry.OriginalIssueBody);
        Add(command, "$codexRecovery", entry.CodexRecovery is null ? null : JsonSerializer.Serialize(entry.CodexRecovery));
        command.Parameters.AddWithValue("$attempt", entry.AttemptNumber);
        command.Parameters.AddWithValue("$resumed", entry.Resumed);
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    private static WorkerInfrastructureException PersistenceFailure(string action, Exception ex) =>
        new($"Could not {action}; execution history is required to continue safely: {ex.Message}", ex);
}
