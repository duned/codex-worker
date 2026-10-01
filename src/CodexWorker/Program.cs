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
        if (commandLine.Command is "status" or "config" or "capabilities")
        {
            var administrationCli = new WorkerAdministrationCli(new WorkerStatusService(),
                new WorkerConfigurationAdministrationService(),
                new WorkerCapabilityAdministrationService(new CodexProvisioning.NodeCapabilityDiscovery()), output);
            return commandLine.Command switch
            {
                "status" => await administrationCli.ShowStatusAsync(commandLine),
                "config" => administrationCli.AdministerConfiguration(commandLine),
                _ => await administrationCli.ShowCapabilitiesAsync(commandLine)
            };
        }
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
            var command = ProvisioningCli.Parse(commandLine.Arguments);
            var configuration = GlobalWorkerConfiguration.Load(commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            var identity = await WorkerIdentity.LoadOrCreateAsync(configuration.Server.IdentityFile ?? WorkerIdentity.DefaultPath);
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
                if (!command.IsStatus)
                    Console.Error.WriteLine($"Running {command.Verb} for {command.CapabilityId}...");
                var service = new WorkerProvisioningAdministrationService(configuration.Worker.Provisioning,
                    new CodexProvisioning.NodeCapabilityDiscovery(), identity);
                var result = await ProvisioningCli.ExecuteAsync(command, service, stop.Token, (progress, _) =>
                    {
                        if (command.Json)
                            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(progress));
                        else if (progress.LoginInstructions is { } instructions)
                            Console.WriteLine($"Open {instructions.VerificationUri} and enter code {instructions.UserCode}.");
                        return Task.CompletedTask;
                    });
                ProvisioningCli.Write(result, command.Json);
                return result.Status == "succeeded"
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
                Console.WriteLine("Usage: codex-worker config set <setting> <value> [--config <path>] [--json]");
                Console.WriteLine("Updates a bounded known Worker setting after validating the complete candidate configuration.");
                Console.WriteLine("Action policy settings accept comma-separated keys; an empty value clears a list.");
                Console.WriteLine("Options: --json  Write the versioned update result as JSON.");
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
                Console.WriteLine($"Usage: codex-worker provision <status|operation> {configOption} [options]");
                Console.WriteLine("Show local policy and capability state, or run a bounded typed capability operation.");
                Console.WriteLine("Operations: install, upgrade, uninstall, detect, check-authentication, logout, check-configuration,");
                Console.WriteLine("            prepare-authentication, login, generate-ssh-key, inspect-ssh-key, remove-ssh-key,");
                Console.WriteLine("            verify-repository-access.");
                Console.WriteLine("Options: --json; verify-repository-access uses --repository <owner/repository>.");
                Console.WriteLine("        install/upgrade/uninstall require explicit --allow-elevation.");
                Console.WriteLine($"Default configuration: {WorkerCommandLine.DefaultConfigurationPath}");
                Console.WriteLine("Examples:");
                Console.WriteLine("  codex-worker provision status");
                Console.WriteLine("  codex-worker provision install git --allow-elevation");
                Console.WriteLine("  codex-worker provision upgrade codex-cli --allow-elevation --json");
                Console.WriteLine("  codex-worker provision uninstall github-cli --config /etc/codex-worker/worker.yml --allow-elevation");
                Console.WriteLine("Privileged action policy keys use tool:<capability-id>:<install|update|uninstall>.");
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
