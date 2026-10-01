namespace CodexWorker;

using System.Text.Json;
using CodexProvisioning;

public sealed record ProvisioningCliCommand(bool IsStatus, string Verb, string? CapabilityId = null,
    ProvisioningCommandAction? Action = null, bool AllowElevation = false, bool Json = false, string? Repository = null);

public sealed record ProvisioningActionAvailability(string Action, string PolicyKey, bool Allowed,
    bool RequiresElevation, string? Reason = null, string? Remediation = null);

public sealed record ProvisioningStatusCapability(string Id, string DisplayName, CapabilityState State,
    IReadOnlyList<ProvisioningActionAvailability> Actions);

public sealed record ProvisioningCliResult(string Command, string Status, ProvisioningDiagnostic Diagnostic,
    string? CapabilityId = null, ProvisioningCommandAction? Action = null, string? Reason = null,
    string? Remediation = null, CapabilityState? Capability = null,
    IReadOnlyList<ProvisioningStatusCapability>? Capabilities = null, ProvisioningCommandReport? Report = null);

/// <summary>Parser, local policy presentation, and result formatting for bounded provisioning commands.</summary>
public static class ProvisioningCli
{
    private static readonly IReadOnlyDictionary<string, ProvisioningCommandAction> Verbs =
        new Dictionary<string, ProvisioningCommandAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["detect"] = ProvisioningCommandAction.Detect,
            ["install"] = ProvisioningCommandAction.Install,
            ["upgrade"] = ProvisioningCommandAction.Update,
            ["uninstall"] = ProvisioningCommandAction.Uninstall,
            ["check-authentication"] = ProvisioningCommandAction.CheckAuthentication,
            ["logout"] = ProvisioningCommandAction.Logout,
            ["check-configuration"] = ProvisioningCommandAction.CheckConfiguration,
            ["prepare-authentication"] = ProvisioningCommandAction.PrepareAuthentication,
            ["generate-ssh-key"] = ProvisioningCommandAction.GenerateSshKey,
            ["inspect-ssh-key"] = ProvisioningCommandAction.InspectSshKey,
            ["remove-ssh-key"] = ProvisioningCommandAction.RemoveSshKey,
            ["verify-repository-access"] = ProvisioningCommandAction.VerifyRepositoryAccess,
            ["login"] = ProvisioningCommandAction.Login
        };

    public static ProvisioningCliCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0) throw Usage("Expected 'status' or a provisioning operation.");
        if (arguments[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count != 1 && !(arguments.Count == 2 && arguments[1] == "--json"))
                throw Usage("The status command does not accept a capability or action.");
            return new(true, "status", Json: arguments.Count == 2);
        }

        if (arguments.Count < 2)
            throw Usage("An operation and capability id are required.");
        var verb = arguments[0];
        var requestedCapabilityId = arguments[1];
        var definition = CapabilityCatalog.Definitions.FirstOrDefault(item => item.Id.Equals(requestedCapabilityId, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            throw new ArgumentException($"Unknown capability '{requestedCapabilityId}'. Known capabilities: {string.Join(", ", CapabilityCatalog.Definitions.Select(item => item.Id))}.");
        var capabilityId = definition.Id;
        if (!Verbs.TryGetValue(verb, out var action))
            throw Usage($"Unknown provisioning operation '{verb}'.");

        var allowElevation = false;
        var json = false;
        string? repository = null;
        for (var index = 2; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--allow-elevation" when !allowElevation:
                    allowElevation = true;
                    break;
                case "--json" when !json:
                    json = true;
                    break;
                case "--repository" when repository is null && index + 1 < arguments.Count:
                    repository = arguments[++index];
                    break;
                default:
                    throw Usage($"Unexpected or repeated option '{arguments[index]}'.");
            }
        }
        if (allowElevation && action is not (ProvisioningCommandAction.Install or ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall))
            throw Usage("--allow-elevation is only valid for install, upgrade, and uninstall.");
        if ((action == ProvisioningCommandAction.VerifyRepositoryAccess) != (repository is not null))
            throw Usage("verify-repository-access requires --repository <owner/repository>; other operations do not accept it.");
        if (repository is not null && !System.Text.RegularExpressions.Regex.IsMatch(repository,
            @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z"))
            throw Usage("--repository must be a repository name in owner/repository form.");

        return new(false, DisplayVerb(action), capabilityId, action, allowElevation, json, repository);
    }

    public static async Task<ProvisioningCliResult> ExecuteAsync(ProvisioningCliCommand command,
        ProvisioningPolicy policy, NodeCapabilityDiscovery discovery, string nodeId, CancellationToken cancellationToken,
        Func<ProvisioningCommandReport, CancellationToken, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(discovery);
        if (command.IsStatus)
        {
            var states = await discovery.GetAsync(refresh: true, cancellationToken);
            var capabilities = CapabilityCatalog.Definitions.Select(definition =>
            {
                var state = states.FirstOrDefault(candidate => candidate.Id == definition.Id) ?? CapabilityCatalog.Unknown(definition);
                var actions = definition.SupportedActions.Select(ToAction).Where(action => action.HasValue)
                    .Select(action => ActionAvailability(nodeId, definition.Id, action.GetValueOrDefault(), policy)).ToArray();
                return new ProvisioningStatusCapability(definition.Id, definition.DisplayName, state, actions);
            }).ToArray();
            return new("status", "succeeded", ProvisioningDiagnostic.Completed, Capabilities: capabilities);
        }

        var selectedAction = command.Action ?? throw new InvalidOperationException("A provisioning action is required.");
        var request = new ProvisioningCommandRequest(nodeId, command.CapabilityId ?? throw new InvalidOperationException("A capability id is required."),
            selectedAction, AllowElevation: command.AllowElevation, Repository: command.Repository);
        if (!ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request))
        {
            return Failed(command, ProvisioningDiagnostic.Unsupported,
                $"Operation '{command.Verb}' is not supported for capability '{request.CapabilityId}'.");
        }

        var authorization = WorkerProvisioning.Decide(request, policy);
        if (!authorization.Permitted)
            return Failed(command, ProvisioningDiagnostic.Denied,
                authorization.Reason ?? "Local provisioning policy denied this operation.", authorization.Remediation);

        var report = await WorkerProvisioning.ExecuteLocalAsync(request, policy, discovery, cancellationToken, progress);
        var state = (await discovery.GetAsync(cancellationToken: CancellationToken.None))
            .FirstOrDefault(candidate => candidate.Id == request.CapabilityId);
        var reason = report.Diagnostic switch
        {
            ProvisioningDiagnostic.Unsupported when IsPrivileged(selectedAction) =>
                "Tool package operations are supported only on Debian-based Linux systems.",
            ProvisioningDiagnostic.Unsupported when NodeGitHubSetup.Handles(request) && !OperatingSystem.IsLinux() =>
                "This GitHub and SSH provisioning operation is supported only on Linux.",
            ProvisioningDiagnostic.Unsupported => $"Operation '{command.Verb}' is not supported for capability '{request.CapabilityId}'.",
            ProvisioningDiagnostic.ProcessFailed when IsPrivileged(selectedAction) =>
                "The installer failed. Capability state was re-detected; check the local package manager and network state.",
            ProvisioningDiagnostic.ProcessFailed => "The tool operation failed. Capability state was re-detected.",
            ProvisioningDiagnostic.Denied => "The local provisioning executor denied this operation.",
            ProvisioningDiagnostic.TimedOut => "The operation exceeded its configured time limit. Capability state was re-detected.",
            ProvisioningDiagnostic.Cancelled => "The operation was cancelled. Capability state was re-detected.",
            _ => null
        };
        var remediation = report.Diagnostic switch
        {
            ProvisioningDiagnostic.Unsupported when IsPrivileged(selectedAction) => "Run provisioning on a Debian-based Linux Worker.",
            ProvisioningDiagnostic.Unsupported when NodeGitHubSetup.Handles(request) && !OperatingSystem.IsLinux() => "Run the operation on a Linux Worker.",
            ProvisioningDiagnostic.ProcessFailed => "Resolve the local operation issue, then run 'codex-worker provision status'.",
            ProvisioningDiagnostic.Denied => "Review worker.provisioning policy settings and rerun with --allow-elevation when required.",
            ProvisioningDiagnostic.TimedOut => "Check package manager availability and rerun the operation.",
            _ => null
        };
        return new(command.Verb, report.Status.ToString().ToLowerInvariant(), report.Diagnostic,
            request.CapabilityId, selectedAction, reason, remediation, state, Report: report);
    }

    public static void Write(ProvisioningCliResult result, bool json)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return;
        }

        if (result.Capabilities is not null)
        {
            Console.WriteLine("Local provisioning status:");
            foreach (var capability in result.Capabilities)
            {
                var state = capability.State;
                Console.WriteLine($"{capability.Id} ({capability.DisplayName}): installation={state.Installation.ToString().ToLowerInvariant()}, " +
                    $"version={state.DetectedVersion ?? "unknown"}, update={state.Update.ToString().ToLowerInvariant()}, health={state.Health.ToString().ToLowerInvariant()}");
                foreach (var action in capability.Actions)
                {
                    var authorization = action.RequiresElevation ? " (requires --allow-elevation)" : string.Empty;
                    Console.WriteLine($"  {action.Action}: {(action.Allowed ? "allowed" : "denied")}{authorization}" +
                        (action.Reason is null ? string.Empty : $" — {action.Reason}"));
                    if (!action.Allowed && action.Remediation is not null) Console.WriteLine($"    Remediation: {action.Remediation}");
                }
            }
            return;
        }

        Console.WriteLine($"{Capitalize(result.Command)} {result.CapabilityId}: {result.Status}.");
        if (result.Reason is not null) Console.WriteLine($"Reason: {result.Reason}");
        if (result.Remediation is not null) Console.WriteLine($"Remediation: {result.Remediation}");
        if (result.Capability is { } capabilityState)
            Console.WriteLine($"Current state: installation={capabilityState.Installation.ToString().ToLowerInvariant()}, " +
                $"version={capabilityState.DetectedVersion ?? "unknown"}, update={capabilityState.Update.ToString().ToLowerInvariant()}, " +
                $"health={capabilityState.Health.ToString().ToLowerInvariant()}");
    }

    public static ProvisioningActionAvailability ActionAvailability(string nodeId, string capabilityId,
        ProvisioningCommandAction action, ProvisioningPolicy policy)
    {
        var verb = DisplayVerb(action);
        var keyType = NodeGitHubSetup.Handles(new(nodeId, capabilityId, action)) ||
            action is ProvisioningCommandAction.Login or ProvisioningCommandAction.Logout or ProvisioningCommandAction.CheckAuthentication
            ? "authentication" : "tool";
        var key = $"{keyType}:{capabilityId}:{action}".ToLowerInvariant();
        if (!ProvisioningCommandProtocol.Supported(new(nodeId, capabilityId, action)))
            return new(verb, key, false, IsPrivileged(action), $"Operation '{verb}' is not supported for this capability.");
        var request = new ProvisioningCommandRequest(nodeId, capabilityId, action, AllowElevation: IsPrivileged(action));
        var platformReason = PlatformReason(request);
        if (platformReason is not null)
            return new(verb, key, false, IsPrivileged(action), platformReason.Value.Reason, platformReason.Value.Remediation);
        // Status describes whether policy permits the operation when its documented elevation flag is supplied.
        var decision = WorkerProvisioning.Decide(request, policy);
        return new(verb, key, decision.Permitted, decision.RequiresElevation, decision.Reason, decision.Remediation);
    }

    public static string DisplayVerb(ProvisioningCommandAction action) => action switch
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

    private static ProvisioningCliResult Failed(ProvisioningCliCommand command, ProvisioningDiagnostic diagnostic,
        string reason, string? remediation = null) => new(command.Verb, "failed", diagnostic,
            command.CapabilityId, command.Action, reason, remediation);

    private static ProvisioningCommandAction? ToAction(string action)
    {
        if (action == "refresh") return ProvisioningCommandAction.Detect;
        return Enum.TryParse<ProvisioningCommandAction>(action, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static bool IsPrivileged(ProvisioningCommandAction action) => action is ProvisioningCommandAction.Install or
        ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall;

    private static (string Reason, string Remediation)? PlatformReason(ProvisioningCommandRequest request)
    {
        if (IsPrivileged(request.Action) && !NodeProvisioningCommandExecutor.SupportsPackageProvisioning())
            return ("Tool package operations are supported only on Debian-based Linux systems.",
                "Run provisioning on a Debian-based Linux Worker.");
        if (NodeGitHubSetup.Handles(request) && !OperatingSystem.IsLinux())
            return ("This GitHub and SSH provisioning operation is supported only on Linux.", "Run the operation on a Linux Worker.");
        return null;
    }

    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static ArgumentException Usage(string reason) => new($"{reason} Use 'codex-worker provision --help' for usage.");
}
