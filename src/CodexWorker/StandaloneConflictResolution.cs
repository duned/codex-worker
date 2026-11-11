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
            source.RecoveryBaseCommit is null || source.CompletionJson is not null ||
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

    internal static async Task<GitHubIssue> VerifyIntentAsync(WorkerConfiguration config, IGitHubClient github,
        ExecutionHistoryEntry source, CancellationToken ct)
    {
        if (source.OriginalIssueBody is null)
            throw ConflictIssueIntent.Refuse("issue-original-metadata-missing");
        GitHubIssueState state;
        GitHubIssue? issue;
        int? parent;
        try
        {
            state = await github.ReadIssueStateAsync(source.IssueNumber, ct);
            if (!state.IsOpen) throw ConflictIssueIntent.Refuse("issue-closed");
            issue = await github.GetIssueAsync(source.IssueNumber, ct);
            if (issue is null || issue.Number != source.IssueNumber)
                throw ConflictIssueIntent.Refuse("issue-missing");
            parent = await github.GetParentIssueNumberAsync(source.IssueNumber, ct);
        }
        catch (ConflictIntentException) { throw; }
        catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested)
        {
            // Never forward provider output (which can contain Issue content or credentials).
            throw ConflictIssueIntent.Refuse("issue-query-unavailable");
        }
        if (issue.Title != source.IssueTitle) throw ConflictIssueIntent.Refuse("issue-title-changed");
        if (source.OriginalIssueBodyFingerprint is { } fingerprint)
        {
            if (fingerprint.Length != 64 || fingerprint.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
                throw ConflictIssueIntent.Refuse("issue-original-metadata-missing");
            if (ConflictIssueIntent.BodyFingerprint(issue.Body) != fingerprint)
                throw ConflictIssueIntent.Refuse("issue-body-changed");
        }
        else
        {
            // Historical redaction is lossy: never approve by redacting the fresh body.
            if (source.OriginalIssueBody.Contains("[redacted]", StringComparison.OrdinalIgnoreCase))
                throw ConflictIssueIntent.Refuse("issue-original-metadata-missing");
            if (ConflictIssueIntent.NormalizeBody(issue.Body) != ConflictIssueIntent.NormalizeBody(source.OriginalIssueBody))
                throw ConflictIssueIntent.Refuse("issue-body-changed");
        }
        string? provenance = null;
        if (!source.ParentIssueCaptured && source.ParentIssueNumber is null && source.ServerExecutionId is not null)
        {
            provenance = await github.GetParentProvenanceAsync(source.IssueNumber, source.StartedAtUtc, parent, ct)
                ?? throw ConflictIssueIntent.Refuse("issue-parent-history-unavailable");
            var proof = CodexProvisioning.IssueParentProvenance.Parse(provenance);
            if (proof is null || !string.Equals(proof.Repository, source.Repository, StringComparison.OrdinalIgnoreCase) ||
                proof.IssueNumber != source.IssueNumber || proof.ExecutionStartedAtUtc != source.StartedAtUtc ||
                proof.ParentNumber != parent || !proof.IsConsistent(source.StartedAtUtc))
                throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
        }
        else if (parent != source.ParentIssueNumber)
            throw ConflictIssueIntent.Refuse(source.ParentIssueNumber is not null ? "issue-parent-changed" :
                source.ParentIssueCaptured ? "issue-original-parent-absent" : "issue-parent-metadata-missing");
        bool Has(string label) => state.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
        if (new[] { config.GitHub.DoneLabel, config.GitHub.ReadyLabel, config.GitHub.BlockedLabel, config.GitHub.FailedLabel }.Any(Has))
            throw ConflictIssueIntent.Refuse("issue-conflicting-lifecycle-label");
        if (!new[] { config.GitHub.IntegrationConflictLabel, config.GitHub.IntegrationRecoveryLabel, config.GitHub.WorkingLabel }.Any(Has))
            throw ConflictIssueIntent.Refuse("issue-recovery-label-missing");
        return issue with { ParentIssueNumber = parent, ParentIssueCaptured = true, ParentProvenance = provenance };
    }
}

public sealed partial class Worker
{
    private async Task VerifyConflictIntentAsync(ExecutionHistoryEntry source, CancellationToken ct, string? expectedProvenance = null)
    {
        var issue = await StandaloneConflictResolution.VerifyIntentAsync(config, github, source, ct);
        if (issue.ParentProvenance != expectedProvenance)
            throw ConflictIssueIntent.Refuse("issue-parent-proof-changed");
    }

    private async Task<GitHubIssue> VerifyConflictResolutionAsync(ExecutionHistoryEntry source,
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
        var issue = await StandaloneConflictResolution.VerifyIntentAsync(config, github, source, ct);
        await _repositoryGate.WaitAsync(ct);
        try
        {
            if (await IntegrationRecovery.ValidateSourceAsync(source, ct) is { } invalid) throw new InvalidOperationException(invalid);
            if (await git.InspectExecutionProcessesAsync(source, ct) is { } blocker) throw new InvalidOperationException(blocker);
            if (await git.GetIntegrationBaseAsync(ct) is null) throw new InvalidOperationException("Authoritative integration base is unavailable.");
        }
        finally { _repositoryGate.Release(); }
        return issue;
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
