using System.Diagnostics;

namespace CodexWorker;

public enum IssueOutcomeKind { Succeeded, Blocked, Failed }
public sealed record IssueProcessingResult(IssueOutcomeKind Kind, string Summary, TimeSpan Duration = default);

public sealed class Worker(WorkerConfiguration config, IGitHubClient github, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, TelegramNotifier telegram, WorkerConsole? output = null)
{
    private readonly WorkerConsole _output = output ?? new WorkerConsole();

    public async Task RunAsync(CancellationToken ct)
    {
        _output.Startup(config.Project.Name, config.Project.Repository);
        var safelyIdle = false;
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
                await _output.StopWaitingAsync();
                safelyIdle = false;
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
                _output.IssueStarted(issue);
                await telegram.StartingAsync(config.Project.Name, issue, ct);
                var issueTimer = Stopwatch.StartNew();
                var result = await ProcessClaimedIssueAsync(issue, ct);
                issueTimer.Stop();
                await ReportResultAsync(issue, result with { Duration = issueTimer.Elapsed }, ct);
                safelyIdle = true;
            }
            await _output.StopWaitingAsync();
            _output.Shutdown();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            await _output.StopWaitingAsync();
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

    private async Task<IssueProcessingResult> ProcessClaimedIssueAsync(GitHubIssue issue, CancellationToken ct)
    {
        await git.StartIssueAsync(issue, ct);
        var outcome = await _output.RunProgressAsync("Codex working", () =>
            codex.RunAsync(config.Project.Directory, config.Codex.InstructionsFile, issue, ct),
            completion: x => x.Status, succeeded: x => x.Status == "success",
            warning: x => x.Status == "blocked", ct: ct);
        await git.VerifyCodexStateAsync(ct);
        if (outcome.Status == "blocked") return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Blocked, outcome.Question!, ct);
        if (outcome.Status == "failed") return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Failed, outcome.Summary, ct);

        var repairAttempts = 0;
        while (true)
        {
            var validationResult = await _output.RunProgressAsync("Validation", () =>
                validation.RunAsync(config.Validation.Commands, config.Project.Directory, ct),
                x => x.Succeeded ? "passed" : $"command {x.Failure!.CommandNumber} failed",
                x => x.Succeeded, ct: ct);
            if (validationResult.Succeeded) break;

            await git.VerifyCodexStateAsync(ct);
            var failure = validationResult.Failure!;
            if (repairAttempts >= config.Validation.MaxFixAttempts)
                return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Failed, failure.ToSummary(), ct);

            repairAttempts++;
            outcome = await _output.RunProgressAsync($"Repair {repairAttempts}/{config.Validation.MaxFixAttempts}", () =>
                codex.RepairAsync(config.Project.Directory, config.Codex.InstructionsFile, issue,
                    failure, repairAttempts, config.Validation.MaxFixAttempts, ct),
                completion: x => x.Status, succeeded: x => x.Status == "success",
                warning: x => x.Status == "blocked", ct: ct);
            await git.VerifyCodexStateAsync(ct);
            if (outcome.Status == "blocked") return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Blocked, outcome.Question!, ct);
            if (outcome.Status == "failed") return await CleanupOutcomeAsync(issue, IssueOutcomeKind.Failed, outcome.Summary, ct);
        }

        await git.VerifyCodexStateAsync(ct);
        var integration = await _output.RunProgressAsync("Integrating", () => git.CommitAndIntegrateAsync(issue, ct),
            completion: x => x.HasChanges ? "complete" : "no changes", ct: ct);
        var summary = integration.Summary;
        if (integration.HasChanges) summary += $"\n\nCodex summary: {outcome.Summary}";
        return new IssueProcessingResult(IssueOutcomeKind.Succeeded, summary);
    }

    private async Task<IssueProcessingResult> CleanupOutcomeAsync(GitHubIssue issue, IssueOutcomeKind kind, string summary, CancellationToken ct)
    {
        await git.DiscardUncommittedIssueChangesAsync(ct);
        return new IssueProcessingResult(kind, summary);
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
                await telegram.SuccessAsync(config.Project.Name, issue, result.Duration, result.Summary, ct);
                _output.IssueCompleted(issue, result.Duration, ShortCompletion(result.Summary));
                break;
            case IssueOutcomeKind.Blocked:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, $"Human input is required to continue: {result.Summary}", ct);
                await telegram.BlockedAsync(config.Project.Name, issue, result.Duration, result.Summary, ct);
                _output.IssueBlocked(issue, result.Duration, result.Summary);
                break;
            case IssueOutcomeKind.Failed:
                var message = Limit(result.Summary, 1400);
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.FailedLabel, ct);
                await github.CommentAsync(issue.Number, $"Worker could not complete this Issue: {message}", ct);
                await telegram.FailedAsync(config.Project.Name, issue, result.Duration, message, ct);
                _output.IssueFailed(issue, result.Duration, FirstLine(message));
                break;
        }
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(config.Worker.PollingSeconds), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    private static string ShortCompletion(string summary)
    {
        return FirstLine(summary);
    }
    private static string FirstLine(string value) => value.Split('\n', 2)[0];
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 20)] + " … [truncated]";
}
