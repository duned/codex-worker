namespace CodexServer;

using Microsoft.Extensions.Configuration;

public static class Program
{
    private const string Help = """
        Codex Server
        Usage: CodexServer [--Server:<setting>=<value>] [--Logging:<setting>=<value>]
               codex-server <command> [arguments] [options]
               codex-server --help | -h | --version

        Commands:
          status, diagnostics  Inspect local Server readiness and aggregate state
          config               Show, validate or update installed configuration
          projects             Manage project definitions and revisions
          executions           Inspect, cancel or reconcile execution requests
          github               Inspect project Issues and manage eligibility/relationships
          credential           Manage protected credentials and Worker assignments
          provision            Queue and inspect typed node provisioning operations
          worker               List registered Worker IDs, inspect and control scheduling
          worker-token         Create or revoke Worker registration/API tokens
          backup               Export, validate or restore Server backups

        No arguments starts the web Server. Administration runs locally without starting it.
        Use codex-server <command> --help (or -h) for contextual command help.
        Installed Linux helper: sudo codex-server <command>; uses /etc/codex-server/server.env
        and the service account. Example: sudo codex-server worker-token create
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(Help);
            return 0;
        }
        if (ServerCommandHelp.TryWrite(args, Console.Out)) return 0;
        if (args is ["--version"])
        {
            Console.WriteLine($"Codex Server {ServerApplication.DisplayVersion}");
            return 0;
        }
        if (args.Length > 0 && args[0] is "status" or "diagnostics" or "config" or "projects" or "executions" or "github" or "credential" or "provision")
            return await RunLocalAdministrationAsync(args);
        if (args.Length > 0 && args[0] is "backup" or "worker-token" or "worker")
        {
            try
            {
                if (args[0] == "backup") await ServerBackupCommand.RunAsync(args);
                else if (args[0] == "worker-token") await WorkerTokenCommand.RunAsync(args);
                else await WorkerAdministrationCommand.RunAsync(args);
                return 0;
            }
            catch (ArgumentException)
            {
                Console.Error.WriteLine("Invalid administrative arguments. " + ServerCommandHelp.UsageHint(args));
                return 2;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Do not echo arguments or exception messages: these can contain credentials.
                Console.Error.WriteLine("Administrative command failed. Check the service state configuration and file permissions. " + ServerCommandHelp.UsageHint(args));
                return 1;
            }
        }
        if (!ValidHostArguments(args))
        {
            Console.Error.WriteLine("Unknown command or invalid Server option. Run CodexServer --help for usage.");
            return 2;
        }
        await using var app = await ServerApplication.BuildAsync(args);
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> RunLocalAdministrationAsync(string[] args)
    {
        using var userCancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            userCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(userCancellation.Token, timeout.Token);
        try
        {
            var configuration = new ServerConfigurationAdministrationService();
            var exitCode = args[0] == "provision"
                ? await new ServerProvisioningCommandCli(configuration, new LocalProvisioningCommandAdministrationServiceFactory())
                    .RunAsync(args.Skip(1).ToArray(), cancellation.Token)
                : args[0] == "credential"
                    ? await new ServerCredentialAdministrationCli(configuration, new LocalServerCredentialAdministrationServiceFactory())
                        .RunAsync(args.Skip(1).ToArray(), cancellation.Token)
                : await new ServerAdministrationCli(configuration, new LocalServerAdministrationServiceFactory())
                    .RunAsync(args, cancellation.Token);
            if (userCancellation.IsCancellationRequested)
            {
                Console.Error.WriteLine("Server administration command canceled.");
                return ServerAdministrationExitCodes.Canceled;
            }
            if (timeout.IsCancellationRequested)
            {
                Console.Error.WriteLine("Server administration command timed out after 15 seconds. Check local database availability and try again.");
                return ServerAdministrationExitCodes.OperationalFailure;
            }
            return exitCode;
        }
        catch (OperationCanceledException) when (userCancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Server administration command canceled.");
            return ServerAdministrationExitCodes.Canceled;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Console.Error.WriteLine("Server administration command timed out after 15 seconds. Check local database availability and try again.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }

    private static bool ValidHostArguments(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index].Split('=', 2)[0];
            if (!option.StartsWith("--Server:", StringComparison.OrdinalIgnoreCase) &&
                !option.StartsWith("--Logging:", StringComparison.OrdinalIgnoreCase)) return false;
            if (!args[index].Contains('=') &&
                (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal))) return false;
        }
        return true;
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
        var section = ServerApplication.CreateBuilder([]).Configuration.GetSection("Server");
        section.Bind(configuration);
        var explicitDatabase = args[1] == "create" ? args.Length == 3 : args.Length == 4;
        // The installed helper runs as the service account with the service's environment.
        // Its opt-in preserves legacy service-home defaults without overriding appsettings.
        var serviceContext = Environment.GetEnvironmentVariable("CODEX_SERVER_OPERATOR_SERVICE_CONTEXT") == "1";
        if (!explicitDatabase && !serviceContext && section["DataDirectory"] is null && section["DatabasePath"] is null)
            throw new InvalidDataException("Token commands require explicit service state configuration.");
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
        else if (args[1] == "revoke-worker" && await store.RevokeWorkerTokenAsync(args[2]))
            Console.WriteLine("Per-Worker API token revoked; calls using it are denied, and active leases may expire into recovery. The shared Server registration-token fallback remains server-wide if configured.");
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
