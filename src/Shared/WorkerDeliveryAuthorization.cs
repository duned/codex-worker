namespace CodexProvisioning;

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

public sealed partial class SqliteCredentialStore
{
    // Only operation metadata and a verifier are stored. The Worker creates the secret.
    public async Task<WorkerDeliveryOperation?> RequestWorkerDeliveryAsync(string workerId, bool repair,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _)) throw new InvalidDataException("Worker identity is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO worker_credential_auth(worker_id,token_hash,delivery_operation,delivery_deadline)
            VALUES($worker,randomblob(32),$operation,$deadline)
            ON CONFLICT(worker_id) DO UPDATE SET delivery_operation=$operation,delivery_deadline=$deadline
            WHERE $repair=1 AND (worker_credential_auth.delivery_deadline IS NULL OR worker_credential_auth.delivery_deadline <= $now);
            SELECT delivery_operation FROM worker_credential_auth WHERE worker_id=$worker AND delivery_deadline > $now;
            """;
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$operation", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$deadline", _timeProvider.GetUtcNow().AddMinutes(5).ToString("O"));
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$repair", repair ? 1 : 0);
        var operation = await command.ExecuteScalarAsync(cancellationToken) as string;
        return operation is null ? null : new(operation);
    }

    public async Task<bool> ConfirmWorkerDeliveryAsync(string workerId, WorkerDeliveryConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _) || !Guid.TryParseExact(confirmation.OperationId, "N", out _) ||
            confirmation.Secret is null || confirmation.Secret.Value is not { Length: >= 32 and <= 512 } token ||
            !WorkerEnrollmentProtocol.ValidToken(token)) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // A replay only succeeds for the already confirmed verifier. A stale operation cannot rotate it.
            command.CommandText = """
                UPDATE worker_credential_auth SET token_hash=$hash,revoked_at_utc=NULL,delivery_deadline=NULL
                WHERE worker_id=$worker AND delivery_operation=$operation AND
                (delivery_deadline > $now OR (delivery_deadline IS NULL AND revoked_at_utc IS NULL AND token_hash=$hash));
                """;
            command.Parameters.AddWithValue("$worker", workerId);
            command.Parameters.AddWithValue("$operation", confirmation.OperationId);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}
