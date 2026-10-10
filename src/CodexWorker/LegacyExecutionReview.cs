namespace CodexWorker;

public sealed partial class ExecutionHistoryStore
{
    public async Task<string?> ReadLegacyReviewAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT checked_at_utc || ' · legacy-nonrecoverable-review · ' || reason FROM execution_legacy_reviews WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteScalarAsync(ct) as string;
    }

    internal async Task<bool> RecordLegacyReviewAsync(Guid id, string reason, DateTimeOffset checkedAt, CancellationToken ct, string? evidence = null)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reason || COALESCE(evidence, '') FROM execution_legacy_reviews WHERE execution_id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        var previous = await command.ExecuteScalarAsync(ct) as string;
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO execution_legacy_reviews VALUES($id,$checked,$reason,$evidence) ON CONFLICT(execution_id) DO UPDATE SET checked_at_utc=excluded.checked_at_utc,reason=excluded.reason,evidence=excluded.evidence";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$checked", checkedAt.ToString("O"));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$evidence", (object?)evidence ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        return previous != reason + evidence;
    }
}
