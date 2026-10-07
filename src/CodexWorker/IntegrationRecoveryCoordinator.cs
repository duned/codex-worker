using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CodexWorker;

/// <summary>Discovers preserved integration resources and reconciles durable local claims.
/// Scheduling, capacity and execution remain owned by WorkerHost, Worker and ExecutionRunner.</summary>
internal sealed class IntegrationRecoveryCoordinator(WorkerConfiguration config, IGitHubClient github, IGitRepository git,
    TelegramNotifier telegram, WorkerConsole output, ExecutionHistoryStore? history, SemaphoreSlim repositoryGate,
    Action<string> operationalLog, Func<int, bool> isActive)
{
    private readonly ConcurrentDictionary<(int IssueNumber, Guid? SourceId), string> _recoveryDiagnostics = new();
    private int _recoveryReconciled;

    /// <summary>Reconciles durable claims once before this runtime starts scheduling.
    /// Interrupted recovery consumes its base budget; changed or uncertain workspaces require inspection.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (history is null || Interlocked.Exchange(ref _recoveryReconciled, 1) != 0) return;
        var entries = await history.ReadAllAsync(ct);
        foreach (var source in entries.Where(entry => entry.Project == config.Project.Name &&
                     entry.Repository == config.Project.Repository && entry.IntegrationRecoveryClaim is not null))
        {
            var claimId = source.IntegrationRecoveryClaim ?? throw new InvalidOperationException("Recovery claim is missing.");
            var attempt = entries.SingleOrDefault(entry => entry.ExecutionId == claimId);
            // A validated integration handoff owns post-integration completion. Do not mutate
            // its Issue or reclassify its preserved resources as another recovery attempt.
            if (attempt?.CompletionJson is not null) continue;
            if (attempt?.State == "Completed" && source.RecoveryState == "integration-recovered")
            {
                await history.FinishIntegrationRecoveryAsync(source.ExecutionId, claimId, null, ct);
                continue;
            }
            // Managed claims remain fenced by Server lease reconciliation, including uncertain integration.
            if (attempt?.ServerExecutionId is not null)
            {
                await ReportRejectedAsync(source.IssueNumber, source.ExecutionId,
                    "interrupted managed recovery requires Server lease reconciliation before another assignment", ct);
                continue;
            }
            string? reason;
            await repositoryGate.WaitAsync(ct);
            try { reason = await ValidateSourceAsync(source, ct); }
            finally { repositoryGate.Release(); }
            if (reason is not null || attempt is null || attempt.RecoveryState == GitHubOperationException.ReconciliationRequiredState)
            {
                await ReportRejectedAsync(source.IssueNumber, source.ExecutionId,
                    reason ?? "recovery ownership or GitHub state is uncertain; inspect the recorded recovery attempt", ct);
                continue;
            }
            if (attempt.CompletedAtUtc is null)
                await history.UpdateAsync(attempt with { State = "Cancelled", CompletedAtUtc = DateTimeOffset.UtcNow,
                    RecoveryState = "integration-recovery-interrupted", FailureReason = "Integration recovery interrupted by Worker restart; its base budget remains consumed." }, ct);
            await github.ReplaceLabelAsync(source.IssueNumber, config.GitHub.WorkingLabel, config.GitHub.IntegrationConflictLabel, ct);
            await history.FinishIntegrationRecoveryAsync(source.ExecutionId, claimId, null, ct);
            operationalLog($"Integration recovery exhausted · original execution {source.ExecutionId} · interrupted attempt {claimId} · unchanged base will not retry automatically.");
        }
    }

    public async Task ReportRejectedAsync(int issueNumber, Guid? sourceId, string reason, CancellationToken ct)
    {
        var safeReason = FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray());
        if (safeReason.Length > 1200) safeReason = safeReason[..1180] + " … [truncated]";
        if (_recoveryDiagnostics.TryGetValue((issueNumber, sourceId), out var previous) && previous == safeReason) return;
        _recoveryDiagnostics[(issueNumber, sourceId)] = safeReason;
        var diagnostic = $"Integration recovery rejected · Issue #{issueNumber} · original execution {sourceId} · human recovery required: {safeReason}";
        operationalLog(diagnostic);
        output.Warning(diagnostic);
        await telegram.IntegrationRecoveryRejectedAsync(config.Project.Name, config.Project.Repository,
            new GitHubIssue(issueNumber, "Integration recovery", "", DateTimeOffset.UtcNow), safeReason, ct);
        await github.CommentAsync(issueNumber, $"### Integration recovery requires inspection\n\nOriginal execution: `{sourceId?.ToString() ?? "unavailable"}`.\n\n{safeReason}\n\n" +
            "### Recovery\n\n- Inspect the execution history, registered worktree and preserved branch.\n- Correct or reconcile the ownership metadata before requesting recovery.\n- The implementation remains preserved; automatic recovery will not guess missing state.\n", ct);
    }

    public Task<string?> ValidateSourceAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        if (source.Project != config.Project.Name || source.Repository != config.Project.Repository || source.BaseBranch != config.Git.BaseBranch)
            return Task.FromResult<string?>("configured project, repository or integration branch differs from the original execution");
        if (source.EffectiveEffort is not null && !new[] { "low", "medium", "high", "xhigh" }.Contains(source.EffectiveEffort, StringComparer.Ordinal) ||
            source.EffectiveModel is not null && !Regex.IsMatch(source.EffectiveModel, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
            return Task.FromResult<string?>("persisted execution profile is invalid");
        return git.ValidateIntegrationRecoveryAsync(source, ct);
    }

    public async Task<IReadOnlyList<CodexProvisioning.IntegrationRecoveryCandidate>> DiscoverManagedAsync(
        string projectId, CancellationToken ct)
    {
        if (history is null) return [];
        var entries = await history.ReadAllAsync(ct);
        var candidates = new List<CodexProvisioning.IntegrationRecoveryCandidate>();
        foreach (var source in entries.Where(entry => entry.Project == config.Project.Name && entry.Repository == config.Project.Repository &&
                     entry.State == "IntegrationConflict" && entry.RecoveryState is (null or "integration-conflict") &&
                     entry.IntegrationRecoveryClaim is null && !entries.Any(parent => parent.ExecutionId == entry.RetryOfExecutionId &&
                         parent.State == "IntegrationConflict" && parent.FeatureBranch == entry.FeatureBranch)))
        {
            if (isActive(source.IssueNumber) || entries.Any(entry => entry.IssueNumber == source.IssueNumber &&
                    entry.Project == source.Project && entry.Repository == source.Repository &&
                    (entry.State == "Completed" || entry.AttemptNumber > source.AttemptNumber && entry.FeatureBranch != source.FeatureBranch))) continue;
            if (source.ServerExecutionId is null)
            {
                await ReportRejectedAsync(source.IssueNumber, source.ExecutionId,
                    "managed recovery has no original Server execution identity; reconcile ownership on the Server", ct);
                continue;
            }
            await repositoryGate.WaitAsync(ct);
            string? reason;
            string? integrationBase;
            try
            {
                reason = await ValidateSourceAsync(source, ct);
                integrationBase = reason is null ? await git.GetIntegrationBaseAsync(ct) : null;
            }
            finally { repositoryGate.Release(); }
            if (reason is not null || integrationBase is null)
            {
                await ReportRejectedAsync(source.IssueNumber, source.ExecutionId,
                    reason ?? "the authoritative integration base could not be identified", ct);
                continue;
            }
            if (source.IntegrationRecoveryAttemptBase == integrationBase) continue;
            candidates.Add(new(projectId, source.ServerExecutionId, source.ExecutionId.ToString(), integrationBase));
            if (candidates.Count == 128) break;
        }
        return candidates;
    }
}
