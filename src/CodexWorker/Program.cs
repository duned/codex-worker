namespace CodexWorker;

using System.Runtime.InteropServices;
using System.Diagnostics;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var output = new WorkerConsole();
        if (args is ["--codex-preflight"])
        {
            try
            {
                await new CodexExecutor(new ProcessRunner(), new CodexSettings { Model = null }).PreflightAsync(CancellationToken.None);
                return ProcessExitCodes.Success;
            }
            catch (Exception ex)
            {
                output.InfrastructureFailure(FailureDiagnosticRedactor.Redact(ex.Message));
                return ProcessExitCodes.StartupFailure;
            }
        }
        WorkerCommandLine commandLine;
        try { commandLine = WorkerCommandLine.Parse(args); }
        catch (ArgumentException ex)
        {
            output.InfrastructureFailure($"{ex.Message} Use 'codex-worker --help' for usage.");
            return ProcessExitCodes.StartupFailure;
        }
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            var helpCommand = args.Length > 0 && (args[0] is "--help" or "-h") ? "root" : commandLine.Command;
            if (helpCommand == "config" && commandLine.Arguments.FirstOrDefault() == "set") helpCommand = "config-set";
            PrintHelp(helpCommand);
            return ProcessExitCodes.Success;
        }
        if (commandLine.Command == "register") return await RegisterAsync(args, output);
        if (commandLine.Command == "provision") return await ProvisionAsync(commandLine, output);
        if (commandLine.Command == "status") return await ShowStatusAsync(commandLine, output);
        if (commandLine.Command == "config") return AdministerConfiguration(commandLine, output);
        if (commandLine.Command == "capabilities") return await ShowCapabilitiesAsync(commandLine, output);
        if (args.Length > 0 && args[0].StartsWith("-", StringComparison.Ordinal) && args[0] != "--config")
        {
            output.InfrastructureFailure($"Unknown option '{args[0]}'. Use 'codex-worker --help' for usage.");
            return ProcessExitCodes.StartupFailure;
        }
        if (commandLine.Arguments.Count > 0)
        {
            output.InfrastructureFailure("Unexpected run arguments. Use 'codex-worker run --help' for usage.");
            return ProcessExitCodes.StartupFailure;
        }
        var configurationPath = commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath;

        GlobalWorkerConfiguration? global = null;
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects = [];
        try
        {
            global = GlobalWorkerConfiguration.Load(configurationPath);
            TelegramNotifier.ValidateConfiguration(global.Telegram.Enabled,
                Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"), Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
            projects = ProjectConfigurationDiscovery.LoadForWorker(global);
        }
        catch (Exception ex)
        {
            output.InfrastructureFailure($"Configuration error: {ex.Message}");
            if (global?.Telegram.Enabled == true &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")) &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")))
            {
                using var startupTelegram = new TelegramNotifier(true, output);
                await startupTelegram.CriticalAsync(null, ex.Message, CancellationToken.None);
            }
            return ProcessExitCodes.StartupFailure;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        try { await new WorkerHost(global, projects, output, operationalLog: message => Trace.WriteLine(message)).RunAsync(shutdown.Token); return ProcessExitCodes.Success; }
        catch (Exception ex) { return ExitCodeFor(ex); }
    }

    private static async Task<int> ProvisionAsync(WorkerCommandLine commandLine, WorkerConsole output)
    {
        try
        {
            var args = commandLine.Arguments;
            var allowElevation = args.Count == 3 && args[2] == "--allow-elevation";
            if (args.Count is not (2 or 3) || args.Count == 3 && !allowElevation ||
                !Enum.TryParse<CodexProvisioning.ProvisioningCommandAction>(args[1], true, out var action) ||
                !Enum.IsDefined(action) || !args[1].All(char.IsAsciiLetter))
                throw new ArgumentException("Invalid provisioning arguments. Use 'codex-worker provision --help' for usage.");
            var configuration = GlobalWorkerConfiguration.Load(commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            var identity = await WorkerIdentity.LoadOrCreateAsync(configuration.Server.IdentityFile ?? WorkerIdentity.DefaultPath);
            var request = new CodexProvisioning.ProvisioningCommandRequest(identity, args[0], action, AllowElevation: allowElevation);
            using var stop = new CancellationTokenSource();
            using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                context => { context.Cancel = true; stop.Cancel(); });
            void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                stop.Cancel();
            }
            Console.CancelKeyPress += OnCancel;
            try
            {
                var result = await WorkerProvisioning.ExecuteLocalAsync(request, configuration.Worker.Provisioning,
                    new CodexProvisioning.NodeCapabilityDiscovery(), stop.Token, (progress, _) =>
                    {
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(progress));
                        return Task.CompletedTask;
                    });
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
                return result.Status == CodexProvisioning.ProvisioningCommandStatus.Succeeded
                    ? ProcessExitCodes.Success : ProcessExitCodes.StartupFailure;
            }
            finally { Console.CancelKeyPress -= OnCancel; }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            output.InfrastructureFailure(FailureDiagnosticRedactor.Redact(ex.Message));
            return ProcessExitCodes.StartupFailure;
        }
    }

    private static async Task<int> RegisterAsync(string[] args, WorkerConsole output)
    {
        string? token = null;
        try
        {
            string? server = null, identityFile = null;
            var capacity = 1;
            var readTokenFromStandardInput = false;
            var options = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length;)
            {
                if (!options.Add(args[index])) throw new ArgumentException("Register options must be specified only once.");
                switch (args[index])
                {
                    case "--token-stdin":
                        if (readTokenFromStandardInput) throw new ArgumentException("Specify only one bootstrap token source.");
                        readTokenFromStandardInput = true;
                        index++;
                        continue;
                    case "--server":
                    case "--token":
                    case "--identity-file":
                    case "--capacity":
                        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal) ||
                            string.IsNullOrWhiteSpace(args[index + 1]))
                            throw new ArgumentException($"Register option '{args[index]}' requires a value.");
                        if (args[index] == "--server") server = args[index + 1];
                        else if (args[index] == "--token") token = args[index + 1];
                        else if (args[index] == "--identity-file") identityFile = args[index + 1];
                        else if (!int.TryParse(args[index + 1], out capacity) || capacity is < 1 or > 8)
                            throw new ArgumentException("Register capacity must be an integer from 1 to 8.");
                        index += 2;
                        continue;
                    default: throw new ArgumentException("Unknown register option. Use the documented options below.");
                }
            }
            if (readTokenFromStandardInput && token is not null) throw new ArgumentException("Specify only one bootstrap token source.");
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Register requires a valid --server URL without credentials, query, or fragment.");
            if (readTokenFromStandardInput) token = await Console.In.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Register requires --token or a nonempty token from --token-stdin.");
            var settings = new WorkerServerSettings { Enabled = true, Url = uri.ToString().TrimEnd('/'), IdentityFile = identityFile };
            settings.Validate();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new WorkerRegistrationClient().BootstrapAsync(settings, capacity, token, shutdown.Token);
            Console.WriteLine("Worker registered. Registration does not imply execution readiness. Start or restart the service to check external execution capabilities.");
            return ProcessExitCodes.Success;
        }
        catch (Exception ex)
        {
            output.InfrastructureFailure($"Worker registration failed: {FailureDiagnosticRedactor.Redact(ex.Message, [token ?? string.Empty])}");
            if (ex is ArgumentException) PrintHelp("register");
            return ProcessExitCodes.StartupFailure;
        }
    }

    private static int ShowConfiguration(WorkerCommandLine commandLine, WorkerConsole output)
    {
        if (commandLine.Arguments.Count != 0)
        {
            output.InfrastructureFailure($"Unexpected {commandLine.Command} arguments. Use 'codex-worker {commandLine.Command} --help' for usage.");
            return ProcessExitCodes.StartupFailure;
        }
        var path = WorkerConfigurationAdministration.ResolvePath(commandLine.ConfigurationPath);
        try
        {
            var configuration = GlobalWorkerConfiguration.Load(path);
            var projects = ProjectConfigurationDiscovery.LoadForWorker(configuration);
            Console.WriteLine($"Configuration: {Path.GetFullPath(path)}");
            Console.WriteLine($"Ownership: {configuration.Projects.Ownership}");
            Console.WriteLine($"Projects: {projects.Count}");
            Console.WriteLine($"Server: {(configuration.Server.Enabled ? "enabled" : "disabled")}");
            return ProcessExitCodes.Success;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            output.InfrastructureFailure($"Configuration error: {FailureDiagnosticRedactor.Redact(ex.Message)}");
            return ProcessExitCodes.StartupFailure;
        }
    }

    private static async Task<int> ShowStatusAsync(WorkerCommandLine commandLine, WorkerConsole output)
    {
        var json = commandLine.Arguments.Count == 1 && commandLine.Arguments[0] == "--json";
        if (commandLine.Arguments.Count != (json ? 1 : 0))
        {
            output.InfrastructureFailure("Unexpected status arguments. Use 'codex-worker status --help' for usage.");
            return ProcessExitCodes.StartupFailure;
        }
        var path = commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath;
        try
        {
            var status = await WorkerStatusReporter.CreateAsync(path);
            WorkerStatusReporter.Write(status, json);
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            output.InfrastructureFailure($"Status collection failed: {FailureDiagnosticRedactor.Redact(ex.Message)}");
            return ProcessExitCodes.Success;
        }
    }

    private static int AdministerConfiguration(WorkerCommandLine commandLine, WorkerConsole output)
    {
        var path = WorkerConfigurationAdministration.ResolvePath(commandLine.ConfigurationPath);
        var arguments = commandLine.Arguments;
        var operation = arguments.Count == 0 ? "show" : arguments[0];
        try
        {
            switch (operation)
            {
                case "show":
                {
                    var json = arguments.Count == 2 && arguments[1] == "--json";
                    if (arguments.Count > 1 && !json) throw new ArgumentException("Usage: codex-worker config show [--json] [--config <path>]");
                    Console.WriteLine(WorkerConfigurationAdministration.Show(path, json));
                    return ProcessExitCodes.Success;
                }
                case "validate":
                {
                    var json = arguments.Count == 2 && arguments[1] == "--json";
                    if (arguments.Count > 1 && !json) throw new ArgumentException("Usage: codex-worker config validate [--json] [--config <path>]");
                    var diagnostics = WorkerConfigurationAdministration.Validate(path);
                    if (json)
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                        {
                            valid = diagnostics.Count == 0,
                            configurationPath = path,
                            diagnostics
                        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    else if (diagnostics.Count == 0)
                        Console.WriteLine($"Configuration is valid: {path}");
                    else
                    {
                        output.InfrastructureFailure("Configuration validation failed:\n- " + string.Join("\n- ", diagnostics));
                    }
                    return diagnostics.Count == 0 ? ProcessExitCodes.Success : ProcessExitCodes.StartupFailure;
                }
                case "set":
                    if (arguments.Count != 3)
                        throw new ArgumentException("Usage: codex-worker config set <setting> <value> [--config <path>]");
                    WorkerConfigurationAdministration.Set(path, arguments[1], arguments[2]);
                    Console.WriteLine($"Updated {arguments[1]} in {path}. Restart the Worker service for the change to take effect.");
                    return ProcessExitCodes.Success;
                default:
                    throw new ArgumentException($"Unknown config command '{operation}'. Use 'codex-worker config --help' for usage.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            output.InfrastructureFailure($"Configuration administration failed: {FailureDiagnosticRedactor.Redact(ex.Message)}");
            return ProcessExitCodes.StartupFailure;
        }
    }

    private static async Task<int> ShowCapabilitiesAsync(WorkerCommandLine commandLine, WorkerConsole output)
    {
        var arguments = commandLine.Arguments;
        var hasSubcommand = arguments.Count > 0 && (arguments[0] is "list" or "refresh");
        var action = hasSubcommand ? arguments[0] : "list";
        var optionStart = hasSubcommand ? 1 : 0;
        var json = false;
        for (var index = optionStart; index < arguments.Count; index++)
        {
            if (arguments[index] == "--json" && !json) json = true;
            else
            {
                output.InfrastructureFailure("Unexpected capabilities arguments. Use 'codex-worker capabilities --help' for usage.");
                return ProcessExitCodes.StartupFailure;
            }
        }
        try
        {
            if (commandLine.ConfigurationPath is not null)
                _ = GlobalWorkerConfiguration.Load(commandLine.ConfigurationPath);
            var inventory = await CapabilityInventoryReporter.CreateAsync(new CodexProvisioning.NodeCapabilityDiscovery(),
                refresh: action == "refresh");
            CapabilityInventoryReporter.Write(inventory, json);
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            output.InfrastructureFailure($"Capability discovery failed: {FailureDiagnosticRedactor.Redact(ex.Message)}");
            return ProcessExitCodes.StartupFailure;
        }
    }

    private static void PrintHelp(string command)
    {
        if (command == "register")
        {
            Console.WriteLine("Usage: codex-worker register --server <url> (--token <registration-token> | --token-stdin) [--capacity 1..8] [--identity-file <path>]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --server <url>                 Required. Codex Server base URL (HTTP or HTTPS).");
            Console.WriteLine("  --token <registration-token>   Required unless --token-stdin. One-time registration token.");
            Console.WriteLine("  --token-stdin                  Read the token from stdin; mutually exclusive with --token.");
            Console.WriteLine("  --capacity <1..8>              Optional. Execution capacity; defaults to 1.");
            Console.WriteLine("  --identity-file <path>         Optional. Worker identity path; defaults to ~/.codex-worker/worker-id.");
            Console.WriteLine("  -h, --help                     Show help without contacting the Server or writing identity files.");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  codex-worker register --server https://server.example --token <registration-token>");
            Console.WriteLine("  codex-worker register --server https://server.example --token-stdin --capacity 2");
            return;
        }
        var configOption = "[--config <path>]";
        switch (command)
        {
            case "run":
                Console.WriteLine($"Usage: codex-worker run {configOption}");
                Console.WriteLine("Start normal Worker execution.");
                Console.WriteLine($"Default configuration: {WorkerCommandLine.DefaultConfigurationPath}");
                Console.WriteLine("Example: codex-worker run --config /path/to/worker.yml");
                break;
            case "status":
                Console.WriteLine($"Usage: codex-worker status {configOption}");
                Console.WriteLine("Show local Worker status without contacting Codex Server or starting execution.");
                Console.WriteLine("Options: --json  Write the stable versioned status contract as JSON.");
                Console.WriteLine("Examples: codex-worker status; codex-worker status --config /path/to/worker.yml --json");
                break;
            case "config":
                Console.WriteLine(WorkerConfigurationAdministration.HelpText);
                Console.WriteLine("Example: codex-worker config show --json");
                Console.WriteLine("Example: codex-worker config set worker.provisioning.enabled true");
                break;
            case "config-set":
                Console.WriteLine("Usage: codex-worker config set <setting> <value> [--config <path>]");
                Console.WriteLine("Updates a bounded known Worker setting after validating the complete candidate configuration.");
                Console.WriteLine("Action policy settings accept comma-separated keys; an empty value clears a list.");
                Console.WriteLine("A service restart is required for the change to take effect.");
                break;
            case "capabilities":
                Console.WriteLine($"Usage: codex-worker capabilities <list|refresh> {configOption} [--json]");
                Console.WriteLine("List local capability observations or force a fresh detection.");
                Console.WriteLine("Missing capabilities are reported as inventory state.");
                Console.WriteLine("Options: --json  Write the typed capability inventory contract as JSON.");
                Console.WriteLine("Examples: codex-worker capabilities list; codex-worker capabilities refresh --json");
                break;
            case "provision":
                Console.WriteLine($"Usage: codex-worker provision {configOption} <capability-id> <action> [--allow-elevation]");
                Console.WriteLine("Run a typed local capability operation under Worker provisioning policy.");
                Console.WriteLine("Example: codex-worker provision --config /etc/codex-worker/worker.yml git detect");
                break;
            default:
                Console.WriteLine("Usage: codex-worker [command] [options]");
                Console.WriteLine("Commands: run, status, config, capabilities, provision, register");
                Console.WriteLine("Use 'codex-worker <command> --help' for command options and examples.");
                Console.WriteLine($"Normal execution defaults to {WorkerCommandLine.DefaultConfigurationPath}.");
                Console.WriteLine("Legacy: codex-worker <worker.yml>");
                break;
        }
        Console.WriteLine("Options: -h, --help  Show this help.");
    }

    internal static int ExitCodeFor(Exception? failure) => failure switch
    {
        null => ProcessExitCodes.Success,
        WorkerStartupException => ProcessExitCodes.StartupFailure,
        _ => ProcessExitCodes.RuntimeFailure
    };
}
