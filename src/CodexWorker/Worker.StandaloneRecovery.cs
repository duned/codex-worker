namespace CodexWorker;

public sealed partial class Worker
{
    private readonly Dictionary<Guid, (DateTimeOffset Next, int Attempts, string? Diagnostic)> _standaloneChecks = [];

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
            if (_activeIssues.ContainsKey(source.IssueNumber)) continue;
            // A bounded read-only campaign per runtime. Restarts re-read authoritative evidence;
            // no failed inspection authorizes cleanup, replay, or a generic implementation retry.
            var check = _standaloneChecks.GetValueOrDefault(source.ExecutionId);
            if (check.Attempts >= 3 || check.Next > _clock.GetUtcNow()) continue;
            _standaloneChecks[source.ExecutionId] = (_clock.GetUtcNow().AddMinutes(1), check.Attempts + 1, check.Diagnostic);
            if (!_activeIssues.TryAdd(source.IssueNumber, 0))
            {
                ReportStandaloneBlocker(source, "issue-execution-active");
                continue;
            }
            var handedOff = false;
            try
            {
                var related = entries.Where(other => other.ExecutionId != source.ExecutionId && other.Project == source.Project &&
                    other.Repository == source.Repository && other.IssueNumber == source.IssueNumber).ToArray();
                if (source.IntegrationRecoveryClaim is not null || related.Any(other => other.CompletedAtUtc is null))
                {
                    ReportStandaloneBlocker(source, "another-attempt-or-recovery-active");
                    continue;
                }
                if (related.Any(other => other.AttemptNumber >= source.AttemptNumber || other.StartedAtUtc >= source.StartedAtUtc || other.State == "Completed"))
                {
                    ReportStandaloneBlocker(source, "newer-or-completed-attempt-exists");
                    continue;
                }
                if (source.CompletionJson is not null)
                {
                    await _repositoryGate.WaitAsync(ct);
                    try
                    {
                        var processBlocker = await git.InspectExecutionProcessesAsync(source, ct);
                        if (processBlocker is not null) ReportStandaloneBlocker(source, processBlocker);
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
                    ReportStandaloneBlocker(source, "workspace-owner-history-inconsistent");
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
                        ReportStandaloneBlocker(source, "issue-no-longer-eligible");
                        continue;
                    }
                    var inspection = await git.InspectInterruptedExecutionAsync(source, ct);
                    if (inspection.Blocker is not null || !inspection.NotIntegrated)
                    {
                        ReportStandaloneBlocker(source, inspection.Blocker ?? "remote-integration-ambiguous");
                        continue;
                    }
                    var issue = await github.GetIssueAsync(source.IssueNumber, ct);
                    if (issue is null)
                    {
                        ReportStandaloneBlocker(source, "issue-no-longer-open");
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
                        ReportStandaloneBlocker(source, inspection.HasChanges ? "uncommitted-work-cannot-be-safely-retired" : "retry-requires-ready-label");
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
                ReportStandaloneBlocker(source, FailureDiagnosticRedactor.Redact(ex.Message, config.Environment.Variables.Values.ToArray()));
            }
            finally
            {
                if (!handedOff) _activeIssues.TryRemove(source.IssueNumber, out _);
            }
        }
        return null;
    }

    private void ReportStandaloneBlocker(ExecutionHistoryEntry source, string reason)
    {
        if (reason.Length > 1200) reason = reason[..1200];
        var previous = _standaloneChecks.GetValueOrDefault(source.ExecutionId);
        if (previous.Diagnostic == reason) return;
        _standaloneChecks[source.ExecutionId] = previous with { Diagnostic = reason };
        var message = $"Standalone recovery blocked · execution {source.ExecutionId} · Issue #{source.IssueNumber} · {reason}. Evidence retained; at most three read-only checks per Worker start.";
        _output.Warning(message);
        _operationalLog(message);
    }
}
