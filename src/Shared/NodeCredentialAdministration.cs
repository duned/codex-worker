namespace CodexProvisioning;

/// <summary>Node-local provider state, without credential payloads or provider process output.</summary>
public sealed record NodeCredentialStatus(string CapabilityId, InstallationState Installation,
    RequirementState? Authentication, IReadOnlyList<AuthenticationDependencyKind> AuthenticationDependencies,
    bool Ready);

public sealed record NodeCredentialResult(string Operation, ProvisioningCommandReport Report,
    IReadOnlyList<NodeCredentialStatus> Credentials, int ContractVersion = 1);

/// <summary>
/// Shared local credential administration. Hosts retain authorization and command execution ownership;
/// login challenges and results use the existing transportable provisioning contracts.
/// Provider CLIs retain their private credentials on this node.
/// </summary>
public sealed class NodeCredentialAdministration(NodeCapabilityDiscovery discovery,
    Func<ProvisioningCommandRequest, CancellationToken,
        Func<ProvisioningCommandReport, CancellationToken, Task>?, Task<ProvisioningCommandReport>> execute)
{
    public async Task<NodeCredentialResult> StatusAsync(CancellationToken cancellationToken = default) =>
        new("status", new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed),
            await ReadStatusAsync(refresh: true, cancellationToken));

    public async Task<NodeCredentialResult> ExecuteAsync(ProvisioningCommandRequest request,
        CancellationToken cancellationToken = default,
        Func<ProvisioningCommandReport, CancellationToken, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action is not (ProvisioningCommandAction.Login or ProvisioningCommandAction.Logout or
            ProvisioningCommandAction.CheckAuthentication) || !ProvisioningCommandProtocol.Valid(request) ||
            !ProvisioningCommandProtocol.Supported(request) || request.AllowElevation || request.Repository is not null)
            throw new ArgumentException("Unsupported credential operation.", nameof(request));

        var report = await execute(request, cancellationToken, progress is null ? null : async (update, token) =>
        {
            if (!ProvisioningCommandProtocol.ValidReport(update))
                throw new InvalidDataException("Invalid authentication progress report.");
            await progress(update, token);
        });
        if (!ProvisioningCommandProtocol.ValidReport(report) || !ProvisioningCommandProtocol.Terminal(report.Status))
            throw new InvalidDataException("Invalid authentication result.");
        // The executor refreshes observations after mutations, including interrupted operations.
        // Preserve the terminal cancellation report without starting more observation I/O on shutdown.
        return new(request.Action.ToString().ToLowerInvariant(), report,
            cancellationToken.IsCancellationRequested ? [] : await ReadStatusAsync(refresh: false, cancellationToken));
    }

    private async Task<IReadOnlyList<NodeCredentialStatus>> ReadStatusAsync(bool refresh, CancellationToken cancellationToken)
    {
        var states = await discovery.GetAsync(refresh, cancellationToken);
        return CapabilityCatalog.Definitions.Select(definition =>
        {
            var state = states.FirstOrDefault(item => item.Id == definition.Id) ?? CapabilityCatalog.Unknown(definition);
            return new NodeCredentialStatus(definition.Id, state.Installation, state.Authentication,
                definition.AuthenticationDependencies, CapabilityCatalog.Ready([state]));
        }).ToArray();
    }
}
