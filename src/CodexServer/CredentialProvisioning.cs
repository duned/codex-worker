namespace CodexServer;

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

/// <summary>Secret material is deliberately not part of the public credential metadata contract.</summary>
public sealed record CredentialMetadata(string Id, string Provider, string Type, string SecretReference,
    string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string? AssignedWorkerId,
    DateTimeOffset? RevokedAtUtc, long Version);

/// <summary>Request-only secret input. Its diagnostic representation never includes the value.</summary>
public sealed class CredentialSecretInput(string value)
{
    public string Value { get; } = value;
    public override string ToString() => "[credential secret omitted]";
}

public sealed record CreateCredentialRequest(string Provider, string Type, CredentialSecretInput Secret);
public sealed record CredentialAssignmentRequest(string WorkerId);
public sealed record CredentialDeliveryResponse(string Id, string Provider, string Type, long Version, string Secret)
{
    public override string ToString() => $"CredentialDeliveryResponse {{ Id = {Id}, Provider = {Provider}, Type = {Type}, Version = {Version}, Secret = [redacted] }}";
}

/// <summary>Reusable redaction for credential-like values in externally supplied operational text.</summary>
public static class SecretSanitizer
{
    public static string Sanitize(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maximumLength < 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        var safe = System.Text.RegularExpressions.Regex.Replace(value,
            "(?i)(token|password|secret|credential|api[_-]?key)(\\s*[:=]\\s*)[^\\s,;]+", "$1$2[redacted]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, "(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]+=*", "Bearer [redacted]");
        safe = new string(safe.Where(character => !char.IsControl(character)).ToArray());
        return safe[..Math.Min(safe.Length, maximumLength)];
    }
}

/// <summary>
/// Secret storage boundary. Implementations may be replaced with a dedicated secret provider without
/// changing credential metadata or assignment contracts.
/// </summary>
public interface ICredentialStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<CredentialMetadata> CreateAsync(CreateCredentialRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> GetAsync(string credentialId, CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> AssignAsync(string credentialId, string workerId, CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> ReplaceSecretAsync(string credentialId, CredentialSecretInput secret, CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> RevokeAsync(string credentialId, CancellationToken cancellationToken = default);
    Task SetWorkerDeliveryTokenAsync(string workerId, CredentialSecretInput token, CancellationToken cancellationToken = default);
    Task<bool> IsWorkerDeliveryTokenValidAsync(string workerId, string? token, CancellationToken cancellationToken = default);
    /// <summary>Call only after the transport has authenticated the caller as <paramref name="workerId"/>.</summary>
    Task<string?> RetrieveForWorkerAsync(string credentialId, string workerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local encrypted SQLite secret store. AES-GCM protects database copies; the encryption key must be
/// supplied out of band through CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY as base64 for 32 random bytes.
/// The key and running Server remain trusted, and this is not a substitute for a dedicated secret store.
/// </summary>
public sealed class SqliteCredentialStore(string databasePath, string? encryptionKey = null, TimeProvider? timeProvider = null) : ICredentialStore
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly byte[]? _key = ParseKey(encryptionKey ?? Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY"));
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath) ?? throw new InvalidDataException("Credential database path must include a directory.");
        Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS credentials (id TEXT PRIMARY KEY, provider TEXT NOT NULL, credential_type TEXT NOT NULL, secret_reference TEXT NOT NULL UNIQUE, status TEXT NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, assigned_worker_id TEXT NULL, revoked_at_utc TEXT NULL, version INTEGER NOT NULL, nonce BLOB NOT NULL, ciphertext BLOB NOT NULL, tag BLOB NOT NULL); CREATE TABLE IF NOT EXISTS worker_credential_auth (worker_id TEXT PRIMARY KEY, token_hash BLOB NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CredentialMetadata> CreateAsync(CreateCredentialRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Secret);
        ValidateLabel(request.Provider, nameof(request.Provider), 80);
        ValidateLabel(request.Type, nameof(request.Type), 80);
        if (string.IsNullOrWhiteSpace(request.Secret.Value) || request.Secret.Value.Length > 16_384)
            throw new InvalidDataException("Credential secret must contain 1 to 16384 characters.");
        var key = _key ?? throw new InvalidOperationException("Credential encryption is not configured.");
        var id = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();
        var metadata = new CredentialMetadata(id, request.Provider.Trim(), request.Type.Trim(), "credential:" + id,
            "Ready", now, now, null, null, 1);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(request.Secret.Value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try { using var aes = new AesGcm(key, tag.Length); aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(metadata.SecretReference)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO credentials (id,provider,credential_type,secret_reference,status,created_at_utc,updated_at_utc,version,nonce,ciphertext,tag) VALUES ($id,$provider,$type,$reference,'Ready',$created,$updated,1,$nonce,$ciphertext,$tag);";
        command.Parameters.AddWithValue("$id", metadata.Id);
        command.Parameters.AddWithValue("$provider", metadata.Provider);
        command.Parameters.AddWithValue("$type", metadata.Type);
        command.Parameters.AddWithValue("$reference", metadata.SecretReference);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$ciphertext", ciphertext);
        command.Parameters.AddWithValue("$tag", tag);
        await command.ExecuteNonQueryAsync(cancellationToken);
        CryptographicOperations.ZeroMemory(ciphertext);
        return metadata;
    }

    public async Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,provider,credential_type,secret_reference,status,created_at_utc,updated_at_utc,assigned_worker_id,revoked_at_utc,version FROM credentials ORDER BY created_at_utc,id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<CredentialMetadata>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadMetadata(reader));
        return items;
    }

    public async Task<CredentialMetadata?> GetAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,provider,credential_type,secret_reference,status,created_at_utc,updated_at_utc,assigned_worker_id,revoked_at_utc,version FROM credentials WHERE id=$id;";
        command.Parameters.AddWithValue("$id", credentialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMetadata(reader) : null;
    }

    public Task<CredentialMetadata?> AssignAsync(string credentialId, string workerId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _)) throw new InvalidDataException("Worker identity is invalid.");
        return UpdateAsync(credentialId, workerId, revoke: false, cancellationToken);
    }

    public Task<CredentialMetadata?> RevokeAsync(string credentialId, CancellationToken cancellationToken = default) =>
        UpdateAsync(credentialId, null, revoke: true, cancellationToken);

    public async Task SetWorkerDeliveryTokenAsync(string workerId, CredentialSecretInput token, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _)) throw new InvalidDataException("Worker identity is invalid.");
        ArgumentNullException.ThrowIfNull(token);
        if (token.Value.Length < 32 || token.Value.Length > 512) throw new InvalidDataException("Worker delivery token must contain 32 to 512 characters.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Value));
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO worker_credential_auth(worker_id,token_hash) VALUES($worker,$hash) ON CONFLICT(worker_id) DO UPDATE SET token_hash=excluded.token_hash;";
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
        CryptographicOperations.ZeroMemory(hash);
    }

    public async Task<bool> IsWorkerDeliveryTokenValidAsync(string workerId, string? token, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _) || string.IsNullOrEmpty(token)) return false;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM worker_credential_auth WHERE worker_id=$worker;";
        command.Parameters.AddWithValue("$worker", workerId);
        var stored = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        if (stored is null) return false;
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        try { return CryptographicOperations.FixedTimeEquals(stored, supplied); }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }

    public async Task<CredentialMetadata?> ReplaceSecretAsync(string credentialId, CredentialSecretInput secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrWhiteSpace(secret.Value) || secret.Value.Length > 16_384)
            throw new InvalidDataException("Credential secret must contain 1 to 16384 characters.");
        var key = _key ?? throw new InvalidOperationException("Credential encryption is not configured.");
        var metadata = await GetAsync(credentialId, cancellationToken);
        if (metadata is null || metadata.Status is not ("Ready" or "NeedsReprovision")) return metadata;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(secret.Value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try { using var aes = new AesGcm(key, tag.Length); aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(metadata.SecretReference)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var now = _timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE credentials SET status='Ready',nonce=$nonce,ciphertext=$ciphertext,tag=$tag,updated_at_utc=$now,version=version+1 WHERE id=$id AND status IN ('Ready','NeedsReprovision');";
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$ciphertext", ciphertext);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", credentialId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        CryptographicOperations.ZeroMemory(ciphertext);
        return await GetAsync(credentialId, cancellationToken);
    }

    public async Task<string?> RetrieveForWorkerAsync(string credentialId, string workerId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _)) return null;
        var key = _key ?? throw new InvalidOperationException("Credential encryption is not configured.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret_reference,assigned_worker_id,status,nonce,ciphertext,tag FROM credentials WHERE id=$id;";
        command.Parameters.AddWithValue("$id", credentialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(1) || reader.GetString(1) != workerId || reader.GetString(2) != "Ready") return null;
        var reference = reader.GetString(0);
        var nonce = (byte[])reader[3]; var ciphertext = (byte[])reader[4]; var tag = (byte[])reader[5];
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(reference));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException) { throw new InvalidDataException("Credential secret could not be decrypted."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task<CredentialMetadata?> UpdateAsync(string id, string? workerId, bool revoke, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = revoke
            ? "UPDATE credentials SET status='Revoked',revoked_at_utc=$now,updated_at_utc=$now,assigned_worker_id=NULL,nonce=X'',ciphertext=X'',tag=X'',version=version+1 WHERE id=$id AND status='Ready';"
            : "UPDATE credentials SET assigned_worker_id=$worker,updated_at_utc=$now,version=version+1 WHERE id=$id AND status='Ready';";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (!revoke) command.Parameters.AddWithValue("$worker", workerId!);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return await GetAsync(id, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private static CredentialMetadata ReadMetadata(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetInt64(9));

    private static byte[]? ParseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var key = Convert.FromBase64String(value);
            if (key.Length != 32) throw new InvalidDataException("Credential encryption key must be base64 for exactly 32 random bytes.");
            return key;
        }
        catch (FormatException) { throw new InvalidDataException("Credential encryption key must be base64 for exactly 32 random bytes."); }
    }

    private static void ValidateLabel(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl) ||
            System.Text.RegularExpressions.Regex.IsMatch(value, "(?i)(token|password|secret|credential|api[_-]?key)\\s*[:=]"))
            throw new InvalidDataException($"Credential {name} must contain 1 to {maximumLength} printable characters.");
    }
}
