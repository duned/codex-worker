namespace CodexWorker;

public sealed partial class Worker
{
    private const int MaxStandaloneRecoveryChecksPerStart = 3;
    private static readonly HashSet<string> ExpectedStandaloneBlockers = new(StringComparer.Ordinal)
    {
        "issue-execution-active", "another-attempt-or-recovery-active", "newer-or-completed-attempt-exists",
        "process-inspection-unavailable", "process-scope-changed", "workspace-process-active",
        "workspace-owner-history-inconsistent", "issue-no-longer-eligible", "remote-integration-ambiguous",
        "issue-no-longer-open", "uncommitted-work-cannot-be-safely-retired", "retry-requires-ready-label",
        "integration-or-ownership-metadata-unproven", "preparation-workspace-ownership-unproven",
        "authoritative-base-unavailable", "preparation-resources-unproven", "retirement-receipt-invalid",
        "owned-workspace-missing", "workspace-head-invalid", "worktree-repository-mismatch",
        "authoritative-remote-unavailable", "original-base-not-on-authoritative-remote", "workspace-history-diverged",
        "integration-present-without-validated-provenance", "workspace-work-already-on-remote", "workspace-inspection-unavailable"
    };
    private readonly Dictionary<Guid, (DateTimeOffset Next, int Attempts, string? Diagnostic)> _standaloneChecks = [];
    private int _standaloneRecoveryChecksThisStart;
    private bool _standaloneBlockerSummaryEmitted;

    private bool OwnsStandaloneHistory(ExecutionHistoryEntry entry) => serverSettings?.Enabled != true &&
        entry.ServerExecutionId is null && entry.AssignmentId is null && entry.OwnershipGeneration is null &&
        entry.RecoveryState is not ("operator-acknowledged" or "managed-completion-quarantined");

    internal bool HandlesStandaloneRecovery(ExecutionHistoryEntry entry) => OwnsStandaloneHistory(entry) &&
        (IsStandaloneInterruption(entry) || entry.CompletionJson is not null && !ExecutionCompletion.IsSettled(entry));

    internal bool IsStandaloneInterruption(ExecutionHistoryEntry entry) => OwnsStandaloneHistory(entry) &&
        entry.CompletionJson is null && entry.State is not ("Completed" or "Failed" or "Blocked" or "Superseded" or "IntegrationConflict") &&
        entry.RecoveryState is not ("standalone-retired" or "operator-acknowledged") &&
        (entry.RecoveryState is "standalone-continuing" or "standalone-retiring" ||
         entry.RecoveryState == "uncertain" && entry.State is "Cancelled" or "InfrastructureFailure" ||
         entry.CompletedAtUtc is null && (entry.State is "Created" or "Claimed" or "Preparing" or "Validating" or "Integrating" or "Reporting" ||
             entry.CodexRecovery?.ImplementationSucceeded == true && entry.State is "Implementing" or "Repairing"));

