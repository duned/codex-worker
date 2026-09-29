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
    string? AssignmentId = null);

/// <summary>Local, single-worker SQLite history with an SQLite user_version migration sequence.</summary>
public sealed class ExecutionHistoryStore : IDisposable
{
    private const int CurrentSchemaVersion = 5;
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO executions (execution_id, project, repository, issue_number, issue_title, feature_branch, base_branch,
                started_at_utc, completed_at_utc, state, duration_ms, implementation_summary, validation_outcome,
                repair_count, repairs_json, commit_sha, integration_branch, completed_branch, failure_reason,
                recovery_state, recovery_base_commit, recovery_status, retry_of_execution_id, attempt_number, resumed, recovery_expires_at_utc,
                server_execution_id, assignment_id)
            VALUES ($id,$project,$repository,$number,$title,$feature,$base,$started,$completed,$state,$duration,$summary,$validation,
                $repairCount,$repairs,$sha,$integration,$completedBranch,$failure,$recoveryState,$recoveryBase,$recoveryStatus,
                $retryOf,$attempt,$resumed,$recoveryExpires,$serverExecutionId,$assignmentId)
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
                assignment_id=COALESCE($assignmentId,assignment_id)
                WHERE execution_id=$id AND completed_at_utc IS NULL
                    AND state NOT IN ('Completed','Blocked','Failed','InfrastructureFailure','Cancelled')
            """;
        Bind(command, entry);
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) != 1)
                throw new WorkerInfrastructureException($"Execution history row was not found for {entry.ExecutionId}.");
        }
        catch (SqliteException ex) { throw PersistenceFailure("update execution history", ex); }
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

    public async Task<IReadOnlyList<ExecutionHistoryEntry>> ReadAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT execution_id, project, repository, issue_number, issue_title, feature_branch, base_branch, started_at_utc, completed_at_utc, state, duration_ms, implementation_summary, validation_outcome, repair_count, repairs_json, commit_sha, integration_branch, completed_branch, failure_reason, recovery_state, recovery_base_commit, recovery_status, retry_of_execution_id, attempt_number, resumed, recovery_expires_at_utc, server_execution_id, assignment_id FROM executions ORDER BY started_at_utc";
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
                NullableString(reader, 26), NullableString(reader, 27)));
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
        command.Parameters.AddWithValue("$attempt", entry.AttemptNumber);
        command.Parameters.AddWithValue("$resumed", entry.Resumed);
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    private static WorkerInfrastructureException PersistenceFailure(string action, Exception ex) =>
        new($"Could not {action}; execution history is required to continue safely: {ex.Message}", ex);
}
