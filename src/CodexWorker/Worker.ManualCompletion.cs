namespace CodexWorker;

using CodexProvisioning;

public sealed partial class Worker
{
    /// <summary>Operator-confirmed completion of an execution whose reporting failed after integration.
    /// The exact validated commit must already be on the authoritative remote base. This never
    /// integrates, validates or runs an engine: it leaves the Issue closed with only the done
    /// label, posts the success report once, and reports the execution completed to the Server.</summary>
    internal async Task<ExecutionMaintenanceReport> CompleteManuallyAsync(ExecutionMaintenanceCommand command,
        Func<CancellationToken, Task<bool>> authorize, CancellationToken ct)
    {
        var request = command.Request;
        if (!ExecutionMaintenanceProtocol.Valid(request) || request.Action != ExecutionMaintenanceProtocol.ManualCompletion ||
            command.Status != "running" || command.DeadlineUtc is null || command.DeadlineUtc <= _clock.GetUtcNow())
            return new("refused", "maintenance-not-current", []);
        if (history is null) return new("refused", "worker-record-missing", []);
        if (serverSettings is not { Enabled: true }) return new("refused", "server-authority-required", []);
        await _repositoryGate.WaitAsync(ct);
        try
        {
            ExecutionMaintenanceReport Refuse(string reason) => new("refused", reason, []);
            var entry = await history.ReadExecutionAsync(request.WorkerExecutionId ?? Guid.Empty, ct);
            if (entry is null) return Refuse("worker-record-missing");
            if (entry.ServerExecutionId != request.ServerExecutionId || entry.AssignmentId != request.AssignmentId ||
                entry.OwnershipGeneration != request.Generation || entry.Project != config.Project.Name ||
                entry.Repository != config.Project.Repository)
                return Refuse("worker-identity-mismatch");
            if (entry.CompletedAtUtc is null || entry.State == "Completed") return Refuse("failed-terminal-execution-required");
            if (entry.ValidationOutcome != "passed") return Refuse("validation-not-passed");
            var others = await history.ReadAllAsync(ct);
            if (others.Any(other => other.ExecutionId != entry.ExecutionId && other.Project == entry.Project &&
                other.Repository == entry.Repository && other.IssueNumber == entry.IssueNumber &&
                (other.CompletedAtUtc is null || other.AttemptNumber > entry.AttemptNumber || other.State == "Completed")))
                return Refuse("issue-execution-ownership-changed");
            if (!await authorize(ct)) return Refuse("server-authority-rejected");
            // The only proof that matters: the exact recorded integration commit is on the remote base.
            if (entry.CommitSha is null || !await git.VerifyRemoteIntegrationAsync(entry, ct))
                return Refuse("validated-commit-missing-on-remote-base");
            var issue = await github.GetCompletionIssueAsync(entry.IssueNumber, ct);
            if (issue is null || issue.Number != entry.IssueNumber) return Refuse("completion-issue-unavailable");

            var state = await github.ReadIssueStateAsync(entry.IssueNumber, ct);
            foreach (var label in new[] { config.GitHub.ReadyLabel, config.GitHub.FailedLabel, config.GitHub.BlockedLabel,
                         config.GitHub.IntegrationConflictLabel, config.GitHub.IntegrationRecoveryLabel }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                if (state.Labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                    await github.RemoveLabelAsync(entry.IssueNumber, label, ct);
            if (!state.Labels.Contains(config.GitHub.DoneLabel, StringComparer.OrdinalIgnoreCase))
                await github.ReplaceLabelAsync(entry.IssueNumber, config.GitHub.WorkingLabel, config.GitHub.DoneLabel, ct);
            else if (state.Labels.Contains(config.GitHub.WorkingLabel, StringComparer.OrdinalIgnoreCase))
                await github.RemoveLabelAsync(entry.IssueNumber, config.GitHub.WorkingLabel, ct);

            // The marker keeps the report single even if this operation is requested again.
            var report = entry.CompletionJson is null ? null : TryReadCompletion(entry)?.Report;
            var body = IssueFormatting.ReportHeading(issue) + (report is not null
                ? report.ToMarkdown(IssueOutcomeKind.Succeeded) + "\n\n"
                : $"### Execution\n\nExecution `[{ExecutionFormatting.ShortId(entry.ExecutionId)}]` (`{entry.ExecutionId}`).\n\n") +
                $"### Manual reconciliation\n\nAn operator confirmed this completion. Commit `{entry.CommitSha}` is on `{entry.BaseBranch}`; the original success report could not be delivered.";
            await github.EnsureSuccessCommentAsync(entry.IssueNumber, entry.ExecutionId, body, ct);
            if ((await github.ReadIssueStateAsync(entry.IssueNumber, ct)).IsOpen) await github.CloseAsync(entry.IssueNumber, ct);

            if (!await authorize(ct)) return Refuse("server-authority-rejected");
            await _registration.ReportExecutionAsync(serverSettings, entry with { State = "Completed" }, "Completed", null,
                request.Generation ?? 0, ct, request.OperationId);
            await history.MarkManuallyCompletedAsync(entry.ExecutionId, ct);
            await history.RecordMaintenanceReportDispositionAsync(entry.ExecutionId, "acknowledged", ct);
            _operationalLog($"Execution {entry.ExecutionId} · Issue #{entry.IssueNumber} · manually completed by operator · commit {entry.CommitSha} verified on {entry.BaseBranch}.");
            return new("succeeded", "manually-completed", [new(entry.ExecutionId, entry.ServerExecutionId, entry.AssignmentId,
                entry.OwnershipGeneration, "Completed", ExecutionHistoryStore.ManuallyCompletedState, "acknowledged",
                entry.Project, entry.IssueNumber)]);
        }
        finally { _repositoryGate.Release(); }
    }

    private static ExecutionCompletion? TryReadCompletion(ExecutionHistoryEntry entry)
    {
        try { return ExecutionCompletion.Read(entry); }
        catch (WorkerInfrastructureException) { return null; }
    }
}
