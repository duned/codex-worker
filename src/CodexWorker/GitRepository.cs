using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed class GitRepository(ProcessRunner runner, string directory, string repository, GitSettings settings)
{
    private string? _featureBranch;
    private string? _completedBranch;
    private string? _startingCommit;
    public static string SanitizeTitle(string title)
    {
        var value = title.ToLowerInvariant();
        value = Regex.Replace(value, "[^a-z0-9]+", "-").Trim('-');
        value = Regex.Replace(value, "-+", "-");
        return string.IsNullOrWhiteSpace(value) ? "issue" : value[..Math.Min(value.Length, 72)].TrimEnd('-');
    }

    public async Task<string> StartIssueAsync(GitHubIssue issue, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) throw new InvalidOperationException($"Project directory does not exist: {directory}");
        var origin = (await GitAsync(["remote", "get-url", "origin"], ct)).StandardOutput.Trim();
        var normalizedOrigin = origin.TrimEnd('/');
        if (normalizedOrigin.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) normalizedOrigin = normalizedOrigin[..^4];
        var remoteParts = normalizedOrigin
            .Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries);
        var expectedParts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (remoteParts.Length < 2 || !remoteParts[^2].Equals(expectedParts[0], StringComparison.OrdinalIgnoreCase) ||
            !remoteParts[^1].Equals(expectedParts[1], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Git origin '{origin}' does not match configured repository '{expectedParts[0]}/{expectedParts[1]}'.");
        var status = await GitAsync(["status", "--porcelain=v1", "--untracked-files=all"], ct);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
            throw new InvalidOperationException("Project repository is not clean before issue start.");

        await GitAsync(["checkout", settings.BaseBranch], ct);
        await GitAsync(["pull", "--ff-only", "origin", settings.BaseBranch], ct);
        _startingCommit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
        var slug = SanitizeTitle(issue.Title);
        _featureBranch = $"{settings.FeaturePrefix}{issue.Number}-{slug}";
        _completedBranch = $"{settings.CompletedPrefix}{issue.Number}-{slug}";
        await GitAsync(["switch", "-c", _featureBranch], ct);
        return _featureBranch;
    }

    public async Task DiscardUncommittedIssueChangesAsync(CancellationToken ct)
    {
        if (_featureBranch is null || _startingCommit is null) return;
        if (await GetCurrentBranchAsync(ct) != _featureBranch)
            throw new InvalidOperationException("Refusing to clean up because the current branch is not the worker-created feature branch.");
        var currentCommit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
        if (currentCommit != _startingCommit)
            throw new InvalidOperationException("Refusing to discard changes because Git history changed during the Issue.");
        await GitAsync(["reset", "--hard", _startingCommit], ct);
        await GitAsync(["clean", "-fd"], ct);
        await GitAsync(["switch", settings.BaseBranch], ct);
        await GitAsync(["branch", "-d", _featureBranch], ct);
        _featureBranch = null;
    }

    public async Task<string> CommitAndIntegrateAsync(GitHubIssue issue, CancellationToken ct)
    {
        await EnsureBranchAsync(_featureBranch, ct);
        var currentCommit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
        if (currentCommit != _startingCommit)
            throw new InvalidOperationException("Codex changed Git history; worker requires the original feature branch history.");

        await GitAsync(["add", "--all"], ct);
        var staged = await GitAsync(["diff", "--cached", "--quiet"], ct, allowExitCodes: [0, 1]);
        if (staged.ExitCode == 0) throw new InvalidOperationException("Codex reported success but produced no changes to commit.");
        await GitAsync(["commit", "-m", $"Implement #{issue.Number}: {issue.Title}"], ct);
        var commit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();

        if (settings.AutoMerge)
        {
            await GitAsync(["switch", settings.BaseBranch], ct);
            await GitAsync(["pull", "--ff-only", "origin", settings.BaseBranch], ct);
            await GitAsync(["merge", "--no-ff", "--no-edit", _featureBranch!], ct);
            await GitAsync(["push", "origin", settings.BaseBranch], ct);
            if (settings.PushCompletedBranch)
                await GitAsync(["push", "origin", $"{_featureBranch}:refs/heads/{_completedBranch}"], ct);
        }

        if (settings.DeleteLocalFeatureBranch && settings.AutoMerge)
        {
            if (await GetCurrentBranchAsync(ct) != settings.BaseBranch)
                throw new InvalidOperationException("Refusing to delete feature branch while it is checked out.");
            await GitAsync(["branch", "-d", _featureBranch!], ct);
        }
        var summary = $"Committed as `{commit[..Math.Min(commit.Length, 12)]}`.";
        if (settings.AutoMerge) summary += $" Merged into `{settings.BaseBranch}`.";
        if (settings.AutoMerge && settings.PushCompletedBranch) summary += $" Preserved on origin as `{_completedBranch}`.";
        else if (!settings.AutoMerge) summary += $" Local feature branch: `{_featureBranch}`.";
        return summary;
    }

    private async Task<string> GetCurrentBranchAsync(CancellationToken ct) =>
        (await GitAsync(["branch", "--show-current"], ct)).StandardOutput.Trim();

    private async Task EnsureBranchAsync(string? branch, CancellationToken ct)
    {
        if (branch is null || await GetCurrentBranchAsync(ct) != branch)
            throw new InvalidOperationException("Unexpected Git branch detected; refusing to continue.");
    }

    private async Task<ProcessResult> GitAsync(IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null)
    {
        var result = await runner.RunAsync("git", args, directory, cancellationToken: ct);
        if (result.ExitCode != 0 && !(allowExitCodes?.Contains(result.ExitCode) ?? false))
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new CommandFailedException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}). {Tail(detail)}", result);
        }
        return result;
    }

    private static string Tail(string value) => value.Length <= 1200 ? value : value[^1200..];
}
