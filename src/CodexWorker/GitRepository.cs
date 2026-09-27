using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed record GitIntegrationResult(bool HasChanges, string Summary);

public sealed class GitRepository(ProcessRunner runner, string directory, string repository, GitSettings settings, WorkerSettings timeouts) : IGitRepository, IDisposable
{
    private string? _featureBranch;
    private string? _completedBranch;
    private string? _startingCommit;
    private FileStream? _workerLock;

    public void Dispose() => _workerLock?.Dispose();

    public static string SanitizeTitle(string title)
    {
        var value = title.ToLowerInvariant();
        value = Regex.Replace(value, "[^a-z0-9]+", "-").Trim('-');
        value = Regex.Replace(value, "-+", "-");
        return string.IsNullOrWhiteSpace(value) ? "issue" : value[..Math.Min(value.Length, 72)].TrimEnd('-');
    }

    public static string FeatureBranchName(GitSettings settings, GitHubIssue issue) =>
        $"{settings.FeaturePrefix}{issue.Number}-{SanitizeTitle(issue.Title)}";

    public static string CompletedBranchName(GitSettings settings, GitHubIssue issue) =>
        $"{settings.CompletedPrefix}{issue.Number}-{SanitizeTitle(issue.Title)}";

    public static async Task<bool> IsValidBranchRefAsync(ProcessRunner runner, string workingDirectory, string branch,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await runner.RunAsync("git", ["check-ref-format", $"refs/heads/{branch}"], workingDirectory,
            timeout ?? TimeSpan.FromSeconds(10), ct);
        return result.ExitCode == 0;
    }

