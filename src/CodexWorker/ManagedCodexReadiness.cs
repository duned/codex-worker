namespace CodexWorker;

using CodexProvisioning;

/// <summary>Tracks execution readiness without making unavailable tools fatal to node liveness.</summary>
internal sealed class ManagedCodexReadiness(IAgentAuthenticationProvider provider, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _retryAt;
    private int _failures;
    internal void Interrupt(string reason)
    {
        _ready = false;
        _retryAt = _clock.GetUtcNow().AddMinutes(5);
        DiagnosticCode = $"{FailureDiagnosticRedactor.Redact(reason)} · automatic preflight at {_retryAt:O}";
    }
    private CapabilityState? _lastObservation;
    private bool _ready;
    internal string? DiagnosticCode { get; private set; }

    internal async Task<bool> EvaluateAsync(NodeCapabilityDiscovery discovery, bool changed,
        CancellationToken token)
    {
        var states = await discovery.GetAsync(cancellationToken: token);
        var observation = states.Single(state => state.Id == "codex-cli") with { DetectedAtUtc = null };
        if (!changed && observation == _lastObservation && (_ready || _clock.GetUtcNow() < _retryAt)) return _ready;
        _lastObservation = observation;
        _ready = false;
        DiagnosticCode = null;
        if (!CapabilityCatalog.Ready([observation]))
        {
            DiagnosticCode = observation.Installation != InstallationState.Installed
                ? "Codex CLI unavailable · provisioning/repair required"
                : observation.Authentication == RequirementState.Required
                    ? "Codex authentication required · use the supported node authentication flow"
                    : "Codex capability unavailable · inspect node capability diagnostics";
            return false;
        }
        try
        {
            await provider.ValidateAsync(token);
            _ready = true;
            _failures = 0;
        }
        catch (WorkerInfrastructureException ex)
        {
            _failures = Math.Min(_failures + 1, 4);
            _retryAt = _clock.GetUtcNow().AddMinutes(5 * (1 << (_failures - 1)));
            var reason = FailureDiagnosticRedactor.Redact(ex.Message);
            if (reason.Length > 600) reason = reason[..600] + " [truncated]";
            DiagnosticCode = $"codex-cli:execution-preflight-failed · {reason} · automatic preflight at {_retryAt:O}";
        }
        return _ready;
    }
}
