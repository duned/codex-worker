using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexWorker;

/// <summary>Validated integration handoff and individually durable completion checkpoints.</summary>
internal sealed record ExecutionCompletion(IssueExecutionReport Report, string IssueFingerprint, string PolicyFingerprint,
    bool RemoteConfirmed = false, bool LabelsReported = false, bool CommentReported = false,
    bool IssueClosed = false, bool NotificationAttempted = false, bool CleanupCompleted = false, bool Finished = false)
{
    internal static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string IssueIntent(GitHubIssue issue) => Fingerprint(JsonSerializer.Serialize(new { issue.Title, issue.Body }));
    internal static string Policy(WorkerConfiguration config) => Fingerprint(JsonSerializer.Serialize(new
    {
        config.Project.Repository, config.Project.Directory, config.Git, config.GitHub
    }));
    internal static IssueExecutionReport Sanitize(IssueExecutionReport report, IReadOnlyList<string> secrets)
    {
        string? Redact(string? value) => value is null ? null : FailureDiagnosticRedactor.Redact(value, secrets);
        return report with
        {
            SecretValues = null, ImplementationSummary = Redact(report.ImplementationSummary),
            ValidationRepairs = report.ValidationRepairs.Select(repair => repair with
            {
                FailedCommand = Redact(repair.FailedCommand) ?? "", RepairSummary = Redact(repair.RepairSummary),
                ValidationBeforeRepair = Redact(repair.ValidationBeforeRepair), ValidationAfterRepair = Redact(repair.ValidationAfterRepair)
            }).ToArray()
        };
    }
    internal static ExecutionCompletion Read(ExecutionHistoryEntry entry)
    {
        try
        {
            var completion = JsonSerializer.Deserialize<ExecutionCompletion>(entry.CompletionJson ?? "null");
            if (completion?.Report is not { ValidationRepairs: not null, Integration: { HasChanges: true } } report ||
                entry.ExecutionId == Guid.Empty || report.ExecutionId != entry.ExecutionId ||
                !GitRepository.IsCommitId(report.Integration.CommitSha) || report.Integration.CommitSha != entry.CommitSha ||
                report.Integration.IntegrationBranch != entry.BaseBranch || report.Integration.ResourceExecutionId is null ||
                report.Integration.ResourceExecutionId == Guid.Empty || report.Integration.ResourceAttemptNumber is not > 0 ||
                string.IsNullOrWhiteSpace(completion.IssueFingerprint) || string.IsNullOrWhiteSpace(completion.PolicyFingerprint) ||
                completion.CommentReported && !completion.LabelsReported || completion.IssueClosed && !completion.CommentReported ||
                completion.NotificationAttempted && !completion.IssueClosed || completion.CleanupCompleted && !completion.NotificationAttempted ||
                completion.Finished && (!completion.CleanupCompleted || !completion.RemoteConfirmed))
                throw new WorkerInfrastructureException("Validated integration provenance is missing or inconsistent; manual reconciliation is required.");
            return completion;
        }
        catch (JsonException)
        {
            throw new WorkerInfrastructureException("Validated integration provenance is corrupt; manual reconciliation is required.");
        }
    }
}

internal sealed class CompletionReconciliationRequiredException(string project, Guid executionId, int issueNumber,
    string message, Exception inner) : WorkerInfrastructureException(message, inner)
{
    internal string Project { get; } = project;
    internal Guid ExecutionId { get; } = executionId;
    internal int IssueNumber { get; } = issueNumber;
}

