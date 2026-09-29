namespace CodexServer;

using Microsoft.Data.Sqlite;
using System.Text.Json;

/// <summary>Persistence boundary for future worker and project registry services.</summary>
public interface IRegistryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task RegisterWorkerAsync(WorkerRegistrationRequest worker, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkerRegistrationResponse>> GetWorkersAsync(CancellationToken cancellationToken = default);
    Task<WorkerRegistrationResponse?> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default);
}

/// <summary>Versioned public registration request; intentionally independent of persistence entities.</summary>
public sealed record WorkerRegistrationRequest(int ContractVersion, string WorkerId, string DisplayName,
    string WorkerVersion, string Platform, int Capacity, IReadOnlyList<string> Capabilities);
public sealed record WorkerRegistrationResponse(int ContractVersion, string WorkerId, string DisplayName,
    string WorkerVersion, string Platform, int Capacity, IReadOnlyList<string> Capabilities,
    DateTimeOffset FirstRegisteredAtUtc, DateTimeOffset LastSeenAtUtc);

/// <summary>Creates the server's durable registry schema without coupling APIs to SQLite.</summary>
public sealed class SqliteRegistryStore(string databasePath) : IRegistryStore
{
    public const int CurrentSchemaVersion = 2;
    private readonly string _databasePath = Path.GetFullPath(databasePath);
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
                INSERT OR IGNORE INTO schema_metadata (singleton, schema_version) VALUES (1, 2);
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
            }
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS workers (
                    worker_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    registered_at_utc TEXT NOT NULL,
                    status_json TEXT NULL,
                    registration_json TEXT NULL,
                    last_seen_at_utc TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS projects (
                    project_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    configuration_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS execution_metadata (
                    execution_id TEXT NOT NULL PRIMARY KEY,
                    worker_id TEXT NULL,
                    project_id TEXT NULL,
                    state TEXT NOT NULL,
                    metadata_json TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RegisterWorkerAsync(WorkerRegistrationRequest worker, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = JsonSerializer.Serialize(worker);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workers (worker_id, display_name, registered_at_utc, status_json, registration_json, last_seen_at_utc)
            VALUES ($id, $name, $now, NULL, $metadata, $now)
            ON CONFLICT(worker_id) DO UPDATE SET display_name = excluded.display_name,
                registration_json = excluded.registration_json, last_seen_at_utc = excluded.last_seen_at_utc;
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
        command.CommandText = "SELECT registration_json, registered_at_utc, last_seen_at_utc FROM workers WHERE registration_json IS NOT NULL ORDER BY worker_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var workers = new List<WorkerRegistrationResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var request = JsonSerializer.Deserialize<WorkerRegistrationRequest>(reader.GetString(0))!;
            workers.Add(ToResponse(request, DateTimeOffset.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2))));
        }
        return workers;
    }

    public async Task<WorkerRegistrationResponse?> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT registration_json, registered_at_utc, last_seen_at_utc FROM workers WHERE worker_id = $id AND registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$id", workerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var request = JsonSerializer.Deserialize<WorkerRegistrationRequest>(reader.GetString(0))!;
        return ToResponse(request, DateTimeOffset.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2)));
    }

    private static WorkerRegistrationResponse ToResponse(WorkerRegistrationRequest request, DateTimeOffset registered, DateTimeOffset seen) =>
        new(request.ContractVersion, request.WorkerId, request.DisplayName, request.WorkerVersion, request.Platform,
            request.Capacity, request.Capabilities, registered, seen);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == CurrentSchemaVersion;
    }
}
