namespace CodexServer;

/// <summary>Planning order for MCP clients: blocked-by links are set before an Issue becomes ready.
/// The Server may assign a ready Issue within seconds, so a blocker added afterwards can arrive
/// after the Issue is already running and no longer prevents it.</summary>
internal static class IssueDependencyOrdering
{
    internal const string ReadyFirstReason = "issue-already-ready-remove-ready-set-dependencies-then-mark-ready";
    internal const string ActiveExecutionReason = "issue-execution-active-dependency-would-not-apply";

    /// <summary>Returns a refusal reason when adding new blockers to the Issue would no longer be honoured
    /// in order, or null when they can be added. Blockers that already exist and removals are never refused.</summary>
    internal static async Task<string?> AddBlockersRefusalAsync(IRegistryStore registry,
        IServerGitHubAdministrationService github, CentralProject project, int issueNumber,
        IReadOnlyCollection<int> blockers, CancellationToken cancellationToken)
    {
        ManagedGitHubIssue? issue;
        using (github.BeginReadOperation(refresh: true))
            issue = await github.GetIssueAsync(project.Id, issueNumber, cancellationToken);
        if (issue is null || blockers.All(blocker => issue.BlockedBy.Any(existing => existing.Number == blocker))) return null;
        var executions = await registry.ListExecutionsAsync(new ExecutionQuery(project.Id, WorkType: "github-issue",
            WorkId: issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), Limit: 100), cancellationToken);
        if (executions.Any(execution => execution.State is "Queued" or "Assigned" or "Running"))
            return ActiveExecutionReason;
        return project.IssueReadyLabel is { Length: > 0 } ready && issue.Labels.Contains(ready, StringComparer.OrdinalIgnoreCase)
            ? ReadyFirstReason : null;
    }
}
