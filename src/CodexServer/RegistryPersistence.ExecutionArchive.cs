using Microsoft.Data.Sqlite;

namespace CodexServer;

public sealed record ExecutionArchiveRequest(IReadOnlyList<string>? ExecutionIds, bool Archived);
public sealed record ExecutionArchiveRefusal(string Id, string Reason);
public sealed record ExecutionArchiveResult(IReadOnlyList<string> Changed, IReadOnlyList<ExecutionArchiveRefusal> Refused);

public static class ExecutionArchiveValidation
{
    public const int MaximumExecutions = 100;

    public static string? Error(ExecutionArchiveRequest? request)
    {
        if (request?.ExecutionIds is not { Count: > 0 } ids || ids.Count > MaximumExecutions)
            return $"executionIds must contain 1..{MaximumExecutions} execution identifiers.";
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(char.IsControl)))
            return "executionIds must contain printable identifiers of at most 100 characters.";
        return null;
    }
}

public sealed partial class SqliteRegistryStore
{
    // Only finished executions can be archived. An uncertain lease still counts toward project
    // capacity and needs reconciliation, so it stays visible until that lifecycle settles it.
    private const string ArchivableCondition =
        "state IN ('Completed','Failed','Cancelled') AND (state!='Failed' OR recovery_state IS NOT 'LeaseExpiredUncertain')";

    /// <summary>Archives or restores executions in the dashboard's default lists. Archive is a
    /// presentation flag: it never changes execution state, leases, scheduling or Worker history.</summary>
    public async Task<ExecutionArchiveResult> SetExecutionsArchivedAsync(IReadOnlyList<string> executionRequestIds, bool archived,
        CancellationToken cancellationToken = default)
    {
        if (ExecutionArchiveValidation.Error(new(executionRequestIds, archived)) is { } error) throw new InvalidDataException(error);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        var changed = new List<string>();
        var refused = new List<ExecutionArchiveRefusal>();
        foreach (var id in executionRequestIds.Distinct(StringComparer.Ordinal))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
            command.CommandText = archived
                ? $"INSERT OR IGNORE INTO execution_archive (execution_id, archived_at_utc) SELECT id, $now FROM execution_requests WHERE id=$id AND {ArchivableCondition};"
                : "DELETE FROM execution_archive WHERE execution_id=$id;";
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                changed.Add(id);
                continue;
            }
            command.CommandText = $"SELECT (SELECT COUNT(*) FROM execution_requests WHERE id=$id), (SELECT COUNT(*) FROM execution_archive WHERE execution_id=$id);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var exists = reader.GetInt64(0) == 1;
            var isArchived = reader.GetInt64(1) == 1;
            refused.Add(new(id, !exists ? "not-found" : archived == isArchived ? (archived ? "already-archived" : "not-archived") : "not-finished"));
        }
        await transaction.CommitAsync(cancellationToken);
        return new(changed, refused);
    }
}
