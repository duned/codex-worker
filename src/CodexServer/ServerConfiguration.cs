namespace CodexServer;

public sealed class ServerConfiguration
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:5090";
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-server", "codex-server.db");

    public void Validate()
    {
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Server:ListenUrl must be an absolute HTTP or HTTPS URL with a valid port and no credentials, query, or fragment.");
        if (string.IsNullOrWhiteSpace(DatabasePath))
            throw new InvalidDataException("Server:DatabasePath must not be empty.");
    }

    public string ResolveDatabasePath() => Path.GetFullPath(DatabasePath);
}
