namespace CodexWorker;

public sealed record ProvisioningPolicyDecision(bool Permitted, string Reason);

public static class ProvisioningPolicyEvaluator
{
    public static string ActionKey(ProvisioningActionContract action) =>
        $"{action.Type}:{action.Name}:{action.Operation}".ToLowerInvariant();

    public static ProvisioningPolicyDecision Decide(ProvisioningPolicy policy, ProvisioningActionContract action, bool privileged)
    {
        var key = ActionKey(action);
        if (policy.DeniedActions.Contains(key, StringComparer.OrdinalIgnoreCase))
            return new ProvisioningPolicyDecision(false, "denied by Worker provisioning policy");
        if (!policy.Enabled)
            return new ProvisioningPolicyDecision(false, "denied by Worker provisioning policy because provisioning is disabled");
        if (action.Type == "authentication" && !policy.AllowCredentials)
            return new ProvisioningPolicyDecision(false, "denied by Worker provisioning policy because credential provisioning is disabled");
        if (privileged && !policy.AllowedPrivilegedActions.Contains(key, StringComparer.OrdinalIgnoreCase))
            return new ProvisioningPolicyDecision(false, "denied by Worker provisioning policy because this privileged action is not allowlisted");
        if (!privileged && !policy.AllowNonPrivileged)
            return new ProvisioningPolicyDecision(false, "denied by Worker provisioning policy because non-privileged provisioning is disabled");
        return new ProvisioningPolicyDecision(true, privileged
            ? "Permitted by Worker policy as an explicitly allowlisted privileged action."
            : "Permitted by Worker policy as a non-privileged action.");
    }
}

/// <summary>Executes structured provisioning actions through explicit Worker-side installers.</summary>
public sealed class ProvisioningPlanExecutor
{
    private readonly WorkerCapabilityDiscovery _discovery;
    private readonly IReadOnlyList<IDependencyInstaller> _installers;
    private readonly ProvisioningPolicy _policy;
    private readonly IAuthenticationActionExecutor? _authenticationExecutor;

    public ProvisioningPlanExecutor(WorkerCapabilityDiscovery discovery, IEnumerable<IDependencyInstaller>? installers = null,
        ProvisioningPolicy? policy = null, IAuthenticationActionExecutor? authenticationExecutor = null)
    {
        _discovery = discovery;
        _installers = (installers ?? [new DebianAptDependencyInstaller()]).ToArray();
        _policy = policy ?? new ProvisioningPolicy();
        _authenticationExecutor = authenticationExecutor;
    }

