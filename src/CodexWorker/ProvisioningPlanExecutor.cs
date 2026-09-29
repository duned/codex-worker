namespace CodexWorker;

/// <summary>Executes structured provisioning actions through explicit Worker-side installers.</summary>
public sealed class ProvisioningPlanExecutor
{
    private readonly WorkerCapabilityDiscovery _discovery;
    private readonly IReadOnlyList<IDependencyInstaller> _installers;

    public ProvisioningPlanExecutor(WorkerCapabilityDiscovery discovery, IEnumerable<IDependencyInstaller>? installers = null)
    {
        _discovery = discovery;
        _installers = (installers ?? [new DebianAptDependencyInstaller()]).ToArray();
    }

    public async Task<ProvisioningWorkerReportContract> ExecuteAsync(ProvisioningPlanContract plan, string workerId,
        Func<ProvisioningWorkerReportContract, CancellationToken, Task> report, CancellationToken cancellationToken)
    {
        var installedCapabilities = new List<string>();
        if (plan.Actions.Count == 0)
        {
            const string noOp = "No provisioning actions were required.";
            await report(new ProvisioningWorkerReportContract(workerId, "Completed", Result: noOp), cancellationToken);
            return new ProvisioningWorkerReportContract(workerId, "Completed", Result: noOp);
        }

        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await report(new ProvisioningWorkerReportContract(workerId, "Running", action.Id), cancellationToken);
            if (action.Type != "refresh-capabilities" && action.Operation is not ("ensure" or "install"))
            {
                var unsupported = new ProvisioningWorkerReportContract(workerId, "Failed", action.Id,
                    Failure: $"Action '{action.Id}' could not be completed: no local executor is registered for operation '{action.Operation}'.");
                await report(unsupported, cancellationToken);
                return unsupported;
            }
            if (action.Type == "refresh-capabilities")
            {
                await _discovery.RefreshAsync(cancellationToken);
                continue;
            }

            var requirement = new ProvisioningRequirement(action.Type, action.Name, action.Version);
            var capabilities = await _discovery.GetCachedAsync(cancellationToken);
            if (capabilities.Any(capability => CapabilityVersionMatcher.Satisfies(capability, requirement))) continue;

            var installer = _installers.FirstOrDefault(candidate => candidate.Supports(requirement));
            if (installer is null)
                return await FailedAsync(workerId, action, "no supported installer is registered for this requirement", report, cancellationToken);

            var installPlan = installer.Plan(requirement, capabilities);
            if (!installPlan.Supported)
                return await FailedAsync(workerId, action, installPlan.Reason ?? "the requirement is unsupported", report, cancellationToken);
            if (installPlan.AlreadySatisfied) continue;

            var installed = await installer.InstallAsync(installPlan, cancellationToken);
            if (!installed.Succeeded)
                return await FailedAsync(workerId, action, installed.Message ?? "installation failed", report, cancellationToken);

            var verified = await _discovery.RefreshAsync(cancellationToken);
            var detected = verified.FirstOrDefault(capability => CapabilityVersionMatcher.Satisfies(capability, requirement));
            if (detected is null)
                return await FailedAsync(workerId, action, "installation completed but the requested capability/version was not detected afterward", report, cancellationToken);
            installed = installed with { DetectedVersion = detected.Version, DetectedCapability = detected };
            var detectedCapability = installed.DetectedCapability ?? detected;
            installedCapabilities.Add($"{detectedCapability.Type} {detectedCapability.Name}{(installed.DetectedVersion is null ? "" : " " + installed.DetectedVersion)}");
        }

        var completed = installedCapabilities.Count == 0
            ? "All requested provisioning actions are satisfied."
            : $"All requested provisioning actions are satisfied. Installed and verified: {string.Join(", ", installedCapabilities)}.";
        var result = new ProvisioningWorkerReportContract(workerId, "Completed", Result: completed);
        await report(result, cancellationToken);
        return result;
    }

    private static async Task<ProvisioningWorkerReportContract> FailedAsync(string workerId, ProvisioningActionContract action,
        string reason, Func<ProvisioningWorkerReportContract, CancellationToken, Task> report, CancellationToken cancellationToken)
    {
        var failure = $"Action '{action.Id}' could not be completed: {reason}.";
        var failed = new ProvisioningWorkerReportContract(workerId, "Failed", action.Id, Failure: failure);
        await report(failed, cancellationToken);
        return failed;
    }
}
