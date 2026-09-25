namespace CodexWorker;

public sealed class Worker(WorkerConfiguration config, GitHubClient github, GitRepository git, CodexExecutor codex,
    ValidationRunner validation, TelegramNotifier telegram)
{
    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"Worker started for {config.Project.Name} ({config.Project.Repository}).");
        while (!ct.IsCancellationRequested)
        {
            GitHubIssue? issue;
            try { issue = await github.FindOldestReadyAsync(config.GitHub.ReadyLabel, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not query GitHub: {ex.Message}");
                await DelayAsync(ct);
                continue;
            }

            if (issue is null)
            {
                await DelayAsync(ct);
                continue;
            }

            try { await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not claim Issue #{issue.Number}: {ex.Message}");
                await DelayAsync(ct);
                continue;
            }

            await ProcessClaimedIssueAsync(issue, ct);
        }
        Console.WriteLine("Worker stopped.");
    }

    private async Task ProcessClaimedIssueAsync(GitHubIssue issue, CancellationToken ct)
    {
        try
        {
            await telegram.StartingAsync(config.Project.Name, issue.Number, issue.Title, ct);
            Console.WriteLine($"Starting Issue #{issue.Number}: {issue.Title}");
            await git.StartIssueAsync(issue, ct);
            var outcome = await codex.RunAsync(config.Project.Directory, config.Codex.InstructionsFile, issue, ct);

            if (outcome.Status == "blocked")
            {
                var details = outcome.Question ?? outcome.Summary;
                await git.DiscardUncommittedIssueChangesAsync(ct);
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, $"Codex is blocked and needs human input: {details}\n\nSummary: {outcome.Summary}", ct);
                await telegram.BlockedAsync(config.Project.Name, issue.Number, details, ct);
                Console.WriteLine($"Issue #{issue.Number} blocked: {details}");
                return;
            }
            if (outcome.Status == "failed")
            {
                await git.DiscardUncommittedIssueChangesAsync(ct);
                await MarkFailedAsync(issue, outcome.Summary, ct);
                return;
            }

            try { await validation.RunAsync(config.Validation.Commands, config.Project.Directory, ct); }
            catch
            {
                await git.DiscardUncommittedIssueChangesAsync(ct);
                throw;
            }
            var completion = await git.CommitAndIntegrateAsync(issue, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.DoneLabel, ct);
            await github.CommentAsync(issue.Number, $"{completion}\n\nCodex summary: {outcome.Summary}", ct);
            await github.CloseAsync(issue.Number, ct);
            await telegram.SuccessAsync(config.Project.Name, issue.Number, completion, ct);
            Console.WriteLine($"Issue #{issue.Number} completed. {completion}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine($"Cancellation received while processing Issue #{issue.Number}; it remains labeled working for manual review.");
        }
        catch (Exception ex)
        {
            try { await git.DiscardUncommittedIssueChangesAsync(ct); }
            catch (Exception cleanupError) { Console.Error.WriteLine($"Issue cleanup was not safe: {cleanupError.Message}"); }
            await MarkFailedAsync(issue, Concise(ex), ct);
        }
    }

    private async Task MarkFailedAsync(GitHubIssue issue, string details, CancellationToken ct)
    {
        var message = Limit(details, 1400);
        try
        {
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.FailedLabel, ct);
            await github.CommentAsync(issue.Number, $"Worker failed this Issue: {message}", ct);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Could not report failure for Issue #{issue.Number} to GitHub: {ex.Message}"); }
        await telegram.FailedAsync(config.Project.Name, issue.Number, message, ct);
        Console.Error.WriteLine($"Issue #{issue.Number} failed: {message}");
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(config.Worker.PollingSeconds), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static string Concise(Exception exception)
    {
        var message = exception.Message;
        return Limit(message, 1400);
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 20)] + " … [truncated]";
}
