namespace CodexWorker;

/// <summary>Reconciles interruption history before scheduling; never grants managed lease authority.</summary>
internal sealed class CodexRecoveryCoordinator(WorkerConfiguration config, IGitRepository git,
    ExecutionHistoryStore? history, SemaphoreSlim repositoryGate, WorkerConsole output, TimeProvider clock, Func<ExecutionHistoryEntry, Task>? report = null)
{
    internal static string? ValidateMetadata(ExecutionHistoryEntry source, IReadOnlyList<ExecutionHistoryEntry> entries, WorkerConfiguration config)
    {
        var snapshot = source.CodexRecovery;
        if (snapshot is null) return "workspace/configuration ownership snapshot is missing";
        if (source.OriginalIssueBody is null) return "original Issue intent is missing";
        if (source.RecoveryBaseCommit is null) return "original worktree base commit is missing";
        if (source.EffectiveEffort is null || !new[] { "low", "medium", "high", "xhigh" }.Contains(source.EffectiveEffort, StringComparer.Ordinal) ||
            source.EffectiveModel is not null && !System.Text.RegularExpressions.Regex.IsMatch(source.EffectiveModel, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
            return "effective Codex execution profile is missing or invalid";
        if (snapshot.ResumeCount < 0 || snapshot.ResumeCount >= CodexInterruptionRecovery.MaximumResumes)
            return "automatic infrastructure resume budget is invalid or exhausted";
        if (snapshot.ConfigurationFingerprint != CodexInterruptionRecovery.Fingerprint(config))
            return "execution configuration changed since interruption";
        var owner = entries.SingleOrDefault(entry => entry.ExecutionId == snapshot.WorkspaceExecutionId);
        if (owner is null || owner.Project != source.Project || owner.Repository != source.Repository ||
            owner.IssueNumber != source.IssueNumber || owner.FeatureBranch != source.FeatureBranch ||
            owner.AttemptNumber != snapshot.WorkspaceAttemptNumber)
            return "original workspace ownership history is missing or inconsistent";
        return null;
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        if (history is null) return;
        var entries = await history.ReadAllAsync(ct);
        foreach (var entry in entries.Where(entry => entry.Project == config.Project.Name && entry.Repository == config.Project.Repository &&
                     entry.CompletedAtUtc is null && entry.CodexRecovery is not null &&
                     entry.State is "Created" or "Claimed" or "Preparing" or "Implementing" or "Repairing"))
        {
            string? reason;
            await repositoryGate.WaitAsync(ct);
            try { reason = await git.ValidateCodexRecoveryAsync(entry, ct); }
            finally { repositoryGate.Release(); }
            var interrupted = entry with
            {
                State = "InfrastructureFailure", CompletedAtUtc = clock.GetUtcNow(),
                RecoveryState = reason is null ? "codex-interrupted" : "codex-recovery-inspection-required",
                FailureReason = reason is null ? "Implementation interrupted by Worker restart; workspace retained for continuation." :
                    "Interrupted implementation cannot be resumed safely: " + FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray()),
                CodexRecovery = entry.CodexRecovery is { } recovery ? recovery with { RetryAfterUtc = clock.GetUtcNow().AddMinutes(5) } : null
            };
            await history.UpdateAsync(interrupted, ct);
            if (report is not null)
            {
                try { await report(interrupted); }
                catch (HttpRequestException) { output.Warning($"Execution {entry.ExecutionId} · interrupted outcome reporting pending; Server lease authority is still required for recovery."); }
            }
            output.Warning($"Execution {entry.ExecutionId} · " + (reason is null ? "preserved for Codex recovery after restart" : "recovery requires inspection; see execution history"));
        }
        foreach (var entry in entries.Where(entry => entry.Project == config.Project.Name && entry.Repository == config.Project.Repository &&
                     entry.CodexRecovery is null && entry.State is "InfrastructureFailure" or "Cancelled" && entry.RecoveryState == "uncertain"))
            output.Warning($"Execution {entry.ExecutionId} · automatic Codex recovery unavailable: legacy interruption has insufficient workspace/configuration/intent metadata; inspect preserved state.");
    }
}
