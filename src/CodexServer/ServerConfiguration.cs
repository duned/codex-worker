namespace CodexServer;

public sealed class ServerConfiguration
{
    /// <summary>Public instance name; defaults to the host name when not configured.</summary>
    public string? DisplayName { get; set; }
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Environment.MachineName : DisplayName;
    public CodexProvisioning.GeneratedMessageOrigin MessageOrigin => new(CodexProvisioning.CodexComponent.Server, EffectiveDisplayName);
    public string ListenUrl { get; set; } = "http://127.0.0.1:5090";
    /// <summary>Directory for durable Server state. Relative paths are resolved from the user's home directory.</summary>
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "codex-server");
    /// <summary>Optional legacy override for the SQLite file; relative values are resolved inside DataDirectory.</summary>
    public string? DatabasePath { get; set; }
    // Mutating local provisioning and elevation each require operator opt-in.
    public bool EnableLocalProvisioning { get; set; }
    public bool AllowLocalProvisioningElevation { get; set; }
    public int WorkerStaleAfterSeconds { get; set; } = 90;
    public int ExecutionLeaseDurationSeconds { get; set; } = 900;
    public int ExecutionLeaseRenewalIntervalSeconds { get; set; } = 60;
    /// <summary>Registration credential is read from CODEX_SERVER_REGISTRATION_TOKEN, never configuration files.</summary>
    public string? RegistrationToken => Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
    /// <summary>Management credential is read from CODEX_SERVER_MANAGEMENT_TOKEN, never configuration files.</summary>
    public string? ManagementToken => Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");

    public void Validate()
    {
        if (DisplayName is not null && (DisplayName.Length > 200 || DisplayName.Any(char.IsControl)))
            throw new InvalidDataException("Server:DisplayName must contain at most 200 printable characters.");
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Server:ListenUrl must be an absolute HTTP or HTTPS URL with a valid port and no credentials, query, or fragment.");
        if (uri.Scheme == "http" && !(System.Net.IPAddress.TryParse(uri.Host, out var address)
                ? System.Net.IPAddress.IsLoopback(address) : uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Server:ListenUrl must use HTTPS unless it binds to loopback.");
        if (string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidDataException("Server:DataDirectory must not be empty.");
        if (DatabasePath is not null && string.IsNullOrWhiteSpace(DatabasePath))
            throw new InvalidDataException("Server:DatabasePath must not be empty when specified.");
        if (WorkerStaleAfterSeconds is < 10 or > 3600)
            throw new InvalidDataException("Server:WorkerStaleAfterSeconds must be between 10 and 3600.");
        if (ExecutionLeaseDurationSeconds is < 120 or > 86400)
            throw new InvalidDataException("Server:ExecutionLeaseDurationSeconds must be between 120 and 86400.");
        if (ExecutionLeaseRenewalIntervalSeconds is < 10 or > 3600 || ExecutionLeaseRenewalIntervalSeconds * 3 >= ExecutionLeaseDurationSeconds)
            throw new InvalidDataException("Server:ExecutionLeaseRenewalIntervalSeconds must be at least 10 and less than one third of ExecutionLeaseDurationSeconds.");
    }

    public string ResolveDataDirectory() => Path.GetFullPath(DataDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string ResolveDatabasePath() => DatabasePath is null
        ? Path.Combine(ResolveDataDirectory(), "codex-server.db")
        : Path.GetFullPath(DatabasePath, ResolveDataDirectory());
}
