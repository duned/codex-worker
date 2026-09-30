namespace CodexServer;

using Microsoft.Extensions.Configuration;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "backup")
        {
            await ServerBackupCommand.RunAsync(args);
            return;
        }
        if (args.Length > 0 && args[0] == "worker-token")
        {
            await WorkerTokenCommand.RunAsync(args);
            return;
        }
        await using var app = await ServerApplication.BuildAsync(args);
        await app.RunAsync();
    }
}

internal static class WorkerTokenCommand
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("create" or "revoke" or "revoke-worker") ||
            args[1] == "create" && args.Length is not (2 or 3) ||
            (args[1] is "revoke" or "revoke-worker") && args.Length is not (3 or 4))
            throw new ArgumentException("Usage: codex-server worker-token create [database-path] | revoke <registration-token> [database-path] | revoke-worker <worker-id> [database-path]");
        var configuration = new ServerConfiguration();
        ServerApplication.CreateBuilder([]).Configuration.GetSection("Server").Bind(configuration);
        configuration.Validate();
        var database = args[1] == "create"
            ? args.Length == 3 ? Path.GetFullPath(args[2]) : configuration.ResolveDatabasePath()
            : args.Length == 4 ? Path.GetFullPath(args[3]) : configuration.ResolveDatabasePath();
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        if (args[1] == "create")
        {
            var token = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
            Console.Error.WriteLine($"Worker registration token (valid for 15 minutes; use once). Database: {database}");
            Console.WriteLine(token);
        }
        else if (args[1] == "revoke" && await store.RevokeWorkerBootstrapTokenAsync(args[2])) Console.WriteLine("Worker registration token revoked.");
        else if (args[1] == "revoke-worker" && await store.RevokeWorkerTokenAsync(args[2])) Console.WriteLine("Worker authentication credential revoked.");
        else Console.WriteLine("Credential was not active.");
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
