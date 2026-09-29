namespace CodexServer;

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
                        Contents: ["workers", "projects", "execution metadata and history", "execution queue and leases", "provisioning plans", "credential metadata"]), ManifestJson, cancellationToken);
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
        var restored = await ExtractAndValidateAsync(archivePath, cancellationToken, targetDirectory);
        try
        {
            if (File.Exists(databasePath))
            {
                await using var current = new SqliteConnection(ConnectionString(databasePath));
                await current.OpenAsync(cancellationToken);
                await using var checkpoint = current.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken);
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
        command.CommandText = "UPDATE credentials SET status=CASE WHEN status='Ready' THEN 'NeedsReprovision' ELSE status END, assigned_worker_id=NULL, nonce=X'', ciphertext=X'', tag=X''; DELETE FROM worker_credential_auth;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
        command.CommandText = "SELECT COUNT(*) FROM credentials WHERE length(nonce)>0 OR length(ciphertext)>0 OR length(tag)>0;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Backup contains credential secret material.");
        command.CommandText = "SELECT COUNT(*) FROM worker_credential_auth;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Backup contains Worker credential delivery authentication material.");
    }

    private string RequireDatabasePath() => _databasePath ?? throw new InvalidOperationException("This backup operation requires a Server database path.");
    private static string ConnectionString(string path, bool readOnly = false) => new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString();
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    private sealed record BackupManifest(int FormatVersion, DateTimeOffset CreatedAtUtc, int RegistrySchemaVersion, string[] Contents);
}
