using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodexWorker;

public sealed partial class Worker
{
    private async Task ReportInterruptedExecutionAsync(WorkerExecution execution, GitHubIssue issue, string reason,
        Exception? primaryFailure = null, bool suppressGitHubMutations = false)
    {
        var safeReason = Limit(FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray()), 1200);
        var primaryGitHubFailure = primaryFailure is null ? null : GitHubOperationException.Find(primaryFailure);
        var recoveryState = primaryGitHubFailure is { IsMutation: true }
            ? GitHubOperationException.ReconciliationRequiredState : "uncertain";
        if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, safeReason, recoveryState);
        var recovery = history is null ? null : await history.ReadExecutionAsync(execution.ExecutionId, CancellationToken.None);
        var recoveryGuidance = recovery?.RecoveryState switch
        {
            "codex-interrupted" => $"Eligible recovery resumes automatically after execution readiness and cooldown ({recovery.CodexRecovery?.RetryAfterUtc:O}), within the remaining resume budget.",
            "codex-recovery-exhausted" => "Automatic recovery budget exhausted. Inspect the preserved workspace and execution history before requesting further recovery; reapplying the ready label does not reset this budget.",
            _ => "Inspect execution history and the preserved workspace, and reconcile Git and GitHub state before requesting another attempt."
        };
        if (primaryFailure is CodexExecutionInfrastructureException)
            await telegram.FailedAsync(config.Project.Name, config.Project.Repository, issue, _clock.GetUtcNow() - execution.StartedAtUtc,
                execution.ExecutionId, safeReason + "\nWorkspace preserved. " + recoveryGuidance, CancellationToken.None);
        var diagnostic = primaryGitHubFailure is not null && execution.IsTerminal &&
            execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled)
            ? $"Execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · Issue #{issue.Number} · outcome {execution.State} · GitHub reporting failure · {safeReason}"
            : $"Execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · Issue #{issue.Number} · infrastructure failure · {safeReason}";
        if (execution.ServerExecutionId is not null)
            diagnostic = ManagedExecutionLog.Execution(CreateEntry(execution, null, null, null),
                primaryGitHubFailure is not null && execution.IsTerminal &&
                    execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled)
                    ? $"outcome {execution.State} · GitHub reporting failure · {safeReason}"
                    : $"infrastructure failure · {safeReason}",
                config.Environment.Variables.Values.ToArray());
        _operationalLog(diagnostic);
        if (execution.ServerExecutionId is null) _output.Warning(diagnostic);
        if (primaryGitHubFailure is not null)
        {
            if (history is not null)
            {
                if (execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled))
                {
                    var reportingFailure = Limit(FailureDiagnosticRedactor.Redact(primaryGitHubFailure.Message,
                        config.Environment.Variables.Values.ToArray()), 1600);
                    await history.UpdateReportingFailureAsync(execution.ExecutionId, reportingFailure, CancellationToken.None);
                }
                if (primaryGitHubFailure.IsMutation)
                    await history.UpdateRecoveryAsync(execution.ExecutionId, GitHubOperationException.ReconciliationRequiredState, CancellationToken.None);
            }
            _operationalLog($"Execution {execution.ExecutionId} · primary GitHub failure during {primaryGitHubFailure.Operation}; no further GitHub mutation will be attempted · remote Issue state {(primaryGitHubFailure.RemoteStateUncertain ? "uncertain" : primaryGitHubFailure.RemoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "read only")}.");
            return;
        }
        if (execution.IsTerminal && execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled))
        {
            _operationalLog($"Execution {execution.ExecutionId} · outcome {execution.State} remains authoritative; secondary reporting failed with {safeReason} · no further GitHub mutation will be attempted.");
            return;
        }
        if (history is not null && await history.ReadExecutionAsync(execution.ExecutionId, CancellationToken.None)
            is { CompletionJson: not null })
        {
            await history.UpdateRecoveryAsync(execution.ExecutionId, GitHubOperationException.ReconciliationRequiredState, CancellationToken.None);
            _operationalLog($"Execution {execution.ExecutionId} · remote push outcome uncertain or completion reporting pending · verify the exact integration commit on the authoritative remote base; do not replay Codex.");
            return;
        }
        // An explicit local recovery failure may have observed changed intent or uncertain
        // state. Retain diagnostics and the claim without changing that Issue again.
        if (suppressGitHubMutations) return;
        try
        {
            // These reporting mutations are attempted once. A failed mutation is retained for
            // reconciliation and must never be replayed as an automatic retry.
            if (primaryFailure is not CodexExecutionInfrastructureException { Recoverable: true })
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, CancellationToken.None);
            await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
                $"### Execution interrupted\n\nExecution `{execution.ExecutionId}` stopped because of an infrastructure failure.\n\n{safeReason}\n\n" +
                (execution.CodexProfile is { } profile ? $"Codex model: `{profile.EffectiveModel ?? "unknown (CLI model unavailable)"}` · effort: `{profile.Effort}`.\n\n" : "") +
                $"### Recovery\n\n- The implementation workspace is preserved.\n- {recoveryGuidance}\n", CancellationToken.None);
            if (history is not null)
            {
                var entry = (await history.ReadAllAsync(CancellationToken.None)).Single(row => row.ExecutionId == execution.ExecutionId);
                try { await ReportServerAsync(entry, execution.State, CancellationToken.None); }
                catch (HttpRequestException) when (primaryFailure is CodexExecutionInfrastructureException)
                {
                    _operationalLog($"Execution {execution.ExecutionId} · Codex interruption durably recorded; Server outcome reporting pending. Managed continuation still requires a fresh lease.");
                }
            }
        }
        catch (GitHubOperationException reportingFailure)
        {
            var secondaryMessage = Limit(FailureDiagnosticRedactor.Redact(reportingFailure.Message,
                config.Environment.Variables.Values.ToArray()), 1600);
            var correlated = reportingFailure.DuringReporting(execution.ExecutionId, safeReason);
            var followUp = reportingFailure.IsMutation && reportingFailure.RemoteStateUncertain
                ? "manual reconciliation is required"
                : "project scheduling will pause until GitHub reporting recovers";
            _operationalLog($"Execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · primary failure · {safeReason} · secondary GitHub reporting failure · {secondaryMessage} · remote Issue state {(reportingFailure.RemoteStateUncertain ? "uncertain" : reportingFailure.RemoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "read only")} · {followUp}.");
            _output.Warning($"Execution {execution.ExecutionId} · secondary GitHub reporting failure; primary failure remains authoritative. {secondaryMessage}");
            if (history is not null)
            {
                await history.UpdateReportingFailureAsync(execution.ExecutionId, secondaryMessage, CancellationToken.None);
                if (reportingFailure.IsMutation)
                    await history.UpdateRecoveryAsync(execution.ExecutionId, GitHubOperationException.ReconciliationRequiredState, CancellationToken.None);
            }
            throw correlated;
        }
    }

    private async Task RejectPreparationAsync(WorkerExecution execution, GitHubIssue issue, string claimedLabel,
        string reason, CancellationToken ct, string? recoveryState = "uncertain")
    {
        var safeReason = Limit(FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray()), 1200);
        await RecordInfrastructureFailureAsync(execution, safeReason, recoveryState);
        var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · preparation rejected · {safeReason}";
        if (execution.ServerExecutionId is not null)
            diagnostic = ManagedExecutionLog.Execution(CreateEntry(execution, null, null, null), $"preparation rejected · {safeReason}",
                config.Environment.Variables.Values.ToArray());
        _operationalLog(diagnostic);
        if (execution.ServerExecutionId is null) _output.Warning(diagnostic);
        // Remove eligibility before reporting so this Issue cannot repeatedly consume capacity.
        // A failed GitHub update remains infrastructure failure: its remote state is uncertain.
        try
        {
            await github.ReplaceLabelAsync(issue.Number, claimedLabel, config.GitHub.BlockedLabel, ct);
            await telegram.PreparationRejectedAsync(config.Project.Name, config.Project.Repository, issue,
                execution.ExecutionId, safeReason, CancellationToken.None);
            await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
                $"### Preparation rejected\n\nExecution `{execution.ExecutionId}` could not start.\n\n{safeReason}\n\n### Recovery\n\n" +
                $"- Inspect the previous execution and preserved workspace.\n- Correct the preparation problem or select `worker.retryMode: restart`.\n- Apply `{config.GitHub.ReadyLabel}` for a new attempt, or `{config.GitHub.IntegrationRecoveryLabel}` to recover a preserved integration conflict.\n", ct);
        }
        catch (GitHubOperationException reportingFailure)
        {
            var secondary = Limit(FailureDiagnosticRedactor.Redact(reportingFailure.Message,
                config.Environment.Variables.Values.ToArray()), 1600);
            _operationalLog($"Execution {execution.ExecutionId} · primary preparation failure · {safeReason} · secondary GitHub reporting failure · {secondary} · remote Issue state {(reportingFailure.RemoteStateUncertain ? "uncertain" : "known unchanged")}.");
            if (history is not null)
            {
                await history.UpdateReportingFailureAsync(execution.ExecutionId, secondary, CancellationToken.None);
                if (reportingFailure.IsMutation)
                    await history.UpdateRecoveryAsync(execution.ExecutionId, GitHubOperationException.ReconciliationRequiredState, CancellationToken.None);
            }
            throw reportingFailure.DuringReporting(execution.ExecutionId, safeReason);
        }
    }

    private async Task<IssueExecutionReport> ReportResultAsync(GitHubIssue issue, IssueProcessingResult result, CancellationToken ct)
    {
        var executionId = result.Report.ExecutionId ?? throw new WorkerInfrastructureException("Report execution identity is unavailable.");
        // Any failed GitHub operation is infrastructure failure. Stop and leave the partial state for a human to reconcile.
        switch (result.Kind)
        {
            case IssueOutcomeKind.Succeeded:
                if (history is not null && result.Report.ExecutionId is { } successId &&
                    await history.ReadExecutionAsync(successId, ct) is { CompletionJson: not null } pending)
                {
                    await ReportSuccessAsync(issue, ExecutionCompletion.Read(pending).Report, ExecutionCompletion.Read(pending), ct);
                    var completed = await history.ReadExecutionAsync(successId, ct);
                    return completed is null ? result.Report : ExecutionCompletion.Read(completed).Report;
                }
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.DoneLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + result.Summary, ct);
                await github.CloseAsync(issue.Number, ct);
                result = result with { Report = result.Report with { QuotaAtEnd = await ReadQuotaAsync(final: true, CancellationToken.None) } };
                await telegram.SuccessAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, TelegramCompletion(result.Report), ct);
                _output.Group(() =>
                {
                    _output.IssueCompleted(issue, result.Report.Duration, result.Report.ExecutionId!.Value,
                    result.Report.AttemptNumber, result.Report.RetryOfExecutionId);
                    if (result.Report.QuotaAtEnd is { } quota) _output.Quota(executionId, quota, operationalLog);
                });
                break;
            case IssueOutcomeKind.Blocked:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + result.Summary, ct);
                result = result with { Report = result.Report with { QuotaAtEnd = await ReadQuotaAsync(final: true, CancellationToken.None) } };
                await telegram.BlockedAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, FailureDiagnosticRedactor.Redact(
                        result.Report.HumanInput ?? "A required prerequisite is unavailable.", result.Report.SecretValues) + QuotaNotification(result.Report), ct);
                _output.Group(() =>
                {
                    _output.IssueBlocked(issue, result.Report.Duration, result.Report.ExecutionId!.Value,
                    FailureDiagnosticRedactor.Redact(result.Report.HumanInput ?? "A required prerequisite is unavailable.", result.Report.SecretValues));
                    if (result.Report.QuotaAtEnd is { } quota) _output.Quota(executionId, quota, operationalLog);
                });
                break;
            case IssueOutcomeKind.Failed:
                var message = result.Summary;
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.FailedLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + message, ct);
                result = result with { Report = result.Report with { QuotaAtEnd = await ReadQuotaAsync(final: true, CancellationToken.None) } };
                await telegram.FailedAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, "See the Issue report for validation diagnostics and recovery details." + QuotaNotification(result.Report), ct);
                var recoveryDetails = $"execution {ExecutionFormatting.Display(result.Report.ExecutionId!.Value)}" +
                    (result.Report.RecoveryBranch is null ? " · workspace not preserved · retry/resume unavailable" :
                        $" · workspace preserved on {result.Report.RecoveryBranch} · retry/resume {(result.Report.RetryAvailable ? "available" : "unavailable")}");
                _output.Group(() =>
                {
                    _output.IssueFailed(issue, result.Report.Duration, result.Report.ExecutionId!.Value, recoveryDetails);
                    if (result.Report.QuotaAtEnd is { } quota) _output.Quota(executionId, quota, operationalLog);
                });
                break;
            case IssueOutcomeKind.IntegrationConflict:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.IntegrationConflictLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + result.Summary, ct);
                result = result with { Report = result.Report with { QuotaAtEnd = await ReadQuotaAsync(final: true, CancellationToken.None) } };
                await telegram.FailedAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, FailureDiagnosticRedactor.Redact($"Integration recovery is required after {result.Report.ValidationRepairs.Count(repair => repair.IntegrationRepair)} integration repair attempt(s). " +
                        (result.Report.FinalValidationDiagnostics ?? result.Report.Failure ?? "See the Issue report."), result.Report.SecretValues) + QuotaNotification(result.Report), ct);
                var conflictDetails = $"execution {ExecutionFormatting.Display(result.Report.ExecutionId!.Value)}" +
                    (result.Report.WorkspacePreserved ? $" · implementation workspace preserved on {result.Report.RecoveryBranch} · integration recovery available" :
                        " · implementation workspace preservation could not be verified");
                _output.Group(() =>
                {
                    _output.IssueFailed(issue, result.Report.Duration, result.Report.ExecutionId!.Value, conflictDetails);
                    if (result.Report.QuotaAtEnd is { } quota) _output.Quota(executionId, quota, operationalLog);
                });
                break;
            case IssueOutcomeKind.Superseded:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
                    $"### Execution superseded\n\nExecution `{result.Report.ExecutionId}` did not integrate.\n\n{result.Report.Failure}\n\n" +
                    "### Recovery\n\n- Inspect the later execution or closed Issue before requesting another attempt.\n", ct);
                _operationalLog($"Execution {result.Report.ExecutionId} · Issue #{issue.Number} · superseded; integration skipped and workspace retained.");
                _output.Warning($"Execution {result.Report.ExecutionId} · Issue #{issue.Number} · superseded; integration skipped and workspace retained.");
                result = result with { Report = result.Report with { QuotaAtEnd = await ReadQuotaAsync(final: true, CancellationToken.None) } };
                if (result.Report.QuotaAtEnd is { } supersededQuota) _output.Quota(executionId, supersededQuota, operationalLog);
                break;
        }
        return result.Report;
    }
}
