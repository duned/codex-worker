namespace CodexServer;

using System.ComponentModel;
using CodexProvisioning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

/// <summary>Managed maintenance projections; private node data never crosses this boundary.</summary>
[McpServerToolType]
public sealed class ServerWorkerTools(IRegistryStore registry, ProvisioningCommandStore commands)
{
    [McpServerTool(Name = "get_worker_maintenance", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect registered Worker version, restart/update lifecycle and typed provisioning history. Reloads Server state only; does not restart the Worker. Local provisioning policy remains authoritative.")]
    public async Task<object> Inspect(string workerId, CancellationToken cancellationToken)
    {
        Identity(workerId);
        var worker = await registry.GetWorkerAsync(workerId, cancellationToken);
        if (worker is null) return Rejected("worker-not-found");
        var history = await commands.ListNodeAsync(workerId, cancellationToken);
        return new
        {
            workerId, version = WorkerUpdateProtocol.ValidVersion(worker.WorkerVersion) ? worker.WorkerVersion : null,
            update = DescribeUpdate(await registry.GetWorkerUpdateAsync(workerId, cancellationToken)),
            supportedActions = CapabilityCatalog.Definitions.Select(definition => new
            {
                capabilityId = definition.Id,
                installation = worker.CapabilityInventory?.FirstOrDefault(state => state.Id == definition.Id)?.Installation.ToString(),
                authentication = worker.CapabilityInventory?.FirstOrDefault(state => state.Id == definition.Id)?.Authentication.ToString(),
                actions = Enum.GetValues<ProvisioningCommandAction>().Where(action =>
                    ProvisioningCommandProtocol.Supported(new(workerId, definition.Id, action))).Select(action => action.ToString())
            }),
            operations = history.Select(Describe)
        };
    }

    [McpServerTool(Name = "request_worker_maintenance", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Request restart or update through the managed Worker lifecycle. Supply a stable 32-character operationId for idempotency; poll get_worker_maintenance. No arbitrary version, executable or command is accepted.")]
    public async Task<object> Maintain(string workerId, string operationId, string action, CancellationToken cancellationToken)
    {
        Identity(workerId);
        Identity(operationId);
        if (action is not ("restart" or "update")) return Rejected("unsupported-action");
        try
        {
            var operation = await registry.RequestWorkerUpdateAsync(workerId, operationId, cancellationToken, action == "restart");
            return operation.Restart != (action == "restart") ? Rejected("operation-scope-conflict") :
                DescribeUpdate(operation) ?? Rejected("worker-not-found");
        }
        catch (InvalidOperationException) { return Rejected("worker-maintenance-unavailable-or-conflicting"); }
    }

    [McpServerTool(Name = "request_worker_provisioning", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Queue an existing typed provisioning action for a connected managed Worker. Returns operationId; poll get_worker_provisioning_operation. Timeout is 5–600 seconds. Node-local authorization may deny the operation. Active capability operations reject duplicates; requests are never automatically replayed.")]
    public async Task<object> Provision(string workerId, string capabilityId, string action, CancellationToken cancellationToken,
        int timeoutSeconds = 120, bool allowElevation = false, string? repository = null)
    {
        Identity(workerId);
        if (!Enum.GetNames<ProvisioningCommandAction>().Contains(action, StringComparer.Ordinal) ||
            !Enum.TryParse<ProvisioningCommandAction>(action, out var parsed)) return Rejected("unsupported-action");
        var request = new ProvisioningCommandRequest(workerId, capabilityId, parsed, timeoutSeconds, allowElevation, repository);
        if (!ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request))
            return Rejected("unsupported-or-invalid-provisioning-action");
        var worker = await registry.GetWorkerAsync(workerId, cancellationToken);
        if (worker is null) return Rejected("worker-not-found");
        if (worker.Availability is not ("online" or "draining")) return Rejected("worker-offline");
        try { return Describe(await commands.CreateAsync(request, cancellationToken)); }
        catch (InvalidOperationException) { return Rejected("active-capability-operation"); }
    }

    [McpServerTool(Name = "get_worker_provisioning_operation", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect a managed Worker provisioning operation, including pending/running/succeeded/failed/cancelled/timedout status. Omits keys, login instructions, paths and raw diagnostics.")]
    public async Task<object> Operation(string workerId, string operationId, CancellationToken cancellationToken)
    {
        Identity(workerId);
        Identity(operationId);
        var operation = await commands.GetAsync(operationId, cancellationToken);
        return operation is null || operation.Request.NodeId != workerId ? Rejected("operation-not-found") : Describe(operation);
    }

    [McpServerTool(Name = "cancel_worker_provisioning", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Cancel a pending provisioning operation. Running or uncertain operations are rejected and require existing node inspection/reconciliation; cancellation never replays a command.")]
    public async Task<object> Cancel(string workerId, string operationId, CancellationToken cancellationToken)
    {
        Identity(workerId);
        Identity(operationId);
        var operation = await commands.GetAsync(operationId, cancellationToken);
        if (operation is null || operation.Request.NodeId != workerId) return Rejected("operation-not-found");
        try
        {
            var cancelled = await commands.CancelAsync(operationId, cancellationToken);
            return cancelled is null ? Rejected("operation-not-found") : Describe(cancelled);
        }
        catch (InvalidOperationException) { return Rejected("operation-requires-reconciliation"); }
    }

    private static object Describe(ProvisioningCommand command) => new
    {
        operationId = command.Id, workerId = command.Request.NodeId, command.Request.CapabilityId,
        action = command.Request.Action.ToString(), status = command.Status.ToString().ToLowerInvariant(),
        diagnostic = command.Diagnostic.ToString(), command.CreatedAtUtc, command.StartedAtUtc,
        command.DeadlineUtc, command.CompletedAtUtc, failureCode = command.FailureDetail?.Code.ToString()
    };

    private static object? DescribeUpdate(WorkerUpdateOperation? operation) => operation is null ? null : new
    {
        operation.OperationId, operation.WorkerId, action = operation.Restart ? "restart" : "update",
        status = operation.State is "succeeded" or "failed" ? operation.State : "pending",
        stage = operation.State, operation.RequestedAtUtc, operation.DeadlineUtc
    };

    private static object Rejected(string reason) => new { outcome = "rejected", reason };
    private static void Identity(string value)
    {
        if (!Guid.TryParseExact(value, "N", out _)) throw new McpException("Identity must be a 32-character GUID.");
    }
}
