namespace CodexServer;

using Microsoft.Extensions.Configuration;

public static class Program
{
    private const string Help = """
        Codex Server
        Usage: CodexServer [--Server:<setting>=<value>] [--Logging:<setting>=<value>]
               CodexServer --help | --version
               CodexServer status [--json] [Server configuration options]
               CodexServer diagnostics [--json] [Server configuration options]
               CodexServer config <show|validate> [--json] [Server configuration options]
               CodexServer worker-token create [database-path]
               CodexServer worker-token revoke <registration-token> [database-path]
               CodexServer worker-token revoke-worker <worker-id> [database-path]
               CodexServer worker <show|enable|drain|disable|revoke-token|revoke-delivery-token> <worker-id> [database-path]
               CodexServer backup <export|restore> <archive-path> <database-path>
               CodexServer backup validate <archive-path>

        No arguments starts the web Server. Help, version and administrative commands
        do not start the Server. Status and diagnostics inspect configured local state;
        they do not contact the running loopback Server. Process health is not observed.
        Configuration show and validate are offline and do not open the database.
        Backup restore requires the service to be stopped.

        Installed Linux Server: sudo codex-server status|diagnostics|config show|validate|worker <operation>
        The installed helper uses /etc/codex-server/server.env and the service account
        for local administration, Worker administration and token commands, even while codex-server.service is running.
        Create a Worker token with sudo codex-server worker-token create.
        The one-use bootstrap token is printed only to stdout and expires in 15 minutes.
        Protect that output; use codex-worker register --token-stdin to register.

        Direct token and Worker administration commands require an explicit database-path or configured
        Server:DataDirectory / Server:DatabasePath (environment: Server__DataDirectory /
        Server__DatabasePath). They never silently select the invoking user's home.
        --help (-h) prints this help; --version prints the Server version.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"] || args is ["worker-token", "--help"] or ["worker", "--help"] or ["backup", "--help"])
        {
            Console.WriteLine(Help);
            return 0;
        }
        if (args is ["--version"])
        {
            Console.WriteLine($"Codex Server {ServerApplication.DisplayVersion}");
            return 0;
        }
        if (args.Length > 0 && args[0] is "status" or "diagnostics" or "config")
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
                Console.Error.WriteLine("Invalid administrative arguments. Run CodexServer --help for usage.");
                return 2;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Do not echo arguments or exception messages: these can contain credentials.
                Console.Error.WriteLine("Administrative command failed. Check the service state configuration, file permissions and command usage (CodexServer --help).");
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
            var cli = new ServerAdministrationCli(new ServerConfigurationAdministrationService(), new LocalServerAdministrationServiceFactory());
            var exitCode = await cli.RunAsync(args.Skip(1).ToArray(), cancellation.Token);
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

internal static class WorkerAdministrationCommand
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 3 || args.Length > 4 || args[1] is not ("show" or "enable" or "drain" or "disable" or "revoke-token" or "revoke-delivery-token") ||
            !Guid.TryParseExact(args[2], "N", out _))
            throw new ArgumentException("Usage: codex-server worker <show|enable|drain|disable|revoke-token|revoke-delivery-token> <worker-id> [database-path]");
        var configuration = new ServerConfiguration();
        var section = ServerApplication.CreateBuilder([]).Configuration.GetSection("Server");
        section.Bind(configuration);
        var serviceContext = Environment.GetEnvironmentVariable("CODEX_SERVER_OPERATOR_SERVICE_CONTEXT") == "1";
        if (args.Length == 3 && !serviceContext && section["DataDirectory"] is null && section["DatabasePath"] is null)
            throw new InvalidDataException("Worker administration requires explicit service state configuration.");
        configuration.Validate();
        var database = args.Length == 4 ? Path.GetFullPath(args[3]) : configuration.ResolveDatabasePath();
        var registry = new SqliteRegistryStore(database, configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds);
        var credentials = new SqliteCredentialStore(database);
        await registry.InitializeAsync();
        await credentials.InitializeAsync();
        var workerId = args[2];
        var worker = await registry.GetWorkerAsync(workerId);
        if (worker is null)
        {
            Console.WriteLine("Worker was not found.");
            return;
        }
        switch (args[1])
        {
            case "show":
                var delivery = await credentials.GetWorkerDeliveryAuthorizationStatusAsync(workerId);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Worker = worker, CredentialDeliveryAuthorization = delivery },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
                break;
            case "enable":
            case "drain":
            case "disable":
                var policy = args[1] switch
                {
                    "enable" => WorkerSchedulingPolicy.Enabled,
                    "drain" => WorkerSchedulingPolicy.Draining,
                    _ => WorkerSchedulingPolicy.Disabled
                };
                worker = await registry.SetWorkerSchedulingPolicyAsync(workerId, policy) ?? worker;
                var drainState = worker.SchedulingPolicy == WorkerSchedulingPolicy.Draining
                    ? worker.ActiveAssignments == 0 ? "drained" : $"draining; {worker.ActiveAssignments} active assignments retain their leases"
                    : worker.SchedulingPolicy.ToLowerInvariant();
                Console.WriteLine($"Worker scheduling policy: {drainState}.");
                break;
            case "revoke-token":
                var apiTokenRevoked = await registry.RevokeWorkerTokenAsync(workerId);
                Console.WriteLine(apiTokenRevoked
                    ? "Per-Worker API token revoked; calls using it are denied, and active leases may expire into recovery. The shared Server registration-token fallback remains server-wide if configured."
                    : "No active per-Worker API authentication token was found.");
                break;
            case "revoke-delivery-token":
                var deliveryTokenRevoked = await credentials.RevokeWorkerDeliveryTokenAsync(workerId);
                Console.WriteLine(deliveryTokenRevoked
                    ? "Worker credential-delivery authorization revoked. Worker API authentication is unchanged."
                    : "No active Worker credential-delivery authorization was found.");
                break;
        }
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
