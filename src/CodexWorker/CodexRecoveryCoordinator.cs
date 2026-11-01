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
            owner.AttemptNumber != snapshot.WorkspaceAttemptNumber || owner.Engine != source.Engine)
            return "original workspace ownership history is missing or inconsistent";
        return null;
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        if (history is null) return;
        var entries = await history.ReadAllAsync(ct);
        foreach (var entry in entries.Where(entry => entry.Project == config.Project.Name && entry.Repository == config.Project.Repository &&
                     entry.CompletedAtUtc is null && entry.CodexRecovery is { ImplementationSucceeded: not true } &&
                     entry.RecoveryState is not ("standalone-continuing" or "standalone-retiring") &&
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
        var unavailable = 0;
        var changed = 0;
        var newDiagnostics = new List<string>();
        foreach (var entry in entries.Where(entry => entry.Project == config.Project.Name && entry.Repository == config.Project.Repository &&
                     entry.Engine == "codex" && entry.CodexRecovery is null && entry.State is "InfrastructureFailure" or "Cancelled" && entry.RecoveryState == "uncertain"))
        {
            var (reconstructed, reason) = await ReconstructAsync(entry, entries, ct);
            if (reconstructed is not null && await history.TryReconstructCodexRecoveryAsync(reconstructed, ct))
                output.Warning($"Legacy interruption reconstructed · execution {entry.ExecutionId} · Codex session {reconstructed.CodexRecovery?.SessionId} · awaiting execution readiness");
            else
            {
                unavailable++;
                var diagnostic = reason ?? "another execution owns this Issue";
                var evidence = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        entry,
                        configuration = CodexInterruptionRecovery.Fingerprint(config),
                        siblings = entries.Where(e => e.Repository == entry.Repository && e.IssueNumber == entry.IssueNumber)
                            .Select(e => new { e.ExecutionId, e.State, e.RecoveryState, e.CompletedAtUtc }).OrderBy(e => e.ExecutionId)
                    }))));
                if (entry.ServerExecutionId is null && entry.AssignmentId is null && entry.OwnershipGeneration is null &&
                    await history.RecordLegacyReviewAsync(entry.ExecutionId, diagnostic, clock.GetUtcNow(), ct, evidence))
                {
                    changed++;
                    if (newDiagnostics.Count < 5) newDiagnostics.Add($"{entry.ExecutionId}: {diagnostic}");
                }
            }
        }
        if (unavailable > 0)
            output.Warning($"Legacy recovery requires review · {unavailable} executions ({changed} new or changed diagnostics); inspect execution legacy-review diagnostics. Resources and outcomes retained." +
                (newDiagnostics.Count == 0 ? "" : " New diagnostics: " + string.Join("; ", newDiagnostics)));
    }

    private async Task<(ExecutionHistoryEntry? Entry, string? Reason)> ReconstructAsync(ExecutionHistoryEntry source,
        IReadOnlyList<ExecutionHistoryEntry> entries, CancellationToken ct)
    {
        var legacyValidation = IsLegacyPostRepairValidation(source, config);
        if (source.CommitSha is not null || source.IntegrationBranch is not null || source.CompletedBranch is not null ||
            source.ValidationOutcome is not null && !legacyValidation || source.ImplementationSummary is not null && !legacyValidation)
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
        try
        {
            workspace = await git.InspectLegacyCodexWorkspaceAsync(source, ct);
            if (workspace is null) return (null, "preserved worktree missing or ownership cannot be verified");
            var processBlocker = await git.InspectExecutionProcessesAsync(source, ct);
            if (processBlocker is not null) return (null, processBlocker);
        }
        catch (WorkerInfrastructureException ex) when (!ct.IsCancellationRequested)
        {
            var diagnostic = FailureDiagnosticRedactor.Redact(ex.Message, config.Environment.Variables.Values.ToArray());
            return (null, diagnostic.Length <= 500 ? diagnostic : diagnostic[..500]);
        }
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
            ValidationOutcome = legacyValidation ? "legacy: pending validation" : source.ValidationOutcome,
            CodexRecovery = new(source.ExecutionId, source.AttemptNumber, 0,
                CodexInterruptionRecovery.Fingerprint(config), match.SessionId, clock.GetUtcNow().AddMinutes(5),
                ImplementationSucceeded: legacyValidation ? true : null)
        };
        var rejection = ValidateMetadata(candidate, entries, config);
        if (rejection is not null) return (null, rejection);
        await repositoryGate.WaitAsync(ct);
        try { rejection = await git.ValidateCodexRecoveryAsync(candidate, ct); }
        catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested) { rejection = "preserved workspace verification failed"; }
        finally { repositoryGate.Release(); }
        return rejection is null ? (candidate, null) : (null, "preserved workspace verification failed");
    }

    internal static bool IsLegacyPostRepairValidation(ExecutionHistoryEntry source, WorkerConfiguration config)
    {
        var outcome = source.ValidationOutcome;
        var supportedOutcome = outcome == "failed or interrupted" ||
            outcome?.StartsWith("failed: validation", StringComparison.OrdinalIgnoreCase) == true ||
            outcome?.StartsWith("pending: validation", StringComparison.OrdinalIgnoreCase) == true;
        if (source.State != "Cancelled" || source.RecoveryState != "uncertain" || source.CompletedAtUtc is null ||
            source.CommitSha is not null || source.IntegrationBranch is not null || source.CompletedBranch is not null ||
            source.IntegrationRecoveryClaim is not null || source.CompletionJson is not null ||
            string.IsNullOrWhiteSpace(source.ImplementationSummary) || !supportedOutcome ||
            source.RepairCount < 1 || source.RepairCount > config.Validation.MaxFixAttempts ||
            source.Repairs.Count != source.RepairCount)
            return false;

        var ordered = source.Repairs.OrderBy(repair => repair.Attempt).ToArray();
        if (ordered.Where((repair, index) => repair.IntegrationRepair || repair.Attempt != index + 1 ||
                repair.MaximumAttempts != config.Validation.MaxFixAttempts).Any())
            return false;

        if (ordered.Take(ordered.Length - 1).Any(repair => repair.PassedAfterRepair || repair.ValidationAfterRepair is null ||
                string.IsNullOrWhiteSpace(repair.RepairSummary)))
            return false;

        // A completed repair record plus Cancelled (rather than Failed/Blocked) and no
        // post-repair validation result is the legacy evidence that shutdown interrupted
        // the following validation. A placeholder record means shutdown interrupted the
        // repair invocation itself and is deliberately not eligible.
        var last = ordered[^1];
        return !string.IsNullOrWhiteSpace(last.RepairSummary) &&
            !last.RepairSummary.StartsWith("Repair invocation interrupted", StringComparison.Ordinal) &&
            !last.PassedAfterRepair && last.ValidationAfterRepair is null;
    }
}