public sealed partial class Worker
{
    // Caller holds the repository gate. This never claims an Issue or runs implementation.
    internal async Task ResumeCompletionAsync(ExecutionHistoryEntry entry, CancellationToken ct)
    {
        if (history is null) throw new WorkerInfrastructureException("Completion history is unavailable.");
        if (entry.Project != config.Project.Name || entry.Repository != config.Project.Repository)
            throw new WorkerInfrastructureException("Completion execution belongs to another project or repository.");
        ExecutionCompletion completion;
        try { completion = ExecutionCompletion.Read(entry); }
        catch (WorkerInfrastructureException)
        {
            // Old records cannot authorize completion effects. A terminal Issue can only
            // acknowledge them for scheduling; retain history and all recovery resources.
            var remote = await github.ReadIssueStateAsync(entry.IssueNumber, ct);
            bool Has(string label) => remote.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
            if (remote.IsOpen || !Has(config.GitHub.DoneLabel) || Has(config.GitHub.ReadyLabel) ||
                Has(config.GitHub.WorkingLabel) || Has(config.GitHub.FailedLabel) || Has(config.GitHub.BlockedLabel) ||
                Has(config.GitHub.IntegrationConflictLabel) || Has(config.GitHub.IntegrationRecoveryLabel))
                throw;
            _operationalLog($"Execution {entry.ExecutionId} · Issue #{entry.IssueNumber} · legacy completion record acknowledged because authoritative Issue state is closed with the configured done label · no completion effects replayed.");
            return;
        }
        if (completion.Finished)
        {
            if (entry.State != "Completed") await history.SaveCompletionAsync(entry.ExecutionId, completion, true, ct);
            await FinishCompletionLineageAsync(entry, ct);
            return;
        }
        var integration = completion.Report.Integration;
        if (entry.Project != config.Project.Name || entry.Repository != config.Project.Repository ||
            entry.BaseBranch != config.Git.BaseBranch || completion.PolicyFingerprint != ExecutionCompletion.Policy(config) ||
            completion.Report.ExecutionId != entry.ExecutionId || integration is not { HasChanges: true } ||
            integration.CommitSha != entry.CommitSha || integration.IntegrationBranch != entry.BaseBranch ||
            entry.ValidationOutcome != "passed" || !config.Git.AutoMerge ||
            entry.State is not ("Integrating" or "Reporting" or "InfrastructureFailure" or "Cancelled" or "Completed"))
            throw new WorkerInfrastructureException("Integration identity, validation or completion policy is incompatible; manual reconciliation is required.");
        // A retained managed execution is not a lease. Do not infer delivery authority after restart.
        if (entry.ServerExecutionId is not null || serverSettings?.Enabled == true)
            throw new WorkerInfrastructureException("Managed completion requires current Server ownership of the original execution; manual reconciliation is required.");
        var others = await history.ReadAllAsync(ct);
        if (others.Any(other => other.ExecutionId != entry.ExecutionId && other.Project == entry.Project &&
            other.Repository == entry.Repository && other.IssueNumber == entry.IssueNumber &&
            (other.CompletedAtUtc is null || other.AttemptNumber >= entry.AttemptNumber || other.State == "Completed")))
            throw new WorkerInfrastructureException("Issue execution ownership changed; manual reconciliation is required.");
        var resourceOwner = integration.ResourceExecutionId == entry.ExecutionId ? entry :
            integration.ResourceExecutionId == entry.RetryOfExecutionId
                ? others.SingleOrDefault(other => other.ExecutionId == entry.RetryOfExecutionId) : null;
        if (resourceOwner is null || integration.ResourceAttemptNumber != resourceOwner.AttemptNumber ||
            resourceOwner.Project != entry.Project || resourceOwner.Repository != entry.Repository ||
            resourceOwner.IssueNumber != entry.IssueNumber || resourceOwner.FeatureBranch != entry.FeatureBranch)
            throw new WorkerInfrastructureException("Integration resource ownership or attempt lineage is inconsistent; manual reconciliation is required.");
        if (!await git.VerifyRemoteIntegrationAsync(entry, ct))
            throw new WorkerInfrastructureException("Authoritative remote base is missing the exact validated integration commit. Verify/retry only the original push manually; completion remains pending.");
        _operationalLog($"Execution {entry.ExecutionId} · verified exact validated integration commit on authoritative remote base · resuming pending completion reporting without replaying Codex.");
        completion = completion with { RemoteConfirmed = true };
        await history.SaveCompletionAsync(entry.ExecutionId, completion, false, ct);
        var issue = await github.GetCompletionIssueAsync(entry.IssueNumber, ct)
            ?? throw new WorkerInfrastructureException("Completion Issue is unavailable; manual reconciliation is required.");
        await ReportSuccessAsync(issue, completion.Report, completion, ct, repositoryGateHeld: true);
        var completed = await history.ReadExecutionAsync(entry.ExecutionId, ct)
            ?? throw new WorkerInfrastructureException("Completion history disappeared.");
        await history.SaveCompletionAsync(entry.ExecutionId, ExecutionCompletion.Read(completed), true, ct);
        await FinishCompletionLineageAsync(entry, ct);
        await ReportServerAsync(completed with { State = "Completed" }, ExecutionState.Completed, ct);
        _operationalLog($"Execution {entry.ExecutionId} · completed reconciliation without replaying Codex.");
    }

