namespace CodexWorker;

public sealed partial class Worker
{
    private const int MaxStandaloneRecoveryChecksPerMinute = 3;
    private static readonly HashSet<string> ExpectedStandaloneBlockers = new(StringComparer.Ordinal)
    {
        "issue-execution-active", "another-attempt-or-recovery-active", "newer-or-completed-attempt-exists",
        "process-inspection-unavailable", "process-scope-missing", "process-scope-unreadable", "relevant-process-unreadable", "process-scope-changed", "workspace-process-active",
        "workspace-owner-history-inconsistent", "issue-no-longer-eligible", "remote-integration-ambiguous",
        "issue-no-longer-open", "uncommitted-work-cannot-be-safely-retired", "retry-requires-ready-label",
        "integration-or-ownership-metadata-unproven", "preparation-workspace-ownership-unproven",
        "authoritative-base-unavailable", "preparation-resources-unproven", "retirement-receipt-invalid",
        "recovery-inspection-budget-exhausted", "retirement-already-started", "execution-infrastructure-failure",
        "original-issue-intent-changed", "execution-profile-unproven", "recovery-configuration-changed", "implementation-checkpoint-unproven",
        "owned-workspace-missing", "workspace-head-invalid", "worktree-repository-mismatch",
        "authoritative-remote-unavailable", "original-base-not-on-authoritative-remote", "workspace-history-diverged",
        "integration-present-without-validated-provenance", "workspace-work-already-on-remote", "workspace-inspection-unavailable",
        "recovery-check-attempt-limit", "legacy-recovery-proof-incomplete",
        "completed-issue-conflicting-labels", "different-or-active-attempt", "legacy-completion-provenance-missing",
        "completion-provenance-incompatible", "completion-commit-not-on-remote", "completed-issue-proof-incomplete",
        "completed-issue-state-changed"
    };
    private readonly Dictionary<Guid, (DateTimeOffset Next, string? Diagnostic)> _standaloneChecks = [];
    private readonly Dictionary<int, (DateTimeOffset Next, GitHubIssueState State)> _standaloneIssueStates = [];
    private int _standaloneIssueChecks;
    private int _standaloneCompletionChecks;
    private DateTimeOffset _standaloneBudgetReset;
    private int _standaloneRecoveryChecks;
    private bool _standaloneBlockerSummaryEmitted;

    private static bool OwnsStandaloneHistory(ExecutionHistoryEntry entry) =>
        entry.ServerExecutionId is null && entry.AssignmentId is null && entry.OwnershipGeneration is null &&
        entry.RecoveryState is not ("operator-acknowledged" or "standalone-success-reconciled" or "managed-completion-quarantined");

    internal bool HandlesStandaloneRecovery(ExecutionHistoryEntry entry) => serverSettings?.Enabled != true && IsStandaloneRecoveryHistory(entry);

    internal static bool IsStandaloneRecoveryHistory(ExecutionHistoryEntry entry) => OwnsStandaloneHistory(entry) &&
        (IsStandaloneInterruptionHistory(entry) || entry.CompletionJson is not null && !ExecutionCompletion.IsSettled(entry));

    internal bool IsStandaloneInterruption(ExecutionHistoryEntry entry) => serverSettings?.Enabled != true && IsStandaloneInterruptionHistory(entry);

    private static bool IsStandaloneInterruptionHistory(ExecutionHistoryEntry entry) => OwnsStandaloneHistory(entry) &&
        entry.CompletionJson is null && entry.State is not ("Completed" or "Failed" or "Blocked" or "Superseded" or "IntegrationConflict") &&
        entry.RecoveryState is not ("standalone-retired" or "operator-acknowledged") &&
        (entry.RecoveryState is "standalone-continuing" or "standalone-retiring" ||
         entry.RecoveryState == "uncertain" && entry.State is "Cancelled" or "InfrastructureFailure" ||
         entry.CompletedAtUtc is null && (entry.State is "Created" or "Claimed" or "Preparing" or "Validating" or "Integrating" or "Reporting" ||
             entry.CodexRecovery?.ImplementationSucceeded == true && entry.State is "Implementing" or "Repairing"));

