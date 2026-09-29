namespace CodexServer;

using Microsoft.Data.Sqlite;

/// <summary>Persistence boundary for future worker and project registry services.</summary>
public interface IRegistryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates the server's durable registry schema without coupling APIs to SQLite.</summary>
public sealed class SqliteRegistryStore(string databasePath) : IRegistryStore
{
    public const int CurrentSchemaVersion = 1;
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
                INSERT OR IGNORE INTO schema_metadata (singleton, schema_version) VALUES (1, 1);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton = 1;";
            var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException($"Database schema version {schemaVersion} is not supported; expected {CurrentSchemaVersion}.");
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS workers (
                    worker_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    registered_at_utc TEXT NOT NULL,
                    status_json TEXT NULL
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

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == CurrentSchemaVersion;
    }
}