    private async Task FinishCompletionLineageAsync(ExecutionHistoryEntry entry, CancellationToken ct)
    {
        if (history is null || entry.RetryOfExecutionId is not { } sourceId) return;
        var source = await history.ReadExecutionAsync(sourceId, ct);
        if (source?.IntegrationRecoveryClaim == entry.ExecutionId && source.Project == entry.Project &&
            source.Repository == entry.Repository && source.IssueNumber == entry.IssueNumber)
        {
            await history.UpdateRecoveryAsync(sourceId, "integration-recovered", ct);
            await history.FinishIntegrationRecoveryAsync(sourceId, entry.ExecutionId, null, ct);
        }
    }

    private async Task ReportSuccessAsync(GitHubIssue issue, IssueExecutionReport report,
        ExecutionCompletion completion, CancellationToken ct, bool repositoryGateHeld = false)
    {
        if (history is null || report.ExecutionId is not { } executionId)
            throw new WorkerInfrastructureException("Completion execution identity is unavailable.");
        async Task<GitHubIssueState> ReadStateAsync()
        {
            var current = await github.GetCompletionIssueAsync(issue.Number, ct)
                ?? throw new WorkerInfrastructureException("Completion Issue is unavailable; manual reconciliation is required.");
            if (current.Number != issue.Number || ExecutionCompletion.IssueIntent(current) != completion.IssueFingerprint)
                throw new WorkerInfrastructureException("Issue title or body changed since validation; manual reconciliation is required.");
            var state = await github.ReadIssueStateAsync(issue.Number, ct);
            bool Has(string label) => state.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
            if (Has(config.GitHub.ReadyLabel) || Has(config.GitHub.FailedLabel) || Has(config.GitHub.BlockedLabel) ||
                Has(config.GitHub.IntegrationConflictLabel) ||
                Has(config.GitHub.WorkingLabel) == Has(config.GitHub.DoneLabel) ||
                completion.LabelsReported && !Has(config.GitHub.DoneLabel) ||
                completion.IssueClosed && state.IsOpen || !state.IsOpen && (!Has(config.GitHub.DoneLabel) || !completion.CommentReported))
                throw new WorkerInfrastructureException("Issue state conflicts with pending success reporting; manual reconciliation is required.");
            return state;
        }
        var state = await ReadStateAsync();
        async Task SaveAsync() => await history.SaveCompletionAsync(executionId, completion, false, ct);
        if (!state.Labels.Contains(config.GitHub.DoneLabel, StringComparer.OrdinalIgnoreCase))
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.DoneLabel, ct);
        completion = completion with { LabelsReported = true };
        await SaveAsync();
        await ReadStateAsync();
        // Always inspect the marker: a lost comment response must not append a second report.
        await github.EnsureSuccessCommentAsync(issue.Number, executionId,
            IssueFormatting.ReportHeading(issue) + report.ToMarkdown(IssueOutcomeKind.Succeeded), ct, allowCreate: !completion.CommentReported);
        completion = completion with { CommentReported = true };
        await SaveAsync();
        state = await ReadStateAsync();
        if (state.IsOpen) await github.CloseAsync(issue.Number, ct);
        completion = completion with { IssueClosed = true };
        await SaveAsync();
        if (!completion.NotificationAttempted)
        {
            // Telegram has no idempotency key or delivery query. Persist before sending: at most
            // one attempt, with possible omission if the Worker stops between checkpoint and send.
            completion = completion with { NotificationAttempted = true };
            await SaveAsync();
            await telegram.SuccessAsync(config.Project.Name, config.Project.Repository, issue, report.Duration,
                executionId, TelegramCompletion(report), ct);
        }
        if (!completion.CleanupCompleted)
        {
            var entry = await history.ReadExecutionAsync(executionId, ct)
                ?? throw new WorkerInfrastructureException("Completion history disappeared.");
            if (!repositoryGateHeld) await _repositoryGate.WaitAsync(ct);
            try { await git.FinishIntegratedExecutionAsync(entry, ct); }
            finally { if (!repositoryGateHeld) _repositoryGate.Release(); }
            completion = completion with { CleanupCompleted = true };
            await SaveAsync();
        }
        // Normal execution still uses its established terminal transition/history path.
        completion = completion with { Finished = true };
        await history.SaveCompletionAsync(executionId, completion, false, ct);
        _output.IssueCompleted(issue, report.Duration, executionId, report.AttemptNumber, report.RetryOfExecutionId);
    }
}
