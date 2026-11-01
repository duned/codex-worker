namespace CodexWorker;

public sealed partial class Worker
{
    // Terminal Issue state is only an eligibility filter. Durable Worker provenance,
    // not a remotely editable comment, authorizes an individual successful result.
    internal async Task ReconcileTerminalStandaloneAsync(ExecutionHistoryEntry source,
        IReadOnlyList<ExecutionHistoryEntry> entries, GitHubIssueState remote, CancellationToken ct)
    {
        if (history is null || serverSettings?.Enabled == true || !OwnsStandaloneHistory(source)) return;
        await _repositoryGate.WaitAsync(ct);
        try
        {
            if (!ExecutionAdministrationCli.IsResolved(remote, config.GitHub))
            {
                await ReportStandaloneBlockerAsync(source, "completed-issue-conflicting-labels", ct);
                return;
            }
            var successfulSiblings = entries.Where(other => other.ExecutionId != source.ExecutionId &&
                other.Repository == source.Repository && other.Project == source.Project &&
                other.IssueNumber == source.IssueNumber && other.State == "Completed" &&
                other.CompletedAtUtc is not null && other.CompletionJson is not null &&
                other.ValidationOutcome == "passed" && other.ReportingFailure is null &&
                other.ServerExecutionId is null && other.AssignmentId is null && other.OwnershipGeneration is null &&
                other.IntegrationRecoveryClaim is null).ToArray();
            if (successfulSiblings is [var successor] && source.CompletedAtUtc is not null &&
                source.IntegrationRecoveryClaim is null && successor.AttemptNumber > source.AttemptNumber &&
                !entries.Any(other => other.ExecutionId != source.ExecutionId && other.Repository == source.Repository &&
                    other.IssueNumber == source.IssueNumber && (other.CompletedAtUtc is null ||
                        other.AttemptNumber > successor.AttemptNumber)))
            {
                // Resolve only queue disposition. An independently proven different
                // success never changes this attempt's outcome or grants resource deletion.
                remote = await github.ReadIssueStateAsync(source.IssueNumber, ct);
                var proof = await ExecutionAdministrationCli.VerifyResolutionAsync(successor, config, remote,
                    git.VerifyRemoteIntegrationAsync, ct);
                await history.AcknowledgeAsync(source,
                    $"Recovery superseded by independently verified execution {successor.ExecutionId}; original outcome {source.State} retained; {proof}",
                    false, ct);
                _standaloneChecks.Remove(source.ExecutionId);
                return;
            }
            if (source.IntegrationRecoveryClaim is not null || entries.Any(other =>
                    other.ExecutionId != source.ExecutionId && other.Repository == source.Repository &&
                    other.IssueNumber == source.IssueNumber && (other.CompletedAtUtc is null ||
                        other.AttemptNumber >= source.AttemptNumber || other.StartedAtUtc >= source.StartedAtUtc || other.State == "Completed")))
            {
                await ReportStandaloneBlockerAsync(source, "different-or-active-attempt", ct,
                    "Issue completion does not establish success of this attempt. Original outcome and resources retained.");
                return;
            }
            if (source.CompletionJson is null)
            {
                // Legacy commit-only history cannot establish the exact success-report
                // handoff. Even an authentic-looking comment cannot fill that gap.
                await ReportStandaloneBlockerAsync(source, "legacy-completion-provenance-missing", ct,
                    "Closed/done Issue is excluded from execution recovery. No durable execution-bound completion payload exists; use executions acknowledge for independently verifiable legacy provenance. Resources retained.");
                return;
            }
            // Cached terminal state grants no correction/cleanup authority.
            remote = await github.ReadIssueStateAsync(source.IssueNumber, ct);
            if (!ExecutionAdministrationCli.IsResolved(remote, config.GitHub))
            {
                await ReportStandaloneBlockerAsync(source, "completed-issue-state-changed", ct);
                return;
            }
            var completion = ExecutionCompletion.Read(source);
            var integration = completion.Report.Integration;
            if (source.State is not ("Integrating" or "Reporting" or "InfrastructureFailure" or "Cancelled" or "Completed") ||
                completion.Report.AttemptNumber != source.AttemptNumber ||
                source.ValidationOutcome != "passed" || source.ReportingFailure is not null ||
                source.CommitSha != source.CommitSha?.ToLowerInvariant() ||
                source.BaseBranch != config.Git.BaseBranch || source.Project != config.Project.Name ||
                source.Repository != config.Project.Repository || completion.PolicyFingerprint != ExecutionCompletion.Policy(config) ||
                completion.Report.Failure is not null || completion.Report.FinalValidationFailure is not null ||
                integration is null || integration.ResourceExecutionId != source.ExecutionId ||
                integration.ResourceAttemptNumber != source.AttemptNumber)
            {
                await ReportStandaloneBlockerAsync(source, "completion-provenance-incompatible", ct);
                return;
            }
            if (!await git.VerifyRemoteIntegrationAsync(source, ct))
            {
                await ReportStandaloneBlockerAsync(source, "completion-commit-not-on-remote", ct);
                return;
            }
            // No Issue, comment, label or notification effects are replayed. Resource
            // safety is deliberately independent of the proven business result.
            completion = completion with { RemoteConfirmed = true, TerminalIssueAcknowledged = true,
                LabelsReported = true, CommentReported = true, IssueClosed = true, NotificationAttempted = true };
            await history.ReconcileStandaloneSuccessAsync(source, completion, ct);
            _standaloneChecks.Remove(source.ExecutionId);
            var current = await history.ReadAllAsync(ct);
            // Cleanup checks the selected snapshot against this exact inventory,
            // including collection-valued history fields.
            var reconciled = current.SingleOrDefault(entry => entry.ExecutionId == source.ExecutionId);
            if (reconciled is null) return;
            var cleanup = await git.CleanupStaleExecutionAsync(reconciled, current, ct);
            if (cleanup.Decision != "safe")
            {
                await SaveCompletedCleanupDiagnosticAsync(reconciled, cleanup.ReasonCode,
                    $"Task success reconciled; cleanup deferred: {cleanup.Message}", ct);
                return;
            }
            await history.SaveCompletionAsync(source.ExecutionId,
                completion with { CleanupCompleted = true, Finished = true }, true, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is WorkerInfrastructureException or InvalidOperationException))
        {
            var current = await history.ReadExecutionAsync(source.ExecutionId, ct);
            if (current?.RecoveryState == "standalone-success-reconciled")
                await SaveCompletedCleanupDiagnosticAsync(current, "completed-cleanup-incomplete", ex.Message, ct);
            else await ReportStandaloneBlockerAsync(source, "completed-issue-proof-incomplete", ct,
                FailureDiagnosticRedactor.Redact(ex.Message, config.Environment.Variables.Values.ToArray()));
        }
        finally { _repositoryGate.Release(); }
    }
    private async Task SaveCompletedCleanupDiagnosticAsync(ExecutionHistoryEntry entry, string reason,
        string explanation, CancellationToken ct)
    {
        if (history is null) return;
        explanation = FailureDiagnosticRedactor.Redact(explanation, config.Environment.Variables.Values.ToArray());
        var diagnostic = new StandaloneRecoveryDiagnostic(entry.ExecutionId, entry.IssueNumber, reason,
            explanation.Length > 500 ? explanation[..500] : explanation, "cleanup-deferred", _clock.GetUtcNow());
        await history.SaveStandaloneRecoveryDiagnosticAsync(entry.ExecutionId,
            System.Text.Json.JsonSerializer.Serialize(diagnostic), ct);
    }

}
