namespace CodexWorker;

using System.Runtime.InteropServices;
using System.Diagnostics;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "update")
            return await CodexProvisioning.SelfUpdateCommand.RunAsync(new("worker", "Codex Worker", ApplicationVersion.Display, "/opt/codex-worker/CodexWorker"), args[1..]);
        using var shutdown = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        }
        Console.CancelKeyPress += OnCancel;
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            context => { context.Cancel = true; shutdown.Cancel(); });
        try { return await RunAsync(args, shutdown.Token); }
        catch (OperationCanceledException)
        {
            WorkerCommandLine command;
            try { command = WorkerCommandLine.Parse(args); }
            catch (ArgumentException) { command = new("run", null, []); }
            WorkerCliOutput.Failure(command, new WorkerConsole(), Console.Out, "cancelled", "Worker command cancelled.");
            return WorkerCliOutput.Cancelled;
        }
        finally { Console.CancelKeyPress -= OnCancel; }
    }

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken, TextReader? registrationInput = null)
    {
        if (args is ["--version"])
        {
            Console.WriteLine($"Codex Worker {ApplicationVersion.Display}");
            return 0;
        }
        var output = new WorkerConsole();
        if (args is ["--codex-preflight"])
        {
            try
            {
                await new CodexExecutor(new ProcessRunner(), new CodexSettings { Model = null }).PreflightAsync(cancellationToken);
                return ProcessExitCodes.Success;
            }
            catch (OperationCanceledException) { throw; }
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
            WorkerCliOutput.Failure(new WorkerCommandLine("run", null, args), output, Console.Out,
                "invalid-arguments", $"{ex.Message} Use 'codex-worker --help' for usage.");
            return ProcessExitCodes.StartupFailure;
        }
        if (args is ["h"] || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            var helpCommand = args is ["h"] || (args.Length > 0 && (args[0] is "--help" or "-h")) ? "root" : commandLine.Command;
            if (helpCommand == "config" && commandLine.Arguments.FirstOrDefault() is "show" or "validate" or "set")
                helpCommand = "config-" + commandLine.Arguments[0];
            if (helpCommand == "capabilities" && commandLine.Arguments.FirstOrDefault() is "list" or "refresh")
                helpCommand = "capabilities-" + commandLine.Arguments[0];
            if (helpCommand == "provision" && ProvisioningCli.IsVerb(commandLine.Arguments.FirstOrDefault()))
            {
                ProvisioningCli.WriteHelp(commandLine.Arguments[0], Console.Out);
                return ProcessExitCodes.Success;
            }
            WorkerCommandHelp.Write(helpCommand);
            return ProcessExitCodes.Success;
        }
        try
        {
            if (await WorkerServiceAdministrationContext.TryRunAsync(commandLine, cancellationToken) is { } serviceExitCode)
                return serviceExitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            WorkerCliOutput.Failure(commandLine, output, Console.Out, "service-context-unavailable",
                "Worker service administration could not run. Use sudo codex-worker and check the packaged service account, systemd, protected worker.env and command usage. " +
                FailureDiagnosticRedactor.Redact(ex.Message));
            return ProcessExitCodes.StartupFailure;
        }
        if (commandLine.Command == "executions")
            return await ExecutionAdministrationCli.RunAsync(commandLine, output, cancellationToken);
        if (commandLine.Command == "register")
        {
            using var standardInput = registrationInput is null && commandLine.Arguments.Contains("--token-stdin", StringComparer.Ordinal)
                ? new StreamReader(Console.OpenStandardInput()) : null;
            var input = registrationInput ?? standardInput ?? TextReader.Null;
            return await new WorkerRegistrationCli(new WorkerRegistrationClient().BootstrapAsync, output, input, Console.Out, registrationInput is null ? WorkerPairingConsole.ReadSecretAsync : null)
                .ExecuteAsync(commandLine, cancellationToken);
        }
        if (commandLine.Command == "provision") return await ProvisionAsync(commandLine, output, cancellationToken);
        if (commandLine.Command == "credential") return await CredentialAsync(commandLine, output, cancellationToken);
        if (commandLine.Command is "status" or "diagnostics" or "config" or "capabilities")
        {
            var administrationCli = new WorkerAdministrationCli(new WorkerStatusService(),
                new WorkerConfigurationAdministrationService(),
                new WorkerCapabilityAdministrationService(new CodexProvisioning.NodeCapabilityDiscovery()), output);
            return commandLine.Command switch
            {
                "status" or "diagnostics" => await administrationCli.ShowStatusAsync(commandLine, cancellationToken),
                "config" => administrationCli.AdministerConfiguration(commandLine),
                _ => await administrationCli.ShowCapabilitiesAsync(commandLine, cancellationToken)
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

        try { await new WorkerHost(global, projects, output, operationalLog: message => Trace.WriteLine(message)).RunAsync(cancellationToken); return ProcessExitCodes.Success; }
        catch (Exception ex) { return ExitCodeFor(ex); }
    }

    private static async Task<int> CredentialAsync(WorkerCommandLine commandLine, WorkerConsole output, CancellationToken cancellationToken)
    {
        try
        {
            var command = WorkerCredentialCli.Parse(commandLine.Arguments);
            var configuration = GlobalWorkerConfiguration.Load(commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            var identity = command.IsStatus ? string.Empty :
                await WorkerIdentity.LoadOrCreateAsync(configuration.Server.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
            var discovery = new CodexProvisioning.NodeCapabilityDiscovery();
            var executor = new CodexProvisioning.NodeProvisioningCommandExecutor(discovery);
            var service = new CodexProvisioning.NodeCredentialAdministration(discovery,
                (request, token, progress) => WorkerProvisioning.ExecuteLocalAsync(request,
                    configuration.Worker.Provisioning, discovery, token, progress, executor));
            return await new WorkerCredentialCli(service, identity, Console.Out, Console.Error).RunAsync(command, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WorkerCliOutput.Failure(commandLine, output, Console.Out, "credential-failed",
                "Local credential administration failed. Check command usage, Worker configuration, provider installation, and file permissions.");
            return ProcessExitCodes.StartupFailure;
        }
    }

    private static async Task<int> ProvisionAsync(WorkerCommandLine commandLine, WorkerConsole output, CancellationToken cancellationToken)
    {
        try
        {
            var command = ProvisioningCli.Parse(commandLine.Arguments);
            var configuration = GlobalWorkerConfiguration.Load(commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            // Observations must work before enrollment and without write access to node state.
            // Status never sends a provisioning request, so it needs no durable identity.
            var identity = command.IsStatus ? Guid.Empty.ToString("N") :
                await WorkerIdentity.LoadOrCreateAsync(configuration.Server.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
            if (!command.IsStatus)
                Console.Error.WriteLine($"Running {command.Verb} for {command.CapabilityId}...");
            var service = new WorkerProvisioningAdministrationService(configuration.Worker.Provisioning,
                new CodexProvisioning.NodeCapabilityDiscovery(), identity);
            var result = await ProvisioningCli.ExecuteAsync(command, service, cancellationToken, (progress, _) =>
                {
                    if (command.Json)
                        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(progress));
                    else if (progress.LoginInstructions is { } instructions)
                        Console.WriteLine($"Open {instructions.VerificationUri} and enter code {instructions.UserCode}.");
                    return Task.CompletedTask;
                });
            ProvisioningCli.Write(result, command.Json);
            return result.Status == "succeeded"
                ? ProcessExitCodes.Success : result.Diagnostic is CodexProvisioning.ProvisioningDiagnostic.Cancelled or CodexProvisioning.ProvisioningDiagnostic.TimedOut
                    ? WorkerCliOutput.Cancelled : ProcessExitCodes.StartupFailure;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WorkerCliOutput.Failure(commandLine, output, Console.Out, "provisioning-failed", ex.Message);
            return ProcessExitCodes.StartupFailure;
        }
    }

    internal static int ExitCodeFor(Exception? failure) => failure switch
    {
        null => ProcessExitCodes.Success,
        WorkerStartupException => ProcessExitCodes.StartupFailure,
        _ => ProcessExitCodes.RuntimeFailure
    };
}
