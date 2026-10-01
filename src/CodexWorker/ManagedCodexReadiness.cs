namespace CodexWorker;

using CodexProvisioning;

/// <summary>Keep the registered agent provisionable for known missing CLI/authentication states.
/// Unknown/probe errors proceed to the normal authoritative preflight; they are not service retries.</summary>
internal static class ManagedCodexReadiness
{
    internal static async Task WaitAsync(NodeCapabilityDiscovery discovery,
        Func<CancellationToken, Task<bool>> executeCommand, TimeProvider clock, CancellationToken token)
    {
        while (true)
        {
            var codex = (await discovery.GetAsync(cancellationToken: token)).Single(state => state.Id == "codex-cli");
            if (codex.Installation != InstallationState.Missing &&
                !(codex.Installation == InstallationState.Installed && codex.Authentication == RequirementState.Required)) return;
            if (!await executeCommand(token)) await Task.Delay(TimeSpan.FromSeconds(1), clock, token);
        }
    }
}
