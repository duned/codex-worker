namespace CodexWorker;

/// <summary>Executes the safe, locally supported subset of a structured Server provisioning plan.</summary>
public sealed class ProvisioningPlanExecutor(WorkerCapabilityDiscovery discovery)
{
    public async Task<ProvisioningWorkerReportContract> ExecuteAsync(ProvisioningPlanContract plan, string workerId,
        Func<ProvisioningWorkerReportContract, CancellationToken, Task> report, CancellationToken cancellationToken)
    {
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
            var capabilities = action.Type == "refresh-capabilities"
                ? await discovery.RefreshAsync(cancellationToken)
                : await discovery.GetCachedAsync(cancellationToken);
            var satisfied = action.Type == "refresh-capabilities" || capabilities.Any(capability =>
                string.Equals(capability.Type, action.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(capability.Name, action.Name, StringComparison.OrdinalIgnoreCase) &&
                (action.Version is null || string.Equals(capability.Version, action.Version, StringComparison.OrdinalIgnoreCase)));
            if (!satisfied)
            {
                var failure = $"Action '{action.Id}' could not be completed: no local provisioner is registered for {action.Type} '{action.Name}'.";
                var failed = new ProvisioningWorkerReportContract(workerId, "Failed", action.Id, Failure: failure);
                await report(failed, cancellationToken);
                return failed;
            }
        }

        const string completed = "All requested provisioning actions are satisfied.";
        var result = new ProvisioningWorkerReportContract(workerId, "Completed", Result: completed);
        await report(result, cancellationToken);
        return result;
    }
}
