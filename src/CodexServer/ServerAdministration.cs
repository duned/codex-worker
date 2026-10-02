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

public interface IServerProjectAdministrationService
{
    Task<IReadOnlyList<CentralProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<CentralProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default);
    Task<CentralProject> CreateProjectAsync(CentralProjectDefinition definition, CancellationToken cancellationToken = default);
    Task<CentralProject?> UpdateProjectAsync(string projectId, CentralProjectDefinition definition, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<CentralProject?> UpdateProjectLifecycleAsync(string projectId, bool enabled, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<bool> RemoveProjectAsync(string projectId, long expectedRevision, CancellationToken cancellationToken = default);
}

public interface IServerExecutionAdministrationService
{
    Task<IReadOnlyList<ExecutionRequest>> ListExecutionsAsync(ExecutionQuery query, CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> GetExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> CancelQueuedExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default);
    Task<ExecutionReconciliationResult?> ReconcileUncertainExecutionAsync(string executionRequestId,
        ExecutionReconciliationRequest request, CancellationToken cancellationToken = default);
}

public sealed class LocalServerAdministrationServiceFactory : IServerAdministrationServiceFactory
{
    public IServerAdministrationService Create(ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var registry = new SqliteRegistryStore(configuration.ResolveDatabasePath(), configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds);
        return new LocalServerAdministrationService(configuration, registry, new ServerHealthService(registry),
            githubAdministration: new ServerGitHubAdministrationService(registry, new ServerGitHubReadService()));
    }
}

/// <summary>Reads local Server state through the existing registry and health service contracts.</summary>
public sealed class LocalServerAdministrationService(ServerConfiguration configuration, IRegistryStore registry,
    IServerHealthService healthService, TimeProvider? timeProvider = null,
    IServerGitHubAdministrationService? githubAdministration = null) : IServerAdministrationService,
    IServerProjectAdministrationService, IServerExecutionAdministrationService, IServerGitHubAdministrationService
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

    public async Task<IReadOnlyList<CentralProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.GetProjectsAsync(cancellationToken);
    }

    public async Task<CentralProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.GetProjectAsync(projectId, cancellationToken);
    }

    public async Task<CentralProject> CreateProjectAsync(CentralProjectDefinition definition, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.CreateProjectAsync(definition, cancellationToken);
    }

    public async Task<CentralProject?> UpdateProjectAsync(string projectId, CentralProjectDefinition definition, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.UpdateProjectAsync(projectId, definition, expectedRevision, cancellationToken);
    }

    public async Task<CentralProject?> UpdateProjectLifecycleAsync(string projectId, bool enabled, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.UpdateProjectLifecycleAsync(projectId, enabled, expectedRevision, cancellationToken);
    }

    public async Task<bool> RemoveProjectAsync(string projectId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.RemoveProjectAsync(projectId, expectedRevision, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionRequest>> ListExecutionsAsync(ExecutionQuery query, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.ListExecutionsAsync(query, cancellationToken);
    }

    public async Task<ExecutionRequest?> GetExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.GetExecutionAsync(executionRequestId, cancellationToken);
    }

    public async Task<ExecutionRequest?> CancelQueuedExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.CancelQueuedExecutionAsync(executionRequestId, cancellationToken);
    }

    public async Task<ExecutionReconciliationResult?> ReconcileUncertainExecutionAsync(string executionRequestId,
        ExecutionReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await registry.ReconcileUncertainExecutionAsync(executionRequestId, request, cancellationToken);
    }

    public async Task<GitHubRepositoryAccess?> CheckAccessAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().CheckAccessAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<ManagedGitHubIssue>?> ListIssuesAsync(string projectId, GitHubIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().ListIssuesAsync(projectId, query, cancellationToken);
    }

    public async Task<ManagedGitHubIssue?> GetIssueAsync(string projectId, int issueNumber,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().GetIssueAsync(projectId, issueNumber, cancellationToken);
    }

    public async Task<ExecutionRequest> EnqueueIssueAsync(string projectId, WorkReference workReference,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().EnqueueIssueAsync(projectId, workReference, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionRequest>?> RefreshQueuedEligibilityAsync(string projectId, int issueNumber,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().RefreshQueuedEligibilityAsync(projectId, issueNumber, cancellationToken);
    }

    public async Task<WorkAssignmentResponse> RequestAssignmentAsync(WorkerAssignmentRequest request,
        CancellationToken cancellationToken = default) =>
        await RequireGitHubAdministration().RequestAssignmentAsync(request, cancellationToken);

    private IServerGitHubAdministrationService RequireGitHubAdministration() => githubAdministration
        ?? throw new InvalidOperationException("Server GitHub administration is unavailable through this service.");

    private async Task EnsurePersistenceAvailableAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.PersistenceAvailable)
            throw new IOException(status.Diagnostic ?? LocalStatusDiagnostic);
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
    public const int Conflict = 3;
    public const int NotFound = 4;
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
        if (command == "projects") return await RunProjectsAsync(arguments, cancellationToken);
        if (command == "executions") return await RunExecutionsAsync(arguments, cancellationToken);
        if (command == "github") return await RunGitHubAsync(arguments, cancellationToken);
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

    private async Task<int> RunProjectsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        const string usage = "Usage: codex-server projects <list|show|create|update|enable|disable|delete> [arguments] [--json] [Server configuration options]";
        if (arguments.Count == 2 && arguments[1] == "--help")
        {
            _output.WriteLine(usage);
            _output.WriteLine("Create and update read a CentralProjectDefinition JSON file. Mutations require the current project revision.");
            return ServerAdministrationExitCodes.Success;
        }
        if (arguments.Count < 2)
            return InvalidArguments(usage);

        var operation = arguments[1];
        var expectedArguments = operation switch
        {
            "list" => 0,
            "show" => 1,
            "create" => 1,
            "update" => 3,
            "enable" or "disable" or "delete" => 2,
            _ => -1
        };
        if (expectedArguments < 0 || !TryParseProjectOptions(arguments, 2, out var positionals, out var json, out var configurationArguments) ||
            positionals.Count != expectedArguments)
            return InvalidArguments(usage);
        if (operation is "update" or "enable" or "disable" or "delete")
        {
            if (!long.TryParse(positionals[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var revision) || revision < 1)
                return InvalidArguments("Project mutations require a positive expected revision.");
        }

        var inspected = configurationService.Inspect(configurationArguments);
        if (!inspected.Document.IsValid || inspected.Configuration is null)
        {
            WriteConfigurationFailure(inspected.Document, json);
            return ServerAdministrationExitCodes.OperationalFailure;
        }

        try
        {
            if (serviceFactory.Create(inspected.Configuration) is not IServerProjectAdministrationService projects)
            {
                _error.WriteLine("Local project administration is unavailable through the configured Server service.");
                return ServerAdministrationExitCodes.OperationalFailure;
            }

            switch (operation)
            {
                case "list":
                    var all = await projects.GetProjectsAsync(cancellationToken);
                    WriteProjects(all, json);
                    break;
                case "show":
                    var found = await projects.GetProjectAsync(positionals[0], cancellationToken);
                    if (found is null) return ProjectNotFound(positionals[0]);
                    WriteProject(found, json);
                    break;
                case "create":
                    var created = await projects.CreateProjectAsync(await ReadProjectDefinitionAsync(positionals[0], cancellationToken), cancellationToken);
                    WriteProject(created, json);
                    break;
                case "update":
                    var updated = await projects.UpdateProjectAsync(positionals[0], await ReadProjectDefinitionAsync(positionals[2], cancellationToken),
                        ParseRevision(positionals[1]), cancellationToken);
                    if (updated is null) return ProjectNotFound(positionals[0]);
                    WriteProject(updated, json);
                    break;
                case "enable":
                case "disable":
                    var lifecycle = await projects.UpdateProjectLifecycleAsync(positionals[0], operation == "enable",
                        ParseRevision(positionals[1]), cancellationToken);
                    if (lifecycle is null) return ProjectNotFound(positionals[0]);
                    WriteProject(lifecycle, json);
                    break;
                case "delete":
                    if (!await projects.RemoveProjectAsync(positionals[0], ParseRevision(positionals[1]), cancellationToken))
                        return ProjectNotFound(positionals[0]);
                    _output.WriteLine(json ? "{\"deleted\":true}" : "Project deleted.");
                    break;
                default:
                    return InvalidArguments(usage);
            }
            return ServerAdministrationExitCodes.Success;
        }
        catch (OperationCanceledException) { return ServerAdministrationExitCodes.Canceled; }
        catch (ProjectRevisionConflictException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.Conflict;
        }
        catch (ProjectInUseException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.Conflict;
        }
        catch (InvalidDataException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.InvalidArguments;
        }
        catch (JsonException)
        {
            _error.WriteLine("Project definition file is not valid CentralProjectDefinition JSON.");
            return ServerAdministrationExitCodes.InvalidArguments;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _error.WriteLine("Project definition file could not be found.");
            return ServerAdministrationExitCodes.InvalidArguments;
        }
        catch (KeyNotFoundException)
        {
            _error.WriteLine("Project was not found.");
            return ServerAdministrationExitCodes.NotFound;
        }
        catch (InvalidOperationException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.Conflict;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error.WriteLine("Local project administration failed. Check the configured Server database, definition file, and file permissions.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
    }

    private async Task<int> RunExecutionsAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        const string usage = "Usage: codex-server executions <list|show|cancel|reconcile> [arguments] [filters] [--json] [Server configuration options]";
        if (arguments.Count == 2 && arguments[1] == "--help")
        {
            _output.WriteLine(usage);
            _output.WriteLine("List accepts --project, --state, --work-type, --work-id, --limit (1..100) and --offset (0..10000). Reconcile requires an expired uncertain execution, an explicit disposition, and evidence; Integrated also requires the full commit ID.");
            return ServerAdministrationExitCodes.Success;
        }
        if (arguments.Count < 2) return InvalidArguments(usage);

        var operation = arguments[1];
        var positional = new List<string>();
        var configurationArguments = new List<string>();
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var index = 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--json" && !json)
            {
                json = true;
                continue;
            }
            if (IsConfigurationOption(argument))
            {
                configurationArguments.Add(argument);
                if (!argument.Contains('=') && index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    configurationArguments.Add(arguments[++index]);
                else if (!argument.Contains('=')) return InvalidArguments(usage);
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = argument.IndexOf('=');
                var name = separator < 0 ? argument[2..] : argument[2..separator];
                var value = separator < 0 && index + 1 < arguments.Count ? arguments[++index] :
                    separator < 0 ? "" : argument[(separator + 1)..];
                if (value.Length == 0 || !filters.TryAdd(name, value)) return InvalidArguments(usage);
                continue;
            }
            positional.Add(argument);
        }

        var validPositionals = operation switch
        {
            "list" => positional.Count == 0,
            "show" or "cancel" => positional.Count == 1,
            "reconcile" => positional.Count is 3 or 4,
            _ => false
        };
        if (!validPositionals) return InvalidArguments(usage);
        if (operation != "list" && filters.Count != 0) return InvalidArguments(usage);
        if (filters.Keys.Any(key => key is not ("project" or "state" or "work-type" or "work-id" or "limit" or "offset")))
            return InvalidArguments(usage);
        if (!int.TryParse(filters.GetValueOrDefault("limit", "50"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var limit) ||
            !int.TryParse(filters.GetValueOrDefault("offset", "0"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var offset))
            return InvalidArguments("Execution list limit and offset must be decimal integers.");

        var query = new ExecutionQuery(filters.GetValueOrDefault("project"), filters.GetValueOrDefault("state"),
            filters.GetValueOrDefault("work-type"), filters.GetValueOrDefault("work-id"), limit, offset);
        if (operation == "list" && ExecutionAdministrationValidation.QueryError(query) is { } queryError)
            return InvalidArguments(queryError);

        var inspected = configurationService.Inspect(configurationArguments);
        if (!inspected.Document.IsValid || inspected.Configuration is null)
        {
            WriteConfigurationFailure(inspected.Document, json);
            return ServerAdministrationExitCodes.OperationalFailure;
        }

        try
        {
            if (serviceFactory.Create(inspected.Configuration) is not IServerExecutionAdministrationService executions)
            {
                _error.WriteLine("Local execution administration is unavailable through the configured Server service.");
                return ServerAdministrationExitCodes.OperationalFailure;
            }
            switch (operation)
            {
                case "list":
                    var items = await executions.ListExecutionsAsync(query, cancellationToken);
                    if (json) _output.WriteLine(JsonSerializer.Serialize(items, JsonOptions));
                    else if (items.Count == 0) _output.WriteLine("No matching execution requests were found.");
                    else foreach (var item in items) _output.WriteLine($"{item.Id}\t{item.State}\t{item.ProjectId}\t{item.WorkReference.Type}:{item.WorkReference.Id}\tattempt {item.AttemptNumber}");
                    break;
                case "show":
                    var shown = await executions.GetExecutionAsync(positional[0], cancellationToken);
                    if (shown is null) return ExecutionNotFound(positional[0]);
                    WriteExecution(shown, json);
                    break;
                case "cancel":
                    var cancelled = await executions.CancelQueuedExecutionAsync(positional[0], cancellationToken);
                    if (cancelled is null) return ExecutionNotFound(positional[0]);
                    WriteExecution(cancelled, json);
                    break;
                case "reconcile":
                    var reconciliationRequest = new ExecutionReconciliationRequest(positional[1], positional[2],
                        positional.Count == 4 ? positional[3] : null);
                    if (ExecutionAdministrationValidation.ReconciliationError(reconciliationRequest) is { } reconciliationError)
                        return InvalidArguments(reconciliationError);
                    var reconciled = await executions.ReconcileUncertainExecutionAsync(positional[0], reconciliationRequest, cancellationToken);
                    if (reconciled is null) return ExecutionNotFound(positional[0]);
                    if (json) _output.WriteLine(JsonSerializer.Serialize(reconciled, JsonOptions));
                    else
                    {
                        _output.WriteLine($"Execution {reconciled.Execution.Id} reconciled as {reconciled.Execution.RecoveryState}.");
                        if (reconciled.Retry is not null) _output.WriteLine($"Queued linked retry {reconciled.Retry.Id} (attempt {reconciled.Retry.AttemptNumber}).");
                    }
                    break;
            }
            return ServerAdministrationExitCodes.Success;
        }
        catch (OperationCanceledException) { return ServerAdministrationExitCodes.Canceled; }
        catch (ExecutionRequestCancellationException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.Conflict;
        }
        catch (ExecutionRequestReconciliationException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.Conflict;
        }
        catch (InvalidDataException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.InvalidArguments;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error.WriteLine("Local execution administration failed. Check the configured Server database and file permissions.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
    }

    private async Task<int> RunGitHubAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        const string usage = "Usage: codex-server github <access|issues|issue|enqueue|refresh> <project-id> [issue-number] [--state open|closed|all] [--limit 1..100] [--label <name>] [--json] [Server configuration options]";
        if (arguments.Count == 2 && arguments[1] == "--help")
        {
            _output.WriteLine(usage);
            _output.WriteLine("Issue discovery is read-only. Enqueue and refresh are explicit actions. Project issueReadyLabel and issueBlockedLabel control label eligibility; every open blocked-by dependency also makes an Issue ineligible.");
            return ServerAdministrationExitCodes.Success;
        }
        if (arguments.Count < 3) return InvalidArguments(usage);

        var operation = arguments[1];
        var positionals = new List<string>();
        var configurationArguments = new List<string>();
        var json = false;
        var state = "open";
        var limit = 50;
        string? label = null;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--json" && !json) { json = true; continue; }
            if (IsConfigurationOption(argument))
            {
                configurationArguments.Add(argument);
                if (!argument.Contains('=') && index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    configurationArguments.Add(arguments[++index]);
                else if (!argument.Contains('=')) return InvalidArguments(usage);
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = argument.IndexOf('=');
                var name = separator < 0 ? argument[2..] : argument[2..separator];
                if (name is not ("state" or "limit" or "label") || !seenOptions.Add(name)) return InvalidArguments(usage);
                var value = separator >= 0 ? argument[(separator + 1)..] :
                    index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[++index] : "";
                if (string.IsNullOrWhiteSpace(value)) return InvalidArguments(usage);
                if (name == "state") state = value;
                else if (name == "label") label = value;
                else if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit))
                    return InvalidArguments("GitHub Issue limit must be a decimal integer from 1 to 100.");
                continue;
            }
            positionals.Add(argument);
        }

        var expected = operation switch
        {
            "access" or "issues" => 1,
            "issue" or "enqueue" or "refresh" => 2,
            _ => -1
        };
        if (expected < 0 || positionals.Count != expected || operation is not "issues" && (seenOptions.Contains("state") || seenOptions.Contains("limit") || seenOptions.Contains("label")))
            return InvalidArguments(usage);
        int issueNumber = 0;
        if (expected == 2 && (!int.TryParse(positionals[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out issueNumber) || issueNumber <= 0))
            return InvalidArguments("Issue number must be a positive decimal integer.");
        if (operation == "issues" && (state is not ("open" or "closed" or "all") || limit is < 1 or > 100))
            return InvalidArguments("GitHub Issue query must use state open, closed, or all and limit 1 to 100.");

        var inspected = configurationService.Inspect(configurationArguments);
        if (!inspected.Document.IsValid || inspected.Configuration is null)
        {
            WriteConfigurationFailure(inspected.Document, json);
            return ServerAdministrationExitCodes.OperationalFailure;
        }
        try
        {
            if (serviceFactory.Create(inspected.Configuration) is not IServerGitHubAdministrationService github)
            {
                _error.WriteLine("Local GitHub administration is unavailable through the configured Server service.");
                return ServerAdministrationExitCodes.OperationalFailure;
            }
            switch (operation)
            {
                case "access":
                    var access = await github.CheckAccessAsync(positionals[0], cancellationToken);
                    if (access is null) return ProjectNotFound(positionals[0]);
                    if (json) _output.WriteLine(JsonSerializer.Serialize(access, JsonOptions));
                    else
                    {
                        _output.WriteLine($"Repository: {access.Repository}");
                        _output.WriteLine($"Server gh authentication: {(access.CliAuthenticated ? "available" : "unavailable")}");
                        _output.WriteLine($"Repository read access: {(access.RepositoryReadable ? "available" : "unavailable")}");
                        _output.WriteLine(access.Diagnostic);
                    }
                    break;
                case "issues":
                    var issues = await github.ListIssuesAsync(positionals[0], new GitHubIssueQuery(state, limit, label), cancellationToken);
                    if (issues is null) return ProjectNotFound(positionals[0]);
                    if (json) _output.WriteLine(JsonSerializer.Serialize(issues, JsonOptions));
                    else if (issues.Count == 0) _output.WriteLine("No GitHub Issues matched the query.");
                    else foreach (var item in issues) WriteGitHubIssue(item);
                    break;
                case "issue":
                    var issue = await github.GetIssueAsync(positionals[0], issueNumber, cancellationToken);
                    if (issue is null)
                    {
                        _error.WriteLine($"Project '{positionals[0]}' or GitHub Issue #{issueNumber} was not found.");
                        return ServerAdministrationExitCodes.NotFound;
                    }
                    if (json) _output.WriteLine(JsonSerializer.Serialize(issue, JsonOptions));
                    else WriteGitHubIssue(issue);
                    break;
                case "enqueue":
                    var enqueued = await github.EnqueueIssueAsync(positionals[0], new WorkReference("github-issue",
                        issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
                    WriteExecution(enqueued, json);
                    break;
                case "refresh":
                    var refreshed = await github.RefreshQueuedEligibilityAsync(positionals[0], issueNumber, cancellationToken);
                    if (refreshed is null) return ProjectNotFound(positionals[0]);
                    if (json) _output.WriteLine(JsonSerializer.Serialize(refreshed, JsonOptions));
                    else if (refreshed.Count == 0) _output.WriteLine("No queued request exists for that Issue.");
                    else foreach (var execution in refreshed)
                        _output.WriteLine($"{execution.Id}\t{execution.State}\t{execution.ManagedEligibilityState}\t{string.Join("; ", execution.ManagedEligibilityReasons ?? [])}");
                    break;
            }
            return ServerAdministrationExitCodes.Success;
        }
        catch (OperationCanceledException) { return ServerAdministrationExitCodes.Canceled; }
        catch (GitHubIssueNotFoundException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.NotFound;
        }
        catch (ManagedIssueIneligibleException exception)
        {
            _error.WriteLine(exception.Message);
            foreach (var reason in exception.Issue.EligibilityReasons) _error.WriteLine($"- {reason}");
            return ServerAdministrationExitCodes.Conflict;
        }
        catch (ExecutionRequestConflictException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.Conflict;
        }
        catch (GitHubReadUnavailableException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.OperationalFailure;
        }
        catch (InvalidDataException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.InvalidArguments;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error.WriteLine("Local GitHub administration failed. Check the configured Server database and Server service-account gh authentication.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
    }

    private void WriteGitHubIssue(ManagedGitHubIssue issue)
    {
        _output.WriteLine($"#{issue.Number}\t{issue.State}\t{(issue.IsEligible ? "eligible" : "ineligible")}\t{issue.Title}");
        _output.WriteLine($"  Labels: {(issue.Labels.Count == 0 ? "none" : string.Join(", ", issue.Labels))}");
        if (issue.BlockedBy.Count > 0)
            _output.WriteLine("  Blocked by: " + string.Join(", ", issue.BlockedBy.Select(blocker => $"#{blocker.Number} ({blocker.State})")));
        foreach (var reason in issue.EligibilityReasons) _output.WriteLine($"  Eligibility: {reason}");
        _output.WriteLine($"  {issue.Url}");
    }

    private void WriteExecution(ExecutionRequest execution, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(execution, JsonOptions));
            return;
        }
        _output.WriteLine($"Execution: {execution.Id}");
        _output.WriteLine($"State: {execution.State}");
        _output.WriteLine($"Work: {execution.WorkReference.Type}:{execution.WorkReference.Id} · project {execution.ProjectId}");
        _output.WriteLine($"Attempt: {execution.AttemptNumber}{(execution.RetryOfExecutionId is null ? "" : $" · retry of {execution.RetryOfExecutionId}")}");
        if (execution.RecoveryState is not null) _output.WriteLine($"Recovery: {execution.RecoveryState} · {execution.RecoveryReason}");
        if (execution.Lease is not null) _output.WriteLine($"Lease: {execution.Lease.State} · generation {execution.Lease.Generation} · Worker {execution.Lease.WorkerId}");
        if (execution.CompletionSummary is not null) _output.WriteLine($"Summary: {execution.CompletionSummary}");
    }

    private int ExecutionNotFound(string executionId)
    {
        _error.WriteLine($"Execution '{ServerAdministrationRedaction.Redact(executionId)}' was not found.");
        return ServerAdministrationExitCodes.NotFound;
    }

    private async Task<CentralProjectDefinition> ReadProjectDefinitionAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };
        return JsonSerializer.Deserialize<CentralProjectDefinition>(json, options)
            ?? throw new InvalidDataException("Project definition file must contain a JSON object.");
    }

    private void WriteProjects(IReadOnlyList<CentralProject> projects, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(projects, JsonOptions));
            return;
        }
        if (projects.Count == 0)
        {
            _output.WriteLine("No central projects are registered.");
            return;
        }
        foreach (var project in projects)
            _output.WriteLine($"{project.Id}\t{(project.Enabled ? "enabled" : "disabled")}\trevision {project.Revision}\t{project.Name}\t{project.Repository}");
    }

    private void WriteProject(CentralProject project, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(project, JsonOptions));
            return;
        }
        _output.WriteLine($"Project: {project.Name} ({project.Id})");
        _output.WriteLine($"Lifecycle: {(project.Enabled ? "enabled" : "disabled")}");
        _output.WriteLine($"Revision: {project.Revision}");
        _output.WriteLine($"Repository: {project.Repository}");
        _output.WriteLine($"Default branch: {project.DefaultBranch}");
        _output.WriteLine($"Description: {project.Description}");
        _output.WriteLine("Requirements: " + (project.Requirements.Count == 0
            ? "none"
            : string.Join(", ", project.Requirements.Select(requirement => $"{requirement.Type}:{requirement.Name}{(requirement.Version is null ? "" : " " + requirement.Version)}"))));
    }

    private int ProjectNotFound(string projectId)
    {
        _error.WriteLine($"Project '{ServerAdministrationRedaction.Redact(projectId)}' was not found.");
        return ServerAdministrationExitCodes.NotFound;
    }

    private static long ParseRevision(string value) => long.Parse(value, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryParseProjectOptions(IReadOnlyList<string> arguments, int start,
        out IReadOnlyList<string> positionals, out bool json, out IReadOnlyList<string> configurationArguments)
    {
        var values = new List<string>();
        var configuration = new List<string>();
        json = false;
        for (var index = start; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--json" && !json)
            {
                json = true;
                continue;
            }
            if (IsConfigurationOption(argument))
            {
                configuration.Add(argument);
                if (!argument.Contains('=') && index + 1 < arguments.Count &&
                    !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    configuration.Add(arguments[++index]);
                else if (!argument.Contains('='))
                    return Fail(out positionals, out configurationArguments);
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                return Fail(out positionals, out configurationArguments);
            values.Add(argument);
        }
        positionals = values;
        configurationArguments = configuration;
        return true;
    }

    private static bool Fail(out IReadOnlyList<string> positionals, out IReadOnlyList<string> configurationArguments)
    {
        positionals = [];
        configurationArguments = [];
        return false;
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
