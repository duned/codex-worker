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
        var ready = connected && !stale && worker.LifecycleState == "running" && CapabilityCatalog.Ready(capabilities.Select(item => item.State));
        return new(worker.WorkerId, "worker", worker.DisplayName, connected ? "connected" : "disconnected",
            ready ? "ready" : "not-ready", !connected ? "unavailable" : operationRunning ? "busy" : "ready",
            stale, capabilities, !connected ? "unknown" : worker.Availability == "draining" ? "degraded" : "healthy");
    }

    public static ProvisionableNode WithCommands(ProvisionableNode node, IReadOnlyList<ProvisioningCommand> commands)
    {
        var capabilities = node.Capabilities.Select(capability =>
        {
            var latest = commands.LastOrDefault(command => command.Request.NodeId == node.Id &&
                command.Request.CapabilityId == capability.Definition.Id);
            if (latest is null) return capability;
            var busy = !ProvisioningCommandProtocol.Terminal(latest.Status);
            var failed = latest.Status is ProvisioningCommandStatus.Failed or ProvisioningCommandStatus.TimedOut;
            if (!busy && !failed) return capability;
            return capability with
            {
                State = capability.State with { Operation = new(busy ? CapabilityOperationState.Running : CapabilityOperationState.Failed,
                    latest.Request.Action.ToString().ToLowerInvariant(), failed ? "operation-failed" : null) },
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
