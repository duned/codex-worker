namespace CodexWorker;

using CodexProvisioning;

/// <summary>Tracks execution readiness without making unavailable tools fatal to node liveness.</summary>
internal sealed class ManagedCodexReadiness(IAgentAuthenticationProvider provider)
{
    private CapabilityState? _lastObservation;
    private bool _ready;

    internal async Task<bool> EvaluateAsync(NodeCapabilityDiscovery discovery, bool changed,
        CancellationToken token)
    {
        var observation = (await discovery.GetAsync(cancellationToken: token))
            .Single(state => state.Id == "codex-cli") with { DetectedAtUtc = null };
        if (!changed && observation == _lastObservation) return _ready;
        _lastObservation = observation;
        _ready = false;
        if (observation.Installation != InstallationState.Installed ||
            observation.Authentication != RequirementState.Satisfied ||
            observation.Health != CapabilityHealth.Healthy) return false;
        try
        {
            await provider.ValidateAsync(token);
            _ready = true;
        }
        catch (WorkerInfrastructureException)
        {
            // A failed readiness probe does not kill a provisionable node. Retry only
            // after an observation changes or an explicit provisioning operation.
        }
        return _ready;
    }
}
