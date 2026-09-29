namespace CodexServer;

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Persistence boundary for future worker and project registry services.</summary>
public interface IRegistryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task RegisterWorkerAsync(WorkerRegistrationRequest worker, CancellationToken cancellationToken = default);
    Task HeartbeatWorkerAsync(WorkerHeartbeatRequest heartbeat, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkerRegistrationResponse>> GetWorkersAsync(CancellationToken cancellationToken = default);
    Task<WorkerRegistrationResponse?> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CentralProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<CentralProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default);
    Task<CentralProject> CreateProjectAsync(CentralProjectDefinition definition, CancellationToken cancellationToken = default);
    Task<CentralProject?> UpdateProjectAsync(string projectId, CentralProjectDefinition definition, long expectedRevision, CancellationToken cancellationToken = default);
    Task<bool> RemoveProjectAsync(string projectId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ExecutionRequest> EnqueueExecutionAsync(EnqueueExecutionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionRequest>> GetExecutionsAsync(CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> TransitionExecutionAsync(string executionRequestId, ExecutionStateTransition transition, CancellationToken cancellationToken = default);
}

public sealed record WorkReference(string Type, string Id, string? Url = null);
public sealed record EnqueueExecutionRequest(string ProjectId, WorkReference WorkReference);
public sealed record ExecutionRequest(string Id, string ProjectId, WorkReference WorkReference,
    DateTimeOffset CreatedAtUtc, string State, string? AssignedWorkerId, DateTimeOffset? AssignedAtUtc, string? ExecutionId);
public sealed record ExecutionStateTransition(string State, string? AssignedWorkerId = null, string? ExecutionId = null);

public static class ExecutionRequestValidation
{
    public static string? Error(EnqueueExecutionRequest? request)
    {
        if (request is null) return "Execution request is required.";
        if (string.IsNullOrWhiteSpace(request.ProjectId) || request.ProjectId.Length > 80) return "projectId must contain 1 to 80 characters.";
        if (request.WorkReference is null) return "workReference is required.";
        if (string.IsNullOrWhiteSpace(request.WorkReference.Type) || request.WorkReference.Type.Length > 80 || request.WorkReference.Type.Any(char.IsControl)) return "workReference.type must contain 1 to 80 printable characters.";
        if (string.IsNullOrWhiteSpace(request.WorkReference.Id) || request.WorkReference.Id.Length > 300 || request.WorkReference.Id.Any(char.IsControl)) return "workReference.id must contain 1 to 300 printable characters.";
        if (request.WorkReference.Url is { Length: > 2000 } || request.WorkReference.Url?.Any(char.IsControl) == true) return "workReference.url must contain at most 2000 printable characters.";
        return null;
    }
}

public sealed class ExecutionRequestConflictException() : Exception("An active execution request already exists for this project and work reference.") { }
public sealed class ExecutionRequestTransitionException() : Exception("The requested execution state transition is invalid.") { }

/// <summary>Portable Server-owned project definition. It deliberately excludes Worker paths and secrets.</summary>
public sealed record CentralProjectDefinition(string Name, string Repository, string DefaultBranch,
    string Description, IReadOnlyList<string> Requirements);
public sealed record CentralProject(string Id, string Name, string Repository, string DefaultBranch,
    string Description, IReadOnlyList<string> Requirements, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public static class CentralProjectValidation
{
    public static string? Error(CentralProjectDefinition? value)
    {
        if (value is null) return "Project definition is required.";
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 120) return "name must contain 1 to 120 characters.";
        if (!Regex.IsMatch(value.Name, "^[\\p{L}\\p{N}][\\p{L}\\p{N} ._-]*$")) return "name contains unsupported characters.";
        if (string.IsNullOrWhiteSpace(value.Repository) || !Regex.IsMatch(value.Repository, "^[^/\\s]+/[^/\\s]+$")) return "repository must be in owner/repository form.";
        if (string.IsNullOrWhiteSpace(value.DefaultBranch) || value.DefaultBranch.Length > 200 || value.DefaultBranch.Any(char.IsControl)) return "defaultBranch must contain 1 to 200 printable characters.";
        if (value.Description is null || value.Description.Length > 4000) return "description must contain at most 4000 characters.";
        if (value.Requirements is null || value.Requirements.Count > 64 || value.Requirements.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100 || x.Any(char.IsControl))) return "requirements must contain at most 64 non-empty values of at most 100 characters.";
        if (value.Requirements.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Requirements.Count) return "requirements must be unique.";
        return null;
    }

    public static string IdFor(string name)
    {
        var id = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (id.Length == 0) id = "project-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
        return id.Length > 80 ? id[..80].TrimEnd('-') : id;
    }
}

public sealed class ProjectRevisionConflictException(long currentRevision)
    : Exception($"Project revision is stale; current revision is {currentRevision}.")
{
    public long CurrentRevision { get; } = currentRevision;
}

/// <summary>Versioned public registration request; intentionally independent of persistence entities.</summary>
public sealed record WorkerRegistrationRequest(int ContractVersion, string WorkerId, string DisplayName,
    string WorkerVersion, string Platform, int Capacity, IReadOnlyList<string> Capabilities);
public sealed record WorkerHeartbeatRequest(int ContractVersion, string WorkerId, string WorkerVersion,
    string LifecycleState, int ActiveExecutions, int MaximumCapacity, IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ActiveProjects);
public sealed record WorkerRegistrationResponse(int ContractVersion, string WorkerId, string DisplayName,
    string WorkerVersion, string Platform, int Capacity, IReadOnlyList<string> Capabilities,
    DateTimeOffset FirstRegisteredAtUtc, DateTimeOffset LastSeenAtUtc, string Availability,
    int ActiveExecutions, int MaximumCapacity, int AvailableCapacity, string LifecycleState,
    IReadOnlyList<string> ActiveProjects);

/// <summary>Creates the server's durable registry schema without coupling APIs to SQLite.</summary>
public sealed class SqliteRegistryStore(string databasePath, int staleAfterSeconds = 90, TimeProvider? timeProvider = null) : IRegistryStore
{
    public const int CurrentSchemaVersion = 4;
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(staleAfterSeconds);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private static readonly JsonSerializerOptions ProjectJson = new(JsonSerializerDefaults.Web);
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidDataException("Database path must include a directory.");
        Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_metadata (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                    schema_version INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO schema_metadata (singleton, schema_version) VALUES (1, 4);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton = 1;";
            var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion is < 1 or > CurrentSchemaVersion)
                throw new InvalidDataException($"Database schema version {schemaVersion} is not supported; expected {CurrentSchemaVersion}.");
            if (schemaVersion == 1)
            {
                command.CommandText = "ALTER TABLE workers ADD COLUMN registration_json TEXT NULL; ALTER TABLE workers ADD COLUMN last_seen_at_utc TEXT NULL; UPDATE schema_metadata SET schema_version = 2 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 2;
            }
            if (schemaVersion == 2)
            {
                command.CommandText = "ALTER TABLE workers ADD COLUMN heartbeat_json TEXT NULL; UPDATE schema_metadata SET schema_version = 3 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 3;
            }
            if (schemaVersion == 3)
            {
                command.CommandText = "UPDATE schema_metadata SET schema_version = 4 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS workers (
                    worker_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    registered_at_utc TEXT NOT NULL,
                    status_json TEXT NULL,
                    registration_json TEXT NULL,
                    last_seen_at_utc TEXT NULL,
                    heartbeat_json TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS projects (
                    project_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    configuration_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_repository ON projects (lower(json_extract(configuration_json, '$.repository')));
                CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_name ON projects (lower(display_name));
                CREATE TABLE IF NOT EXISTS execution_metadata (
                    execution_id TEXT NOT NULL PRIMARY KEY,
                    worker_id TEXT NULL,
                    project_id TEXT NULL,
                    state TEXT NOT NULL,
                    metadata_json TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS execution_requests (
                    queue_order INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    id TEXT NOT NULL UNIQUE,
                    project_id TEXT NOT NULL,
                    work_type TEXT NOT NULL,
                    work_id TEXT NOT NULL,
                    work_reference_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    state TEXT NOT NULL CHECK (state IN ('Queued', 'Assigned', 'Running', 'Completed', 'Failed')),
                    assigned_worker_id TEXT NULL,
                    assigned_at_utc TEXT NULL,
                    execution_id TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_execution_requests_active_work ON execution_requests (project_id, work_type, work_id) WHERE state IN ('Queued', 'Assigned', 'Running');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ExecutionRequest> EnqueueExecutionAsync(EnqueueExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var error = ExecutionRequestValidation.Error(request);
        if (error is not null) throw new InvalidDataException(error);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM projects WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", request.ProjectId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null) throw new KeyNotFoundException($"Project '{request.ProjectId}' was not found.");
        var id = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO execution_requests (id, project_id, work_type, work_id, work_reference_json, created_at_utc, state) VALUES ($id, $projectId, $workType, $workId, $workReference, $created, 'Queued');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$projectId", request.ProjectId);
        command.Parameters.AddWithValue("$workType", request.WorkReference.Type);
        command.Parameters.AddWithValue("$workId", request.WorkReference.Id);
        command.Parameters.AddWithValue("$workReference", JsonSerializer.Serialize(request.WorkReference, ProjectJson));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new ExecutionRequestConflictException(); }
        return new(id, request.ProjectId, request.WorkReference, now, "Queued", null, null, null);
    }

    public async Task<IReadOnlyList<ExecutionRequest>> GetExecutionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, project_id, work_reference_json, created_at_utc, state, assigned_worker_id, assigned_at_utc, execution_id FROM execution_requests ORDER BY queue_order;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ExecutionRequest>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadExecution(reader));
        return result;
    }

    public async Task<ExecutionRequest?> TransitionExecutionAsync(string executionRequestId, ExecutionStateTransition transition, CancellationToken cancellationToken = default)
    {
        if (transition is null || (transition.AssignedWorkerId is not null && !Printable(transition.AssignedWorkerId, 128)) ||
            (transition.ExecutionId is not null && !Printable(transition.ExecutionId, 200)))
            throw new InvalidDataException("State transition contract is invalid.");
        var valid = transition.State switch
        {
            "Assigned" => !string.IsNullOrWhiteSpace(transition.AssignedWorkerId),
            "Running" => transition.AssignedWorkerId is null,
            "Completed" or "Failed" => transition.AssignedWorkerId is null,
            _ => false
        };
        if (!valid) throw new InvalidDataException("State transition contract is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE execution_requests SET state = $next, assigned_worker_id = COALESCE($worker, assigned_worker_id), assigned_at_utc = CASE WHEN $next = 'Assigned' THEN $now ELSE assigned_at_utc END, execution_id = COALESCE($executionId, execution_id) WHERE id = $id AND (state = CASE $next WHEN 'Assigned' THEN 'Queued' WHEN 'Running' THEN 'Assigned' WHEN 'Completed' THEN 'Running' WHEN 'Failed' THEN 'Running' ELSE '' END) AND ($next != 'Running' OR ($workerForRun IS NULL AND assigned_worker_id IS NOT NULL) OR assigned_worker_id = $workerForRun);";
        command.Parameters.AddWithValue("$next", transition.State);
        command.Parameters.AddWithValue("$worker", (object?)transition.AssignedWorkerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerForRun", (object?)transition.AssignedWorkerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$executionId", (object?)transition.ExecutionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", executionRequestId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM execution_requests WHERE id = $id;";
            exists.Parameters.AddWithValue("$id", executionRequestId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;
            throw new ExecutionRequestTransitionException();
        }
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT id, project_id, work_reference_json, created_at_utc, state, assigned_worker_id, assigned_at_utc, execution_id FROM execution_requests WHERE id = $id;";
        read.Parameters.AddWithValue("$id", executionRequestId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExecution(reader) : null;
    }

    private static bool Printable(string value, int maxLength) => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static ExecutionRequest ReadExecution(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        JsonSerializer.Deserialize<WorkReference>(reader.GetString(2), ProjectJson) ?? throw new InvalidDataException("Stored work reference is invalid."),
        DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7));

    public async Task<IReadOnlyList<CentralProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id, configuration_json, created_at_utc FROM projects ORDER BY lower(display_name), project_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CentralProject>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ToProject(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<CentralProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id, configuration_json, created_at_utc FROM projects WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ToProject(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public async Task<CentralProject> CreateProjectAsync(CentralProjectDefinition definition, CancellationToken cancellationToken = default)
    {
        var validationError = CentralProjectValidation.Error(definition);
        if (validationError is not null) throw new InvalidDataException(validationError);
        var now = _timeProvider.GetUtcNow();
        var id = CentralProjectValidation.IdFor(definition.Name);
        if (id.Length == 0) throw new InvalidDataException("Project name does not produce a valid project identifier.");
        var project = new CentralProject(id, definition.Name.Trim(), definition.Repository.Trim(), definition.DefaultBranch.Trim(),
            definition.Description.Trim(), definition.Requirements.Select(x => x.Trim()).ToArray(), 1, now, now);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO projects (project_id, display_name, configuration_json, created_at_utc) VALUES ($id, $name, $json, $created);";
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(project, ProjectJson));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("A project with this identifier, name, or repository already exists.", ex); }
        return project;
    }

    public async Task<CentralProject?> UpdateProjectAsync(string projectId, CentralProjectDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var validationError = CentralProjectValidation.Error(definition);
        if (validationError is not null) throw new InvalidDataException(validationError);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE projects SET display_name = $name, configuration_json = json_set(configuration_json, '$.name', $name, '$.repository', $repository, '$.defaultBranch', $branch, '$.description', $description, '$.requirements', json($requirements), '$.revision', $nextRevision, '$.updatedAtUtc', $updated) WHERE project_id = $id AND CAST(json_extract(configuration_json, '$.revision') AS INTEGER) = $expectedRevision;";
        write.Parameters.AddWithValue("$name", definition.Name.Trim());
        write.Parameters.AddWithValue("$repository", definition.Repository.Trim());
        write.Parameters.AddWithValue("$branch", definition.DefaultBranch.Trim());
        write.Parameters.AddWithValue("$description", definition.Description.Trim());
        write.Parameters.AddWithValue("$requirements", JsonSerializer.Serialize(definition.Requirements.Select(x => x.Trim()).ToArray()));
        write.Parameters.AddWithValue("$nextRevision", expectedRevision + 1);
        write.Parameters.AddWithValue("$updated", now.ToString("O"));
        write.Parameters.AddWithValue("$id", projectId);
        write.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        try { if (await write.ExecuteNonQueryAsync(cancellationToken) == 1) return await GetProjectAsync(projectId, cancellationToken); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("A project with this name or repository already exists.", ex); }
        var current = await GetProjectAsync(projectId, cancellationToken);
        if (current is not null) throw new ProjectRevisionConflictException(current.Revision);
        return null;
    }

    public async Task<bool> RemoveProjectAsync(string projectId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE project_id = $id AND CAST(json_extract(configuration_json, '$.revision') AS INTEGER) = $revision;";
        command.Parameters.AddWithValue("$id", projectId);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return true;
        var current = await GetProjectAsync(projectId, cancellationToken);
        if (current is not null) throw new ProjectRevisionConflictException(current.Revision);
        return false;
    }

    private static CentralProject ToProject(string id, string json, string created) =>
        JsonSerializer.Deserialize<CentralProject>(json, ProjectJson) is { } value ? value with { Id = id, CreatedAtUtc = DateTimeOffset.Parse(created) } :
        throw new InvalidDataException($"Stored project '{id}' is invalid.");

    public async Task HeartbeatWorkerAsync(WorkerHeartbeatRequest heartbeat, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workers SET heartbeat_json = $heartbeat, last_seen_at_utc = $now WHERE worker_id = $id AND registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$heartbeat", JsonSerializer.Serialize(heartbeat));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", heartbeat.WorkerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Worker must be registered before sending heartbeats.");
    }

    public async Task RegisterWorkerAsync(WorkerRegistrationRequest worker, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var metadata = JsonSerializer.Serialize(worker);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workers (worker_id, display_name, registered_at_utc, status_json, registration_json, last_seen_at_utc)
            VALUES ($id, $name, $now, NULL, $metadata, $now)
            ON CONFLICT(worker_id) DO UPDATE SET display_name = excluded.display_name,
                registration_json = excluded.registration_json, last_seen_at_utc = excluded.last_seen_at_utc, heartbeat_json = NULL;
            """;
        command.Parameters.AddWithValue("$id", worker.WorkerId);
        command.Parameters.AddWithValue("$name", worker.DisplayName);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$metadata", metadata);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkerRegistrationResponse>> GetWorkersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT registration_json, registered_at_utc, last_seen_at_utc, heartbeat_json FROM workers WHERE registration_json IS NOT NULL ORDER BY worker_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var workers = new List<WorkerRegistrationResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var request = JsonSerializer.Deserialize<WorkerRegistrationRequest>(reader.GetString(0))!;
            workers.Add(ToResponse(request, DateTimeOffset.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return workers;
    }

    public async Task<WorkerRegistrationResponse?> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT registration_json, registered_at_utc, last_seen_at_utc, heartbeat_json FROM workers WHERE worker_id = $id AND registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$id", workerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var request = JsonSerializer.Deserialize<WorkerRegistrationRequest>(reader.GetString(0))!;
        return ToResponse(request, DateTimeOffset.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private WorkerRegistrationResponse ToResponse(WorkerRegistrationRequest request, DateTimeOffset registered, DateTimeOffset seen, string? heartbeatJson)
    {
        var heartbeat = heartbeatJson is null ? null : JsonSerializer.Deserialize<WorkerHeartbeatRequest>(heartbeatJson);
        var online = heartbeat is not null && _timeProvider.GetUtcNow() - seen <= _staleAfter;
        var availability = !online ? "stale" : heartbeat!.LifecycleState switch
        {
            "draining" => "draining",
            "stopped" => "offline",
            _ => "online"
        };
        var active = online ? heartbeat!.ActiveExecutions : 0;
        var capacity = online ? heartbeat!.MaximumCapacity : request.Capacity;
        return new(request.ContractVersion, request.WorkerId, request.DisplayName, heartbeat?.WorkerVersion ?? request.WorkerVersion,
            request.Platform, request.Capacity, heartbeat?.Capabilities ?? request.Capabilities, registered, seen,
            availability, active, capacity, Math.Max(0, capacity - active), heartbeat?.LifecycleState ?? "unknown",
            online ? heartbeat!.ActiveProjects : Array.Empty<string>());
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == CurrentSchemaVersion;
    }
}