    private async Task<Task<IssueProcessingResult?>?> ClaimStandaloneInterruptionAsync(CancellationToken ct)
    {
        if (history is null || serverSettings?.Enabled == true) return null;
        var now = _clock.GetUtcNow();
        if (now >= _standaloneBudgetReset)
        {
            _standaloneRecoveryChecks = 0;
            _standaloneIssueChecks = 0;
            _standaloneCompletionChecks = 0;
            _standaloneBudgetReset = now.AddMinutes(1);
        }

        var entries = await history.ReadAllAsync(ct);
        foreach (var source in entries.Where(entry => entry.Project == config.Project.Name &&
                     entry.Repository == config.Project.Repository && HandlesStandaloneRecovery(entry))
                 .OrderBy(RecoveryDiagnosticAge)
                 .ThenByDescending(entry => entry.StartedAtUtc).ThenBy(entry => entry.ExecutionId))
        {
            if (RecoveryNextEligibility(source) > now) continue;
            // Bound read-only recovery independently of normal execution capacity.
            var check = _standaloneChecks.GetValueOrDefault(source.ExecutionId);
            if (check.Next > now) continue;
            // Issue eligibility has its own bounded cache. Terminal Issues never spend
            // the scarce process/worktree inspection budget, even when proof is incomplete.
            var cachedIssue = _standaloneIssueStates.GetValueOrDefault(source.IssueNumber);
            GitHubIssueState remote;
            if (cachedIssue.Next > now) remote = cachedIssue.State;
            else
            {
                if (_standaloneIssueChecks >= 20) continue;
                _standaloneIssueChecks++;
                try { remote = await github.ReadIssueStateAsync(source.IssueNumber, ct); }
                catch (WorkerInfrastructureException ex) when (!ct.IsCancellationRequested)
                {
                    await ReportStandaloneBlockerAsync(source, "issue-status-unavailable", ct,
                        FailureDiagnosticRedactor.Redact(ex.Message, config.Environment.Variables.Values.ToArray()));
                    continue;
                }
                _standaloneIssueStates[source.IssueNumber] = (now.AddMinutes(5), remote);
            }
            if (!remote.IsOpen && remote.Labels.Contains(config.GitHub.DoneLabel, StringComparer.OrdinalIgnoreCase))
            {
                if (source.CompletionJson is not null)
                {
                    if (_standaloneCompletionChecks >= MaxStandaloneRecoveryChecksPerMinute) continue;
                    _standaloneCompletionChecks++;
                }
                if (!_activeIssues.TryAdd(source.IssueNumber, 0)) continue;
                try { await ReconcileTerminalStandaloneAsync(source, entries, remote, ct); }
                finally { _activeIssues.TryRemove(source.IssueNumber, out _); }
                continue;
            }
            if (_standaloneRecoveryChecks >= MaxStandaloneRecoveryChecksPerMinute) continue;
            _standaloneRecoveryChecks++;
            _standaloneChecks[source.ExecutionId] = (now.AddHours(1), check.Diagnostic);
            if (!_activeIssues.TryAdd(source.IssueNumber, 0))
            {
                await ReportStandaloneBlockerAsync(source, "issue-execution-active", ct);
                continue;
            }
            var handedOff = false;
            try
            {
                var related = entries.Where(other => other.ExecutionId != source.ExecutionId && other.Project == source.Project &&
                    other.Repository == source.Repository && other.IssueNumber == source.IssueNumber).ToArray();
                if (source.IntegrationRecoveryClaim is not null || related.Any(other => other.CompletedAtUtc is null))
                {
                    await ReportStandaloneBlockerAsync(source, "another-attempt-or-recovery-active", ct);
                    continue;
                }
                if (related.Any(other => other.AttemptNumber >= source.AttemptNumber || other.StartedAtUtc >= source.StartedAtUtc || other.State == "Completed"))
                {
                    await ReportStandaloneBlockerAsync(source, "newer-or-completed-attempt-exists", ct);
                    continue;
                }
                // Remote eligibility is a continuation filter, never deletion or
                // integration proof. Closed/done histories retain all local evidence.
                // A cached read can exclude work, but cannot authorize continuation
                // or retirement after the inspection slot becomes available.
                if (cachedIssue.Next > now)
                    remote = await github.ReadIssueStateAsync(source.IssueNumber, ct);
                bool Has(string label) => remote.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
                if (source.CompletionJson is null && (!remote.IsOpen ||
                        !Has(config.GitHub.ReadyLabel) && !Has(config.GitHub.WorkingLabel) ||
                        Has(config.GitHub.DoneLabel) || Has(config.GitHub.BlockedLabel) || Has(config.GitHub.FailedLabel)) ||
                    source.CompletionJson is not null && !remote.IsOpen && Has(config.GitHub.DoneLabel))
                {
                    await ReportStandaloneBlockerAsync(source, "issue-no-longer-eligible", ct);
                    continue;
                }
                if (source.CompletionJson is not null)
                {
                    await _repositoryGate.WaitAsync(ct);
                    try
                    {
                        var processBlocker = await git.InspectExecutionProcessesAsync(source, ct);
                        if (processBlocker is not null) await ReportStandaloneBlockerAsync(source, processBlocker, ct);
                        else await ResumeCompletionAsync(source, ct);
                    }
                    finally { _repositoryGate.Release(); }
                    continue;
                }
                if (source.CodexRecovery is { } workspace && !entries.Any(owner =>
                    owner.ExecutionId == workspace.WorkspaceExecutionId && owner.AttemptNumber == workspace.WorkspaceAttemptNumber &&
                    owner.Project == source.Project && owner.Repository == source.Repository && owner.IssueNumber == source.IssueNumber &&
                    owner.FeatureBranch == source.FeatureBranch))
                {
                    await ReportStandaloneBlockerAsync(source, "workspace-owner-history-inconsistent", ct);
                    continue;
                }
                await _repositoryGate.WaitAsync(ct);
                try
                {
                    var inspection = await git.InspectInterruptedExecutionAsync(source, ct);
                    if (inspection.Blocker is not null || !inspection.NotIntegrated)
                    {
                        await ReportStandaloneBlockerAsync(source, inspection.Blocker ?? "remote-integration-ambiguous", ct);
                        continue;
                    }
                    var issue = await github.GetIssueAsync(source.IssueNumber, ct);
                    if (issue is null)
                    {
                        await ReportStandaloneBlockerAsync(source, "issue-no-longer-open", ct);
                        continue;
                    }
                    if (source.CodexRecovery is null && source.State == "Cancelled" &&
                        source.RecoveryState == "uncertain" && source.RepairCount > 0)
                    {
                        var legacyReview = await history.ReadLegacyReviewAsync(source.ExecutionId, ct);
                        await ReportStandaloneBlockerAsync(source, "legacy-recovery-proof-incomplete", ct,
                            legacyReview is null
                                ? "This retained post-repair history has not passed legacy process, workspace, session, Issue-intent, and execution-profile reconstruction. Preserve it for review; automatic resume and retirement are blocked."
                                : $"Legacy recovery proof failed: {legacyReview}. Preserve the worktree; automatic resume and retirement are blocked.");
                        continue;
                    }
                    var continuationBlocker = source.RecoveryState == "standalone-retiring" ? "retirement-already-started" :
                        source.State == "InfrastructureFailure" ? "execution-infrastructure-failure" :
                        source.OriginalIssueBody != issue.Body || source.IssueTitle != issue.Title ? "original-issue-intent-changed" :
                        source.EffectiveEffort is not ("low" or "medium" or "high" or "xhigh") ||
                        source.EffectiveModel is not null && !System.Text.RegularExpressions.Regex.IsMatch(source.EffectiveModel,
                            "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                            ? "execution-profile-unproven" :
                        !inspection.NoWorkspace && source.CodexRecovery?.ConfigurationFingerprint != CodexInterruptionRecovery.Fingerprint(config)
                            ? "recovery-configuration-changed" :
                        !inspection.NoWorkspace && !(source.CodexRecovery is { ResumeCount: >= 0 } snapshot &&
                            (snapshot.ImplementationSucceeded == true || snapshot.ImplementationSucceeded is null && source.ValidationOutcome == "passed"))
                            ? "implementation-checkpoint-unproven" : null;
                    if (continuationBlocker is null)
                    {
                        var legacyValidation = CodexRecoveryCoordinator.IsLegacyPostRepairValidation(source, config);
                        var continuation = source with
                        {
                            ValidationOutcome = legacyValidation ? "legacy: pending validation" : source.ValidationOutcome,
                            CodexRecovery = source.CodexRecovery is { } saved
                                ? saved with { ProcessScope = await InterruptedWorkspaceProcesses.ReadScopeAsync(ct),
                                    ImplementationSucceeded = legacyValidation ? true : saved.ImplementationSucceeded }
                                : legacyValidation ? new(source.ExecutionId, source.AttemptNumber, 0,
                                    CodexInterruptionRecovery.Fingerprint(config),
                                    ProcessScope: await InterruptedWorkspaceProcesses.ReadScopeAsync(ct), ImplementationSucceeded: true) : null
                        };
                        var execution = WorkerExecution.Continue(continuation, config, issue);
                        if (!await history.TryTransitionStandaloneAsync(continuation, "standalone-continuing", inspection, ct)) continue;
                        var resumedDiagnostic = new StandaloneRecoveryDiagnostic(source.ExecutionId, source.IssueNumber,
                            "preserved-work-continuation", "Ownership, Issue and workspace checks authorized continuation through normal validation.",
                            "resumed", _clock.GetUtcNow());
                        await history.SaveStandaloneRecoveryDiagnosticAsync(source.ExecutionId,
                            System.Text.Json.JsonSerializer.Serialize(resumedDiagnostic), ct);
                        // Keep the same execution identity. The runner rechecks ownership and
                        // validates the preserved source through the normal integration pipeline.
                        await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
                        _operationalLog($"Execution {source.ExecutionId} · " + (inspection.NoWorkspace
                            ? "resuming the unstarted claim through normal execution."
                            : "continuing interrupted validation/integration without rerunning implementation."));
                        handedOff = true;
                        return ProcessClaimedAsync(execution, issue, inspection.NoWorkspace ? null : continuation, source.IssueNumber, ct,
                            interruptedExecution: !inspection.NoWorkspace);
                    }
                    if (!Has(config.GitHub.ReadyLabel) || inspection.HasChanges)
                    {
                        await ReportStandaloneBlockerAsync(source, continuationBlocker ?? (inspection.HasChanges ? "uncommitted-work-cannot-be-safely-retired" : "retry-requires-ready-label"), ct);
                        continue;
                    }
                    // Persist intent before cleanup so interruption between removal and the
                    // final receipt is recoverable. The branch keeps useful commits reachable.
                    if (!await history.TryTransitionStandaloneAsync(source, "standalone-retiring", inspection, ct)) continue;
                    var retiring = await history.ReadExecutionAsync(source.ExecutionId, ct)
                        ?? throw new WorkerInfrastructureException("Standalone retirement history disappeared.");
                    await git.RetireInterruptedExecutionAsync(retiring, ct);
                    if (!await history.TryTransitionStandaloneAsync(retiring, "standalone-retired", inspection, ct))
                        throw new WorkerInfrastructureException("Standalone retirement changed while cleanup was recorded.");
                    await github.RemoveLabelAsync(issue.Number, config.GitHub.WorkingLabel, ct);
                    _operationalLog($"Execution {source.ExecutionId} · superseded after verified non-integration; history and feature commits retained; ready Issue will start a fresh attempt.");
                }
                finally { _repositoryGate.Release(); }
            }
            catch (GitHubOperationException ex) when (ex.IsMutation)
            {
                // Mutations retain the host's existing uncertain-GitHub failure boundary.
                // Only read-only inspection failures participate in this bounded campaign.
                throw ex.ForExecution(source.ExecutionId);
            }
            catch (WorkerInfrastructureException ex) when (!ct.IsCancellationRequested)
            {
                await ReportStandaloneBlockerAsync(source, "recovery-check-failed", ct,
                    FailureDiagnosticRedactor.Redact(ex.Message, config.Environment.Variables.Values.ToArray()));
            }
            finally
            {
                if (!handedOff) _activeIssues.TryRemove(source.IssueNumber, out _);
            }
        }
        if (!_standaloneBlockerSummaryEmitted)
        {
            _standaloneBlockerSummaryEmitted = true;
            var reasonGroups = _standaloneChecks.Values.Where(check => check.Diagnostic is not null)
                .GroupBy(check => check.Diagnostic!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();
            var grouped = reasonGroups.Take(8).Select(group => $"{group.Key} ({group.Count()})").ToList();
            if (reasonGroups.Length > grouped.Count) grouped.Add($"{reasonGroups.Length - grouped.Count} other reasons");
            if (grouped.Count > 0)
            {
                var message = $"Standalone recovery checks blocked · {_standaloneChecks.Values.Count(check => check.Diagnostic is not null)} retained executions · {string.Join(", ", grouped)}. Inspect with 'codex-worker recovery blocked' or './cw recovery blocked'.";
                _output.Warning(message);
                _operationalLog(message);
            }
        }
        return null;
    }

    private static DateTimeOffset RecoveryDiagnosticAge(ExecutionHistoryEntry entry)
    {
        if (entry.StandaloneRecoveryDiagnosticJson is null) return DateTimeOffset.MinValue;
        try
        {
            var diagnostic = System.Text.Json.JsonSerializer.Deserialize<StandaloneRecoveryDiagnostic>(entry.StandaloneRecoveryDiagnosticJson);
            return diagnostic?.ReasonCode == "recovery-inspection-budget-exhausted"
                ? DateTimeOffset.MinValue : diagnostic?.CheckedAtUtc ?? DateTimeOffset.MinValue;
        }
        catch (System.Text.Json.JsonException) { return DateTimeOffset.MinValue; }
    }

    private async Task ReportStandaloneBlockerAsync(ExecutionHistoryEntry source, string reason, CancellationToken ct, string? explanation = null)
    {
        var knownCode = reason.Length <= 120 && reason.Length > 0 && reason.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
        var reasonCode = knownCode ? reason : "recovery-check-failed";
        explanation ??= reasonCode switch
        {
            "original-issue-intent-changed" => "Current Issue title/body differs from the original execution intent.",
            "execution-profile-unproven" => "The retained execution model/effort profile is missing or invalid.",
            "recovery-configuration-changed" => "Current configuration does not match the retained workspace fingerprint.",
            "implementation-checkpoint-unproven" => "History does not prove implementation completed with a valid continuation checkpoint.",
            "execution-infrastructure-failure" => "The original execution records an infrastructure failure; standalone validation continuation is not authorized.",
            "process-scope-unreadable" => "A live process has unreadable cgroup evidence; the Worker cannot prove it is outside the owned execution scope.",
            "process-scope-missing" => "No historical Linux process scope or joined-child shutdown proof is available. Preserve the workspace.",
            "relevant-process-unreadable" => "A potentially owned live process has an unreadable workspace link. Preserve the workspace and inspect service process permissions.",
            "process-scope-changed" => "The Worker cgroup differs from the recorded execution scope and no joined-child shutdown proof exists.",
            "process-inspection-unavailable" => "The Worker could not safely inspect processes associated with the retained workspace.",
            "issue-no-longer-eligible" => "The Issue is no longer eligible for automatic recovery.",
            _ when !knownCode => reason,
            _ => "Automatic recovery is blocked pending inspection."
        };
        explanation = FailureDiagnosticRedactor.Redact(explanation, config.Environment.Variables.Values.ToArray());
        if (explanation.Length > 500) explanation = explanation[..500];
        var previous = _standaloneChecks.GetValueOrDefault(source.ExecutionId);
        if (previous.Next <= _clock.GetUtcNow()) previous.Next = _clock.GetUtcNow().AddHours(1);

        _standaloneChecks[source.ExecutionId] = previous with { Diagnostic = reasonCode };
        var diagnostic = new StandaloneRecoveryDiagnostic(source.ExecutionId, source.IssueNumber, reasonCode, explanation,
            "blocked", _clock.GetUtcNow(), previous.Next);
        var historyStore = history ?? throw new InvalidOperationException("Standalone recovery history is unavailable.");
        await historyStore.SaveStandaloneRecoveryDiagnosticAsync(source.ExecutionId, System.Text.Json.JsonSerializer.Serialize(diagnostic), ct);
        if (reasonCode == "recovery-check-failed" || !ExpectedStandaloneBlockers.Contains(reasonCode))
        {
            var message = $"Standalone recovery check failed · execution {source.ExecutionId} · Issue #{source.IssueNumber} · {explanation}. Evidence retained.";
            _output.Warning(message);
            _operationalLog(message);
        }
    }

    private static DateTimeOffset RecoveryNextEligibility(ExecutionHistoryEntry source)
    {
        if (source.StandaloneRecoveryDiagnosticJson is null) return DateTimeOffset.MinValue;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<StandaloneRecoveryDiagnostic>(source.StandaloneRecoveryDiagnosticJson)
                ?.NextEligibleAtUtc ?? DateTimeOffset.MinValue;
        }
        catch (System.Text.Json.JsonException) { return DateTimeOffset.MinValue; }
    }
}

public sealed record StandaloneRecoveryDiagnostic(Guid ExecutionId, int IssueNumber, string ReasonCode,
    string Explanation, string CheckStatus, DateTimeOffset CheckedAtUtc, DateTimeOffset? NextEligibleAtUtc = null)
{
    public DateTimeOffset? LastInspectedAtUtc => CheckStatus == "queued" ? null : CheckedAtUtc;
}