    public static bool OriginMatchesRepository(string origin, string repository)
    {
        var expected = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (expected.Length != 2) return false;
        string host;
        string path;
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("https" or "ssh") || !uri.IsDefaultPort ||
                (uri.Scheme == "https" && uri.UserInfo.Length > 0) ||
                (uri.Scheme == "ssh" && uri.UserInfo.Length > 0 && !uri.UserInfo.Equals("git", StringComparison.OrdinalIgnoreCase)) ||
                uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
            host = uri.Host;
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            var match = Regex.Match(origin, "^(?:git@)?([^:]+):([^/]+)/([^/]+)$");
            if (!match.Success) return false;
            host = match.Groups[1].Value;
            path = $"{match.Groups[2].Value}/{match.Groups[3].Value}";
        }
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var actual = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && actual.Length == 2 &&
            actual[0].Equals(expected[0], StringComparison.OrdinalIgnoreCase) && actual[1].Equals(expected[1], StringComparison.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(directory)) throw new WorkerInfrastructureException($"Dedicated project checkout does not exist: {directory}");
            var top = Path.GetFullPath((await GitAsync(["rev-parse", "--show-toplevel"], ct)).StandardOutput.Trim());
            if (!Path.GetFullPath(directory).Equals(top, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new WorkerInfrastructureException($"Configured directory must be the Git checkout root. Git root: {top}");
            var gitDir = (await GitAsync(["rev-parse", "--absolute-git-dir"], ct)).StandardOutput.Trim();
            try { _workerLock = new FileStream(Path.Combine(gitDir, "codex-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { throw new WorkerInfrastructureException("Another codex-worker process holds the checkout lock; stopping.", ex); }
            await EnsureOriginAsync(ct);
            await EnsureCleanAsync("before worker startup", ct);
            await ValidateBranchRefAsync(settings.BaseBranch, ct);
            await ValidateBranchRefAsync($"{settings.FeaturePrefix}1-sample", ct);
            await ValidateBranchRefAsync($"{settings.CompletedPrefix}1-sample", ct);
            var startupBranch = await GetCurrentBranchAsync(ct);
            if (startupBranch != settings.BaseBranch)
                throw new WorkerInfrastructureException($"Checkout is on '{startupBranch}' at startup, not configured base branch '{settings.BaseBranch}'; inspect it before restarting the worker.");
            await GitAsync(["switch", "--", settings.BaseBranch], ct);
            await GitAsync(["pull", "--ff-only", "origin", $"refs/heads/{settings.BaseBranch}"], ct);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not safely initialize the configured checkout: {ex.Message}", ex); }
    }

    public async Task StartIssueAsync(GitHubIssue issue, CancellationToken ct)
    {
        try
        {
            await EnsureOriginAsync(ct);
            await EnsureCleanAsync($"before Issue #{issue.Number}", ct);
            await ValidateBranchRefAsync(FeatureBranchName(settings, issue), ct);
            await ValidateBranchRefAsync(CompletedBranchName(settings, issue), ct);
            await GitAsync(["switch", "--", settings.BaseBranch], ct);
            await GitAsync(["pull", "--ff-only", "origin", $"refs/heads/{settings.BaseBranch}"], ct);
            _startingCommit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            _featureBranch = FeatureBranchName(settings, issue);
            _completedBranch = CompletedBranchName(settings, issue);
            await GitAsync(["switch", "-c", _featureBranch], ct);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not prepare Git checkout for Issue #{issue.Number}: {ex.Message}", ex); }
    }

    public async Task VerifyCodexStateAsync(CancellationToken ct)
    {
        try
        {
            await EnsureBranchAsync(_featureBranch, ct);
            var commit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (commit != _startingCommit) throw new WorkerInfrastructureException("Codex changed Git history; refusing to continue.");
            await EnsureOriginAsync(ct);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not verify checkout state after Codex: {ex.Message}", ex); }
    }

    public async Task DiscardUncommittedIssueChangesAsync(CancellationToken ct)
    {
        try
        {
            if (_featureBranch is null || _startingCommit is null) throw new WorkerInfrastructureException("No worker-owned feature branch is available for safe cleanup.");
            await EnsureBranchAsync(_featureBranch, ct);
            var currentCommit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (currentCommit != _startingCommit) throw new WorkerInfrastructureException("Refusing cleanup because Git history changed during the Issue.");
            // Safe only under the documented dedicated-checkout model and with the worker's branch/HEAD invariants intact.
            await GitAsync(["reset", "--hard", _startingCommit], ct);
            await GitAsync(["clean", "-fd"], ct);
            await GitAsync(["switch", "--", settings.BaseBranch], ct);
            await GitAsync(["branch", "-d", "--", _featureBranch], ct);
            _featureBranch = null;
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not safely clean the task branch: {ex.Message}", ex); }
    }

    public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue, CancellationToken ct)
    {
        try
        {
            await EnsureBranchAsync(_featureBranch, ct);
            var currentCommit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (currentCommit != _startingCommit) throw new WorkerInfrastructureException("Codex changed Git history; worker requires the original feature branch history.");
            await GitAsync(["add", "--all"], ct);
            var staged = await GitAsync(["diff", "--cached", "--quiet"], ct, [0, 1]);
            if (staged.ExitCode == 0)
            {
                var status = (await GitAsync(["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
                if (!string.IsNullOrWhiteSpace(status)) throw new WorkerInfrastructureException("Unexpected unstaged or untracked changes remain after staging; preserving checkout.");
                await GitAsync(["switch", "--", settings.BaseBranch], ct);
                await GitAsync(["branch", "-d", "--", _featureBranch!], ct);
                _featureBranch = null;
                return new GitIntegrationResult(false, "No code changes were required; the Issue was completed without a commit or integration.");
            }

            await GitAsync(["commit", "-m", $"Implement #{issue.Number}: {issue.Title}"], ct);
            var commit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (settings.AutoMerge)
            {
                await GitAsync(["switch", "--", settings.BaseBranch], ct);
                await GitAsync(["pull", "--ff-only", "origin", $"refs/heads/{settings.BaseBranch}"], ct);
                await GitAsync(["merge", "--no-ff", "--no-edit", $"refs/heads/{_featureBranch}"], ct);
                await GitAsync(["push", "origin", $"refs/heads/{settings.BaseBranch}:refs/heads/{settings.BaseBranch}"], ct);
                if (settings.PushCompletedBranch)
                    await GitAsync(["push", "origin", $"{_featureBranch}:refs/heads/{_completedBranch}"], ct);
            }
            if (settings.DeleteLocalFeatureBranch && settings.AutoMerge)
            {
                if (await GetCurrentBranchAsync(ct) != settings.BaseBranch)
                    throw new WorkerInfrastructureException("Refusing to delete feature branch while it is checked out.");
                await GitAsync(["branch", "-d", "--", _featureBranch!], ct);
            }
            var summary = $"Committed as `{commit[..Math.Min(commit.Length, 12)]}`.";
            if (settings.AutoMerge) summary += $" Merged into `{settings.BaseBranch}`.";
            if (settings.AutoMerge && settings.PushCompletedBranch) summary += $" Preserved on origin as `{_completedBranch}`.";
            else if (!settings.AutoMerge) summary += $" Local feature branch: `{_featureBranch}`.";
            return new GitIntegrationResult(true, summary);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Git commit/integration for Issue #{issue.Number} failed; checkout state is preserved for diagnosis: {ex.Message}", ex); }
    }

    private async Task EnsureOriginAsync(CancellationToken ct)
    {
        var origin = (await GitAsync(["remote", "get-url", "origin"], ct)).StandardOutput.Trim();
        if (!OriginMatchesRepository(origin, repository))
            throw new WorkerInfrastructureException($"Git origin '{origin}' does not match configured repository '{repository}'.");
    }

    private async Task EnsureCleanAsync(string phase, CancellationToken ct)
    {
        var status = await GitAsync(["status", "--porcelain=v1", "--untracked-files=all"], ct);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
            throw new WorkerInfrastructureException($"Project checkout is dirty {phase}; refusing to proceed.");
    }

    private async Task ValidateBranchRefAsync(string branch, CancellationToken ct)
    {
        if (!await IsValidBranchRefAsync(runner, directory, branch, TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct))
            throw new WorkerInfrastructureException($"Invalid Git branch ref generated from configuration/title: '{branch}'.");
    }

    private async Task<string> GetCurrentBranchAsync(CancellationToken ct) =>
        (await GitAsync(["branch", "--show-current"], ct)).StandardOutput.Trim();

    private async Task EnsureBranchAsync(string? branch, CancellationToken ct)
    {
        if (branch is null || await GetCurrentBranchAsync(ct) != branch)
            throw new WorkerInfrastructureException("Unexpected Git branch detected; refusing to continue.");
    }

    private async Task<ProcessResult> GitAsync(IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null)
    {
        try
        {
            var result = await runner.RunAsync("git", args, directory, TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct);
            if (result.ExitCode != 0 && !(allowExitCodes?.Contains(result.ExitCode) ?? false))
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                throw new WorkerInfrastructureException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}). {Tail(detail)}");
            }
            return result;
        }
        catch (OperationCanceledException ex) { throw new WorkerInfrastructureException($"Git operation was cancelled; repository state may be uncertain: {string.Join(' ', args)}", ex); }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Git command failed or timed out: {string.Join(' ', args)}. {ex.Message}", ex); }
    }

    private static string Tail(string value) => value.Length <= 1200 ? value : value[^1200..];
}
