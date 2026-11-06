namespace CodexWorker;

using CodexProvisioning;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class StandaloneReportingSettings
{
    public string CredentialFile { get; set; } = "~/.codex-worker/observer.json";
    public bool AllowBranchMaintenance { get; set; }
    public int IntervalSeconds { get; set; } = 30;
    internal void ResolveAndValidate(string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(CredentialFile) || CredentialFile.Any(char.IsControl))
            throw new InvalidDataException("reporting.credentialFile must be a local file path.");
        if (IntervalSeconds is < 10 or > 3600) throw new InvalidDataException("reporting.intervalSeconds must be between 10 and 3600.");
        CredentialFile = WorkerPath.Resolve(CredentialFile, Path.GetDirectoryName(configurationPath) ?? throw new InvalidDataException("Configuration directory is missing."));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StandaloneReportingCredential(int ContractVersion, string ObserverId, string Server, string Credential,
    bool Enabled, bool Accepted, string LastResult = "pending", DateTimeOffset? LastReportAtUtc = null,
    ObserverStatusSnapshot? PendingSnapshot = null)
{
    public bool IsValid() => ContractVersion == 1 && ObserverProtocol.ValidIdentity(ObserverId) &&
        ObserverProtocol.ValidOrigin(Server) && ObserverProtocol.ValidCredential(Credential) &&
        (LastReportAtUtc is null || ObserverProtocol.Timestamp(LastReportAtUtc.Value)) &&
        (PendingSnapshot is null || PendingSnapshot.IsValid() && PendingSnapshot.ObserverId == ObserverId) &&
        LastResult is "pending" or "paired" or "reporting" or "unavailable" or "unauthorized" or "rejected";
    public override string ToString() => "[protected observer credential]";
}

/// <summary>Node-local observer opt-in and credential, independent of managed enrollment files.</summary>
internal sealed class StandaloneReportingStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { AllowDuplicateProperties = false, MaxDepth = 8 };
    // One bounded snapshot plus the credential envelope; no execution logs or diagnostics.
    private const int MaximumStateBytes = 96 * 1024;
    internal string FilePath { get; } = WorkerPath.Resolve(path, Environment.CurrentDirectory);
    private static void RejectLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Reporting files must not be symbolic links.");
    }
    internal async Task<StandaloneReportingCredential?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return null;
        RejectLink(FilePath);
        if (new FileInfo(FilePath).Length > MaximumStateBytes) throw new InvalidDataException("Invalid local reporting state.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var value = JsonSerializer.Deserialize<StandaloneReportingCredential>(await File.ReadAllTextAsync(FilePath, ct), Json);
        return value is not null && value.IsValid() ? value : throw new InvalidDataException("Invalid local reporting state.");
    }
    internal async Task SaveAsync(StandaloneReportingCredential value, CancellationToken ct)
    {
        if (!value.IsValid()) throw new InvalidDataException("Invalid local reporting state.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? throw new InvalidDataException("Reporting directory is missing."));
        RejectLink(FilePath);
        var body = JsonSerializer.Serialize(value);
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaximumStateBytes) throw new InvalidDataException("Invalid local reporting state.");
        await WorkerRegistrationFile.ReplaceAsync(FilePath, body, ct);
    }
    internal void Delete()
    {
        RejectLink(FilePath);
        File.Delete(FilePath);
    }
    internal async Task<FileStream> LockAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(FilePath) ?? throw new InvalidDataException("Reporting directory is missing.");
        Directory.CreateDirectory(directory);
        var lockPath = FilePath + ".lock";
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            RejectLink(lockPath);
            try { return new FileStream(lockPath, options); }
            catch (IOException) when (attempt < 100) { await Task.Delay(TimeSpan.FromMilliseconds(100), ct); }
        }
    }
}
