namespace CodexWorker;

using CodexProvisioning;

public sealed record WorkerConfigurationDocument(int ContractVersion, string ConfigurationPath, WorkerConfigurationWorker Worker,
    WorkerConfigurationProjects Projects, WorkerConfigurationTelegram Telegram, WorkerConfigurationApi Api,
    WorkerConfigurationServer Server);
public sealed record WorkerConfigurationWorker(int PollingSeconds, int PreflightTimeoutSeconds, int MaxParallelTasks,
    WorkerConfigurationProvisioning Provisioning);
public sealed record WorkerConfigurationProvisioning(bool Enabled, bool AllowNonPrivileged, bool AllowCredentials,
    IReadOnlyList<string> AllowedPrivilegedActions, IReadOnlyList<string> DeniedActions);
public sealed record WorkerConfigurationProjects(string Directory, string Ownership);
public sealed record WorkerConfigurationTelegram(bool Enabled);
public sealed record WorkerConfigurationApi(bool Enabled, string ListenUrl, int EventHistoryLimit);
public sealed record WorkerConfigurationServer(bool Enabled, string Url, int HeartbeatIntervalSeconds, string? IdentityFile);
public sealed record WorkerAdministrationDiagnostic(string Code, string Message);
public sealed record WorkerConfigurationValidationResult(string ConfigurationPath,
    IReadOnlyList<WorkerAdministrationDiagnostic> Diagnostics)
{
    public int ContractVersion => 1;
    public bool IsValid => Diagnostics.Count == 0;
}
public sealed record WorkerConfigurationUpdateResult(string ConfigurationPath, string Setting, bool Succeeded,
    bool RestartRequired, WorkerAdministrationDiagnostic? Diagnostic)
{
    public int ContractVersion => 1;
}

public interface IWorkerStatusService
{
    Task<WorkerStatusDocument> GetStatusAsync(string configurationPath, CancellationToken cancellationToken = default);
}

public interface IWorkerConfigurationAdministrationService
{
    WorkerConfigurationDocument Show(string configurationPath);
    WorkerConfigurationValidationResult Validate(string configurationPath);
    WorkerConfigurationUpdateResult Set(string configurationPath, string setting, string value);
}

public interface IWorkerCapabilityAdministrationService
{
    Task<CapabilityInventoryContract> GetCapabilitiesAsync(string? configurationPath, bool refresh,
        CancellationToken cancellationToken = default);
}

public interface IWorkerProvisioningAdministrationService
{
    Task<WorkerProvisioningResult> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<WorkerProvisioningResult> ExecuteAsync(WorkerProvisioningOperation operation, CancellationToken cancellationToken = default,
        Func<ProvisioningCommandReport, CancellationToken, Task>? progress = null);
}

public sealed class WorkerStatusService(WorkerCapabilityDiscovery? discovery = null) : IWorkerStatusService
{
    public Task<WorkerStatusDocument> GetStatusAsync(string configurationPath, CancellationToken cancellationToken = default) =>
        WorkerStatusReporter.CreateAsync(configurationPath, discovery, cancellationToken);
}

public sealed class WorkerConfigurationAdministrationService : IWorkerConfigurationAdministrationService
{
    public WorkerConfigurationDocument Show(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var fullPath = Path.GetFullPath(configurationPath);
        return WorkerConfigurationAdministration.CreateDocument(fullPath, GlobalWorkerConfiguration.Load(fullPath));
    }

    public WorkerConfigurationValidationResult Validate(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var fullPath = Path.GetFullPath(configurationPath);
        var diagnostics = WorkerConfigurationAdministration.Validate(fullPath)
            .Select(message => new WorkerAdministrationDiagnostic(
                File.Exists(fullPath) ? "configuration-invalid" : "configuration-not-found",
                SafeDiagnostic(message, fullPath))).ToArray();
        return new(fullPath, diagnostics);
    }

