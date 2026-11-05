namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

public sealed partial class SqliteRegistryStore
{
    public async Task<ObserverPairingAuthorization?> CreateObserverAuthorizationAsync(ObserverPairingRequest request, CancellationToken ct = default)
    {
        if (!request.IsValid()) throw new ArgumentException("Invalid observer pairing request.", nameof(request));
        var authorization = "oba_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = _timeProvider.GetUtcNow().AddMinutes(5);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // Pending identities are reservations, not observers. An expired reservation can be replaced.
        command.CommandText = """
            INSERT INTO observer_pairings(observer_id,server_origin,authorization_hash,expires_at_utc)
            SELECT $id,$server,$hash,$expires WHERE NOT EXISTS (SELECT 1 FROM observers WHERE observer_id=$id)
            ON CONFLICT(observer_id) DO UPDATE SET server_origin=$server,authorization_hash=$hash,expires_at_utc=$expires
            WHERE observer_pairings.expires_at_utc <= $now AND observer_pairings.consumed_at_utc IS NULL
                AND observer_pairings.revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", request.ObserverId);
        command.Parameters.AddWithValue("$server", request.Server);
        command.Parameters.AddWithValue("$hash", HashToken(authorization));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1
            ? new(ObserverProtocol.Version, request.ObserverId, request.Server, authorization, expires) : null;
    }

    public async Task<ObserverRegistration?> RedeemObserverAuthorizationAsync(ObserverPairingRequest request,
        string authorization, string reportingCredential, CancellationToken ct = default)
    {
        if (!request.IsValid() || !ObserverProtocol.ValidAuthorization(authorization) ||
            !ObserverProtocol.ValidCredential(reportingCredential)) return null;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var now = _timeProvider.GetUtcNow();
        command.CommandText = """
            UPDATE observer_pairings SET consumed_at_utc=$now WHERE observer_id=$id AND server_origin=$server
                AND authorization_hash=$hash AND expires_at_utc > $now AND consumed_at_utc IS NULL AND revoked_at_utc IS NULL
                AND NOT EXISTS (SELECT 1 FROM observers WHERE observer_id=$id);
            """;
        command.Parameters.AddWithValue("$id", request.ObserverId);
        command.Parameters.AddWithValue("$server", request.Server);
        command.Parameters.AddWithValue("$hash", HashToken(authorization));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(ct) != 1) return null;
        command.CommandText = """
            INSERT INTO observers(observer_id,server_origin,credential_hash,accepted_at_utc) VALUES($id,$server,$credential,$now);
            """;
        command.Parameters.AddWithValue("$credential", HashToken(reportingCredential));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(ObserverProtocol.Version, request.ObserverId, ObserverProtocol.Mode, request.Server, now, "awaiting-first-report");
    }

    /// <summary>Only for reporting routes and read-only redemption recovery; never managed Worker authentication.</summary>
    public async Task<ObserverRegistration?> AuthenticateObserverAsync(string observerId, string server, string reportingCredential, CancellationToken ct = default)
    {
        if (!ObserverProtocol.ValidIdentity(observerId) || !ObserverProtocol.ValidOrigin(server) ||
            !ObserverProtocol.ValidCredential(reportingCredential)) return null;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return await AuthenticateObserverAsync(connection, null, observerId, server, reportingCredential, ct);
    }

    private static async Task<ObserverRegistration?> AuthenticateObserverAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string observerId, string server, string reportingCredential, CancellationToken ct)
    {
        if (!ObserverProtocol.ValidIdentity(observerId) || !ObserverProtocol.ValidOrigin(server) ||
            !ObserverProtocol.ValidCredential(reportingCredential)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT credential_hash,accepted_at_utc,EXISTS(SELECT 1 FROM observer_status s WHERE s.observer_id=observers.observer_id AND s.snapshot_json IS NOT NULL)
            FROM observers WHERE observer_id=$id AND server_origin=$server AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", observerId);
        command.Parameters.AddWithValue("$server", server);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(reader.GetString(0)), Convert.FromHexString(HashToken(reportingCredential)))) return null;
        return new(ObserverProtocol.Version, observerId, ObserverProtocol.Mode, server,
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetBoolean(2) ? "reporting" : "awaiting-first-report");
    }

    public async Task<bool> RevokeObserverAsync(string observerId, CancellationToken ct = default)
    {
        if (!ObserverProtocol.ValidIdentity(observerId)) return false;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Atomically revoke pending authorizations and any already redeemed credential.
        command.CommandText = """
            UPDATE observers SET revoked_at_utc=$now WHERE observer_id=$id AND revoked_at_utc IS NULL;
            UPDATE observer_pairings SET revoked_at_utc=$now WHERE observer_id=$id AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", observerId);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        var changed = await command.ExecuteNonQueryAsync(ct) > 0;
        await transaction.CommitAsync(ct);
        return changed;
    }
}
