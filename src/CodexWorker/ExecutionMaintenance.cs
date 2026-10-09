namespace CodexWorker;

public sealed record ExecutionInventoryQuery(string? Project = null, Guid? ExecutionId = null, int? IssueNumber = null,
    string? Outcome = null, int? OlderThanDays = null, string? Attention = null, string? Origin = null,
    int Limit = 50, int Offset = 0, bool IncludeArchived = false);

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
        var authority = entry.ServerExecutionId is not null || entry.AssignmentId is not null || entry.OwnershipGeneration is not null ? "managed" : "local";
        var progress = entry.CompletedAtUtc ?? entry.StartedAtUtc;
        var actions = new List<string> { "inspect" };
        var stale = now - progress >= TimeSpan.FromDays(staleAfterDays);
        if (!projectConfigured)
            return Assessment("orphaned", "project-not-configured", "No current local project matches this history entry; preserve resources until ownership is established.");
        if (entry.ReportingFailure is not null || entry.RecoveryState == "github-reconciliation-required")
            return authority == "managed" && entry.ReportingFailure is not null
                ? Assessment("reconciliation-required", "managed-report-unconfirmed", "The Server report is unconfirmed; reconcile Server authority and reporting.")
                : Assessment("reconciliation-required", "github-report-unconfirmed", "GitHub mutation or reporting is unconfirmed; verify remote state and delivery.");
        if (entry.CompletionJson is not null)
            return Assessment("reconciliation-required", "completion-pending", "A durable completion remains pending; reconcile completion and reporting before cleanup.");
        if (entry.IntegrationRecoveryClaim is not null)
            return Assessment("retained-review", "recovery-claimed", "An integration recovery claim remains; verify the owning attempt and integration result.");

        // Receipts describe resource maintenance, never the business result. Unknown states fail closed.
        var recovery = entry.RecoveryState;
        if (recovery == "recoverable")
            return Assessment("recoverable", "workspace-recoverable", "Useful workspace changes are retained; inspect recovery ownership and provenance before resume or cleanup.");
        if (recovery == "cleanup-pending")
            return Assessment("recoverable", "cleanup-pending", "Cleanup has no completion receipt; verify resources through protected cleanup inspection.");
        if (recovery is "codex-recovered" or "codex-recovery-finished" or "integration-recovered" or "superseded")
            return Assessment("retained-review", "recovery-transfer-unverified", "A later attempt consumed recovery; this receipt alone does not prove source resources are clean. Inspect attempt lineage and resources.");
        if (recovery == "github-reconciled")
            return Assessment("retained-review", "github-resource-proof-missing", "Issue eligibility was reconciled; completion delivery and local resource safety are not established by that receipt.");
        if (recovery is not (null or "operator-cleaned" or "expired-cleaned" or "resumed-cleaned" or "discarded" or "cleaned-no-changes" or "completion-reconciled"))
            return Assessment("retained-review", recovery switch
            {
                "preparation-failed" => "preparation-resource-proof-missing",
                "missing" => "workspace-missing",
                "managed-completion-quarantined" => "managed-completion-quarantined",
                "uncertain" or "integration-conflict" or "integration-conflict-unavailable" or
                    "integration-recovery-interrupted" => "recovery-ambiguous",
                "codex-interrupted" or "codex-resuming" or "codex-recovery-exhausted" or
                    "codex-recovery-inspection-required" => "codex-recovery-pending",
                _ => "recovery-state-unknown"
            }, "Recovery lacks verified ownership, integration or cleanup proof; retain resources for inspection.");
        if (!Terminal.Contains(entry.State) || entry.CompletedAtUtc is null)
            return stale
                ? Assessment("stale", "execution-no-progress", "Execution has no confirmed terminal state and is older than the attention threshold.")
                : Assessment("healthy-active", "execution-active", "Execution is active according to local history.");
        if (recovery is not null)
            return Assessment(entry.State == "Completed" ? "healthy-terminal" : "terminal-clean", "cleanup-recorded", "History records resolved resource maintenance; the original business outcome is unchanged. Fresh resource proof is required for maintenance.");
        if (entry.State != "Completed" || stale)
            return Assessment("retained-review", "terminal-resource-proof-missing", "Terminal history has no resource maintenance receipt; inspect resource state rather than infer safety from outcome or age.");
        return Assessment("healthy-terminal", "terminal-outcome", "Recent successful completion has no recorded recovery or reporting ambiguity.");

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
