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
            githubAdministration: new ServerGitHubAdministrationService(registry, new ServerGitHubReadService(),
                cacheDatabasePath: configuration.ResolveDatabasePath()));
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

    public IDisposable? BeginReadOperation(bool refresh = false) => RequireGitHubAdministration().BeginReadOperation(refresh);
    public Task PrefetchIssueReadsAsync(string projectId, IReadOnlyList<int> issueNumbers, CancellationToken cancellationToken) =>
        RequireGitHubAdministration().PrefetchIssueReadsAsync(projectId, issueNumbers, cancellationToken);

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

    public async Task<ManagedGitHubIssueDiscovery?> DiscoverIssuesAsync(string projectId, GitHubIssueDiscoveryQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().DiscoverIssuesAsync(projectId, query, cancellationToken);
    }

    public async Task<ManagedGitHubIssue?> GetIssueAsync(string projectId, int issueNumber,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().GetIssueAsync(projectId, issueNumber, cancellationToken);
    }

    public async Task<GitHubIssueRelationships?> GetIssueRelationshipsAsync(string projectId, int issueNumber,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().GetIssueRelationshipsAsync(projectId, issueNumber, cancellationToken);
    }

    public async Task<GitHubIssueMutationResult> CreateIssueAsync(string projectId, GitHubIssueCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().CreateIssueAsync(projectId, request, cancellationToken);
    }

    public async Task<GitHubIssueMutationResult> UpdateIssueAsync(string projectId, int issueNumber,
        GitHubIssueUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().UpdateIssueAsync(projectId, issueNumber, request, cancellationToken);
    }

    public async Task<GitHubIssueMutationResult> SetIssueLabelAsync(string projectId, int issueNumber,
        GitHubIssueLabelRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().SetIssueLabelAsync(projectId, issueNumber, request, cancellationToken);
    }

    public async Task<GitHubIssueMutationResult> SetIssueBlockedByAsync(string projectId, int issueNumber,
        GitHubIssueDependencyRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().SetIssueBlockedByAsync(projectId, issueNumber, request, cancellationToken);
    }

    public async Task<GitHubIssueMutationResult> SetIssueParentAsync(string projectId, int childIssueNumber,
        GitHubIssueParentRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().SetIssueParentAsync(projectId, childIssueNumber, request, cancellationToken);
    }

    public async Task<GitHubIssueRelationshipBatchResult> SetIssueParentForChildrenAsync(string projectId,
        int parentIssueNumber, GitHubIssueSubIssueBatchRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().SetIssueParentForChildrenAsync(projectId, parentIssueNumber, request, cancellationToken);
    }

    public async Task<GitHubIssueRelationshipBatchResult> SetIssueBlockedByBatchAsync(string projectId, int issueNumber,
        GitHubIssueDependencyBatchRequest request, CancellationToken cancellationToken = default)
    {
        await EnsurePersistenceAvailableAsync(cancellationToken);
        return await RequireGitHubAdministration().SetIssueBlockedByBatchAsync(projectId, issueNumber, request, cancellationToken);
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
        var helpArguments = arguments.Count == 1 && arguments[0] == "config"
            ? (IReadOnlyList<string>)["config", "--help"]
            : arguments;
        if (ServerCommandHelp.TryWrite(helpArguments, _output)) return ServerAdministrationExitCodes.Success;
        var result = await RunCoreAsync(arguments, cancellationToken);
        if (result == ServerAdministrationExitCodes.InvalidArguments)
            _error.WriteLine(ServerCommandHelp.UsageHint(helpArguments));
        return result;
    }

    private async Task<int> RunCoreAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
        if (configurationCommand && operation == "set")
            return RunConfigurationMutation(arguments);
        if (configurationCommand ? operation is not ("show" or "validate") : operation is not ("status" or "diagnostics"))
            return InvalidArguments("Invalid Server administration command.");

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
        const string usage = "Usage: codex-server github <access|issues|issue|relationships|graph|enqueue|refresh|create|update|label|dependency|parent|sub-issues|dependency-batch> <project-id> [arguments] [--preview] [--refresh] [--json] [Server configuration options]";
        if (arguments.Count < 3) return InvalidArguments(usage);

        var operation = arguments[1];
        var positionals = new List<string>();
        var configurationArguments = new List<string>();
        var json = false;
        var state = "open";
        var limit = 50;
        var refreshData = false;
        var maxDepth = 5;
        var maxIssues = 100;
        var maxEdges = 500;
        string? label = null;
        string? title = null;
        string? body = null;
        var preview = false;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--refresh" && !refreshData && operation is ("issue" or "issues" or "relationships" or "graph")) { refreshData = true; continue; }
            if (argument == "--json" && !json) { json = true; continue; }
            if (argument == "--preview" && !preview) { preview = true; continue; }
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
                if (name is not ("state" or "limit" or "label" or "title" or "body" or "max-depth" or "max-issues" or "max-edges") || !seenOptions.Add(name)) return InvalidArguments(usage);
                var value = separator >= 0 ? argument[(separator + 1)..] :
                    index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[++index] : "";
                if (value.Length == 0 && name is ("state" or "limit" or "label")) return InvalidArguments(usage);
                if (name == "state") state = value;
                else if (name == "label") label = value;
                else if (name == "title") title = value;
                else if (name == "body") body = value;
                else if (name == "max-depth")
                {
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxDepth))
                        return InvalidArguments("GitHub graph max depth must be a decimal integer from 0 to 20.");
                }
                else if (name == "max-issues")
                {
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxIssues))
                        return InvalidArguments("GitHub graph max issues must be a decimal integer from 1 to 200.");
                }
                else if (name == "max-edges")
                {
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxEdges))
                        return InvalidArguments("GitHub graph max edges must be a decimal integer from 1 to 2000.");
                }
                else if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit))
                    return InvalidArguments("GitHub Issue limit must be a decimal integer from 1 to 100.");
                continue;
            }
            positionals.Add(argument);
        }

        var expected = operation switch
        {
            "access" or "issues" or "create" => 1,
            "issue" or "relationships" or "graph" or "enqueue" or "refresh" or "update" => 2,
            "parent" => 3,
            "label" or "dependency" or "sub-issues" or "dependency-batch" => 4,
            _ => -1
        };
        var invalidOptions = operation switch
        {
            "issues" => seenOptions.Any(option => option is not ("state" or "limit" or "label")),
            "graph" => seenOptions.Any(option => option is not ("max-depth" or "max-issues" or "max-edges")),
            "create" or "update" => seenOptions.Any(option => option is not ("title" or "body")),
            _ => seenOptions.Count > 0
        };
        if (expected < 0 || positionals.Count != expected || invalidOptions || preview && operation is not
            ("create" or "update" or "label" or "dependency" or "parent" or "sub-issues" or "dependency-batch"))
            return InvalidArguments(usage);
        int issueNumber = 0;
        if (operation is ("issue" or "relationships" or "graph" or "enqueue" or "refresh" or "update" or "label" or "dependency" or "parent" or "sub-issues" or "dependency-batch") &&
            (!int.TryParse(positionals[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out issueNumber) || issueNumber <= 0))
            return InvalidArguments("Issue number must be a positive decimal integer.");
        var applied = positionals.Count == 4 && positionals[2] == "add";
        if ((operation is "label" or "dependency" or "sub-issues" or "dependency-batch") && positionals[2] is not ("add" or "remove"))
            return InvalidArguments("Mutation action must be add or remove.");
        int blockerIssueNumber = 0;
        if (operation == "dependency" && (!int.TryParse(positionals[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out blockerIssueNumber) || blockerIssueNumber <= 0))
            return InvalidArguments("Blocking Issue number must be a positive decimal integer.");
        int? parentIssueNumber = null;
        if (operation == "parent" && !string.Equals(positionals[2], "none", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(positionals[2], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedParent) || parsedParent <= 0)
                return InvalidArguments("Parent Issue must be a positive decimal number or 'none'.");
            parentIssueNumber = parsedParent;
        }
        IReadOnlyList<int>? relationshipIssueNumbers = null;
        if (operation is "sub-issues" or "dependency-batch")
        {
            if (!TryParseIssueNumberList(positionals[3], out var parsedNumbers))
                return InvalidArguments("Relationship batch must be a comma-separated list of 1 to 50 positive Issue numbers.");
            relationshipIssueNumbers = parsedNumbers;
        }
        if (operation == "create" && GitHubIssueMutationValidation.CreateError(new(title, body)) is { } createError)
            return InvalidArguments(createError);
        if (operation == "update" && GitHubIssueMutationValidation.UpdateError(new(title, body, preview)) is { } updateError)
            return InvalidArguments(updateError);
        if (operation == "issues" && (state is not ("open" or "closed" or "all") || limit is < 1 or > 100))
            return InvalidArguments("GitHub Issue query must use state open, closed, or all and limit 1 to 100.");
        var graphOptions = new GitHubIssueGraphOptions(maxDepth, maxIssues, maxEdges);
        if (operation == "graph" && GitHubIssueGraphOptions.Error(graphOptions) is { } graphError)
            return InvalidArguments(graphError);

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
            using var readOperation = github.BeginReadOperation(refreshData);
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
                case "relationships":
                    var relationships = await github.GetIssueRelationshipsAsync(positionals[0], issueNumber, cancellationToken);
                    if (relationships is null)
                    {
                        _error.WriteLine($"Project '{positionals[0]}' or GitHub Issue #{issueNumber} was not found.");
                        return ServerAdministrationExitCodes.NotFound;
                    }
                    if (json) _output.WriteLine(JsonSerializer.Serialize(relationships, JsonOptions));
                    else WriteGitHubRelationships(relationships);
                    break;
                case "graph":
                    var graph = await GitHubIssueGraphBuilder.BuildAsync(issueNumber,
                        (number, token) => github.GetIssueRelationshipsAsync(positionals[0], number, token),
                        graphOptions, cancellationToken,
                        (numbers, token) => github.PrefetchIssueReadsAsync(positionals[0], numbers, token));
                    if (graph is null)
                    {
                        _error.WriteLine($"Project '{positionals[0]}' or GitHub Issue #{issueNumber} was not found.");
                        return ServerAdministrationExitCodes.NotFound;
                    }
                    if (json) _output.WriteLine(JsonSerializer.Serialize(graph, JsonOptions));
                    else _output.Write(GitHubIssueGraphBuilder.RenderText(graph));
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
                case "create":
                    var createResult = await github.CreateIssueAsync(positionals[0], new GitHubIssueCreateRequest(title, body, preview), cancellationToken);
                    WriteGitHubMutation(createResult, json);
                    break;
                case "update":
                    var updateResult = await github.UpdateIssueAsync(positionals[0], issueNumber,
                        new GitHubIssueUpdateRequest(title, body, preview), cancellationToken);
                    WriteGitHubMutation(updateResult, json);
                    break;
                case "label":
                    var labelResult = await github.SetIssueLabelAsync(positionals[0], issueNumber,
                        new GitHubIssueLabelRequest(positionals[3], applied, preview), cancellationToken);
                    WriteGitHubMutation(labelResult, json);
                    break;
                case "dependency":
                    var dependencyResult = await github.SetIssueBlockedByAsync(positionals[0], issueNumber,
                        new GitHubIssueDependencyRequest(blockerIssueNumber, applied, preview), cancellationToken);
                    WriteGitHubMutation(dependencyResult, json);
                    break;
                case "parent":
                    var parentResult = await github.SetIssueParentAsync(positionals[0], issueNumber,
                        new GitHubIssueParentRequest(parentIssueNumber, preview), cancellationToken);
                    WriteGitHubMutation(parentResult, json);
                    break;
                case "sub-issues":
                    var subIssueResult = await github.SetIssueParentForChildrenAsync(positionals[0], issueNumber,
                        new GitHubIssueSubIssueBatchRequest(relationshipIssueNumbers, applied, preview), cancellationToken);
                    WriteGitHubRelationshipBatch(subIssueResult, json);
                    if (subIssueResult.Items.Any(item => item.Status is "failed" or "partial")) return ServerAdministrationExitCodes.OperationalFailure;
                    break;
                case "dependency-batch":
                    var dependencyBatchResult = await github.SetIssueBlockedByBatchAsync(positionals[0], issueNumber,
                        new GitHubIssueDependencyBatchRequest(relationshipIssueNumbers, applied, preview), cancellationToken);
                    WriteGitHubRelationshipBatch(dependencyBatchResult, json);
                    if (dependencyBatchResult.Items.Any(item => item.Status is "failed" or "partial")) return ServerAdministrationExitCodes.OperationalFailure;
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
        catch (KeyNotFoundException exception)
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
        catch (GitHubIssueWriteUnavailableException exception)
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

    private void WriteGitHubRelationships(GitHubIssueRelationships relationships)
    {
        _output.WriteLine($"Relationships for #{relationships.IssueNumber} in {relationships.Repository}:");
        IReadOnlyList<GitHubRelationshipIssue> parentIssues = relationships.Parent is { } parent
            ? new[] { parent } : Array.Empty<GitHubRelationshipIssue>();
        WriteRelationshipGroup("Parent", parentIssues);
        WriteRelationshipGroup("Sub-issues", relationships.SubIssues);
        WriteRelationshipGroup("Blocked by", relationships.BlockedBy);
        WriteRelationshipGroup("Blocking", relationships.Blocking);
    }

    private void WriteRelationshipGroup(string heading, IReadOnlyList<GitHubRelationshipIssue> issues)
    {
        _output.WriteLine($"{heading}: {(issues.Count == 0 ? "none" : string.Join(", ", issues.Select(issue => $"#{issue.Number} ({issue.State}) {issue.Title}")))}");
        foreach (var issue in issues) _output.WriteLine($"  {issue.Url}");
    }

    private void WriteGitHubRelationshipBatch(GitHubIssueRelationshipBatchResult result, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }
        _output.WriteLine($"{result.Operation} relationship batch for {result.Repository}:");
        foreach (var item in result.Items)
        {
            var issueText = result.Operation is "set-parent" or "remove-parent"
                ? result.Operation == "set-parent"
                    ? $"Child #{item.IssueNumber} → parent #{item.RelatedIssueNumber}"
                    : $"Child #{item.IssueNumber} → parent relationship with Issue #{item.RelatedIssueNumber}"
                : $"Issue #{item.IssueNumber} → blocked by #{item.RelatedIssueNumber}";
            _output.WriteLine($"{item.Status}: {issueText}{(item.Diagnostic is null ? "" : $" — {item.Diagnostic}")}");
        }
    }

    private void WriteGitHubMutation(GitHubIssueMutationResult result, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }
        _output.WriteLine(result.PreviewOnly ? $"Preview: {result.Operation} Issue in {result.Repository}." :
            $"{result.Operation} Issue {(result.Changed ? "applied" : "already matched the requested state")} in {result.Repository}.");
        if (result.IssueNumber is { } issueNumber) _output.WriteLine($"Issue: #{issueNumber}{(result.Url is null ? "" : $" · {result.Url}")}");
        if (result.Operation == "parent")
            _output.WriteLine(result.Applied == true
                ? $"Parent relationship: set parent Issue #{result.RelatedIssueNumber}"
                : "Parent relationship: cleared");
        else if (result.RelatedIssueNumber is { } relatedNumber)
            _output.WriteLine($"Blocked by relationship: {(result.Applied == true ? "add" : "remove")} Issue #{relatedNumber}");
        if (result.Label is { } label) _output.WriteLine($"Configured label: {(result.Applied == true ? "add" : "remove")} '{label}'");
        if (result.Title is { } title) _output.WriteLine($"Title: {title}");
        if (result.Body is { } body) _output.WriteLine($"Body: {body}");
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

    private static bool TryParseIssueNumberList(string value, out IReadOnlyList<int> issueNumbers)
    {
        var values = value.Split(',', StringSplitOptions.None);
        var parsed = new List<int>(values.Length);
        foreach (var item in values)
        {
            if (!int.TryParse(item, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var issueNumber) || issueNumber <= 0)
            {
                issueNumbers = [];
                return false;
            }
            parsed.Add(issueNumber);
        }
        issueNumbers = parsed;
        return parsed.Count is >= 1 and <= 50;
    }

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

    private int RunConfigurationMutation(IReadOnlyList<string> arguments)
    {
        var json = arguments.Count == 5 && arguments[4] == "--json";
        if (arguments.Count is not (4 or 5) || arguments.Count == 5 && !json ||
            arguments[2].StartsWith("--", StringComparison.Ordinal) || arguments[3].StartsWith("--", StringComparison.Ordinal))
            return InvalidArguments("Usage: codex-server config set <setting> <value> [--json].");

        var inspected = configurationService.Inspect([]);
        if (inspected.Configuration is null || !inspected.Document.IsValid)
        {
            _error.WriteLine("Server configuration is invalid. Run 'codex-server config validate' for details.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
        var configurationPath = Environment.GetEnvironmentVariable("CODEX_SERVER_CONFIGURATION_FILE");
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            _error.WriteLine("Configuration mutation is available through the installed 'sudo codex-server config set' helper.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }

        var setting = ServerConfigurationMutation.CanonicalSetting(arguments[2]);
        try
        {
            ServerConfigurationMutation.Set(configurationPath, setting, arguments[3], inspected.Configuration);
            var result = new ServerConfigurationMutationDocument(1, true, $"Server:{setting}", RestartRequired: true);
            if (json) _output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            else _output.WriteLine($"Server:{setting} updated. Restart codex-server.service to apply the change.");
            return ServerAdministrationExitCodes.Success;
        }
        catch (ArgumentException)
        {
            _error.WriteLine("Invalid or unsupported Server configuration setting or value. Run 'codex-server config --help' for supported settings.");
            return ServerAdministrationExitCodes.InvalidArguments;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _error.WriteLine("Server configuration could not be updated. Check the installed configuration, its ownership and permissions, then run 'codex-server config validate'.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
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
