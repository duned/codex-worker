namespace CodexWorker;

public sealed record ExecutionInventoryQuery(string? Project = null, Guid? ExecutionId = null, int? IssueNumber = null,
    string? Outcome = null, int? OlderThanDays = null, string? Attention = null, string? Origin = null,
    int Limit = 50, int Offset = 0);

public sealed record ExecutionMaintenanceAssessment(string Status, string ReasonCode, string Explanation,
    DateTimeOffset LastProgressAtUtc, string Authority, IReadOnlyList<string> Actions);

public sealed record ExecutionInventoryItem(ExecutionRuntimeInfo Execution, ExecutionMaintenanceAssessment Maintenance);
public sealed record ExecutionInventoryPage(IReadOnlyList<ExecutionInventoryItem> Items, int Limit, int Offset,
    bool HasMore, int Scanned);

/// <summary>Derived operator guidance. It never grants cleanup authority; cleanup always requires fresh Git proof.</summary>
public static class ExecutionMaintenanceClassifier
{
    private static readonly HashSet<string> Terminal = new(StringComparer.Ordinal)
        { "Completed", "Blocked", "Failed", "IntegrationConflict", "InfrastructureFailure", "Cancelled", "Superseded" };

    public static ExecutionMaintenanceAssessment Classify(ExecutionHistoryEntry entry, DateTimeOffset now,
        bool projectConfigured, int staleAfterDays = 7)
    {
        var authority = entry.ServerExecutionId is not null || entry.AssignmentId is not null ? "managed" : "local";
        var progress = entry.CompletedAtUtc ?? entry.StartedAtUtc;
        var actions = new List<string> { "inspect" };
        var stale = now - progress >= TimeSpan.FromDays(staleAfterDays);
        if (!projectConfigured)
            return Assessment("orphaned", "project-not-configured", "No current local project matches this history entry; preserve resources until ownership is established.");
        if (entry.ReportingFailure is not null && authority == "managed")
            return Assessment("reconciliation-required", "managed-report-unconfirmed", "The Worker could not confirm the Server report; Server authority must be reconciled.");
        if (entry.IntegrationRecoveryClaim is not null || entry.RecoveryState is "uncertain" or "missing" or "integration-conflict" or "codex-interrupted" or "codex-resuming" or "codex-recovery-exhausted" or "codex-recovery-inspection-required")
            return Assessment("retained-review", "recovery-ambiguous", "Recovery ownership or integration outcome is uncertain; retain resources for inspection.");
        if (!Terminal.Contains(entry.State) || entry.CompletedAtUtc is null)
            return stale
                ? Assessment("stale", "execution-no-progress", "Execution has no confirmed terminal state and is older than the attention threshold.")
                : Assessment("healthy-active", "execution-active", "Execution is active according to local history.");
        if (stale && entry.RecoveryState is not ("operator-cleaned" or "expired-cleaned" or "resumed-cleaned" or "discarded" or "cleaned-no-changes"))
            return Assessment("recoverable", "retained-resources-aged", "Terminal execution has retained resources; age identifies attention but does not prove cleanup safety.");
        if (entry.RecoveryState is "operator-cleaned" or "expired-cleaned" or "resumed-cleaned" or "discarded" or "cleaned-no-changes")
            return Assessment("healthy-terminal", "cleanup-recorded", "Worker history records a prior cleanup; current resource state still requires inspection.");
        return Assessment("healthy-terminal", "terminal-outcome", "Execution has a terminal business outcome and no recorded maintenance ambiguity.");

        ExecutionMaintenanceAssessment Assessment(string status, string reason, string explanation) =>
            new(status, reason, explanation, progress, authority, actions);
    }

    public static string Outcome(string state) => state switch
    {
        "Completed" => "succeeded", "Blocked" => "blocked", "Failed" => "failed",
        "InfrastructureFailure" => "infrastructure-failure", "Cancelled" => "cancelled",
        "IntegrationConflict" => "integration-conflict", _ => "active"
    };
}
