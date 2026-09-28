using System.Diagnostics;

namespace CodexWorker;

public enum IssueOutcomeKind { Succeeded, Blocked, Failed }
public sealed record IssueProcessingResult(IssueOutcomeKind Kind, IssueExecutionReport Report)
{
    public string Summary => Report.ToMarkdown(Kind);
}

public sealed class Worker(WorkerConfiguration config, IGitHubClient github, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, TelegramNotifier telegram, WorkerConsole? output = null)
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
        if (ct.IsCancellationRequested)
        {
            execution.TransitionTo(ExecutionState.Cancelled);
            return false;
        }
        try
        {
            await _output.StopWaitingAsync();
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
            execution.TransitionTo(ExecutionState.Claimed);
            _output.IssueStarted(config.Project.Name, issue);
            await telegram.StartingAsync(config.Project.Name, issue, ct);
            var timer = Stopwatch.StartNew();
            var result = await ProcessClaimedIssueAsync(execution, issue, ct);
            timer.Stop();
            execution.TransitionTo(ExecutionState.Reporting);
            await ReportResultAsync(issue, result with { Report = result.Report with { Duration = timer.Elapsed, ExecutionId = execution.ExecutionId } }, ct);
            execution.TransitionTo(result.Kind switch
            {
                IssueOutcomeKind.Succeeded => ExecutionState.Completed,
                IssueOutcomeKind.Blocked => ExecutionState.Blocked,
                IssueOutcomeKind.Failed => ExecutionState.Failed,
                _ => throw new ArgumentOutOfRangeException()
            });
            return true;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            if (!execution.IsTerminal) execution.TransitionTo(ExecutionState.InfrastructureFailure);
            throw new WorkerInfrastructureException("Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
        }
        catch
        {
            if (!execution.IsTerminal) execution.TransitionTo(ExecutionState.InfrastructureFailure);
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
                await _output.StopWaitingAsync();
                safelyIdle = false;
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
                execution.TransitionTo(ExecutionState.Claimed);
                _output.IssueStarted(issue);
                await telegram.StartingAsync(config.Project.Name, issue, ct);
                var issueTimer = Stopwatch.StartNew();
                var result = await ProcessClaimedIssueAsync(execution, issue, ct);
                issueTimer.Stop();
                execution.TransitionTo(ExecutionState.Reporting);
                await ReportResultAsync(issue, result with { Report = result.Report with { Duration = issueTimer.Elapsed, ExecutionId = execution.ExecutionId } }, ct);
                execution.TransitionTo(result.Kind switch
                {
                    IssueOutcomeKind.Succeeded => ExecutionState.Completed,
                    IssueOutcomeKind.Blocked => ExecutionState.Blocked,
                    IssueOutcomeKind.Failed => ExecutionState.Failed,
                    _ => throw new ArgumentOutOfRangeException()
                });
                activeExecution = null;
                safelyIdle = true;
            }
            await _output.StopWaitingAsync();
            _output.Shutdown();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            await _output.StopWaitingAsync(finalizeLine: true);
            if (activeExecution is { IsTerminal: false }) activeExecution.TransitionTo(ExecutionState.InfrastructureFailure);
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
            if (activeExecution is { IsTerminal: false }) activeExecution.TransitionTo(ExecutionState.InfrastructureFailure);
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
        execution.TransitionTo(ExecutionState.Preparing);
        await git.StartIssueAsync(issue, ct);
        execution.TransitionTo(ExecutionState.Implementing);
        var outcome = await _output.RunProgressAsync("Codex working", () =>
            codex.RunAsync(config.Project.Directory, config.Codex.InstructionsFile, issue, ct),
            completion: x => x.Status, succeeded: x => x.Status == "success",
            warning: x => x.Status == "blocked", ct: ct);
        await git.VerifyCodexStateAsync(ct);
        var implementationSummary = outcome.Summary;
        var repairs = new List<ValidationRepairRecord>();
        if (outcome.Status == "blocked") return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Blocked,
            new IssueExecutionReport(implementationSummary, repairs, HumanInput: outcome.Question), ct);
        if (outcome.Status == "failed") return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Failed,
            new IssueExecutionReport(implementationSummary, repairs, Failure: outcome.Summary), ct);

        var repairAttempts = 0;
        while (true)
        {
            execution.TransitionTo(ExecutionState.Validating);
            var validationResult = await _output.RunProgressAsync("Validation", () =>
                validation.RunAsync(config.Validation.Commands, config.Project.Directory, ct),
                x => x.Succeeded ? "passed" : $"command {x.Failure!.CommandNumber} failed",
                x => x.Succeeded, ct: ct);
            if (validationResult.Succeeded) break;

            await git.VerifyCodexStateAsync(ct);
            var failure = validationResult.Failure!;
            if (repairAttempts >= config.Validation.MaxFixAttempts)
                return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Failed,
                    new IssueExecutionReport(implementationSummary, repairs, FinalValidationFailure: failure.Command,
                        Failure: $"Validation failed after {repairAttempts} repair attempt(s)."), ct);

            repairAttempts++;
            execution.TransitionTo(ExecutionState.Repairing);
            outcome = await _output.RunProgressAsync($"Repair {repairAttempts}/{config.Validation.MaxFixAttempts}", () =>
                codex.RepairAsync(config.Project.Directory, config.Codex.InstructionsFile, issue,
                    failure, repairAttempts, config.Validation.MaxFixAttempts, ct),
                completion: x => x.Status, succeeded: x => x.Status == "success",
                warning: x => x.Status == "blocked", ct: ct);
            await git.VerifyCodexStateAsync(ct);
            if (outcome.Status == "blocked")
            {
                repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                    outcome.Summary, false));
                return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Blocked,
                    new IssueExecutionReport(implementationSummary, repairs, HumanInput: outcome.Question), ct);
            }
            if (outcome.Status == "failed")
            {
                repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                    outcome.Summary, false));
                return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Failed,
                    new IssueExecutionReport(implementationSummary, repairs, Failure: outcome.Summary), ct);
            }
            // The next validation result determines whether this repair passed. Store its independent summary now.
            repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                outcome.Summary, false));
        }

        await git.VerifyCodexStateAsync(ct);
        execution.TransitionTo(ExecutionState.Integrating);
        var integration = await _output.RunProgressAsync("Integrating", () => git.CommitAndIntegrateAsync(issue, ct),
            completion: x => x.HasChanges ? "complete" : "no changes", ct: ct);
        if (repairs.Count > 0) repairs[^1] = repairs[^1] with { PassedAfterRepair = true };
        return new IssueProcessingResult(IssueOutcomeKind.Succeeded,
            new IssueExecutionReport(implementationSummary, repairs, Integration: integration));
    }

    private async Task<IssueProcessingResult> CleanupOutcomeAsync(GitHubIssue issue, IssueOutcomeKind kind, IssueExecutionReport report, CancellationToken ct)
    {
        await git.DiscardUncommittedIssueChangesAsync(ct);
        return new IssueProcessingResult(kind, report);
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
