namespace CodexServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "backup")
        {
            await ServerBackupCommand.RunAsync(args);
            return;
        }
        await using var app = await ServerApplication.BuildAsync(args);
        await app.RunAsync();
    }
}

internal static class ServerBackupCommand
{
    public static async Task RunAsync(string[] args)
    {
        var validArguments = args.Length >= 3 && (args[1] switch
        {
            "validate" => args.Length == 3,
            "export" or "restore" => args.Length == 4,
            _ => false
        });
        if (!validArguments)
            throw new ArgumentException("Usage: dotnet run --project src/CodexServer/CodexServer.csproj -- backup <export|validate|restore> <archive-path> [database-path]");
        var backup = new ServerBackup(args.Length == 4 ? args[3] : null);
        switch (args[1])
        {
            case "export": await backup.ExportAsync(args[2]); break;
            case "validate": await backup.ValidateAsync(args[2]); break;
            case "restore": await backup.RestoreOfflineAsync(args[2]); break;
        }
        Console.WriteLine($"Server backup {args[1]} completed: {Path.GetFullPath(args[2])}");
    }
}