    private async Task<Task<IssueProcessingResult?>?> ClaimStandaloneInterruptionAsync(CancellationToken ct)
    {
        if (history is null || serverSettings?.Enabled == true) return null;
        var entries = await history.ReadAllAsync(ct);
        foreach (var source in entries.Where(entry => entry.Project == config.Project.Name &&
                     entry.Repository == config.Project.Repository && HandlesStandaloneRecovery(entry)))
        {
            if (_standaloneRecoveryChecksThisStart >= MaxStandaloneRecoveryChecksPerStart) break;
            if (_activeIssues.ContainsKey(source.IssueNumber)) continue;
            // A bounded read-only campaign per runtime. Restarts re-read authoritative evidence;
            // no failed inspection authorizes cleanup, replay, or a generic implementation retry.
            var check = _standaloneChecks.GetValueOrDefault(source.ExecutionId);
            if (check.Attempts >= 3 || check.Next > _clock.GetUtcNow()) continue;
            _standaloneRecoveryChecksThisStart++;
            _standaloneChecks[source.ExecutionId] = (_clock.GetUtcNow().AddMinutes(1), check.Attempts + 1, check.Diagnostic);
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
                    var remote = await github.ReadIssueStateAsync(source.IssueNumber, ct);
                    bool Has(string label) => remote.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
                    if (!remote.IsOpen || !Has(config.GitHub.ReadyLabel) && !Has(config.GitHub.WorkingLabel) ||
                        Has(config.GitHub.DoneLabel) || Has(config.GitHub.BlockedLabel) || Has(config.GitHub.FailedLabel))
                    {
                        await ReportStandaloneBlockerAsync(source, "issue-no-longer-eligible", ct);
                        continue;
                    }
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
                    var canContinue = source.RecoveryState != "standalone-retiring" && source.State != "InfrastructureFailure" &&
                        source.OriginalIssueBody == issue.Body && source.IssueTitle == issue.Title &&
                        source.EffectiveEffort is ("low" or "medium" or "high" or "xhigh") &&
                        (source.EffectiveModel is null || System.Text.RegularExpressions.Regex.IsMatch(source.EffectiveModel,
                            "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) &&
                        (inspection.NoWorkspace || source.CodexRecovery is { ResumeCount: >= 0 } snapshot &&
                            snapshot.ConfigurationFingerprint == CodexInterruptionRecovery.Fingerprint(config) &&
                            (snapshot.ImplementationSucceeded == true || snapshot.ImplementationSucceeded is null &&
                                source.ValidationOutcome == "passed"));
                    if (canContinue)
                    {
                        var continuation = source with { CodexRecovery = source.CodexRecovery is { } saved
                            ? saved with { ProcessScope = await InterruptedWorkspaceProcesses.ReadScopeAsync(ct) } : null };
                        var execution = WorkerExecution.Continue(continuation, config, issue);
                        if (!await history.TryTransitionStandaloneAsync(continuation, "standalone-continuing", inspection, ct)) continue;
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
                        await ReportStandaloneBlockerAsync(source, inspection.HasChanges ? "uncommitted-work-cannot-be-safely-retired" : "retry-requires-ready-label", ct);
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

    private async Task ReportStandaloneBlockerAsync(ExecutionHistoryEntry source, string reason, CancellationToken ct, string? explanation = null)
    {
        var knownCode = reason.Length <= 120 && reason.Length > 0 && reason.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
        var reasonCode = knownCode ? reason : "recovery-check-failed";
        explanation ??= reasonCode switch
        {
            "process-inspection-unavailable" => "The Worker could not safely inspect processes associated with the retained workspace.",
            "issue-no-longer-eligible" => "The Issue is no longer eligible for automatic recovery.",
            _ when !knownCode => reason,
            _ => "Automatic recovery is blocked pending inspection."
        };
        explanation = FailureDiagnosticRedactor.Redact(explanation, config.Environment.Variables.Values.ToArray());
        if (explanation.Length > 500) explanation = explanation[..500];
        var previous = _standaloneChecks.GetValueOrDefault(source.ExecutionId);
        if (previous.Diagnostic == reasonCode) return;
        _standaloneChecks[source.ExecutionId] = previous with { Diagnostic = reasonCode };
        var diagnostic = new StandaloneRecoveryDiagnostic(source.ExecutionId, source.IssueNumber, reasonCode, explanation,
            "blocked", _clock.GetUtcNow());
        var historyStore = history ?? throw new InvalidOperationException("Standalone recovery history is unavailable.");
        await historyStore.SaveStandaloneRecoveryDiagnosticAsync(source.ExecutionId, System.Text.Json.JsonSerializer.Serialize(diagnostic), ct);
        if (reasonCode == "recovery-check-failed" || !ExpectedStandaloneBlockers.Contains(reasonCode))
        {
            var message = $"Standalone recovery check failed · execution {source.ExecutionId} · Issue #{source.IssueNumber} · {explanation}. Evidence retained.";
            _output.Warning(message);
            _operationalLog(message);
        }
    }
}

public sealed record StandaloneRecoveryDiagnostic(Guid ExecutionId, int IssueNumber, string ReasonCode,
    string Explanation, string CheckStatus, DateTimeOffset CheckedAtUtc);