    public async Task<ProvisioningWorkerReportContract> ExecuteAsync(ProvisioningPlanContract plan, string workerId,
        Func<ProvisioningWorkerReportContract, CancellationToken, Task> report, CancellationToken cancellationToken)
    {
        var installedCapabilities = new List<string>();
        var decisions = new List<string>();
        if (plan.Actions.Count == 0)
        {
            const string noOp = "No provisioning actions were required.";
            await report(new ProvisioningWorkerReportContract(workerId, "Completed", Result: noOp), cancellationToken);
            return new ProvisioningWorkerReportContract(workerId, "Completed", Result: noOp);
        }

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await report(new ProvisioningWorkerReportContract(workerId, "Running", action.Id,
                Result: action.Type == "refresh-capabilities" ? "Capability refresh is permitted." : null), cancellationToken);
            var actionKey = ProvisioningPolicyEvaluator.ActionKey(action);
            if (action.Type != "refresh-capabilities" && _policy.DeniedActions.Contains(actionKey, StringComparer.OrdinalIgnoreCase))
                return await FailedAsync(workerId, action, "denied by Worker provisioning policy", report, cancellationToken);
            if (action.Type != "refresh-capabilities" && !_policy.Enabled)
                return await FailedAsync(workerId, action, "denied by Worker provisioning policy because provisioning is disabled", report, cancellationToken);
            if (action.Type == "authentication" && !_policy.AllowCredentials)
                return await FailedAsync(workerId, action, "denied by Worker provisioning policy because credential provisioning is disabled", report, cancellationToken);
            if (action.Type != "refresh-capabilities" && action.Type != "authentication" && action.Operation is not ("ensure" or "install"))
            {
                var unsupported = new ProvisioningWorkerReportContract(workerId, "Failed", action.Id,
                    Failure: $"Action '{action.Id}' could not be completed: no local executor is registered for operation '{action.Operation}'.");
                await report(unsupported, cancellationToken);
                return unsupported;
            }
            if (action.Type == "refresh-capabilities")
            {
                await _discovery.RefreshAsync(cancellationToken);
                decisions.Add($"{action.Id}: capability refresh permitted");
                continue;
            }

            if (action.Type == "authentication")
            {
                var authenticationDecision = ProvisioningPolicyEvaluator.Decide(_policy, action, privileged: false);
                if (!authenticationDecision.Permitted)
                    return await FailedAsync(workerId, action, authenticationDecision.Reason!, report, cancellationToken, decisions);
                if (action.Operation != "provision" || _authenticationExecutor is null)
                    return await FailedAsync(workerId, action, "no supported authentication provisioning handler is registered", report, cancellationToken, decisions);
                var provisioned = await _authenticationExecutor.ExecuteAsync(action, cancellationToken);
                if (!provisioned.Succeeded)
                    return await FailedAsync(workerId, action, provisioned.Message ?? "authentication provisioning failed", report, cancellationToken, decisions);
                installedCapabilities.Add($"authentication {action.Name} for {action.Scope}");
                continue;
            }

            var requirement = new ProvisioningRequirement(action.Type, action.Name, action.Version);
            var capabilities = await _discovery.GetCachedAsync(cancellationToken);
            if (capabilities.Any(capability => CapabilityVersionMatcher.Satisfies(capability, requirement)))
            {
                decisions.Add($"{action.Id}: already satisfied; no operation performed");
                continue;
            }

            var installer = _installers.FirstOrDefault(candidate => candidate.Supports(requirement));
            if (installer is null)
                return await FailedAsync(workerId, action, "no supported installer is registered for this requirement", report, cancellationToken);

            var installPlan = installer.Plan(requirement, capabilities);
            if (!installPlan.Supported)
                return await FailedAsync(workerId, action, installPlan.Reason ?? "the requirement is unsupported", report, cancellationToken);
            if (installPlan.AlreadySatisfied) continue;

            var decision = ProvisioningPolicyEvaluator.Decide(_policy, action, installPlan.RequiresElevation);
            if (!decision.Permitted)
                return await FailedAsync(workerId, action, decision.Reason!, report, cancellationToken);
            decisions.Add($"{action.Id}: {decision.Reason}");
            await report(new ProvisioningWorkerReportContract(workerId, "Running", action.Id,
                Result: decision.Reason), cancellationToken);

            var installed = await installer.InstallAsync(installPlan, cancellationToken);
            if (!installed.Succeeded)
                return await FailedAsync(workerId, action, installed.Message ?? "installation failed", report, cancellationToken, decisions);

            var verified = await _discovery.RefreshAsync(cancellationToken);
            var detected = verified.FirstOrDefault(capability => CapabilityVersionMatcher.Satisfies(capability, requirement));
            if (detected is null)
                return await FailedAsync(workerId, action, "installation completed but the requested capability/version was not detected afterward", report, cancellationToken, decisions);
            installed = installed with { DetectedVersion = detected.Version, DetectedCapability = detected };
            var detectedCapability = installed.DetectedCapability ?? detected;
            installedCapabilities.Add($"{detectedCapability.Type} {detectedCapability.Name}{(installed.DetectedVersion is null ? "" : " " + installed.DetectedVersion)}");
        }

        var completed = installedCapabilities.Count == 0
            ? "All requested provisioning actions are satisfied."
            : $"All requested provisioning actions are satisfied. Installed and verified: {string.Join(", ", installedCapabilities)}.";
        if (decisions.Count > 0) completed += " Decisions: " + BoundDecisionHistory(decisions);
        if (completed.Length > 1000) completed = completed[..997] + "...";
        var result = new ProvisioningWorkerReportContract(workerId, "Completed", Result: completed);
        await report(result, cancellationToken);
        return result;
    }

    private static string BoundDecisionHistory(IReadOnlyList<string> decisions)
    {
        const int maximumLength = 850;
        var text = string.Join("; ", decisions);
        return text.Length <= maximumLength ? text : text[..(maximumLength - 3)] + "...";
    }

    private static async Task<ProvisioningWorkerReportContract> FailedAsync(string workerId, ProvisioningActionContract action,
        string reason, Func<ProvisioningWorkerReportContract, CancellationToken, Task> report, CancellationToken cancellationToken,
        IReadOnlyList<string>? decisions = null)
    {
        var failure = $"Action '{action.Id}' could not be completed: {reason}.";
        var history = decisions is { Count: > 0 } ? BoundDecisionHistory(decisions) : null;
        var failed = new ProvisioningWorkerReportContract(workerId, "Failed", action.Id, Result: history, Failure: failure);
        await report(failed, cancellationToken);
        return failed;
    }
}
