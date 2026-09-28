using System.Text.RegularExpressions;

namespace CodexWorker;

/// <summary>Explicit identity and Issue input for one execution attempt.</summary>
public sealed record ExecutionContext(WorkerExecution Execution, GitHubIssue Issue);

/// <summary>
/// Runs the repository workspace, Codex, validation, repair, and integration lifecycle for one execution.
/// Repository mutations that share Git metadata are serialized per repository while implementation and
/// validation use the independent execution worktree concurrently.
/// </summary>
public sealed class ExecutionRunner(WorkerConfiguration config, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, WorkerConsole output, ExecutionHistoryStore? history = null, SemaphoreSlim? repositoryGate = null)
{
    private readonly SemaphoreSlim _repositoryGate = repositoryGate ?? new SemaphoreSlim(1, 1);
    public async Task<IssueProcessingResult> RunAsync(ExecutionContext context, CancellationToken ct)
    {
        var execution = context.Execution;
        var issue = context.Issue;
        try
        {
            await TransitionAsync(execution, ExecutionState.Preparing, ct);
            await _repositoryGate.WaitAsync(ct);
            try { await git.StartIssueAsync(execution.ExecutionId, issue, ct); }
            finally { _repositoryGate.Release(); }
            await TransitionAsync(execution, ExecutionState.Implementing, ct);
            var outcome = await output.RunProgressAsync(TaskLabel(issue, "Codex working"), () =>
                codex.RunAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, issue, ct),
                completion: x => x.Status, succeeded: x => x.Status == "success",
                warning: x => x.Status == "blocked", ct: ct);
            await git.VerifyCodexStateAsync(ct);
            var implementationSummary = outcome.Summary;
            var repairs = new List<ValidationRepairRecord>();
            await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null), ct);
            if (outcome.Status == "blocked") return await CleanupOutcomeAsync(context, IssueOutcomeKind.Blocked,
                new IssueExecutionReport(implementationSummary, repairs, HumanInput: outcome.Question), ct);
            if (outcome.Status == "failed") return await CleanupOutcomeAsync(context, IssueOutcomeKind.Failed,
                new IssueExecutionReport(implementationSummary, repairs, Failure: outcome.Summary), ct);

            var repairAttempts = 0;
            while (true)
            {
                await TransitionAsync(execution, ExecutionState.Validating, ct);
                var validationResult = await output.RunProgressAsync(TaskLabel(issue, "Validation"), () =>
                    validation.RunAsync(config.Validation.Commands, git.ExecutionDirectory, ct),
                    x => x.Succeeded ? "passed" : $"command {x.Failure!.CommandNumber} failed",
                    x => x.Succeeded, ct: ct);
                if (validationResult.Succeeded) break;

                await git.VerifyCodexStateAsync(ct);
                var failure = validationResult.Failure!;
                if (repairAttempts >= config.Validation.MaxFixAttempts)
                    return await CleanupOutcomeAsync(context, IssueOutcomeKind.Failed,
                        new IssueExecutionReport(implementationSummary, repairs, FinalValidationFailure: failure.Command,
                            Failure: $"Validation failed after {repairAttempts} repair attempt(s)."), ct);

                repairAttempts++;
                await TransitionAsync(execution, ExecutionState.Repairing, ct);
                outcome = await output.RunProgressAsync(TaskLabel(issue, $"Repair {repairAttempts}/{config.Validation.MaxFixAttempts}"), () =>
                    codex.RepairAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, issue,
                        failure, repairAttempts, config.Validation.MaxFixAttempts, ct),
                    completion: x => x.Status, succeeded: x => x.Status == "success",
                    warning: x => x.Status == "blocked", ct: ct);
                await git.VerifyCodexStateAsync(ct);
                if (outcome.Status is "blocked" or "failed")
                {
                    repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                        outcome.Summary, false));
                    return await CleanupOutcomeAsync(context,
                        outcome.Status == "blocked" ? IssueOutcomeKind.Blocked : IssueOutcomeKind.Failed,
                        new IssueExecutionReport(implementationSummary, repairs,
                            HumanInput: outcome.Status == "blocked" ? outcome.Question : null,
                            Failure: outcome.Status == "failed" ? outcome.Summary : null), ct);
                }
                repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                    outcome.Summary, false));
                await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null), ct);
            }

            await git.VerifyCodexStateAsync(ct);
            await TransitionAsync(execution, ExecutionState.Integrating, ct);
            await _repositoryGate.WaitAsync(ct);
            GitIntegrationResult integration;
            try
            {
                integration = await output.RunProgressAsync(TaskLabel(issue, "Integrating"), () => git.CommitAndIntegrateAsync(issue, ct),
                    completion: x => x.HasChanges ? "complete" : "no changes", ct: ct);
            }
            finally { _repositoryGate.Release(); }
            if (repairs.Count > 0) repairs[^1] = repairs[^1] with { PassedAfterRepair = true };
            return new IssueProcessingResult(IssueOutcomeKind.Succeeded,
                new IssueExecutionReport(implementationSummary, repairs, Integration: integration));
        }
        catch (Exception ex)
        {
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, ex.Message);
            throw;
        }
    }

    private async Task<IssueProcessingResult> CleanupOutcomeAsync(ExecutionContext context, IssueOutcomeKind kind,
        IssueExecutionReport report, CancellationToken ct)
    {
        await _repositoryGate.WaitAsync(ct);
        try { await git.DiscardUncommittedIssueChangesAsync(ct); }
        finally { _repositoryGate.Release(); }
        await SaveHistoryAsync(CreateEntry(context.Execution, report, null, null), ct);
        return new IssueProcessingResult(kind, report);
    }

    private Task TransitionAsync(WorkerExecution execution, ExecutionState state, CancellationToken ct)
    {
        execution.TransitionTo(state);
        return history is null ? Task.CompletedTask : history.UpdateAsync(CreateEntry(execution, null, null, null), ct);
    }

    private async Task RecordInfrastructureFailureAsync(WorkerExecution execution, string reason)
    {
        if (!execution.IsTerminal) execution.TransitionTo(ExecutionState.InfrastructureFailure);
        try { await SaveHistoryAsync(CreateEntry(execution, null, null, reason), CancellationToken.None); }
        catch (WorkerInfrastructureException) { /* Preserve the original failure; the existing row remains incomplete. */ }
    }

    private Task SaveHistoryAsync(ExecutionHistoryEntry entry, CancellationToken ct) =>
        history is null ? Task.CompletedTask : history.UpdateAsync(entry, ct);

    private static ExecutionHistoryEntry CreateEntry(WorkerExecution execution, IssueExecutionReport? report, TimeSpan? duration, string? failure) =>
        new(execution.ExecutionId, execution.Project, execution.Repository, execution.IssueNumber, execution.IssueTitle,
            execution.FeatureBranch, execution.BaseBranch, execution.StartedAtUtc,
            execution.IsTerminal ? DateTimeOffset.UtcNow : null, execution.State.ToString(),
            execution.IsTerminal ? (long)(duration ?? (DateTimeOffset.UtcNow - execution.StartedAtUtc)).TotalMilliseconds : null,
            report?.ImplementationSummary,
            report?.Integration is not null ? "passed" : report?.FinalValidationFailure is not null ? $"failed: {report.FinalValidationFailure}" :
                report is { ValidationRepairs.Count: > 0 } ? "failed or interrupted" : null,
            report?.ValidationRepairs.Count ?? 0, report?.ValidationRepairs ?? [],
            report?.Integration?.CommitSha ?? Extract(report?.Integration?.Summary, "Committed as `([^`]+)`"),
            report?.Integration?.IntegrationBranch ?? Extract(report?.Integration?.Summary, "Merged into `([^`]+)`"),
            report?.Integration is { HasChanges: true } integration ? integration.CompletedBranch : null,
            failure ?? report?.Failure ?? report?.HumanInput);

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private string TaskLabel(GitHubIssue issue, string stage) => $"{config.Project.Name} · #{issue.Number} · {stage}";
}
