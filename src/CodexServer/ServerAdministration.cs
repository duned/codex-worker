namespace CodexServer;

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

public sealed record ServerConfigurationDiagnostic(string Code, string Message);

public sealed record ServerConfigurationDocument(
    int ContractVersion,
    bool IsValid,
    string ListenUrl,
    string DataDirectory,
    string DatabasePath,
    bool EnableLocalProvisioning,
    bool AllowLocalProvisioningElevation,
    int WorkerStaleAfterSeconds,
    int ExecutionLeaseDurationSeconds,
    int ExecutionLeaseRenewalIntervalSeconds,
    IReadOnlyList<ServerConfigurationDiagnostic> Diagnostics);

public sealed record ServerConfigurationAdministrationResult(ServerConfiguration? Configuration,
    ServerConfigurationDocument Document);

public interface IServerConfigurationAdministrationService
{
    ServerConfigurationAdministrationResult Inspect(IReadOnlyList<string> configurationArguments);
}

/// <summary>Loads the same appsettings, environment and command-line sources as the Server host.</summary>
public sealed class ServerConfigurationAdministrationService : IServerConfigurationAdministrationService
{
    public ServerConfigurationAdministrationResult Inspect(IReadOnlyList<string> configurationArguments)
    {
        ArgumentNullException.ThrowIfNull(configurationArguments);

        ServerConfiguration? configuration = null;
        var diagnostics = new List<ServerConfigurationDiagnostic>();
        try
        {
            var builder = ServerApplication.CreateBuilder(configurationArguments.ToArray());
            configuration = new ServerConfiguration();
            builder.Configuration.GetSection("Server").Bind(configuration);
            try
            {
                configuration.Validate();
                _ = configuration.ResolveDataDirectory();
                _ = configuration.ResolveDatabasePath();
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new("invalid-server-configuration", ServerAdministrationRedaction.Redact(exception.Message)));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new("configuration-path-invalid", "Server data paths could not be resolved. Check Server:DataDirectory and Server:DatabasePath."));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add(new("configuration-load-failed", "Server configuration could not be loaded. Check appsettings.json, environment variables, and command-line settings."));
        }

        var document = configuration is null
            ? new ServerConfigurationDocument(1, false, "[redacted]", "[redacted]", "[redacted]",
                false, false, 0, 0, 0, diagnostics)
            : CreateDocument(configuration, diagnostics);
        return new(configuration, document);
    }

    private static ServerConfigurationDocument CreateDocument(ServerConfiguration configuration,
        IReadOnlyList<ServerConfigurationDiagnostic> diagnostics)
    {
        string listenUrl;
        try { listenUrl = ServerAdministrationRedaction.SafeUrl(configuration.ListenUrl); }
        catch (UriFormatException) { listenUrl = "[redacted]"; }

        return new(1, diagnostics.Count == 0, listenUrl, "[redacted]", "[redacted]",
            configuration.EnableLocalProvisioning, configuration.AllowLocalProvisioningElevation,
            configuration.WorkerStaleAfterSeconds, configuration.ExecutionLeaseDurationSeconds,
            configuration.ExecutionLeaseRenewalIntervalSeconds, diagnostics);
    }
}

public sealed record ServerAdministrationStatusDocument(
    int ContractVersion,
    string Version,
    string ProcessHealth,
    string ControlPlaneReadiness,
    bool PersistenceAvailable,
    bool LoopbackContacted,
    string ListenUrl,
    string DataDirectory,
    string DatabasePath,
    string? Diagnostic);

public sealed record ServerAdministrationDiagnosticsDocument(
    int ContractVersion,
    DateTimeOffset CheckedAtUtc,
    string ControlPlaneReadiness,
    bool PersistenceAvailable,
    bool LoopbackContacted,
    int? RegisteredWorkers,
    IReadOnlyDictionary<string, int>? WorkersByAvailability,
    IReadOnlyDictionary<string, int>? WorkersByLifecycle,
    int? Projects,
    int? ActiveExecutions,
    int? MaximumCapacity,
    int? AvailableCapacity,
    IReadOnlyList<string> Notes,
    string? Diagnostic);

