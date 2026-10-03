namespace CodexWorker;

using System.Text.Json;
using CodexProvisioning;

public sealed record ProvisioningCliCommand(bool IsStatus, string Verb, string? CapabilityId = null,
    ProvisioningCommandAction? Action = null, bool AllowElevation = false, bool Json = false, string? Repository = null, int TimeoutSeconds = 120);

/// <summary>Console adapter for typed Worker provisioning administration.</summary>
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

    public static bool IsVerb(string? verb) => verb is not null &&
        (verb.Equals("status", StringComparison.OrdinalIgnoreCase) || Verbs.ContainsKey(verb));

    public static void WriteHelp(string verb, TextWriter writer)
    {
        if (verb.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteLine("Usage: codex-worker provision status [--config <path>] [--json]");
            writer.WriteLine("Read local capability state and action policy without running a provisioning mutation.");
            return;
        }
        if (!Verbs.TryGetValue(verb, out var action)) throw Usage("Unknown provisioning operation.");
        var canonical = WorkerProvisioningAdministrationService.DisplayAction(action);
        var elevation = action is ProvisioningCommandAction.Install or ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall;
        writer.WriteLine($"Usage: codex-worker provision {canonical} <capability-id> [--config <path>] [--json] [--timeout-seconds 5..600]" +
            (elevation ? " --allow-elevation" : "") +
            (action == ProvisioningCommandAction.VerifyRepositoryAccess ? " --repository <owner/repository>" : ""));
        writer.WriteLine("Capability IDs: " + string.Join(", ", CapabilityCatalog.Definitions.Select(item => item.Id)));
        writer.WriteLine("Runs a typed local operation subject to Worker provisioning policy. Default deadline: 120 seconds.");
        if (elevation) writer.WriteLine("Elevation is opt-in and must also be allowed by node-local policy.");
        writer.WriteLine("Cancellation stops the operation; inspect capability state before retrying a mutation.");
    }

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

        if (arguments.Count < 2) throw Usage("An operation and capability id are required.");
        var verb = arguments[0];
        var requestedCapabilityId = arguments[1];
        var definition = CapabilityCatalog.Definitions.FirstOrDefault(item => item.Id.Equals(requestedCapabilityId, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            throw new ArgumentException($"Unknown capability '{requestedCapabilityId}'. Known capabilities: {string.Join(", ", CapabilityCatalog.Definitions.Select(item => item.Id))}.");
        if (!Verbs.TryGetValue(verb, out var action)) throw Usage($"Unknown provisioning operation '{verb}'.");

        var allowElevation = false;
        var json = false;
        string? repository = null;
        int? timeoutSeconds = null;
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
                case "--timeout-seconds" when timeoutSeconds is null:
                    if (index + 1 >= arguments.Count || !int.TryParse(arguments[++index], out var timeout) || timeout is < 5 or > 600)
                        throw Usage("--timeout-seconds requires an integer from 5 to 600.");
                    timeoutSeconds = timeout;
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

        return new(false, WorkerProvisioningAdministrationService.DisplayAction(action), definition.Id, action,
            allowElevation, json, repository, timeoutSeconds ?? 120);
    }

    public static Task<WorkerProvisioningResult> ExecuteAsync(ProvisioningCliCommand command,
        IWorkerProvisioningAdministrationService service, CancellationToken cancellationToken,
        Func<ProvisioningCommandReport, CancellationToken, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(service);
        if (command.IsStatus) return service.GetStatusAsync(cancellationToken);
        var operation = new WorkerProvisioningOperation(command.CapabilityId ?? throw new InvalidOperationException("A capability id is required."),
            command.Action ?? throw new InvalidOperationException("A provisioning action is required."), command.AllowElevation, command.Repository, command.TimeoutSeconds);
        return service.ExecuteAsync(operation, cancellationToken, progress);
    }

    public static void Write(WorkerProvisioningResult result, bool json)
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
            Console.WriteLine("Node authentication is the Codex/GitHub CLI state observed in the service-account environment; it does not report provider-side permissions, Worker registration, credential delivery, or project scheduling authorization.");
            foreach (var capability in result.Capabilities)
            {
                var state = capability.State;
                Console.WriteLine($"{capability.Id} ({capability.DisplayName}): installation={state.Installation.ToString().ToLowerInvariant()}, " +
                    $"version={state.DetectedVersion ?? "unknown"}, update={state.Update.ToString().ToLowerInvariant()}, " +
                    $"node-authentication={DisplayRequirement(state.Authentication)}, node-configuration={DisplayRequirement(state.Configuration)}, " +
                    $"health={state.Health.ToString().ToLowerInvariant()}");
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
        if (result.Status == "succeeded" && result.CapabilityId == "github-cli" &&
            result.Action == ProvisioningCommandAction.PrepareAuthentication)
        {
            Console.WriteLine("Complete GitHub browser/device login in a terminal on this node as the Worker service account. Clear any inherited GitHub authentication variables first:");
            Console.WriteLine("GH_CONFIG_DIR=\"$HOME/.local/share/codex-provisioning/github\" gh auth login --hostname github.com --git-protocol ssh --web --skip-ssh-key");
            Console.WriteLine("Then run 'codex-worker provision check-authentication github-cli'. This checks node-local login only, not provider scopes or repository write access.");
        }
        if (result.Reason is not null) Console.WriteLine($"Reason: {result.Reason}");
        if (result.Remediation is not null) Console.WriteLine($"Remediation: {result.Remediation}");
        if (result.Capability is { } capabilityState)
            Console.WriteLine($"Current state: installation={capabilityState.Installation.ToString().ToLowerInvariant()}, " +
                $"version={capabilityState.DetectedVersion ?? "unknown"}, update={capabilityState.Update.ToString().ToLowerInvariant()}, " +
                $"node-authentication={DisplayRequirement(capabilityState.Authentication)}, " +
                $"node-configuration={DisplayRequirement(capabilityState.Configuration)}, " +
                $"health={capabilityState.Health.ToString().ToLowerInvariant()}");
    }

    private static string DisplayRequirement(RequirementState? state) => state?.ToString().ToLowerInvariant() ?? "not-required";
    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    private static ArgumentException Usage(string reason) => new($"{reason} Use 'codex-worker provision --help' for usage.");
}
