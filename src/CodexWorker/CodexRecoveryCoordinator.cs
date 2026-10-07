namespace CodexWorker;

/// <summary>Reconciles interruption history before scheduling; never grants managed lease authority.</summary>
internal sealed class CodexRecoveryCoordinator(WorkerConfiguration config, IGitRepository git,
    ExecutionHistoryStore? history, SemaphoreSlim repositoryGate, WorkerConsole output, TimeProvider clock, Func<ExecutionHistoryEntry, Task>? report = null,
    LegacyCodexSessionStore? sessions = null)
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
        {
            var (reconstructed, reason) = await ReconstructAsync(entry, entries, ct);
            if (reconstructed is not null && await history.TryReconstructCodexRecoveryAsync(reconstructed, ct))
                output.Warning($"Legacy interruption reconstructed · execution {entry.ExecutionId} · Codex session {reconstructed.CodexRecovery?.SessionId} · awaiting execution readiness");
            else
                output.Warning($"Legacy recovery unavailable · execution {entry.ExecutionId} · {reason ?? "another execution owns this Issue"}");
        }
    }

    private async Task<(ExecutionHistoryEntry? Entry, string? Reason)> ReconstructAsync(ExecutionHistoryEntry source,
        IReadOnlyList<ExecutionHistoryEntry> entries, CancellationToken ct)
    {
        if (source.CommitSha is not null || source.IntegrationBranch is not null || source.CompletedBranch is not null ||
            source.ValidationOutcome is not null || source.ImplementationSummary is not null)
            return (null, "execution progressed beyond an uncompleted Codex invocation; inspect its lifecycle state");
        if (source.OriginalIssueBody is null) return (null, "original Issue intent is missing from execution history");
        if (source.EffectiveEffort is null) return (null, "effective execution profile is missing from execution history");
        CodexExecutionProfile profile;
        try { profile = CodexExecutionProfile.Resolve(source.OriginalIssueBody, config.Codex); }
        catch (InvalidDataException) { return (null, "persisted Issue execution profile is invalid"); }
        if (profile.Effort != source.EffectiveEffort ||
            (source.ModelSelectedByCli ? profile.Model is not null : profile.Model != source.EffectiveModel))
            return (null, "current configuration conflicts with persisted effective execution profile");
        LegacyWorkspaceEvidence? workspace;
        await repositoryGate.WaitAsync(ct);
        try { workspace = await git.InspectLegacyCodexWorkspaceAsync(source, ct); }
        catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested) { return (null, "execution/worktree identity or branch/base metadata conflicts with preserved execution"); }
        finally { repositoryGate.Release(); }
        if (workspace is null) return (null, "preserved worktree missing or ownership cannot be verified");
        var match = await (sessions ?? new LegacyCodexSessionStore(CodexProvisioning.CodexServiceEnvironment.Home)).FindAsync(source, workspace, ct);
        if (match.Rejection is not null) return (null, match.Rejection);
        // No historical fingerprint exists for legacy attempts. Pin the continuation's
        // current configuration, but never invent original intent or effective profile.
        // The normal runner always performs current authoritative validation.
        var candidate = source with
        {
            RecoveryBaseCommit = workspace.Head, RecoveryState = "codex-interrupted",
            CodexRecovery = new(source.ExecutionId, source.AttemptNumber, 0,
                CodexInterruptionRecovery.Fingerprint(config), match.SessionId, clock.GetUtcNow().AddMinutes(5))
        };
        var rejection = ValidateMetadata(candidate, entries, config);
        if (rejection is not null) return (null, rejection);
        await repositoryGate.WaitAsync(ct);
        try { rejection = await git.ValidateCodexRecoveryAsync(candidate, ct); }
        catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested) { rejection = "preserved workspace verification failed"; }
        finally { repositoryGate.Release(); }
        return rejection is null ? (candidate, null) : (null, "preserved workspace verification failed");
    }
}
