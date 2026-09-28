using System.Diagnostics;

namespace CodexWorker;

public enum IssueOutcomeKind { Succeeded, Blocked, Failed }
public sealed record IssueProcessingResult(IssueOutcomeKind Kind, IssueExecutionReport Report)
{
    public string Summary => Report.ToMarkdown(Kind);
}

public sealed class Worker(WorkerConfiguration config, IGitHubClient github, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, TelegramNotifier telegram, WorkerConsole? output = null, ExecutionHistoryStore? history = null)
{
    private readonly WorkerConsole _output = output ?? new WorkerConsole();

    public WorkerConfiguration Configuration => config;

    public async Task PrepareForHostAsync(CancellationToken ct)
    {
        if (!File.Exists(config.Codex.InstructionsFile))
            throw new WorkerInfrastructureException($"Configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
        await git.InitializeAsync(ct);
    }

    /// <summary>Checks this project's queue once and processes at most one claimed Issue.</summary>
    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        GitHubIssue? issue;
        try { issue = await github.FindOldestReadyAsync(config.GitHub.ReadyLabel, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        if (issue is null) return false;
        var execution = WorkerExecution.Create(config.Project, config.Git, issue);
        await CreateHistoryAsync(execution, ct);
        if (ct.IsCancellationRequested)
        {
            await TransitionAsync(execution, ExecutionState.Cancelled, CancellationToken.None);
            return false;
        }
        try
        {
            await _output.StopWaitingAsync();
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
            await TransitionAsync(execution, ExecutionState.Claimed, ct);
            _output.IssueStarted(config.Project.Name, issue);
            await telegram.StartingAsync(config.Project.Name, issue, ct);
            var timer = Stopwatch.StartNew();
            var result = await ProcessClaimedIssueAsync(execution, issue, ct);
            timer.Stop();
            await TransitionAsync(execution, ExecutionState.Reporting, ct);
            await ReportResultAsync(issue, result with { Report = result.Report with { Duration = timer.Elapsed, ExecutionId = execution.ExecutionId } }, ct);
            await CompleteHistoryAsync(execution, result.Report with { Duration = timer.Elapsed }, result.Kind switch
            {
                IssueOutcomeKind.Succeeded => ExecutionState.Completed,
                IssueOutcomeKind.Blocked => ExecutionState.Blocked,
                IssueOutcomeKind.Failed => ExecutionState.Failed,
                _ => throw new ArgumentOutOfRangeException()
            }, CancellationToken.None);
            return true;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, "Cancellation interrupted execution.");
            throw new WorkerInfrastructureException("Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
        }
        catch (Exception ex)
        {
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, ex.Message);
            throw;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _output.Startup(config.Project.Name, config.Project.Repository);
        var safelyIdle = false;
        WorkerExecution? activeExecution = null;
        try
        {
            if (!File.Exists(config.Codex.InstructionsFile))
                throw new WorkerInfrastructureException($"Configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
            await git.InitializeAsync(ct);
            safelyIdle = true;
            await _output.RunProgressAsync("Codex preflight", async () =>
            {
                await codex.PreflightAsync(ct);
                return true;
            }, failureDetail: ex => ex.Message, ct: ct);
            await telegram.StartedAsync(config.Project.Name, ct);
            _output.Started();
            while (!ct.IsCancellationRequested)
            {
                _output.Waiting();
                var issue = await github.FindOldestReadyAsync(config.GitHub.ReadyLabel, ct);
                if (issue is null) { await DelayAsync(ct); continue; }
                var execution = activeExecution = WorkerExecution.Create(config.Project, config.Git, issue);
                await CreateHistoryAsync(execution, ct);
                await _output.StopWaitingAsync();
                safelyIdle = false;
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
                await TransitionAsync(execution, ExecutionState.Claimed, ct);
                _output.IssueStarted(issue);
                await telegram.StartingAsync(config.Project.Name, issue, ct);
                var issueTimer = Stopwatch.StartNew();
                var result = await ProcessClaimedIssueAsync(execution, issue, ct);
                issueTimer.Stop();
                await TransitionAsync(execution, ExecutionState.Reporting, ct);
                await ReportResultAsync(issue, result with { Report = result.Report with { Duration = issueTimer.Elapsed, ExecutionId = execution.ExecutionId } }, ct);
                await CompleteHistoryAsync(execution, result.Report with { Duration = issueTimer.Elapsed }, result.Kind switch
                {
                    IssueOutcomeKind.Succeeded => ExecutionState.Completed,
                    IssueOutcomeKind.Blocked => ExecutionState.Blocked,
                    IssueOutcomeKind.Failed => ExecutionState.Failed,
                    _ => throw new ArgumentOutOfRangeException()
                }, CancellationToken.None);
                activeExecution = null;
                safelyIdle = true;
            }
            await _output.StopWaitingAsync();
            _output.Shutdown();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            await _output.StopWaitingAsync(finalizeLine: true);
            if (activeExecution is { IsTerminal: false }) await RecordInfrastructureFailureAsync(activeExecution, "Cancellation interrupted execution.");
            if (safelyIdle)
            {
                _output.Shutdown("Worker stopped.");
                await telegram.StoppedAsync(config.Project.Name, CancellationToken.None);
            }
            else
            {
                var infrastructure = new WorkerInfrastructureException(
                    "Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
                _output.InfrastructureFailure($"Infrastructure failure: {infrastructure.Message}");
                await telegram.CriticalAsync(config.Project.Name, infrastructure.Message, CancellationToken.None);
                throw infrastructure;
            }
        }
        catch (Exception ex)
        {
            await _output.StopWaitingAsync();
            if (activeExecution is { IsTerminal: false }) await RecordInfrastructureFailureAsync(activeExecution, ex.Message);
            var infrastructure = ex as WorkerInfrastructureException ??
                new WorkerInfrastructureException($"Unexpected worker failure; queue processing stopped: {ex.Message}", ex);
            _output.InfrastructureFailure($"Infrastructure failure: {infrastructure.Message}");
            await telegram.CriticalAsync(config.Project.Name, infrastructure.Message, CancellationToken.None);
            throw infrastructure;
        }
        finally
        {
            await _output.StopWaitingAsync();
        }
    }

    private async Task<IssueProcessingResult> ProcessClaimedIssueAsync(WorkerExecution execution, GitHubIssue issue, CancellationToken ct)
    {
        await TransitionAsync(execution, ExecutionState.Preparing, ct);
        await git.StartIssueAsync(execution.ExecutionId, issue, ct);
        await TransitionAsync(execution, ExecutionState.Implementing, ct);
        var outcome = await _output.RunProgressAsync("Codex working", () =>
            codex.RunAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, issue, ct),
            completion: x => x.Status, succeeded: x => x.Status == "success",
            warning: x => x.Status == "blocked", ct: ct);
        await git.VerifyCodexStateAsync(ct);
        var implementationSummary = outcome.Summary;
        var repairs = new List<ValidationRepairRecord>();
        await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null), ct);
        if (outcome.Status == "blocked") return await CleanupOutcomeAsync(execution, issue, IssueOutcomeKind.Blocked,
            new IssueExecutionReport(implementationSummary, repairs, HumanInput: outcome.Question), ct);
        if (outcome.Status == "failed") return await CleanupOutcomeAsync(execution, issue, IssueOutcomeKind.Failed,
            new IssueExecutionReport(implementationSummary, repairs, Failure: outcome.Summary), ct);

        var repairAttempts = 0;
        while (true)
        {
            await TransitionAsync(execution, ExecutionState.Validating, ct);
            var validationResult = await _output.RunProgressAsync("Validation", () =>
                validation.RunAsync(config.Validation.Commands, git.ExecutionDirectory, ct),
                x => x.Succeeded ? "passed" : $"command {x.Failure!.CommandNumber} failed",
                x => x.Succeeded, ct: ct);
            if (validationResult.Succeeded) break;

            await git.VerifyCodexStateAsync(ct);
            var failure = validationResult.Failure!;
            if (repairAttempts >= config.Validation.MaxFixAttempts)
                return await CleanupOutcomeAsync(execution, issue, IssueOutcomeKind.Failed,
                    new IssueExecutionReport(implementationSummary, repairs, FinalValidationFailure: failure.Command,
                        Failure: $"Validation failed after {repairAttempts} repair attempt(s)."), ct);

            repairAttempts++;
            await TransitionAsync(execution, ExecutionState.Repairing, ct);
            outcome = await _output.RunProgressAsync($"Repair {repairAttempts}/{config.Validation.MaxFixAttempts}", () =>
                codex.RepairAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, issue,
                    failure, repairAttempts, config.Validation.MaxFixAttempts, ct),
                completion: x => x.Status, succeeded: x => x.Status == "success",
                warning: x => x.Status == "blocked", ct: ct);
            await git.VerifyCodexStateAsync(ct);
            if (outcome.Status == "blocked")
            {
                repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                    outcome.Summary, false));
                return await CleanupOutcomeAsync(execution, issue, IssueOutcomeKind.Blocked,
                    new IssueExecutionReport(implementationSummary, repairs, HumanInput: outcome.Question), ct);
            }
            if (outcome.Status == "failed")
            {
                repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                    outcome.Summary, false));
                return await CleanupOutcomeAsync(execution, issue, IssueOutcomeKind.Failed,
                    new IssueExecutionReport(implementationSummary, repairs, Failure: outcome.Summary), ct);
            }
            // The next validation result determines whether this repair passed. Store its independent summary now.
            repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                outcome.Summary, false));
            await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null), ct);
        }

        await git.VerifyCodexStateAsync(ct);
        await TransitionAsync(execution, ExecutionState.Integrating, ct);
        var integration = await _output.RunProgressAsync("Integrating", () => git.CommitAndIntegrateAsync(issue, ct),
            completion: x => x.HasChanges ? "complete" : "no changes", ct: ct);
        if (repairs.Count > 0) repairs[^1] = repairs[^1] with { PassedAfterRepair = true };
        return new IssueProcessingResult(IssueOutcomeKind.Succeeded,
            new IssueExecutionReport(implementationSummary, repairs, Integration: integration));
    }

    private async Task<IssueProcessingResult> CleanupOutcomeAsync(WorkerExecution execution, GitHubIssue issue, IssueOutcomeKind kind, IssueExecutionReport report, CancellationToken ct)
    {
        await git.DiscardUncommittedIssueChangesAsync(ct);
        await SaveHistoryAsync(CreateEntry(execution, report, null, null), ct);
        return new IssueProcessingResult(kind, report);
    }

    private Task CreateHistoryAsync(WorkerExecution execution, CancellationToken ct) =>
        history is null ? Task.CompletedTask : history.CreateAsync(CreateEntry(execution, null, null, null), ct);

    private Task TransitionAsync(WorkerExecution execution, ExecutionState state, CancellationToken ct)
    {
        execution.TransitionTo(state);
        return history is null ? Task.CompletedTask : history.UpdateAsync(CreateEntry(execution, null, null, null), ct);
    }

    private async Task CompleteHistoryAsync(WorkerExecution execution, IssueExecutionReport report, ExecutionState state,
        CancellationToken ct)
    {
        execution.TransitionTo(state);
        try { await SaveHistoryAsync(CreateEntry(execution, report, null, null), ct); }
        catch (WorkerInfrastructureException ex) { _output.Warning($"Execution completed, but its final history details could not be saved: {ex.Message}"); }
    }

    private async Task RecordInfrastructureFailureAsync(WorkerExecution execution, string reason)
    {
        if (!execution.IsTerminal) execution.TransitionTo(ExecutionState.InfrastructureFailure);
        try { await SaveHistoryAsync(CreateEntry(execution, null, null, reason), CancellationToken.None); }
        catch (WorkerInfrastructureException) { /* Preserve the original infrastructure failure. The existing row remains incomplete. */ }
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
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task ReportResultAsync(GitHubIssue issue, IssueProcessingResult result, CancellationToken ct)
    {
        // Any failed GitHub operation is infrastructure failure. Stop and leave the partial state for a human to reconcile.
        switch (result.Kind)
        {
            case IssueOutcomeKind.Succeeded:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.DoneLabel, ct);
                await github.CommentAsync(issue.Number, result.Summary, ct);
                await github.CloseAsync(issue.Number, ct);
                await telegram.SuccessAsync(config.Project.Name, issue, result.Report.Duration, TelegramCompletion(result.Report), ct);
                _output.IssueCompleted(config.Project.Name, issue, result.Report.Duration, ShortCompletion(result.Summary));
                break;
            case IssueOutcomeKind.Blocked:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, result.Summary, ct);
                await telegram.BlockedAsync(config.Project.Name, issue, result.Report.Duration, result.Report.HumanInput ?? "Human input is required.", ct);
                _output.IssueBlocked(config.Project.Name, issue, result.Report.Duration, result.Report.HumanInput ?? "Human input is required.");
                break;
            case IssueOutcomeKind.Failed:
                var message = result.Summary;
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.FailedLabel, ct);
                await github.CommentAsync(issue.Number, message, ct);
                await telegram.FailedAsync(config.Project.Name, issue, result.Report.Duration, Limit(message, 1400), ct);
                _output.IssueFailed(config.Project.Name, issue, result.Report.Duration, FirstLine(message));
                break;
        }
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        // Compatibility loop remains available to existing workflow tests; production polling is owned by WorkerHost.
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    private static string ShortCompletion(string summary)
    {
        return FirstLine(summary);
    }
    private static string TelegramCompletion(IssueExecutionReport report)
    {
        var details = new List<string>();
        if (report.Integration is not null) details.Add(report.Integration.Summary);
        if (!string.IsNullOrWhiteSpace(report.ImplementationSummary)) details.Add($"Codex summary: {report.ImplementationSummary}");
        return string.Join("\n\n", details);
    }
    private static string FirstLine(string value) => value.Split('\n', 2)[0];
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 20)] + " … [truncated]";
}
