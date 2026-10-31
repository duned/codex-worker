namespace CodexServer;

using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Server-owned OAuth grants. Only SHA-256 digests of random credentials reach SQLite.</summary>
public sealed class McpOAuthStore(string databasePath, ServerConfiguration configuration, TimeProvider clock)
{
    public const string Scope = "mcp:admin";
    public sealed record Grant(string Id, string Client, string Redirect, string Resource, string Challenge,
        string ApprovedScope, string Authority, DateTimeOffset Expires);
    public sealed record Tokens(string access_token, string refresh_token, string token_type = "Bearer",
        int expires_in = 600, string scope = Scope);
    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string Authority => Digest(configuration.ManagementToken ?? "");
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mcp_oauth_grants(id TEXT PRIMARY KEY, body TEXT NOT NULL, expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS mcp_oauth_credentials(hash TEXT PRIMARY KEY, grant_id TEXT NOT NULL,
                kind TEXT NOT NULL, expires INTEGER NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_mcp_oauth_grant ON mcp_oauth_credentials(grant_id);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<string?> AuthorizeAsync(string client, string redirect, string resource, string challenge, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM mcp_oauth_grants WHERE expires <= $now; DELETE FROM mcp_oauth_credentials WHERE grant_id NOT IN (SELECT id FROM mcp_oauth_grants); SELECT COUNT(*) FROM mcp_oauth_grants";
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds());
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) >= 1024) return null;
        var grant = new Grant(Guid.NewGuid().ToString("N"), client, redirect, resource, challenge, Scope, Authority, clock.GetUtcNow().AddDays(90));
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO mcp_oauth_grants VALUES($id,$body,$expires)";
        command.Parameters.AddWithValue("$id", grant.Id);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(grant));
        command.Parameters.AddWithValue("$expires", grant.Expires.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(ct);
        var code = Random();
        await InsertAsync(command, code, grant.Id, "code", clock.GetUtcNow().AddMinutes(5), ct);
        await transaction.CommitAsync(ct);
        return code;
    }
    private static async Task InsertAsync(SqliteCommand command, string token, string id, string kind, DateTimeOffset expires, CancellationToken ct)
    {
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO mcp_oauth_credentials(hash,grant_id,kind,expires) VALUES($hash,$id,$kind,$expires)";
        command.Parameters.AddWithValue("$hash", Digest(token));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$expires", expires.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(ct);
    }
    // A write transaction serializes exchange, replay detection, refresh and revocation across processes.
    public async Task<Tokens?> ExchangeAsync(string token, string client, string resource, bool refresh,
        string? redirect, string? verifier, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var credential = await ReadAsync(command, token, ct);
        if (credential is null || credential.Grant.Client != client || credential.Grant.Resource != resource ||
            !Valid(credential.Grant) || credential.Kind != (refresh ? "refresh" : "code")) return null;
        if (credential.Used)
        {
            await DeleteAsync(command, credential.Grant.Id, ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        if (credential.Expires <= clock.GetUtcNow().ToUnixTimeSeconds() || !refresh &&
            (redirect != credential.Grant.Redirect || !ValidVerifier(verifier) ||
             WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier ?? ""))) != credential.Grant.Challenge)) return null;
        command.Parameters.Clear();
        command.CommandText = "UPDATE mcp_oauth_credentials SET used=1 WHERE hash=$hash; DELETE FROM mcp_oauth_credentials WHERE grant_id=$id AND kind='access'";
        command.Parameters.AddWithValue("$hash", Digest(token));
        command.Parameters.AddWithValue("$id", credential.Grant.Id);
        await command.ExecuteNonQueryAsync(ct);
        var result = new Tokens(Random(), Random());
        await InsertAsync(command, result.access_token, credential.Grant.Id, "access", clock.GetUtcNow().AddMinutes(10), ct);
        await InsertAsync(command, result.refresh_token, credential.Grant.Id, "refresh", credential.Grant.Expires, ct);
        await transaction.CommitAsync(ct);
        return result;
    }
    public static bool ValidVerifier(string? value) => value is { Length: >= 43 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');
    private bool Valid(Grant grant) => grant.Expires > clock.GetUtcNow() && grant.ApprovedScope == Scope && grant.Authority == Authority &&
        !string.IsNullOrWhiteSpace(configuration.ManagementToken) && grant.Resource == configuration.AdministrationOrigin + "/mcp";
    private sealed record Credential(Grant Grant, string Kind, long Expires, bool Used);
    private static async Task<Credential?> ReadAsync(SqliteCommand command, string token, CancellationToken ct)
    {
        command.CommandText = "SELECT g.body,c.kind,c.expires,c.used FROM mcp_oauth_credentials c JOIN mcp_oauth_grants g ON g.id=c.grant_id WHERE c.hash=$hash";
        command.Parameters.AddWithValue("$hash", Digest(token));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && JsonSerializer.Deserialize<Grant>(reader.GetString(0)) is { } grant
            ? new(grant, reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3)) : null;
    }
    public async Task<bool> AuthorizedAsync(string token, CancellationToken ct)
    {
        if (token.Length != 43) return false;
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        var credential = await ReadAsync(command, token, ct);
        return credential is { Kind: "access", Used: false } && Valid(credential.Grant) && credential.Expires > clock.GetUtcNow().ToUnixTimeSeconds();
    }
    private static async Task DeleteAsync(SqliteCommand command, string id, CancellationToken ct)
    {
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM mcp_oauth_credentials WHERE grant_id=$id; DELETE FROM mcp_oauth_grants WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }
    public sealed record Connection(string Id, string Client, string Resource, DateTimeOffset Expires, string Scope = McpOAuthStore.Scope);
    public async Task<IReadOnlyList<Connection>> ListAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM mcp_oauth_grants ORDER BY rowid DESC LIMIT 1024";
        var result = new List<Connection>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (JsonSerializer.Deserialize<Grant>(reader.GetString(0)) is { } grant && Valid(grant))
                result.Add(new(grant.Id, grant.Client, grant.Resource, grant.Expires, grant.ApprovedScope));
        return result;
    }
    public async Task RevokeConnectionAsync(string id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        await DeleteAsync(command, id, ct);
        await transaction.CommitAsync(ct);
    }
    public async Task RevokeAsync(string token, string client, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var credential = await ReadAsync(command, token, ct);
        if (credential?.Grant.Client == client) await DeleteAsync(command, credential.Grant.Id, ct);
        await transaction.CommitAsync(ct);
    }
}
