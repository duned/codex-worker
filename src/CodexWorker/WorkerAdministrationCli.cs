namespace CodexWorker;

using System.Text.Json;

/// <summary>Adapts local administration command arguments and output to reusable application services.</summary>
public sealed class WorkerAdministrationCli(IWorkerStatusService statusService,
    IWorkerConfigurationAdministrationService configurationService,
    IWorkerCapabilityAdministrationService capabilityService, WorkerConsole output, TextWriter? writer = null)
{
    private readonly TextWriter _writer = writer ?? Console.Out;

    public async Task<int> ShowStatusAsync(WorkerCommandLine commandLine, CancellationToken cancellationToken = default)
    {
        var json = commandLine.Arguments.Count == 1 && commandLine.Arguments[0] == "--json";
        if (commandLine.Arguments.Count != (json ? 1 : 0))
        {
            WorkerCliOutput.Failure(commandLine, output, _writer, "invalid-arguments", $"Unexpected arguments. Use 'codex-worker {commandLine.Command} --help' for usage.");
            return ProcessExitCodes.StartupFailure;
        }
        try
        {
            var path = commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath;
            var status = await statusService.GetStatusAsync(path, cancellationToken);
            WorkerStatusReporter.Write(status, json, _writer);
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            WorkerCliOutput.Failure(commandLine, output, _writer, "status-collection-failed", $"Status collection failed: {ex.Message}");
            return ProcessExitCodes.StartupFailure;
        }
    }

    public int AdministerConfiguration(WorkerCommandLine commandLine)
    {
        var arguments = commandLine.Arguments;
        if (arguments.Count == 1 && arguments[0] == "--json") arguments = ["show", "--json"];
        var operation = arguments.Count == 0 ? "show" : arguments[0];
        try
        {
            var path = WorkerConfigurationAdministration.ResolvePath(commandLine.ConfigurationPath);
            switch (operation)
            {
                case "show":
                {
                    var json = arguments.Count == 2 && arguments[1] == "--json";
                    if (arguments.Count > 1 && !json) throw new ArgumentException("Usage: codex-worker config show [--json] [--config <path>]");
                    var configuration = configurationService.Show(path);
                    if (json)
                        _writer.WriteLine(JsonSerializer.Serialize(configuration, new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true
                        }));
                    else
                        WriteConfiguration(configuration);
                    return ProcessExitCodes.Success;
                }
                case "validate":
                {
                    var json = arguments.Count == 2 && arguments[1] == "--json";
                    if (arguments.Count > 1 && !json) throw new ArgumentException("Usage: codex-worker config validate [--json] [--config <path>]");
                    var validation = configurationService.Validate(path);
                    if (json)
                        _writer.WriteLine(JsonSerializer.Serialize(new
                        {
                            contractVersion = validation.ContractVersion,
                            valid = validation.IsValid,
                            configurationPath = validation.ConfigurationPath,
                            diagnostics = validation.Diagnostics.Select(diagnostic => diagnostic.Message),
                            diagnosticDetails = validation.Diagnostics
                        },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                    else if (validation.IsValid)
                        _writer.WriteLine($"Configuration is valid: {path}");
                    else
                        output.InfrastructureFailure("Configuration validation failed:\n- " + string.Join("\n- ", validation.Diagnostics.Select(diagnostic => diagnostic.Message)));
                    return validation.IsValid ? ProcessExitCodes.Success : ProcessExitCodes.StartupFailure;
                }
                case "set":
                    var outputJson = arguments.Count == 4 && arguments[3] == "--json";
                    if (arguments.Count != (outputJson ? 4 : 3))
                        throw new ArgumentException("Usage: codex-worker config set <setting> <value> [--config <path>] [--json]");
                    var update = configurationService.Set(path, arguments[1], arguments[2]);
                    if (outputJson)
                        _writer.WriteLine(JsonSerializer.Serialize(update,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                    if (!update.Succeeded)
                    {
                        if (!outputJson)
                            output.InfrastructureFailure($"Configuration update failed: {update.Diagnostic?.Message ?? "configuration update was rejected"}");
                        return ProcessExitCodes.StartupFailure;
                    }
                    if (!outputJson)
                        _writer.WriteLine($"Updated {update.Setting} in {update.ConfigurationPath}. Restart the Worker service for the change to take effect.");
                    return ProcessExitCodes.Success;
                default:
                    throw new ArgumentException($"Unknown config command '{operation}'. Use 'codex-worker config --help' for usage.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            WorkerCliOutput.Failure(commandLine, output, _writer, "configuration-administration-failed", $"Configuration administration failed: {ex.Message}");
            return ProcessExitCodes.StartupFailure;
        }
    }

    public async Task<int> ShowCapabilitiesAsync(WorkerCommandLine commandLine, CancellationToken cancellationToken = default)
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
                WorkerCliOutput.Failure(commandLine, output, _writer, "invalid-arguments", "Unexpected capabilities arguments. Use 'codex-worker capabilities --help' for usage.");
                return ProcessExitCodes.StartupFailure;
            }
        }
        try
        {
            var configurationPath = commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath;
            var inventory = await capabilityService.GetCapabilitiesAsync(configurationPath, action == "refresh", cancellationToken);
            CapabilityInventoryReporter.Write(inventory, json, _writer);
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            WorkerCliOutput.Failure(commandLine, output, _writer, "capability-discovery-failed", $"Capability discovery failed: {ex.Message}");
            return ProcessExitCodes.StartupFailure;
        }
    }

    private void WriteConfiguration(WorkerConfigurationDocument configuration)
    {
        _writer.WriteLine($"Configuration: {configuration.ConfigurationPath}");
        _writer.WriteLine($"worker.pollingSeconds: {configuration.Worker.PollingSeconds}");
        _writer.WriteLine($"worker.preflightTimeoutSeconds: {configuration.Worker.PreflightTimeoutSeconds}");
        _writer.WriteLine($"worker.maxParallelTasks: {configuration.Worker.MaxParallelTasks}");
        _writer.WriteLine($"worker.provisioning.enabled: {configuration.Worker.Provisioning.Enabled.ToString().ToLowerInvariant()}");
        _writer.WriteLine($"worker.provisioning.allowNonPrivileged: {configuration.Worker.Provisioning.AllowNonPrivileged.ToString().ToLowerInvariant()}");
        _writer.WriteLine($"worker.provisioning.allowCredentials: {configuration.Worker.Provisioning.AllowCredentials.ToString().ToLowerInvariant()}");
        _writer.WriteLine($"worker.provisioning.allowedPrivilegedActions: {string.Join(",", configuration.Worker.Provisioning.AllowedPrivilegedActions)}");
        _writer.WriteLine($"worker.provisioning.deniedActions: {string.Join(",", configuration.Worker.Provisioning.DeniedActions)}");
        _writer.WriteLine($"projects.directory: {configuration.Projects.Directory}");
        _writer.WriteLine($"projects.ownership: {configuration.Projects.Ownership}");
        _writer.WriteLine($"telegram.enabled: {configuration.Telegram.Enabled.ToString().ToLowerInvariant()}");
        _writer.WriteLine($"api.enabled: {configuration.Api.Enabled.ToString().ToLowerInvariant()}");
        _writer.WriteLine($"api.listenUrl: {configuration.Api.ListenUrl}");
        _writer.WriteLine($"api.eventHistoryLimit: {configuration.Api.EventHistoryLimit}");
        _writer.WriteLine($"server.enabled: {configuration.Server.Enabled.ToString().ToLowerInvariant()}");
        _writer.WriteLine($"server.url: {configuration.Server.Url}");
        _writer.WriteLine($"server.heartbeatIntervalSeconds: {configuration.Server.HeartbeatIntervalSeconds}");
        _writer.WriteLine($"server.identityFile: {configuration.Server.IdentityFile ?? ""}");
        _writer.WriteLine("Sensitive paths are redacted.");
    }
}