    public WorkerConfigurationUpdateResult Set(string configurationPath, string setting, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var fullPath = Path.GetFullPath(configurationPath);
        try
        {
            WorkerConfigurationAdministration.Set(fullPath, setting, value);
            return new(fullPath, setting, Succeeded: true, RestartRequired: true, Diagnostic: null);
        }
        catch (ArgumentException ex)
        {
            return UpdateFailed(fullPath, setting, "configuration-setting-rejected", ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            return UpdateFailed(fullPath, setting, "configuration-not-found", ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return UpdateFailed(fullPath, setting, "configuration-elevation-required",
                "Updating the protected Worker configuration requires elevated permissions. Run 'sudo codex-worker config set <setting> <value>' (add '--config <path>' when using a non-default file). The Worker service account can read configuration but cannot write it.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return UpdateFailed(fullPath, setting, "configuration-update-failed", ex.Message);
        }
    }

    private static WorkerConfigurationUpdateResult UpdateFailed(string path, string setting, string code, string message) =>
        new(path, setting, Succeeded: false, RestartRequired: false,
            new WorkerAdministrationDiagnostic(code, SafeDiagnostic(message, path)));

    private static string SafeDiagnostic(string message, string path)
    {
        var safe = FailureDiagnosticRedactor.Redact(message, [path]);
        return safe.Length <= 500 ? safe : safe[..497] + "...";
    }
}

public sealed class WorkerCapabilityAdministrationService(NodeCapabilityDiscovery? discovery = null) : IWorkerCapabilityAdministrationService
{
    private readonly NodeCapabilityDiscovery _discovery = discovery ?? new NodeCapabilityDiscovery();

    public async Task<CapabilityInventoryContract> GetCapabilitiesAsync(string? configurationPath, bool refresh,
        CancellationToken cancellationToken = default)
    {
        if (configurationPath is not null) _ = GlobalWorkerConfiguration.Load(configurationPath);
        return await CapabilityInventoryReporter.CreateAsync(_discovery, refresh, cancellationToken);
    }
}

public sealed record WorkerProvisioningActionAvailability(string Action, string PolicyKey, bool Allowed,
    bool RequiresElevation, string? Reason = null, string? Remediation = null);
public sealed record WorkerProvisioningOperation(string CapabilityId, ProvisioningCommandAction Action,
    bool AllowElevation = false, string? Repository = null, int TimeoutSeconds = 120);
public sealed record WorkerProvisioningStatusCapability(string Id, string DisplayName, CapabilityState State,
    IReadOnlyList<WorkerProvisioningActionAvailability> Actions);
public sealed record WorkerProvisioningResult(string Command, string Status, ProvisioningDiagnostic Diagnostic,
    string? CapabilityId = null, ProvisioningCommandAction? Action = null, string? Reason = null,
    string? Remediation = null, CapabilityState? Capability = null,
    IReadOnlyList<WorkerProvisioningStatusCapability>? Capabilities = null, ProvisioningCommandReport? Report = null,
    int ContractVersion = 1);

/// <summary>Applies node-local provisioning policy and invokes the shared typed provisioning executor.</summary>
public sealed class WorkerProvisioningAdministrationService : IWorkerProvisioningAdministrationService
{
    private readonly ProvisioningPolicy _policy;
    private readonly NodeCapabilityDiscovery _discovery;
    private readonly string _nodeId;

    public WorkerProvisioningAdministrationService(ProvisioningPolicy policy, NodeCapabilityDiscovery discovery, string nodeId)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (nodeId != "server" && !Guid.TryParseExact(nodeId, "N", out _))
            throw new ArgumentException("Worker identity must be a 32-character identifier.", nameof(nodeId));
        _nodeId = nodeId;
    }

    public async Task<WorkerProvisioningResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var states = await _discovery.GetAsync(refresh: true, cancellationToken);
        var capabilities = CapabilityCatalog.Definitions.Select(definition =>
        {
            var state = states.FirstOrDefault(candidate => candidate.Id == definition.Id) ?? CapabilityCatalog.Unknown(definition);
            var actions = definition.SupportedActions.Select(ToAction).Where(action => action.HasValue)
                .Select(action => ActionAvailability(definition.Id, action.GetValueOrDefault())).ToArray();
            return new WorkerProvisioningStatusCapability(definition.Id, definition.DisplayName, state, actions);
        }).ToArray();
        return new("status", "succeeded", ProvisioningDiagnostic.Completed, Capabilities: capabilities);
    }

    public async Task<WorkerProvisioningResult> ExecuteAsync(WorkerProvisioningOperation operation,
        CancellationToken cancellationToken = default,
        Func<ProvisioningCommandReport, CancellationToken, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var request = new ProvisioningCommandRequest(_nodeId, operation.CapabilityId, operation.Action,
            operation.TimeoutSeconds, operation.AllowElevation, operation.Repository);
        if (!ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request) ||
            (request.AllowElevation && !IsPrivileged(request.Action)))
            return Failed(request, ProvisioningDiagnostic.Unsupported,
                $"Operation '{DisplayAction(request.Action)}' is not supported for capability '{request.CapabilityId}'.");

        var authorization = WorkerProvisioning.Decide(request, _policy);
        if (!authorization.Permitted)
            return Failed(request, ProvisioningDiagnostic.Denied,
                authorization.Reason ?? "Local provisioning policy denied this operation.", authorization.Remediation);

        var report = await WorkerProvisioning.ExecuteLocalAsync(request, _policy, _discovery, cancellationToken, progress);
        var state = (await _discovery.GetAsync(cancellationToken: CancellationToken.None))
            .FirstOrDefault(candidate => candidate.Id == request.CapabilityId);
        var reason = report.Diagnostic switch
        {
            ProvisioningDiagnostic.Unsupported when IsPrivileged(request.Action) =>
                "Tool package operations are supported only on Debian-based Linux systems.",
            ProvisioningDiagnostic.Unsupported when NodeGitHubSetup.Handles(request) && !OperatingSystem.IsLinux() =>
                "This GitHub and SSH provisioning operation is supported only on Linux.",
            ProvisioningDiagnostic.Unsupported => $"Operation '{DisplayAction(request.Action)}' is not supported for capability '{request.CapabilityId}'.",
            ProvisioningDiagnostic.ProcessFailed when IsPrivileged(request.Action) =>
                "The installer failed. Capability state was re-detected; check the local package manager and network state.",
            ProvisioningDiagnostic.ProcessFailed => "The tool operation failed. Capability state was re-detected.",
            ProvisioningDiagnostic.Denied => "The local provisioning executor denied this operation.",
            ProvisioningDiagnostic.TimedOut => "The operation exceeded its configured time limit. Capability state was re-detected.",
            ProvisioningDiagnostic.Cancelled => "The operation was cancelled. Capability state was re-detected.",
            _ => null
        };
        var remediation = report.Diagnostic switch
        {
            ProvisioningDiagnostic.Unsupported when IsPrivileged(request.Action) => "Run provisioning on a Debian-based Linux Worker.",
            ProvisioningDiagnostic.Unsupported when NodeGitHubSetup.Handles(request) && !OperatingSystem.IsLinux() => "Run the operation on a Linux Worker.",
            ProvisioningDiagnostic.ProcessFailed => "Resolve the local operation issue, then run 'codex-worker provision status'.",
            ProvisioningDiagnostic.Denied => "Review worker.provisioning policy settings and rerun with --allow-elevation when required.",
            ProvisioningDiagnostic.TimedOut => "Check package manager availability and rerun the operation.",
            _ => null
        };
        return new(DisplayAction(request.Action), report.Status.ToString().ToLowerInvariant(), report.Diagnostic,
            request.CapabilityId, request.Action, reason, remediation, state, Report: report);
    }

