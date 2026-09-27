using System.Text.Json;

namespace CodexWorker;

public sealed record GitHubIssue(int Number, string Title, string Body, DateTimeOffset CreatedAt);

public sealed class GitHubClient(ProcessRunner runner, string repository, int timeoutSeconds) : IGitHubClient
{
    public async Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken)
    {
        var result = await RunGhAsync(["issue", "list", "--repo", repository, "--state", "open", "--label", label,
            "--search", "sort:created-asc", "--limit", "1", "--json", "number,title,body,createdAt"], cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.EnumerateArray()
            .Select(e => new GitHubIssue(e.GetProperty("number").GetInt32(), e.GetProperty("title").GetString() ?? "",
                e.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                e.GetProperty("createdAt").GetDateTimeOffset()))
            .OrderBy(i => i.CreatedAt)
            .FirstOrDefault();
    }

    public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct) =>
        RunGhAsync(["issue", "edit", issueNumber.ToString(), "--repo", repository, "--remove-label", remove, "--add-label", add], ct);

    public Task CommentAsync(int issueNumber, string comment, CancellationToken ct) =>
        RunGhAsync(["issue", "comment", issueNumber.ToString(), "--repo", repository, "--body", comment], ct);

    public Task CloseAsync(int issueNumber, CancellationToken ct) =>
        RunGhAsync(["issue", "close", issueNumber.ToString(), "--repo", repository], ct);

    private async Task<ProcessResult> RunGhAsync(IEnumerable<string> args, CancellationToken ct)
    {
        try
        {
            var result = await runner.RunAsync("gh", args, Environment.CurrentDirectory, TimeSpan.FromSeconds(timeoutSeconds), ct);
            if (result.ExitCode != 0)
                throw new WorkerInfrastructureException($"GitHub CLI command failed (exit {result.ExitCode}); Issue state may require manual reconciliation. {Tail(result.StandardError)}");
            return result;
        }
        catch (OperationCanceledException ex) { throw new WorkerInfrastructureException("GitHub operation was cancelled; remote Issue state may be uncertain.", ex); }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"GitHub CLI operation failed or timed out; GitHub state may require manual reconciliation: {ex.Message}", ex); }
    }

    private static string Tail(string value) => value.Length <= 1000 ? value : value[^1000..];
}
