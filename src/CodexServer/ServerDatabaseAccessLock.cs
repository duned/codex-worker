namespace CodexServer;

/// <summary>Coordinates Server lifetime access with offline restore and concurrent restore attempts.</summary>
internal sealed class ServerDatabaseAccessLock : IDisposable
{
    private readonly FileStream _lockFile;

    public ServerDatabaseAccessLock(string databasePath, bool forRestore)
    {
        var path = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Database path must include a directory.");
        Directory.CreateDirectory(directory);
        var lockPath = path + ".access-lock";
        try
        {
            _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            var message = forRestore
                ? "Offline restore requires exclusive database access. Stop the Server and retry after all restore operations have ended."
                : "The Server database is already in use by another Server instance or an offline restore.";
            throw new IOException(message, exception);
        }
    }

    public void Dispose() => _lockFile.Dispose();
}
