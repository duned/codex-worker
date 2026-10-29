namespace CodexServer;

using Microsoft.Data.Sqlite;

/// <summary>Reserves creation identities before GitHub I/O; uncertain writes are never replayed.</summary>
public sealed class IssuePlanningOperations(string databasePath)
{
    public async Task<bool> ReserveAsync(string operationId, string fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS issue_planning_operations (id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, issue_number INTEGER)";
        await command.ExecuteNonQueryAsync(cancellationToken);
        // Count only the reservation insert; schema initialization can report changes from earlier commands.
        command.CommandText = "INSERT OR IGNORE INTO issue_planning_operations(id, fingerprint) VALUES ($id, $fingerprint)";
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<(string Fingerprint, int? IssueNumber)> GetAsync(string operationId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint, issue_number FROM issue_planning_operations WHERE id=$id";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Creation reservation is missing.");
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }

    public async Task CompleteAsync(string operationId, int issueNumber, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE issue_planning_operations SET issue_number=$number WHERE id=$id";
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$number", issueNumber);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
