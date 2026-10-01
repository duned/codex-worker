namespace CodexWorker;

using CodexProvisioning;

/// <summary>Worker-local authorization and entry point for the shared typed node handlers.</summary>
public static class WorkerProvisioning
{
    public static bool Permitted(ProvisioningCommandRequest request, ProvisioningPolicy policy)
    {
        var action = request.Action;
        var readOnly = action is ProvisioningCommandAction.Detect or ProvisioningCommandAction.CheckAuthentication or ProvisioningCommandAction.CheckConfiguration or ProvisioningCommandAction.InspectSshKey or ProvisioningCommandAction.VerifyRepositoryAccess;
        var type = NodeGitHubSetup.Handles(request) || action is ProvisioningCommandAction.Login or ProvisioningCommandAction.Logout or ProvisioningCommandAction.CheckAuthentication ? "authentication" : "tool";
        var key = $"{type}:{request.CapabilityId}:{action}".ToLowerInvariant();
        var privileged = action is ProvisioningCommandAction.Install or ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall;
        return !policy.DeniedActions.Contains(key, StringComparer.OrdinalIgnoreCase) &&
            (readOnly || policy.Enabled && (type != "authentication" || policy.AllowCredentials) &&
                (privileged ? request.AllowElevation && policy.AllowedPrivilegedActions.Contains(key, StringComparer.OrdinalIgnoreCase) : policy.AllowNonPrivileged));
    }

    public static async Task<ProvisioningCommandReport> ExecuteLocalAsync(ProvisioningCommandRequest request,
        ProvisioningPolicy policy, NodeCapabilityDiscovery discovery, CancellationToken token,
        Func<ProvisioningCommandReport, CancellationToken, Task>? reportProgress = null)
    {
        if (!ProvisioningCommandProtocol.Valid(request)) throw new InvalidDataException("Invalid provisioning request.");
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), request, now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddSeconds(request.TimeoutSeconds));
        return await new NodeProvisioningCommandExecutor(discovery).ExecuteAsync(command, Permitted(request, policy), token, reportProgress);
    }
}
