using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed record GitIntegrationResult(bool HasChanges, string Summary, string? CommitSha = null,
    string? IntegrationBranch = null, string? CompletedBranch = null);

public sealed class GitRepository(ProcessRunner runner, string directory, string repository, GitSettings settings, WorkerSettings timeouts,
    string? executionWorktreeRoot = null) : IGitRepository, IDisposable
{
    private readonly string worktreeRoot = executionWorktreeRoot ?? DefaultWorktreeRoot(repository);
    private string? _executionDirectory;
    private string? _featureBranch;
    private string? _completedBranch;
    private string? _startingCommit;
    private Guid? _executionId;
    private FileStream? _workerLock;

    public void Dispose() => _workerLock?.Dispose();
    public string ExecutionDirectory => _executionDirectory ?? directory;

    internal static string DefaultWorktreeRoot(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new WorkerInfrastructureException($"Configured repository must be owner/name to select an isolated worktree root: '{repository}'.");

        static string Segment(string value) => Regex.Replace(value, "[^A-Za-z0-9._-]", "-");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-worker", "worktrees",
            Segment(parts[0]), Segment(parts[1]));
    }

    /// <summary>
    /// Gives each Issue attempt its own mutable worktree/branch/starting-commit state. The returned instance
    /// shares repository paths and settings; base-branch integration remains a repository-level operation.
    /// </summary>
    public IGitRepository CreateExecutionRepository() =>
        new GitRepository(runner, directory, repository, settings, timeouts, worktreeRoot);

    /// <summary>Read-only safety inspection used across every configured project before any queue is queried.</summary>
    public async Task ValidateStartupReadOnlyAsync(CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(directory)) throw new WorkerInfrastructureException($"Dedicated project checkout does not exist: {directory}");
            var top = Path.GetFullPath((await GitAsync(["rev-parse", "--show-toplevel"], ct)).StandardOutput.Trim());
            if (!Path.GetFullPath(directory).Equals(top, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new WorkerInfrastructureException($"Configured directory must be the Git checkout root. Git root: {top}");
            await EnsureOriginAsync(ct);
            await EnsureCleanAsync("before worker startup", ct);
            await ValidateBranchRefAsync(settings.BaseBranch, ct);
            await ValidateBranchRefAsync($"{settings.FeaturePrefix}1-sample", ct);
            await ValidateBranchRefAsync($"{settings.CompletedPrefix}1-sample", ct);
            var branch = await GetCurrentBranchAsync(ct);
            if (branch != settings.BaseBranch)
                throw new WorkerInfrastructureException($"Checkout is on '{branch}' at startup, not configured base branch '{settings.BaseBranch}'.");
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not validate configured checkout without mutation: {ex.Message}", ex); }
    }

    public static string SanitizeTitle(string title)
    {
        var value = title.ToLowerInvariant();
        value = Regex.Replace(value, "[^a-z0-9]+", "-").Trim('-');
        value = Regex.Replace(value, "-+", "-");
        return string.IsNullOrWhiteSpace(value) ? "issue" : value[..Math.Min(value.Length, 72)].TrimEnd('-');
    }

    public static string FeatureBranchName(GitSettings settings, GitHubIssue issue) =>
        $"{settings.FeaturePrefix}{SanitizeTitle(issue.Title)}-{issue.Number}";

    public static string CompletedBranchName(GitSettings settings, GitHubIssue issue) =>
        $"{settings.CompletedPrefix}{SanitizeTitle(issue.Title)}-{issue.Number}";

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

    public async Task StartIssueAsync(Guid executionId, GitHubIssue issue, CancellationToken ct)
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
            _executionId = executionId;
            _featureBranch = FeatureBranchName(settings, issue);
            _completedBranch = CompletedBranchName(settings, issue);
            var root = Path.GetFullPath(worktreeRoot);
            if (IsWithin(Path.GetFullPath(directory), root))
                throw new WorkerInfrastructureException("Managed execution worktree root must be outside the configured project checkout.");
            _executionDirectory = Path.Combine(root, executionId.ToString("N"));
            Directory.CreateDirectory(root);
            if (Directory.Exists(_executionDirectory) || File.Exists(_executionDirectory))
                throw new WorkerInfrastructureException($"Execution worktree path already exists; preserving it: {_executionDirectory}");
            await GitAsync(["worktree", "add", "-b", _featureBranch, _executionDirectory, _startingCommit], ct);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not prepare Git checkout for Issue #{issue.Number}: {ex.Message}", ex); }
    }

    public async Task VerifyCodexStateAsync(CancellationToken ct)
    {
        try
        {
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            var commit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (commit != _startingCommit) throw new WorkerInfrastructureException("Codex changed Git history; refusing to continue.");
            await EnsureOriginAsync(ct);
            await EnsureWorktreeOwnedAsync(ct);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not verify checkout state after Codex: {ex.Message}", ex); }
    }

    public async Task DiscardUncommittedIssueChangesAsync(CancellationToken ct)
    {
        try
        {
            if (_featureBranch is null || _startingCommit is null) throw new WorkerInfrastructureException("No worker-owned feature branch is available for safe cleanup.");
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            var currentCommit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (currentCommit != _startingCommit) throw new WorkerInfrastructureException("Refusing cleanup because Git history changed during the Issue.");
            // Safe only under the documented dedicated-checkout model and with the worker's branch/HEAD invariants intact.
            await GitAtAsync(ExecutionDirectory, ["reset", "--hard", _startingCommit], ct);
            await GitAtAsync(ExecutionDirectory, ["clean", "-fd"], ct);
            await RemoveExecutionWorktreeAsync(ct);
            await DeleteFeatureBranchIfUnownedAsync(_featureBranch, ct);
            _featureBranch = null;
            _executionDirectory = null;
            _executionId = null;
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not safely clean the task branch: {ex.Message}", ex); }
    }

    public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct)
    {
        try
        {
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            var currentCommit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (currentCommit != _startingCommit) throw new WorkerInfrastructureException("Codex changed Git history; worker requires the original feature branch history.");
            await GitAtAsync(ExecutionDirectory, ["add", "--all"], ct);
            var staged = await GitAtAsync(ExecutionDirectory, ["diff", "--cached", "--quiet"], ct, [0, 1]);
            if (staged.ExitCode == 0)
            {
                var status = (await GitAtAsync(ExecutionDirectory, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
                if (!string.IsNullOrWhiteSpace(status)) throw new WorkerInfrastructureException("Unexpected unstaged or untracked changes remain after staging; preserving checkout.");
                await RemoveExecutionWorktreeAsync(ct);
                await DeleteFeatureBranchIfUnownedAsync(_featureBranch!, ct);
                _featureBranch = null;
                _executionDirectory = null;
                _executionId = null;
                return new GitIntegrationResult(false, "No code changes were required; the Issue was completed without a commit or integration.");
            }

            await GitAtAsync(ExecutionDirectory, ["commit", "-m", $"Implement #{issue.Number}: {issue.Title}"], ct);
            var commit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (settings.AutoMerge)
            {
                await GitAsync(["fetch", "origin", $"refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}"], ct);
                await GitAsync(["switch", "--", settings.BaseBranch], ct);
                await GitAsync(["merge", "--ff-only", $"refs/remotes/origin/{settings.BaseBranch}"], ct);
                var featureContainsBase = await GitAtAsync(ExecutionDirectory,
                    ["merge-base", "--is-ancestor", settings.BaseBranch, $"refs/heads/{_featureBranch}"], ct, [0, 1]);
                if (featureContainsBase.ExitCode != 0)
                {
                    await GitAtAsync(ExecutionDirectory, ["rebase", settings.BaseBranch], ct);
                    var validation = await validateAfterRebase(ct);
                    if (!validation.Succeeded)
                        throw new WorkerInfrastructureException($"Validation failed after rebasing Issue #{issue.Number}; integration was stopped. {validation.Failure!.ToSummary()}");
                }
                await GitAsync(["merge", "--ff-only", $"refs/heads/{_featureBranch}"], ct);
                await GitAsync(["push", "origin", $"refs/heads/{settings.BaseBranch}:refs/heads/{settings.BaseBranch}"], ct);
                if (settings.PushCompletedBranch)
                    await GitAsync(["push", "origin", $"{_featureBranch}:refs/heads/{_completedBranch}"], ct);
            }
            if (settings.DeleteLocalFeatureBranch && settings.AutoMerge)
            {
                await RemoveExecutionWorktreeAsync(ct);
                _executionDirectory = null;
                _executionId = null;
                if (await GetCurrentBranchAsync(ct) != settings.BaseBranch)
                    throw new WorkerInfrastructureException("Refusing to delete feature branch while it is checked out.");
                if (settings.PushCompletedBranch)
                {
                    await GitAsync(["branch", "-m", "--", _featureBranch!, _completedBranch!], ct);
                    _featureBranch = _completedBranch;
                }
                else
                {
                    await DeleteFeatureBranchIfUnownedAsync(_featureBranch!, ct);
                }
            }
            else
            {
                await RemoveExecutionWorktreeAsync(ct);
                _executionDirectory = null;
                _executionId = null;
            }
            var summary = $"Committed as `{commit[..Math.Min(commit.Length, 12)]}`.";
            if (settings.AutoMerge) summary += $" Merged into `{settings.BaseBranch}`.";
            if (settings.AutoMerge && settings.PushCompletedBranch) summary += $" Preserved on origin as `{_completedBranch}`.";
            else if (!settings.AutoMerge) summary += $" Local feature branch: `{_featureBranch}`.";
            var completedBranch = settings.AutoMerge && settings.PushCompletedBranch ? _completedBranch :
                settings.AutoMerge && settings.DeleteLocalFeatureBranch ? null : _featureBranch;
            return new GitIntegrationResult(true, summary, commit, settings.AutoMerge ? settings.BaseBranch : null,
                completedBranch);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Git commit/integration for Issue #{issue.Number} failed; checkout state is preserved for diagnosis: {ex.Message}", ex); }
    }

    private async Task EnsureOriginAsync(CancellationToken ct)
    {
        // Read the configured URL before Git's insteadOf rewriting so the repository identity check
        // validates the operator-configured origin rather than its transport rewrite.
        var origin = (await GitAsync(["config", "--get", "remote.origin.url"], ct)).StandardOutput.Trim();
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

    private async Task EnsureBranchAsync(string? branch, CancellationToken ct, string? workingDirectory = null)
    {
        if (branch is null || (await GitAtAsync(workingDirectory ?? directory, ["branch", "--show-current"], ct)).StandardOutput.Trim() != branch)
            throw new WorkerInfrastructureException("Unexpected Git branch detected; refusing to continue.");
    }

    private async Task EnsureWorktreeOwnedAsync(CancellationToken ct)
    {
        if (_executionDirectory is null || _featureBranch is null || _executionId is null)
            throw new WorkerInfrastructureException("No execution worktree is registered for this Issue.");
        var ownedPath = Path.GetFullPath(Path.Combine(worktreeRoot, _executionId.Value.ToString("N")));
        if (!PathEquals(Path.GetFullPath(_executionDirectory), ownedPath))
            throw new WorkerInfrastructureException("Execution worktree path does not match its execution ID; refusing cleanup or integration.");
        if (!Directory.Exists(_executionDirectory))
            throw new WorkerInfrastructureException($"Execution worktree is missing; preserving Git state for inspection: {_executionDirectory}");
        var top = Path.GetFullPath((await GitAtAsync(_executionDirectory, ["rev-parse", "--show-toplevel"], ct)).StandardOutput.Trim());
        if (!PathEquals(top, Path.GetFullPath(_executionDirectory)))
            throw new WorkerInfrastructureException("Execution directory is not the expected Git worktree; refusing cleanup or integration.");
        var branch = (await GitAtAsync(_executionDirectory, ["branch", "--show-current"], ct)).StandardOutput.Trim();
        if (branch != _featureBranch)
            throw new WorkerInfrastructureException("Execution worktree branch changed unexpectedly; preserving state for inspection.");
        var list = (await GitAsync(["worktree", "list", "--porcelain"], ct)).StandardOutput;
        var expected = Path.GetFullPath(_executionDirectory);
        var registered = list.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry.Split('\n').Any(line => line.StartsWith("worktree ", StringComparison.Ordinal) &&
                PathEquals(Path.GetFullPath(line[9..]), expected)) && entry.Split('\n').Any(line => line == $"branch refs/heads/{_featureBranch}"));
        if (!registered)
            throw new WorkerInfrastructureException("Execution worktree ownership could not be verified; preserving it for inspection.");
    }

    private async Task RemoveExecutionWorktreeAsync(CancellationToken ct)
    {
        await EnsureWorktreeOwnedAsync(ct);
        // Git removes only this verified registered path and updates the corresponding administrative metadata.
        await GitAsync(["worktree", "remove", Path.GetFullPath(_executionDirectory!)], ct);
    }

    private async Task DeleteFeatureBranchIfUnownedAsync(string branch, CancellationToken ct)
    {
        var list = (await GitAsync(["worktree", "list", "--porcelain"], ct)).StandardOutput;
        var checkedOut = list.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry.Split('\n').Any(line => line == $"branch refs/heads/{branch}"));
        if (checkedOut)
            throw new WorkerInfrastructureException($"Refusing to delete feature branch '{branch}' because a registered worktree still has it checked out.");
        await GitAsync(["branch", "-d", "--", branch], ct);
    }

    private static bool PathEquals(string left, string right) =>
        left.Equals(right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private async Task<ProcessResult> GitAsync(IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null)
        => await GitAtAsync(directory, args, ct, allowExitCodes);

    private async Task<ProcessResult> GitAtAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null)
    {
        try
        {
            var result = await runner.RunAsync("git", args, workingDirectory, TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct);
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
