namespace CodexWorker;

using CodexProvisioning;

/// <summary>Worker-local authorization and entry point for the shared typed node handlers.</summary>
public static class WorkerProvisioning
{
    public sealed record AuthorizationDecision(bool Permitted, string? Reason = null, string? Remediation = null,
        bool RequiresElevation = false, ProvisioningFailureCode? FailureCode = null);

    public static bool Permitted(ProvisioningCommandRequest request, ProvisioningPolicy policy)
        => Decide(request, policy).Permitted;

    public static AuthorizationDecision Decide(ProvisioningCommandRequest request, ProvisioningPolicy policy)
    {
        var action = request.Action;
        var readOnly = action is ProvisioningCommandAction.Detect or ProvisioningCommandAction.CheckAuthentication or ProvisioningCommandAction.CheckConfiguration or ProvisioningCommandAction.InspectSshKey or ProvisioningCommandAction.VerifyRepositoryAccess;
        var type = NodeGitHubSetup.Handles(request) || action is ProvisioningCommandAction.Login or ProvisioningCommandAction.Logout or ProvisioningCommandAction.CheckAuthentication ? "authentication" : "tool";
        var key = $"{type}:{request.CapabilityId}:{action}".ToLowerInvariant();
        var privileged = request.CapabilityId != "claude-cli" && action is (ProvisioningCommandAction.Install or ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall or ProvisioningCommandAction.Configure);
        if (policy.DeniedActions.Contains(key, StringComparer.OrdinalIgnoreCase))
            return new(false, $"{key} is denied by local provisioning policy.",
                $"Remove '{key}' from worker.provisioning.deniedActions.", privileged, ProvisioningFailureCode.PolicyActionDenied);
        if (readOnly) return new(true);
        if (!policy.Enabled)
            return new(false, "Local provisioning is disabled.", "Set worker.provisioning.enabled: true.", privileged, ProvisioningFailureCode.PolicyProvisioningDisabled);
        if (type == "authentication" && !policy.AllowCredentials)
            return new(false, "Credential operations are disabled by local provisioning policy.",
                "Set worker.provisioning.allowCredentials: true.", FailureCode: ProvisioningFailureCode.PolicyCredentialsDisabled);
        if (privileged && !policy.AllowedPrivilegedActions.Contains(key, StringComparer.OrdinalIgnoreCase))
            return new(false, $"Privileged action '{key}' is not allowlisted.",
                $"Add '{key}' to worker.provisioning.allowedPrivilegedActions (codex-worker config add worker.provisioning.allowedPrivilegedActions {key}).",
                RequiresElevation: true, FailureCode: ProvisioningFailureCode.PolicyActionNotAllowlisted);
        if (privileged && !request.AllowElevation)
            return new(false, "This privileged operation requires explicit elevation authorization.",
                "Rerun the command with --allow-elevation.", RequiresElevation: true, FailureCode: ProvisioningFailureCode.ElevationNotRequested);
        if (!privileged && !policy.AllowNonPrivileged)
            return new(false, "Non-privileged provisioning operations are disabled by local policy.",
                "Set worker.provisioning.allowNonPrivileged: true.", FailureCode: ProvisioningFailureCode.PolicyNonPrivilegedDisabled);
        return new(true, RequiresElevation: privileged);
    }

    public static async Task<ProvisioningCommandReport> ExecuteLocalAsync(ProvisioningCommandRequest request,
        ProvisioningPolicy policy, NodeCapabilityDiscovery discovery, CancellationToken token,
        Func<ProvisioningCommandReport, CancellationToken, Task>? reportProgress = null,
        NodeProvisioningCommandExecutor? executor = null)
    {
        if (!ProvisioningCommandProtocol.Valid(request)) throw new InvalidDataException("Invalid provisioning request.");
        var now = DateTimeOffset.UtcNow;
        var decision = Decide(request, policy);
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), request, now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddSeconds(request.TimeoutSeconds));
        return await (executor ?? new NodeProvisioningCommandExecutor(discovery))
            .ExecuteAsync(command, decision.Permitted, token, reportProgress, decision.FailureCode);
    }
}
