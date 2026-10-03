namespace CodexWorker;

using System.Text.Json;
using CodexProvisioning;

public sealed record CapabilityInventoryContract(int ContractVersion, IReadOnlyList<CapabilityInventoryItem> Capabilities);

public sealed record CapabilityInventoryItem(
    string Id,
    string DisplayName,
    InstallationState Installation,
    string? DetectedVersion,
    UpdateState Update,
    RequirementState? Authentication,
    RequirementState? Configuration,
    CapabilityHealth Health,
    string? DiagnosticCode,
    IReadOnlyList<string> AvailableActions,
    IReadOnlyList<AuthenticationDependencyKind> AuthenticationDependencies,
    IReadOnlyList<ProvidedToolKind> Provides,
    LocalConfigurationDependencyKind? ConfigurationDependency,
    int? RequiredMajorVersion,
    bool RequiredForExecution)
{
    public CapabilityReadiness Readiness { get; init; } = new(false, ["not-detected"]);
}

public static class CapabilityInventoryReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<CapabilityInventoryContract> CreateAsync(NodeCapabilityDiscovery discovery, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var states = await discovery.GetAsync(refresh, cancellationToken);
        var items = CapabilityCatalog.Definitions.Select(definition =>
        {
            var state = states.FirstOrDefault(item => item.Id == definition.Id) ?? CapabilityCatalog.Unknown(definition);
            var capability = CapabilityCatalog.Describe(definition, state, connected: true);
            return new CapabilityInventoryItem(definition.Id, definition.DisplayName, state.Installation,
                state.DetectedVersion, state.Update, state.Authentication, state.Configuration, state.Health,
                state.DiagnosticCode, capability.AvailableActions, definition.AuthenticationDependencies, definition.Provides,
                definition.ConfigurationDependency, definition.RequiredMajorVersion, definition.RequiredForExecution)
                { Readiness = capability.Readiness };
        }).ToArray();
        return new(1, items);
    }

    public static void Write(CapabilityInventoryContract inventory, bool json, TextWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        writer ??= Console.Out;
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(inventory, JsonOptions));
            return;
        }

        foreach (var capability in inventory.Capabilities)
        {
            var version = capability.DetectedVersion is null ? "version=unknown" : $"version={capability.DetectedVersion}";
            var authentication = capability.Authentication is null ? null : $"auth={capability.Authentication.Value.ToString().ToLowerInvariant()}";
            var configuration = capability.Configuration is null ? null : $"config={capability.Configuration.Value.ToString().ToLowerInvariant()}";
            var actions = capability.AvailableActions.Count == 0 ? "none" : string.Join(",", capability.AvailableActions);
            var dimensions = new[] { authentication, configuration }.Where(value => value is not null);
            writer.WriteLine($"{capability.Id}: installation={capability.Installation.ToString().ToLowerInvariant()} {version} " +
                $"update={capability.Update.ToString().ToLowerInvariant()} {string.Join(" ", dimensions)} " +
                $"available={capability.Readiness.Available.ToString().ToLowerInvariant()} blocking-reasons={string.Join(",", capability.Readiness.BlockingReasons)} " +
                $"health={capability.Health.ToString().ToLowerInvariant()} diagnostic={capability.DiagnosticCode ?? "none"} actions={actions} " +
                $"authentication-dependencies={DisplayAuthenticationDependencies(capability.AuthenticationDependencies)} " +
                $"provides={string.Join(",", capability.Provides)} configuration-dependency={capability.ConfigurationDependency?.ToString() ?? "none"} " +
                $"required-major-version={capability.RequiredMajorVersion?.ToString() ?? "none"}");
        }
    }

    internal static string DisplayAuthenticationDependencies(IReadOnlyList<AuthenticationDependencyKind> dependencies) =>
        dependencies.Count == 0 ? "none" : string.Join(",", dependencies);
}