    private WorkerProvisioningActionAvailability ActionAvailability(string capabilityId, ProvisioningCommandAction action)
    {
        var verb = DisplayAction(action);
        var keyType = NodeGitHubSetup.Handles(new(_nodeId, capabilityId, action)) ||
            action is ProvisioningCommandAction.Login or ProvisioningCommandAction.Logout or ProvisioningCommandAction.CheckAuthentication
            ? "authentication" : "tool";
        var key = $"{keyType}:{capabilityId}:{action}".ToLowerInvariant();
        if (!ProvisioningCommandProtocol.Supported(new(_nodeId, capabilityId, action)))
            return new(verb, key, false, IsPrivileged(action), $"Operation '{verb}' is not supported for this capability.");
        var request = new ProvisioningCommandRequest(_nodeId, capabilityId, action, AllowElevation: IsPrivileged(action));
        var platformReason = PlatformReason(request);
        if (platformReason is not null)
            return new(verb, key, false, IsPrivileged(action), platformReason.Value.Reason, platformReason.Value.Remediation);
        var decision = WorkerProvisioning.Decide(request, _policy);
        return new(verb, key, decision.Permitted, decision.RequiresElevation, decision.Reason, decision.Remediation);
    }

    private static (string Reason, string Remediation)? PlatformReason(ProvisioningCommandRequest request)
    {
        if (IsPrivileged(request.Action) && !NodeProvisioningCommandExecutor.SupportsPackageProvisioning())
            return ("Tool package operations are supported only on Debian-based Linux systems.", "Run provisioning on a Debian-based Linux Worker.");
        if (NodeGitHubSetup.Handles(request) && !OperatingSystem.IsLinux())
            return ("This GitHub and SSH provisioning operation is supported only on Linux.", "Run the operation on a Linux Worker.");
        return null;
    }

    private static WorkerProvisioningResult Failed(ProvisioningCommandRequest request, ProvisioningDiagnostic diagnostic,
        string reason, string? remediation = null) => new(DisplayAction(request.Action), "failed", diagnostic,
            request.CapabilityId, request.Action, reason, remediation);

    private static ProvisioningCommandAction? ToAction(string action)
    {
        if (action == "refresh") return ProvisioningCommandAction.Detect;
        return Enum.TryParse<ProvisioningCommandAction>(action, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static bool IsPrivileged(ProvisioningCommandAction action) => action is ProvisioningCommandAction.Install or
        ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall;

    public static string DisplayAction(ProvisioningCommandAction action) => action switch
    {
        ProvisioningCommandAction.Update => "upgrade",
        ProvisioningCommandAction.CheckAuthentication => "check-authentication",
        ProvisioningCommandAction.CheckConfiguration => "check-configuration",
        ProvisioningCommandAction.PrepareAuthentication => "prepare-authentication",
        ProvisioningCommandAction.GenerateSshKey => "generate-ssh-key",
        ProvisioningCommandAction.InspectSshKey => "inspect-ssh-key",
        ProvisioningCommandAction.RemoveSshKey => "remove-ssh-key",
        ProvisioningCommandAction.VerifyRepositoryAccess => "verify-repository-access",
        _ => action.ToString().ToLowerInvariant()
    };
}
