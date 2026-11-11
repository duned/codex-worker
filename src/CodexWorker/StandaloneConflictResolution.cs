namespace CodexWorker;

/// <summary>Shared preserved-conflict inspection. Local authorization remains separate from Server assignments.</summary>
internal static class StandaloneConflictResolution
{
    internal static string? Refusal(ExecutionHistoryEntry source, IReadOnlyList<ExecutionHistoryEntry> entries, bool managed = false)
    {
        if (!managed && (source.ServerExecutionId is not null || source.AssignmentId is not null || source.OwnershipGeneration is not null))
            return "Managed execution requires Server reconciliation.";
        if (managed && (source.ServerExecutionId is null || source.AssignmentId is null || source.OwnershipGeneration is not > 0))
            return "Original managed assignment and lease identity are required.";
        if (source.CompletedAtUtc is null || source.State != "IntegrationConflict" || source.RecoveryState != "integration-conflict" ||
            source.RecoveryBaseCommit is null || source.OriginalIssueBody is null || source.CompletionJson is not null ||
            source.ReportingFailure is not null || source.IntegrationRecoveryClaim is not null || source.AttemptNumber < 1)
            return "Exact completed conflict, original Issue intent and unclaimed preserved-workspace metadata are required.";
        bool IsSettledRecovery(ExecutionHistoryEntry other) => other.Project == source.Project &&
            other.RetryOfExecutionId == source.ExecutionId && other.FeatureBranch == source.FeatureBranch &&
            other.IssueNumber == source.IssueNumber && other.Repository == source.Repository &&
            other.State == "IntegrationConflict" &&
            other.RecoveryState is ("integration-conflict" or "conflict-resolution-incomplete") &&
            other.IssueTitle == source.IssueTitle && other.OriginalIssueBody == source.OriginalIssueBody &&
            other.BaseBranch == source.BaseBranch && other.Engine == source.Engine &&
            other.CompletedAtUtc is not null && other.ReportingFailure is null &&
            other.IntegrationRecoveryClaim is null &&
            (managed ? other.ServerExecutionId is not null && other.AssignmentId is not null && other.OwnershipGeneration > 0
                : other.ServerExecutionId is null && other.AssignmentId is null && other.OwnershipGeneration is null);
        if (entries.Any(other => other.ExecutionId != source.ExecutionId && other.Repository == source.Repository &&
            (other.CompletedAtUtc is null || other.IssueNumber == source.IssueNumber &&
                (other.State == "Completed" || other.IntegrationRecoveryClaim is not null ||
                 other.AttemptNumber >= source.AttemptNumber && !IsSettledRecovery(other)))))
            return "Competing, later or ambiguous execution exists; reconcile it first.";
        return null;
    }

    internal static async Task VerifyIntentAsync(WorkerConfiguration config, IGitHubClient github,
        ExecutionHistoryEntry source, CancellationToken ct)
    {
        var state = await github.ReadIssueStateAsync(source.IssueNumber, ct);
        var issue = await github.GetIssueAsync(source.IssueNumber, ct);
        if (!state.IsOpen || issue is null || issue.Number != source.IssueNumber || issue.Title != source.IssueTitle ||
            source.OriginalIssueBody is null || issue.Body != source.OriginalIssueBody ||
            await github.GetParentIssueNumberAsync(source.IssueNumber, ct) != source.ParentIssueNumber ||
            state.Labels.Contains(config.GitHub.DoneLabel, StringComparer.OrdinalIgnoreCase) ||
            state.Labels.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) ||
            state.Labels.Contains(config.GitHub.BlockedLabel, StringComparer.OrdinalIgnoreCase) ||
            state.Labels.Contains(config.GitHub.FailedLabel, StringComparer.OrdinalIgnoreCase) ||
            !new[] { config.GitHub.IntegrationConflictLabel, config.GitHub.IntegrationRecoveryLabel, config.GitHub.WorkingLabel }
                .Any(label => state.Labels.Contains(label, StringComparer.OrdinalIgnoreCase)))
            throw new WorkerInfrastructureException("Issue intent/state changed or recovery labels are absent; preserve and inspect the execution.");
    }
}

public sealed partial class Worker
{
    private Task VerifyConflictIntentAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
        StandaloneConflictResolution.VerifyIntentAsync(config, github, source, ct);

    private async Task VerifyConflictResolutionAsync(ExecutionHistoryEntry source,
        IReadOnlyList<ExecutionHistoryEntry> entries, CancellationToken ct, bool managed = false)
    {
        if (!managed && serverSettings?.Enabled == true) throw new InvalidOperationException("Standalone conflict resolution only.");
        if (StandaloneConflictResolution.Refusal(source, entries, managed) is { } reason) throw new InvalidOperationException(reason);
        if (!(engines ?? new ImplementationEngineSelector(settings: config.Codex)).IsAvailable(source.Engine))
            throw new InvalidOperationException("Persisted implementation engine is unavailable.");
        if (!config.Git.AutoMerge) throw new InvalidOperationException("Conflict resolution requires the configured automatic integration lifecycle (git.autoMerge).");
        if (config.Validation.MaxFixAttempts < 1) throw new InvalidOperationException("Conflict resolution requires a positive validation repair budget.");
        if (source.CodexRecovery is { } workspace && workspace.WorkspaceExecutionId != source.ExecutionId)
            throw new InvalidOperationException("Select the original workspace-owning execution; recovery-attempt aliases are refused.");
        await VerifyConflictIntentAsync(source, ct);
        await _repositoryGate.WaitAsync(ct);
        try
        {
            if (await IntegrationRecovery.ValidateSourceAsync(source, ct) is { } invalid) throw new InvalidOperationException(invalid);
            if (await git.InspectExecutionProcessesAsync(source, ct) is { } blocker) throw new InvalidOperationException(blocker);
            if (await git.GetIntegrationBaseAsync(ct) is null) throw new InvalidOperationException("Authoritative integration base is unavailable.");
        }
        finally { _repositoryGate.Release(); }
    }

    /// <summary>Called only by the offline CLI while holding the exclusive checkout lock.</summary>
    internal async Task<IssueProcessingResult?> ResolveStandaloneConflictAsync(Guid executionId, bool confirm, CancellationToken ct)
    {
        if (history is null) throw new InvalidOperationException("Durable execution history is required.");
        var entries = await history.ReadAllAsync(ct);
        var source = entries.SingleOrDefault(entry => entry.ExecutionId == executionId)
            ?? throw new InvalidOperationException("Exact execution ID was not found.");
        await VerifyConflictResolutionAsync(source, entries, ct);
        if (!confirm) return null;
        var issue = await github.GetIssueAsync(source.IssueNumber, ct)
            ?? throw new InvalidOperationException("Issue is unavailable.");
        var task = await ClaimIntegrationRecoveryAsync(issue, ct, resolveExecutionId: executionId)
            ?? throw new InvalidOperationException("Exact execution could not be claimed; inspect its durable state.");
        return await task ?? throw new InvalidOperationException("Recovery did not complete; inspect its retained attempt and workspace.");
    }
}