public interface IServerAdministrationService
{
    Task<ServerAdministrationStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ServerAdministrationDiagnosticsDocument> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}

public interface IServerAdministrationServiceFactory
{
    IServerAdministrationService Create(ServerConfiguration configuration);
}

public sealed class LocalServerAdministrationServiceFactory : IServerAdministrationServiceFactory
{
    public IServerAdministrationService Create(ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var registry = new SqliteRegistryStore(configuration.ResolveDatabasePath(), configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds);
        return new LocalServerAdministrationService(configuration, registry, new ServerHealthService(registry));
    }
}

/// <summary>Reads local Server state through the existing registry and health service contracts.</summary>
public sealed class LocalServerAdministrationService(ServerConfiguration configuration, IRegistryStore registry,
    IServerHealthService healthService, TimeProvider? timeProvider = null) : IServerAdministrationService
{
    private const string LocalStatusDiagnostic = "The configured Server database is unavailable or not initialized.";

    public async Task<ServerAdministrationStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var databasePath = configuration.ResolveDatabasePath();
        if (!File.Exists(databasePath))
            return CreateStatus(false, LocalStatusDiagnostic);

        var health = await healthService.GetHealthAsync(cancellationToken);
        var available = health.PersistenceAvailable && health.ControlPlaneInitialized;
        return CreateStatus(available, available ? null : "Server control-plane persistence is not ready.");
    }

    public async Task<ServerAdministrationDiagnosticsDocument> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.PersistenceAvailable)
            return new(1, (timeProvider ?? TimeProvider.System).GetUtcNow(), status.ControlPlaneReadiness,
                false, false, null, null, null, null, null, null, null,
                ["This command inspects local persisted state only; it does not contact the running Server."], status.Diagnostic);

        var workers = await registry.GetWorkersAsync(cancellationToken);
        var projects = await registry.GetProjectsAsync(cancellationToken);
        var availability = workers.GroupBy(worker => worker.Availability, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var lifecycle = workers.GroupBy(worker => worker.LifecycleState, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new(1, (timeProvider ?? TimeProvider.System).GetUtcNow(), status.ControlPlaneReadiness, true, false,
            workers.Count, availability, lifecycle, projects.Count,
            workers.Sum(worker => worker.ActiveExecutions), workers.Sum(worker => worker.MaximumCapacity),
            workers.Sum(worker => worker.AvailableCapacity),
            ["This command inspects local persisted state only; it does not contact the running Server.",
             "Worker availability does not establish node capability readiness or project eligibility."], null);
    }

    private ServerAdministrationStatusDocument CreateStatus(bool persistenceAvailable, string? diagnostic)
    {
        var readiness = persistenceAvailable ? "ready" : "not-ready";
        return new(1, ServerApplication.DisplayVersion, "not-observed", readiness, persistenceAvailable, false,
            ServerAdministrationRedaction.SafeUrl(configuration.ListenUrl), "[redacted]", "[redacted]", diagnostic);
    }
}

public static class ServerAdministrationExitCodes
{
    public const int Success = 0;
    public const int OperationalFailure = 1;
    public const int InvalidArguments = 2;
    public const int Canceled = 130;
}

