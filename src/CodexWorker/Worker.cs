namespace CodexWorker;

public enum IssueOutcomeKind { Succeeded, Blocked, Failed }
public sealed record IssueProcessingResult(IssueOutcomeKind Kind, string Summary);

public sealed class Worker(WorkerConfiguration config, GitHubClient github, GitRepository git, CodexExecutor codex,
    ValidationRunner validation, TelegramNotifier telegram)
{
    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"Worker started for {config.Project.Name} ({config.Project.Repository}).");
        try
        {
            if (!File.Exists(config.Codex.InstructionsFile))
                throw new WorkerInfrastructureException($"Configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
            await git.InitializeAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var issue = await github.FindOldestReadyAsync(config.GitHub.ReadyLabel, ct);
                if (issue is null) { await DelayAsync(ct); continue; }
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
                await telegram.StartingAsync(config.Project.Name, issue.Number, issue.Title, ct);
                Console.WriteLine($"Starting Issue #{issue.Number}: {issue.Title}");
                var result = await ProcessClaimedIssueAsync(issue, ct);
                await ReportResultAsync(issue, result, ct);
            }
            Console.WriteLine("Worker stopped.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("Cancellation received; stopping without speculative checkout cleanup.");
        }
        catch (Exception ex)
        {
            var infrastructure = ex as WorkerInfrastructureException ??
                new WorkerInfrastructureException($"Unexpected worker failure; queue processing stopped: {ex.Message}", ex);
            Console.Error.WriteLine($"CRITICAL: {infrastructure.Message}");
            await telegram.CriticalAsync(config.Project.Name, infrastructure.Message, CancellationToken.None);
            throw infrastructure;
        }
    }

    private async Task<IssueProcessingResult> ProcessClaimedIssueAsync(GitHubIssue issue, CancellationToken ct)
    {
        await git.StartIssueAsync(issue, ct);
        CodexOutcome outcome;
        try { outcome = await codex.RunAsync(config.Project.Directory, config.Codex.InstructionsFile, issue, ct); }
        catch (TaskFailureException ex)
        {
            await git.VerifyCodexStateAsync(ct);
            await git.DiscardUncommittedIssueChangesAsync(ct);
            return new IssueProcessingResult(IssueOutcomeKind.Failed, Concise(ex));
        }
        await git.VerifyCodexStateAsync(ct);
        if (outcome.Status == "blocked")
        {
            await git.DiscardUncommittedIssueChangesAsync(ct);
            return new IssueProcessingResult(IssueOutcomeKind.Blocked, outcome.Question!);
        }
        if (outcome.Status == "failed")
        {
            await git.DiscardUncommittedIssueChangesAsync(ct);
            return new IssueProcessingResult(IssueOutcomeKind.Failed, outcome.Summary);
        }

        try { await validation.RunAsync(config.Validation.Commands, config.Project.Directory, ct); }
        catch (TaskFailureException ex)
        {
            await git.VerifyCodexStateAsync(ct);
            await git.DiscardUncommittedIssueChangesAsync(ct);
            return new IssueProcessingResult(IssueOutcomeKind.Failed, Concise(ex));
        }
        await git.VerifyCodexStateAsync(ct);
        var completion = await git.CommitAndIntegrateAsync(issue, ct);
        var summary = completion.Summary;
        if (completion.HasChanges) summary += $"\n\nCodex summary: {outcome.Summary}";
        return new IssueProcessingResult(IssueOutcomeKind.Succeeded, summary);
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
                await telegram.SuccessAsync(config.Project.Name, issue.Number, result.Summary, ct);
                Console.WriteLine($"Issue #{issue.Number} completed. {result.Summary}");
                break;
            case IssueOutcomeKind.Blocked:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, $"Human input is required to continue: {result.Summary}", ct);
                await telegram.BlockedAsync(config.Project.Name, issue.Number, result.Summary, ct);
                Console.WriteLine($"Issue #{issue.Number} blocked: {result.Summary}");
                break;
            case IssueOutcomeKind.Failed:
                var message = Limit(result.Summary, 1400);
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.FailedLabel, ct);
                await github.CommentAsync(issue.Number, $"Worker could not complete this Issue: {message}", ct);
                await telegram.FailedAsync(config.Project.Name, issue.Number, message, ct);
                Console.Error.WriteLine($"Issue #{issue.Number} failed: {message}");
                break;
        }
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(config.Worker.PollingSeconds), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    private static string Concise(Exception exception) => Limit(exception.Message, 1400);
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 20)] + " … [truncated]";
}
