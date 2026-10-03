namespace CodexServer;

using CodexProvisioning;

public static class NodeProvisioning
{
    public static ProvisionableNode Describe(WorkerRegistrationResponse worker, IReadOnlyList<ProvisioningPlan> plans)
    {
        var connected = worker.Availability != "stale";
        var capabilities = CapabilityCatalog.Definitions.Select(definition =>
        {
            var observation = worker.CapabilityInventory?.FirstOrDefault(state => state.Id == definition.Id)
                ?? CapabilityCatalog.Unknown(definition);
            var latest = plans.Where(plan => plan.WorkerId == worker.WorkerId &&
                plan.Actions.Any(action => Matches(action, definition.Id)))
                .OrderByDescending(plan => plan.CreatedAtUtc).FirstOrDefault();
            if (latest is not null)
            {
                var action = latest.Actions.FirstOrDefault(action => action.Id == latest.CurrentActionId && Matches(action, definition.Id));
                if (latest.State is "Accepted" or "Running" && (action is not null || latest.CurrentActionId is null))
                    observation = observation with { Operation = new(CapabilityOperationState.Running, action?.Operation) };
                else if (latest.State == "Failed" && action is not null)
                    observation = observation with { Operation = new(CapabilityOperationState.Failed, action.Operation, "operation-failed") };
            }
            return CapabilityCatalog.Describe(definition, observation, connected);
        }).ToArray();
        var operationRunning = capabilities.Any(item => item.State.Operation.State == CapabilityOperationState.Running);
        var stale = !connected || capabilities.Any(item => item.State.DetectedAtUtc is null ||
            DateTimeOffset.UtcNow - item.State.DetectedAtUtc > TimeSpan.FromMinutes(6));
        var executionCapabilities = capabilities.Where(item => item.Definition.RequiredForExecution).ToArray();
        var executionStale = executionCapabilities.Any(item => item.State.DetectedAtUtc is null ||
            DateTimeOffset.UtcNow - item.State.DetectedAtUtc > TimeSpan.FromMinutes(6));
        var ready = connected && !executionStale && worker.LifecycleState == "running" &&
            CapabilityCatalog.Ready(executionCapabilities.Select(item => item.State));
        return new(worker.WorkerId, "worker", worker.DisplayName, connected ? "connected" : "disconnected",
            ready ? "ready" : "not-ready", !connected ? "unavailable" : operationRunning ? "busy" : "ready",
            stale, capabilities, !connected ? "unknown" : worker.Availability == "draining" ? "degraded" : "healthy");
    }

    public static ProvisionableNode WithCommands(ProvisionableNode node, IReadOnlyList<ProvisioningCommand> commands,
        IReadOnlyList<ProvisioningPlan>? plans = null)
    {
        plans ??= [];
        var capabilities = node.Capabilities.Select(capability =>
        {
            var latest = commands.Where(command => command.Request.NodeId == node.Id &&
                    command.Request.CapabilityId == capability.Definition.Id)
                .OrderByDescending(command => command.CreatedAtUtc).FirstOrDefault();
            if (latest is null) return capability;
            var latestPlan = plans.Where(plan => plan.WorkerId == node.Id && plan.Actions.Any(action =>
                    Matches(action, capability.Definition.Id)))
                .OrderByDescending(plan => plan.CreatedAtUtc).FirstOrDefault();
            if (latestPlan is not null && latestPlan.CreatedAtUtc > latest.CreatedAtUtc) return capability;
            var busy = !ProvisioningCommandProtocol.Terminal(latest.Status);
            var failed = latest.Status is ProvisioningCommandStatus.Failed or ProvisioningCommandStatus.TimedOut;
            return capability with
            {
                State = capability.State with { Operation = new(busy ? CapabilityOperationState.Running : failed
                        ? CapabilityOperationState.Failed : CapabilityOperationState.Idle,
                    latest.Request.Action.ToString().ToLowerInvariant(), failed ? latest.Diagnostic.ToString().ToLowerInvariant() : null) },
                AvailableActions = busy ? [] : capability.AvailableActions
            };
        }).ToArray();
        return node with { Capabilities = capabilities,
            ProvisioningReadiness = capabilities.Any(item => item.State.Operation.State == CapabilityOperationState.Running)
                ? "busy" : node.ProvisioningReadiness };
    }

    private static bool Matches(ProvisioningAction action, string id) =>
        action.Type == "refresh-capabilities" || action.Type == "tool" && action.Name == id ||
        action.Type == "authentication" && (action.Name == id || id == "codex-cli" && action.Name == "codex");
}
