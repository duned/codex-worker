namespace CodexServer;

using CodexProvisioning;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>Creates and restores portable, versioned Server state archives.</summary>
public sealed class ServerBackup(string? databasePath = null)
{
    public const int CurrentFormatVersion = 1;
    private const string ManifestName = "manifest.json";
    private const string DatabaseName = "state.sqlite";
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);
    private readonly string? _databasePath = databasePath is null ? null : Path.GetFullPath(databasePath);

    public async Task ExportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(archivePath);
        var databasePath = RequireDatabasePath();
        if (string.Equals(target, databasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Backup archive path must differ from the Server database path.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Backup path must include a directory.");
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, $".codex-server-backup-{Guid.NewGuid():N}");
        var databaseCopy = Path.Combine(staging, DatabaseName);
        var temporaryArchive = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            await using (var source = new SqliteConnection(ConnectionString(databasePath)))
            await using (var destination = new SqliteConnection(ConnectionString(databaseCopy)))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
            }

            await ScrubSecretsAsync(databaseCopy, cancellationToken);
            await ValidateDatabaseAsync(databaseCopy, cancellationToken);
            await using (var file = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
                await using (var stream = manifest.Open())
                    await JsonSerializer.SerializeAsync(stream, new BackupManifest(CurrentFormatVersion, DateTimeOffset.UtcNow,
                        RegistrySchemaVersion: SqliteRegistryStore.CurrentSchemaVersion,
                        Contents: ["workers and scheduling policy", "projects and revisions", "execution metadata, history, queue and leases", "legacy provisioning plans", "typed provisioning command history when present", "credential metadata; ready credentials require re-provisioning", "revoked Worker API-token metadata"]), ManifestJson, cancellationToken);
                archive.CreateEntryFromFile(databaseCopy, DatabaseName, CompressionLevel.Optimal);
            }
            File.Move(temporaryArchive, target, overwrite: true);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
    }

    public async Task ValidateAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var extracted = await ExtractAndValidateAsync(archivePath, cancellationToken);
        File.Delete(extracted);
    }

    /// <summary>Restore requires the Server process to be stopped so no connection can retain the old database.</summary>
    public async Task RestoreOfflineAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var databasePath = RequireDatabasePath();
        var targetDirectory = Path.GetDirectoryName(databasePath) ?? throw new InvalidDataException("Database path must include a directory.");
        Directory.CreateDirectory(targetDirectory);
        using var exclusiveAccess = new ServerDatabaseAccessLock(databasePath, forRestore: true);
        var restored = await ExtractAndValidateAsync(archivePath, cancellationToken, targetDirectory);
        try
        {
            await using (var poolKey = new SqliteConnection(ConnectionString(databasePath)))
            {
                await poolKey.OpenAsync(cancellationToken);
                SqliteConnection.ClearPool(poolKey);
            }
            DeleteIfExists(databasePath + "-wal");
            DeleteIfExists(databasePath + "-shm");
            File.Move(restored, databasePath, overwrite: true);
        }
        finally { DeleteIfExists(restored); }
    }

    private async Task<string> ExtractAndValidateAsync(string archivePath, CancellationToken cancellationToken, string? directory = null)
    {
        var path = Path.GetFullPath(archivePath);
        var temp = Path.Combine(directory ?? Path.GetTempPath(), $"codex-server-restore-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            if (archive.Entries.Count != 2 || archive.GetEntry(ManifestName) is not { } manifestEntry || archive.GetEntry(DatabaseName) is not { } databaseEntry)
                throw new InvalidDataException("Backup archive must contain exactly a manifest and a SQLite state database.");
            BackupManifest? manifest;
            await using (var stream = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ManifestJson, cancellationToken);
            if (manifest is null || manifest.FormatVersion != CurrentFormatVersion || manifest.RegistrySchemaVersion != SqliteRegistryStore.CurrentSchemaVersion)
                throw new InvalidDataException("Backup format or registry schema version is not supported.");
            await using (var stream = databaseEntry.Open())
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.CopyToAsync(output, cancellationToken);
            await ValidateDatabaseAsync(temp, cancellationToken);
            return temp;
        }
        catch
        {
            DeleteIfExists(temp);
            throw;
        }
    }

    private static async Task ScrubSecretsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        foreach (var (table, columns, scrubSql) in new[]
        {
            ("credentials", new[] { "status", "assigned_worker_id", "nonce", "ciphertext", "tag" },
                "UPDATE credentials SET status=CASE WHEN status='Ready' THEN 'NeedsReprovision' ELSE status END, assigned_worker_id=NULL, nonce=X'', ciphertext=X'', tag=X'';"),
            ("worker_credential_auth", new[] { "worker_id", "token_hash", "revoked_at_utc" }, "DELETE FROM worker_credential_auth;"),
            ("worker_auth_tokens", new[] { "worker_id", "token_hash", "created_at_utc", "revoked_at_utc" },
                "UPDATE worker_auth_tokens SET token_hash=zeroblob(length(token_hash)),revoked_at_utc=COALESCE(revoked_at_utc,strftime('%Y-%m-%dT%H:%M:%f+00:00','now'));"),
            ("provisioning_commands", new[] { "body" }, "UPDATE provisioning_commands SET body=json_remove(body, '$.LoginInstructions');"),
            ("worker_bootstrap_tokens", new[] { "token_hash", "expires_at_utc", "consumed_at_utc" }, "DELETE FROM worker_bootstrap_tokens;")
        })
        {
            if (!await TableExistsAsync(connection, table, cancellationToken)) continue;
            await RequireColumnsAsync(connection, table, columns, cancellationToken);
            command.CommandText = scrubSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ValidateDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath, readOnly: true));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Backup database failed SQLite integrity validation.");
        command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton=1;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != SqliteRegistryStore.CurrentSchemaVersion)
            throw new InvalidDataException("Backup database schema version is not supported.");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('workers','projects','execution_metadata','execution_requests','execution_leases','provisioning_plans','credentials','worker_credential_auth');";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 8)
            throw new InvalidDataException("Backup database is missing required Server state tables.");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('worker_auth_tokens','worker_bootstrap_tokens');";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 2)
            throw new InvalidDataException("Backup database is missing required Worker authentication metadata tables.");

        await RequireColumnsAsync(connection, "worker_auth_tokens", ["worker_id", "token_hash", "created_at_utc", "revoked_at_utc"], cancellationToken);
        await RequireColumnsAsync(connection, "worker_bootstrap_tokens", ["token_hash", "expires_at_utc", "consumed_at_utc", "worker_id", "operation"], cancellationToken);
        await RequireColumnsAsync(connection, "credentials", ["id", "provider", "credential_type", "secret_reference", "status", "created_at_utc", "updated_at_utc", "assigned_worker_id", "revoked_at_utc", "version", "nonce", "ciphertext", "tag"], cancellationToken);
        await RequireColumnsAsync(connection, "worker_credential_auth", ["worker_id", "token_hash", "revoked_at_utc"], cancellationToken);

        command.CommandText = "SELECT COUNT(*) FROM credentials WHERE status NOT IN ('NeedsReprovision','Revoked') OR assigned_worker_id IS NOT NULL OR length(nonce)>0 OR length(ciphertext)>0 OR length(tag)>0;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Backup contains an active credential assignment or secret material.");
        command.CommandText = "SELECT COUNT(*) FROM worker_credential_auth;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Backup contains Worker credential delivery authentication material.");
        command.CommandText = "SELECT COUNT(*) FROM worker_auth_tokens WHERE revoked_at_utc IS NULL OR token_hash != zeroblob(length(token_hash));";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Backup contains an active Worker API authentication token.");
        command.CommandText = "SELECT COUNT(*) FROM worker_bootstrap_tokens;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Backup contains reusable Worker bootstrap authorizations.");

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='provisioning_commands';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            await RequireColumnsAsync(connection, "provisioning_commands", ["id", "node", "capability", "active", "body"], cancellationToken);
            await ValidateProvisioningCommandsAsync(connection, cancellationToken);
        }
    }

    private static async Task RequireColumnsAsync(SqliteConnection connection, string table, IReadOnlyCollection<string> requiredColumns,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken)) actual.Add(reader.GetString(1));
        if (!requiredColumns.All(actual.Contains))
            throw new InvalidDataException($"Backup database table '{table}' does not match the current Server metadata schema.");
    }

    private static async Task ValidateProvisioningCommandsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, node, capability, active, body FROM provisioning_commands ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ProvisioningCommand? operation;
            try { operation = JsonSerializer.Deserialize<ProvisioningCommand>(reader.GetString(4)); }
            catch (JsonException exception)
            { throw new InvalidDataException("Backup contains invalid typed provisioning command history.", exception); }
            if (operation is null || operation.Request is not { } request || operation.LoginInstructions is not null ||
                operation.Id != reader.GetString(0) || request.NodeId != reader.GetString(1) || request.CapabilityId != reader.GetString(2) ||
                !ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request) ||
                !Enum.IsDefined(operation.Status) || !Enum.IsDefined(operation.Diagnostic) ||
                !ValidProvisioningCommandLifecycle(operation) ||
                reader.GetInt32(3) != (ProvisioningCommandProtocol.Terminal(operation.Status) ? 0 : 1))
                throw new InvalidDataException("Backup contains typed provisioning command history that does not match the current Server metadata schema.");
        }
    }

    private static bool ValidProvisioningCommandLifecycle(ProvisioningCommand operation)
    {
        var diagnosticMatchesStatus = operation.Status switch
        {
            ProvisioningCommandStatus.Pending => operation.Diagnostic == ProvisioningDiagnostic.Queued,
            ProvisioningCommandStatus.Running => operation.Diagnostic == ProvisioningDiagnostic.Executing,
            ProvisioningCommandStatus.Succeeded => operation.Diagnostic == ProvisioningDiagnostic.Completed,
            ProvisioningCommandStatus.Failed => operation.Diagnostic is ProvisioningDiagnostic.Unsupported or
                ProvisioningDiagnostic.Denied or ProvisioningDiagnostic.ProcessFailed or ProvisioningDiagnostic.Interrupted,
            ProvisioningCommandStatus.Cancelled => operation.Diagnostic == ProvisioningDiagnostic.Cancelled,
            ProvisioningCommandStatus.TimedOut => operation.Diagnostic == ProvisioningDiagnostic.TimedOut,
            _ => false
        };
        if (!diagnosticMatchesStatus) return false;

        return operation.Status switch
        {
            ProvisioningCommandStatus.Pending => operation.StartedAtUtc is null && operation.DeadlineUtc is null && operation.CompletedAtUtc is null,
            ProvisioningCommandStatus.Running => operation.StartedAtUtc is not null && operation.DeadlineUtc is not null && operation.CompletedAtUtc is null,
            ProvisioningCommandStatus.Cancelled => operation.CompletedAtUtc is not null &&
                (operation.StartedAtUtc is null && operation.DeadlineUtc is null || operation.StartedAtUtc is not null && operation.DeadlineUtc is not null),
            ProvisioningCommandStatus.Succeeded or ProvisioningCommandStatus.Failed or ProvisioningCommandStatus.TimedOut =>
                operation.StartedAtUtc is not null && operation.DeadlineUtc is not null && operation.CompletedAtUtc is not null,
            _ => false
        };
    }

    private string RequireDatabasePath() => _databasePath ?? throw new InvalidOperationException("This backup operation requires a Server database path.");
    private static string ConnectionString(string path, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path };
        if (readOnly) builder.Mode = SqliteOpenMode.ReadOnly;
        return builder.ToString();
    }
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    private sealed record BackupManifest(int FormatVersion, DateTimeOffset CreatedAtUtc, int RegistrySchemaVersion, string[] Contents);
}
