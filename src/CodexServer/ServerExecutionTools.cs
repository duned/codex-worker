namespace CodexServer;

using System.ComponentModel;
using CodexProvisioning;
using ModelContextProtocol;
using ModelContextProtocol.Server;

/// <summary>Managed controls use the same eligibility and authority boundaries as administration.</summary>
[McpServerToolType]
public sealed class ServerExecutionTools(IRegistryStore registry, IServerGitHubAdministrationService github,
    ExecutionMaintenanceStore maintenance)
{
    [McpServerTool(Name = "dispatch_execution", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Queue an eligible GitHub Issue through Server scheduling. Returns executionRequestId for get_execution. Duplicate active work is rejected; capacity and leases remain scheduler-owned.")]
    public async Task<object> Dispatch(string projectId, int issueNumber, CancellationToken cancellationToken)
    {
        var request = new EnqueueExecutionRequest(projectId,
            new("github-issue", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (ExecutionRequestValidation.Error(request) is { } error) throw new McpException(error);
        try
        {
            var execution = await github.EnqueueIssueAsync(projectId, request.WorkReference, cancellationToken);
            return new { outcome = "accepted", executionRequestId = execution.Id, state = execution.State };
        }
        catch (GitHubIssueNotFoundException) { return Rejected("issue-not-found"); }
        catch (KeyNotFoundException) { return Rejected("project-not-found"); }
        catch (InvalidDataException) { return Rejected("invalid-issue-reference"); }
        catch (ProjectDisabledException) { return Rejected("project-disabled"); }
        catch (ProjectRevisionConflictException) { return Rejected("project-revision-changed"); }
        catch (ExecutionRequestConflictException) { return Rejected("execution-conflict"); }
        catch (ManagedIssueIneligibleException) { return Rejected("issue-ineligible"); }
        catch (GitHubReadUnavailableException) { return Rejected("github-read-unavailable"); }
    }

    [McpServerTool(Name = "cancel_execution", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Cancel queued managed work. Assigned/running work is rejected because Server has no active cancellation contract; this never revokes a lease or marks running work cancelled.")]
    public async Task<object> Cancel(string executionRequestId, CancellationToken cancellationToken)
    {
        Identity(executionRequestId);
        try
        {
            var execution = await registry.CancelQueuedExecutionAsync(executionRequestId, cancellationToken);
            return execution is null ? Rejected("execution-not-found") :
                new { outcome = "accepted", executionRequestId = execution.Id, state = execution.State };
        }
        catch (ExecutionRequestCancellationException) { return Rejected("execution-not-queued"); }
    }

    [McpServerTool(Name = "retry_execution_report", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Retry only durable managed completion reporting, using existing Worker recovery validation. Does not rerun implementation or integration. Supply a stable 32-character operationId for idempotency; inspect progress with get_execution_operation and get_execution.")]
    public async Task<object> RetryReport(string executionRequestId, string operationId, CancellationToken cancellationToken)
    {
        Identity(executionRequestId);
        Identity(operationId);
        var prior = await maintenance.GetAsync(operationId, cancellationToken);
        if (prior is not null)
            return prior.Request.ServerExecutionId == executionRequestId && prior.Request.Action == "retry-report" && prior.Request.Apply
                ? Describe(prior) : Rejected("operation-scope-conflict");
        var execution = await registry.GetExecutionAsync(executionRequestId, cancellationToken);
        if (execution is null) return Rejected("execution-not-found");
        if (!Guid.TryParse(execution.WorkerExecutionId, out var workerExecutionId) || execution.AssignedWorkerId is null)
            return Rejected("worker-execution-proof-missing");
        var request = new ExecutionMaintenanceRequest(operationId, execution.AssignedWorkerId, execution.Id,
            workerExecutionId, execution.AssignmentId, execution.Lease?.Generation, "retry-report", Apply: true);
        if (!ExecutionMaintenanceProtocol.Valid(request)) return Rejected("invalid-maintenance-scope");
        var reason = await ServerApplication.MaintenanceAuthorityReasonAsync(request, registry, cancellationToken);
        if (reason is not null) return Rejected(reason);
        try { return Describe(await maintenance.CreateAsync(request, "server-management:mcp", cancellationToken)); }
        catch (InvalidOperationException) { return Rejected("maintenance-conflict"); }
    }

    [McpServerTool(Name = "get_execution_operation", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect a managed reporting retry operation by operationId. Returns bounded status/outcome without Worker paths or raw reports.")]
    public async Task<object> Operation(string operationId, CancellationToken cancellationToken)
    {
        Identity(operationId);
        var operation = await maintenance.GetAsync(operationId, cancellationToken);
        return operation is null ? new { found = false } : Describe(operation);
    }

    private static object Describe(ExecutionMaintenanceCommand operation) => new
    {
        operationId = operation.Request.OperationId, executionRequestId = operation.Request.ServerExecutionId,
        status = operation.Status, outcome = operation.Report?.Outcome, operation.CreatedAtUtc, operation.CompletedAtUtc
    };

    private static object Rejected(string reason) => new { outcome = "rejected", reason };

    private static void Identity(string value)
    {
        if (!Guid.TryParseExact(value, "N", out _)) throw new McpException("Identity must be a 32-character GUID.");
    }
}