/// <summary>Thin text/JSON command adapter over Server configuration and application services.</summary>
public sealed class ServerAdministrationCli(IServerConfigurationAdministrationService configurationService,
    IServerAdministrationServiceFactory serviceFactory, TextWriter? output = null, TextWriter? error = null)
{
    private readonly TextWriter _output = output ?? Console.Out;
    private readonly TextWriter _error = error ?? Console.Error;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            return InvalidArguments("Missing Server administration command. Run 'codex-server --help' for usage.");

        var command = arguments[0];
        var configurationCommand = command == "config";
        var operation = configurationCommand && arguments.Count > 1 ? arguments[1] : command;
        var optionStart = configurationCommand ? 2 : 1;
        if (configurationCommand && (arguments.Count == 1 || arguments[1] == "--help"))
        {
            _output.WriteLine("Usage: codex-server config <show|validate> [--json] [Server configuration options]");
            return ServerAdministrationExitCodes.Success;
        }
        if (configurationCommand && arguments.Count == 3 && arguments[2] == "--help")
        {
            _output.WriteLine("Usage: codex-server config <show|validate> [--json] [Server configuration options]");
            return ServerAdministrationExitCodes.Success;
        }
        if (!configurationCommand && arguments.Count == 2 && arguments[1] == "--help")
        {
            _output.WriteLine("Usage: codex-server <status|diagnostics> [--json] [Server configuration options]");
            return ServerAdministrationExitCodes.Success;
        }
        if (configurationCommand ? operation is not ("show" or "validate") : operation is not ("status" or "diagnostics"))
            return InvalidArguments("Invalid Server administration command. Run 'codex-server --help' for usage.");

        if (!TryParseOptions(arguments, optionStart, out var json, out var configurationArguments))
            return InvalidArguments("Invalid Server administration options. Use --json and valid --Server:<setting>=<value> options.");

        if (configurationCommand)
            return RunConfiguration(operation, configurationArguments, json);

        var inspected = configurationService.Inspect(configurationArguments);
        if (!inspected.Document.IsValid || inspected.Configuration is null)
        {
            WriteConfigurationFailure(inspected.Document, json);
            return ServerAdministrationExitCodes.OperationalFailure;
        }

        try
        {
            var service = serviceFactory.Create(inspected.Configuration);
            if (operation == "status")
            {
                var status = await service.GetStatusAsync(cancellationToken);
                WriteStatus(status, json);
                return status.PersistenceAvailable ? ServerAdministrationExitCodes.Success : ServerAdministrationExitCodes.OperationalFailure;
            }

            var diagnostics = await service.GetDiagnosticsAsync(cancellationToken);
            WriteDiagnostics(diagnostics, json);
            return diagnostics.PersistenceAvailable ? ServerAdministrationExitCodes.Success : ServerAdministrationExitCodes.OperationalFailure;
        }
        catch (OperationCanceledException) { return ServerAdministrationExitCodes.Canceled; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error.WriteLine("Local Server administration failed. Check the configured data directory, database, and file permissions.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
    }

    private int RunConfiguration(string operation, IReadOnlyList<string> configurationArguments, bool json)
    {
        var inspected = configurationService.Inspect(configurationArguments);
        if (json)
            _output.WriteLine(JsonSerializer.Serialize(inspected.Document, JsonOptions));
        else if (operation == "show")
            WriteConfiguration(inspected.Document);
        else if (inspected.Document.IsValid)
            _output.WriteLine("Server configuration is valid.");
        else
            WriteConfigurationFailure(inspected.Document, json: false);

        return inspected.Document.IsValid ? ServerAdministrationExitCodes.Success : ServerAdministrationExitCodes.OperationalFailure;
    }

    private int InvalidArguments(string message)
    {
        _error.WriteLine(message);
        return ServerAdministrationExitCodes.InvalidArguments;
    }

    private void WriteConfigurationFailure(ServerConfigurationDocument document, bool json)
    {
        if (json)
            _output.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        else
        {
            _error.WriteLine("Server configuration is invalid:");
            foreach (var diagnostic in document.Diagnostics) _error.WriteLine($"- {diagnostic.Message}");
            if (document.Diagnostics.Count == 0) _error.WriteLine("- Check Server configuration values and paths.");
            _error.WriteLine("Run 'codex-server config validate' for details.");
        }
    }

    private void WriteConfiguration(ServerConfigurationDocument document)
    {
        _output.WriteLine($"Server configuration: {(document.IsValid ? "valid" : "invalid")}");
        _output.WriteLine($"Server:ListenUrl: {document.ListenUrl}");
        _output.WriteLine($"Server:DataDirectory: {document.DataDirectory}");
        _output.WriteLine($"Server:DatabasePath: {document.DatabasePath}");
        _output.WriteLine($"Server:EnableLocalProvisioning: {document.EnableLocalProvisioning.ToString().ToLowerInvariant()}");
        _output.WriteLine($"Server:AllowLocalProvisioningElevation: {document.AllowLocalProvisioningElevation.ToString().ToLowerInvariant()}");
        _output.WriteLine($"Server:WorkerStaleAfterSeconds: {document.WorkerStaleAfterSeconds}");
        _output.WriteLine($"Server:ExecutionLeaseDurationSeconds: {document.ExecutionLeaseDurationSeconds}");
        _output.WriteLine($"Server:ExecutionLeaseRenewalIntervalSeconds: {document.ExecutionLeaseRenewalIntervalSeconds}");
        _output.WriteLine("Path values and credentials are redacted.");
        foreach (var diagnostic in document.Diagnostics) _error.WriteLine($"- {diagnostic.Message}");
    }

    private void WriteStatus(ServerAdministrationStatusDocument status, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
            return;
        }
        _output.WriteLine("Codex Server local status");
        _output.WriteLine($"Version: {status.Version}");
        _output.WriteLine("Process health: not observed by this offline command");
        _output.WriteLine($"Control-plane readiness: {status.ControlPlaneReadiness}");
        _output.WriteLine($"Persistence: {(status.PersistenceAvailable ? "available" : "unavailable")}");
        _output.WriteLine($"Listen URL: {status.ListenUrl}");
        if (status.Diagnostic is not null) _error.WriteLine(status.Diagnostic);
    }

    private void WriteDiagnostics(ServerAdministrationDiagnosticsDocument diagnostics, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(diagnostics, JsonOptions));
            return;
        }
        _output.WriteLine("Codex Server local diagnostics");
        _output.WriteLine($"Control-plane readiness: {diagnostics.ControlPlaneReadiness}");
        _output.WriteLine($"Persistence: {(diagnostics.PersistenceAvailable ? "available" : "unavailable")}");
        if (diagnostics.RegisteredWorkers is { } workers)
        {
            _output.WriteLine($"Registered Workers: {workers}");
            _output.WriteLine($"Active executions reported by Workers: {diagnostics.ActiveExecutions}");
            _output.WriteLine($"Reported capacity: {diagnostics.AvailableCapacity}/{diagnostics.MaximumCapacity} available");
            _output.WriteLine($"Projects: {diagnostics.Projects}");
            _output.WriteLine("Worker availability: " + FormatCounts(diagnostics.WorkersByAvailability));
            _output.WriteLine("Worker lifecycle: " + FormatCounts(diagnostics.WorkersByLifecycle));
        }
        foreach (var note in diagnostics.Notes) _output.WriteLine($"Note: {note}");
        if (diagnostics.Diagnostic is not null) _error.WriteLine(diagnostics.Diagnostic);
    }

    private static string FormatCounts(IReadOnlyDictionary<string, int>? counts) => counts is null || counts.Count == 0
        ? "none"
        : string.Join(", ", counts.Select(item => $"{item.Key}={item.Value}"));

    private static bool TryParseOptions(IReadOnlyList<string> arguments, int start, out bool json,
        out IReadOnlyList<string> configurationArguments)
    {
        json = false;
        var configuration = new List<string>();
        for (var index = start; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--json" && !json)
            {
                json = true;
                continue;
            }
            if (!IsConfigurationOption(argument))
                return Fail(out configurationArguments);

            var separator = argument.IndexOf('=');
            if (separator >= 0)
            {
                if (separator == argument.Length - 1) return Fail(out configurationArguments);
                configuration.Add(argument);
                continue;
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                return Fail(out configurationArguments);
            configuration.Add(argument);
            configuration.Add(arguments[++index]);
        }

        configurationArguments = configuration;
        return true;
    }

    private static bool IsConfigurationOption(string argument)
    {
        var key = argument.Split('=', 2)[0];
        return key.StartsWith("--Server:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("--Logging:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Fail(out IReadOnlyList<string> configurationArguments)
    {
        configurationArguments = [];
        return false;
    }
}

internal static partial class ServerAdministrationRedaction
{
    [GeneratedRegex("(?i)(token|password|secret|credential|api[_-]?key)(\\s*[:=]\\s*)[^\\s,;]+")]
    private static partial Regex SecretAssignmentRegex();

    public static string Redact(string value) => SecretAssignmentRegex().Replace(value, "$1$2[redacted]");

    public static string SafeUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return string.IsNullOrWhiteSpace(value) ? "" : "[redacted]";
        return new UriBuilder(uri) { UserName = "", Password = "", Path = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Authority);
    }
}
