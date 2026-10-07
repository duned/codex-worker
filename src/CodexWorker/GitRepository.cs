using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed record GitIntegrationResult(bool HasChanges, string Summary, string? CommitSha = null,
    string? IntegrationBranch = null, string? CompletedBranch = null, Guid? ResourceExecutionId = null, int? ResourceAttemptNumber = null);
public sealed record GitRecoveryInfo(string Branch, string BaseCommit, string StatusSummary, string? IntegrationBase = null);
public sealed record IntegrationRepairContext(ValidationFailure Failure, string BaseBranch, string OriginalBaseCommit,
    string ImplementationCommit, string IntegratedBaseCommit, string RebasedCommit);
public sealed record IntegrationRepairResult(bool Attempted, bool Completed);
public class GitIntegrationConflictException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GitIntegrationArchiveException(string message, Exception inner) : GitIntegrationConflictException(message, inner);

/// <summary>Post-rebase validation could not be repaired on a preserved implementation.</summary>
public sealed class PostRebaseValidationException(string message, ValidationFailure? failure = null) : GitIntegrationConflictException(message)
{
    public ValidationFailure? Failure { get; } = failure;
}

public sealed partial class GitRepository(ProcessRunner runner, string directory, string repository, GitSettings settings, WorkerSettings timeouts,
    string? executionWorktreeRoot = null, bool requireManagedAuthentication = false) : IGitRepository, IDisposable
{
    private readonly string worktreeRoot = executionWorktreeRoot ?? DefaultWorktreeRoot(repository);
    private string? _executionDirectory;
    private string? _featureBranch;
    private string? _completedBranch;
    private string? _startingCommit;
    private Guid? _executionId;
    private Guid? _resourceExecutionId;
    private int _resourceAttemptNumber;
    private FileStream? _workerLock;
    private bool _implementationAlreadyCommitted;
    private bool _forcePostRebaseValidation;

    private Func<GitIntegrationResult, bool, CancellationToken, Task>? _integrationObserver;
    public IGitRepository WithIntegrationObserver(Func<GitIntegrationResult, bool, CancellationToken, Task> observer)
    {
        _integrationObserver = observer;
        return this;
    }

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
        new GitRepository(runner, directory, repository, settings, timeouts, worktreeRoot, requireManagedAuthentication);

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
            await ValidateRemoteAuthenticationAsync(ct);
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

    /// <summary>Checks the configured remote's read and dry-run write authentication without changing remote refs.</summary>
    public async Task ValidateRemoteAuthenticationAsync(CancellationToken ct)
    {
        await GitRemoteAuthenticationProbe.ValidateAsync(async (arguments, token) =>
        {
            await GitAsync(arguments, token);
        }, repository, settings.FeaturePrefix, ct);
    }

    public async Task ConfigureHttpsCredentialHelperAsync(CancellationToken ct)
    {
        try
        {
            await EnsureOriginAsync(ct);
            var origin = (await GitAsync(["config", "--get", "remote.origin.url"], ct)).StandardOutput.Trim();
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new WorkerInfrastructureException($"Git HTTPS credential provisioning requires an HTTPS GitHub origin for '{repository}'.");
            await AcquireWorkerLockAsync(ct);
            await GitAsync(["config", "--local", "credential.https://github.com.helper", "!gh auth git-credential"], ct);
            await ValidateRemoteAuthenticationAsync(ct);
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception)
        {
            throw new WorkerInfrastructureException($"Git HTTPS credential provisioning could not be verified for '{repository}'.");
        }
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
            await AcquireWorkerLockAsync(ct);
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
        => await StartIssueAsync(executionId, issue, null, false, 1, ct);

    public async Task StartIssueAsync(Guid executionId, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resume, int attemptNumber, CancellationToken ct)
    {
        try
        {
            await EnsureOriginAsync(ct);
            await EnsureCleanAsync($"before Issue #{issue.Number}", ct);
            await ValidateBranchRefAsync(FeatureBranchName(settings, issue), ct);
            await GitAsync(["switch", "--", settings.BaseBranch], ct);
            await GitAsync(["pull", "--ff-only", "origin", $"refs/heads/{settings.BaseBranch}"], ct);
            _startingCommit = (await GitAsync(["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            _executionId = executionId;
            _resourceExecutionId = executionId;
            _resourceAttemptNumber = attemptNumber;
            _featureBranch = attemptNumber <= 1 ? FeatureBranchName(settings, issue) : $"{FeatureBranchName(settings, issue)}-retry-{attemptNumber}";
            _completedBranch = await SelectCompletedBranchAsync(issue, attemptNumber, executionId, ct);
            var root = Path.GetFullPath(worktreeRoot);
            if (IsWithin(Path.GetFullPath(directory), root))
                throw new WorkerInfrastructureException("Managed execution worktree root must be outside the configured project checkout.");
            _executionDirectory = Path.Combine(root, executionId.ToString("N"));
            var startingCommit = _startingCommit ?? throw new WorkerInfrastructureException("Could not identify the current base commit for the execution.");
            Directory.CreateDirectory(root);
            if (Directory.Exists(_executionDirectory) || File.Exists(_executionDirectory))
                throw new WorkerInfrastructureException($"Execution worktree path already exists; preserving it: {_executionDirectory}");
            if (resume)
            {
                if (retryOf is null || retryOf.State is not ("Failed" or "Blocked") || retryOf.RecoveryState != "recoverable" ||
                    string.IsNullOrWhiteSpace(retryOf.RecoveryBaseCommit))
                    throw new IssuePreparationRejectedException("Retry resume was requested, but the previous failed execution has no complete recoverable-state metadata.");
                await ValidateRecoveryWorkspaceAsync(RecoveryWorkspacePath(retryOf), retryOf, ct);
            }
            await GitAsync(["worktree", "add", "-b", _featureBranch, _executionDirectory, startingCommit], ct);
            if (resume)
            {
                var recovery = retryOf ?? throw new IssuePreparationRejectedException("Retry resume has no source execution metadata.");
                var recoveryBaseCommit = recovery.RecoveryBaseCommit ?? throw new IssuePreparationRejectedException("Retry resume has no recorded base commit.");
                var recoveryPath = RecoveryWorkspacePath(recovery);
                try
                {
                    await ApplyRecoveryDeltaAsync(recoveryPath, _executionDirectory, recoveryBaseCommit, ct);
                }
                catch
                {
                    // The original preserved worktree remains the durable recovery source. Do not
                    // leave a half-applied retry worktree that has no persisted recovery identity.
                    await GitAtAsync(_executionDirectory, ["reset", "--hard", startingCommit], CancellationToken.None);
                    await GitAtAsync(_executionDirectory, ["clean", "-fd"], CancellationToken.None);
                    await RemoveExecutionWorktreeAsync(CancellationToken.None);
                    await DeleteFeatureBranchIfUnownedAsync(_featureBranch, CancellationToken.None);
                    _executionDirectory = null;
                    _featureBranch = null;
                    _executionId = null;
                    throw;
                }
            }
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not prepare Git checkout for Issue #{issue.Number}: {ex.Message}", ex); }
    }

    public async Task<string?> GetIntegrationBaseAsync(CancellationToken ct)
    {
        await EnsureOriginAsync(ct);
        await ValidateBranchRefAsync(settings.BaseBranch, ct);
        await GitAsync(["fetch", "origin", $"refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}"], ct);
        return (await GitAsync(["rev-parse", $"refs/remotes/origin/{settings.BaseBranch}"], ct)).StandardOutput.Trim();
    }

    public async Task<GitRecoveryInfo?> InspectExecutionWorkspaceAsync(CancellationToken ct)
    {
        await VerifyCodexStateAsync(ct);
        if (_featureBranch is null || _startingCommit is null) return null;
        var status = (await GitAtAsync(ExecutionDirectory, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
        return new GitRecoveryInfo(_featureBranch, _startingCommit,
            $"{status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} changed path(s); implementation workspace preserved.");
    }

    public async Task<LegacyWorkspaceEvidence?> InspectLegacyCodexWorkspaceAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        if (source.ExecutionId == Guid.Empty || source.AttemptNumber < 1 || source.Repository != repository ||
            source.BaseBranch != settings.BaseBranch) return null;
        var path = RecoveryWorkspacePath(source);
        if (!Directory.Exists(path)) return null;
        await EnsureOriginAsync(ct);
        var commonDirectory = (await GitAsync(["rev-parse", "--git-common-dir"], ct)).StandardOutput.Trim();
        var workspaceCommonDirectory = (await GitAtAsync(path, ["rev-parse", "--git-common-dir"], ct)).StandardOutput.Trim();
        if (!PathEquals(Path.GetFullPath(commonDirectory, directory), Path.GetFullPath(workspaceCommonDirectory, path)))
            throw new IssuePreparationRejectedException("execution/worktree repository identity conflicts with preserved execution");
        var head = (await GitAtAsync(path, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
        if (source.RecoveryBaseCommit is not null && source.RecoveryBaseCommit != head)
            throw new IssuePreparationRejectedException("branch/base metadata conflicts with preserved execution");
        // Reuse the normal ownership checks, including registration in this checkout,
        // Issue branch convention, unfinished operations, and dirty index safety.
        await ValidateRecoveryWorkspaceAsync(path, source with { RecoveryBaseCommit = head }, ct);
        // An advanced main is fine; a feature-only commit cannot be manufactured as
        // the original Worker starting commit merely because a session mentions it.
        await GitAsync(["merge-base", "--is-ancestor", head, settings.BaseBranch], ct);
        return new(path, head);
    }

    public async Task<string?> ValidateCodexRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        try
        {
            var recovery = source.CodexRecovery;
            if (recovery is null || recovery.WorkspaceExecutionId == Guid.Empty || recovery.WorkspaceAttemptNumber < 1 ||
                source.RecoveryBaseCommit is null || source.OriginalIssueBody is null || source.BaseBranch != settings.BaseBranch)
                return "Interrupted execution has insufficient original intent/workspace/base metadata or a changed base branch.";
            await EnsureOriginAsync(ct);
            var owner = source with { ExecutionId = recovery.WorkspaceExecutionId, AttemptNumber = recovery.WorkspaceAttemptNumber };
            await ValidateRecoveryWorkspaceAsync(Path.Combine(Path.GetFullPath(worktreeRoot), owner.ExecutionId.ToString("N")), owner, ct);
            return null;
        }
        catch (WorkerInfrastructureException ex) when (!ct.IsCancellationRequested)
        { return ex.Message; }
    }

    public async Task StartCodexRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        var recovery = source.CodexRecovery ?? throw new IssuePreparationRejectedException("Interrupted execution has no workspace ownership snapshot.");
        if (source.RecoveryBaseCommit is null || source.OriginalIssueBody is null || source.BaseBranch != settings.BaseBranch)
            throw new IssuePreparationRejectedException("Interrupted execution has insufficient original intent/base metadata or a changed base branch.");
        var owner = source with { ExecutionId = recovery.WorkspaceExecutionId, AttemptNumber = recovery.WorkspaceAttemptNumber };
        var path = Path.Combine(Path.GetFullPath(worktreeRoot), owner.ExecutionId.ToString("N"));
        await EnsureOriginAsync(ct);
        await EnsureCleanAsync("before Codex interruption recovery", ct);
        await ValidateRecoveryWorkspaceAsync(path, owner, ct);
        _executionDirectory = path;
        _executionId = owner.ExecutionId;
        _resourceExecutionId = source.ExecutionId;
        _resourceAttemptNumber = source.AttemptNumber;
        _featureBranch = owner.FeatureBranch;
        _startingCommit = owner.RecoveryBaseCommit;
        _completedBranch = await SelectCompletedBranchAsync(new GitHubIssue(owner.IssueNumber, owner.IssueTitle,
            owner.OriginalIssueBody ?? "", owner.StartedAtUtc), source.AttemptNumber, source.ExecutionId, ct);
        _forcePostRebaseValidation = true;
    }

    public async Task StartIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        try
        {
            source = NormalizeIntegrationRecoverySource(source);
            if (source.State != "IntegrationConflict" || string.IsNullOrWhiteSpace(source.RecoveryBaseCommit))
                throw new WorkerInfrastructureException("Integration recovery requires a completed integration-conflict execution with preserved commit metadata.");
            await EnsureOriginAsync(ct);
            await EnsureCleanAsync("before integration recovery", ct);
            await ValidateBranchRefAsync(settings.BaseBranch, ct);
            await GitAsync(["switch", "--", settings.BaseBranch], ct);
            await GitAsync(["fetch", "origin", $"refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}"], ct);
            await GitAsync(["merge", "--ff-only", $"refs/remotes/origin/{settings.BaseBranch}"], ct);
            var sourcePath = RecoveryWorkspacePath(source);
            await ValidateRecoveryWorkspaceAsync(sourcePath, source, ct);
            _executionDirectory = sourcePath;
            _featureBranch = source.FeatureBranch;
            _completedBranch = await SelectCompletedBranchAsync(
                new GitHubIssue(source.IssueNumber, source.IssueTitle, "", source.StartedAtUtc),
                source.AttemptNumber, source.ExecutionId, ct, source.RecoveryBaseCommit);
            _startingCommit = source.RecoveryBaseCommit;
            _executionId = source.CodexRecovery?.WorkspaceExecutionId ?? source.ExecutionId;
            _resourceExecutionId = source.ExecutionId;
            _resourceAttemptNumber = source.AttemptNumber;
            _implementationAlreadyCommitted = true;
            _forcePostRebaseValidation = true;
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not safely prepare integration recovery for Issue #{source.IssueNumber}: {ex.Message}", ex); }
    }

    public async Task<string?> ValidateIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        try
        {
            source = NormalizeIntegrationRecoverySource(source);
            if (source.BaseBranch != settings.BaseBranch || source.Repository != repository)
                return "recovery state invalid: repository or integration branch differs from persisted execution configuration";
            if (source.State != "IntegrationConflict" || string.IsNullOrWhiteSpace(source.RecoveryBaseCommit))
                return "recovery state invalid: implementation commit metadata is missing";
            await ValidateRecoveryWorkspaceAsync(RecoveryWorkspacePath(source), source, ct);
            return null;
        }
        catch (WorkerInfrastructureException ex) { return ex.Message; }
    }

    private static ExecutionHistoryEntry NormalizeIntegrationRecoverySource(ExecutionHistoryEntry source)
    {
        if (source.RecoveryState is "integration-conflict" or "cleanup-pending") return source;
        if (source.RecoveryState is not null) return source;
        // Older history rows stored the preserved implementation HEAD in CommitSha.
        return source with { RecoveryBaseCommit = source.RecoveryBaseCommit ?? source.CommitSha };
    }

    public async Task ValidateRecoveryWorkspaceAsync(string source, ExecutionHistoryEntry recovery, CancellationToken ct)
    {
        var expectedPath = RecoveryWorkspacePath(recovery);
        if (!PathEquals(Path.GetFullPath(source), expectedPath))
            throw new IssuePreparationRejectedException("Persisted recovery path does not match its execution ID.");
        var expectedBranch = FeatureBranchName(settings, new GitHubIssue(recovery.IssueNumber, recovery.IssueTitle, "", recovery.StartedAtUtc));
        var workspaceAttempt = recovery.CodexRecovery?.WorkspaceAttemptNumber ?? recovery.AttemptNumber;
        if (workspaceAttempt > 1) expectedBranch += $"-retry-{workspaceAttempt}";
        if (recovery.FeatureBranch != expectedBranch)
            throw new IssuePreparationRejectedException("Persisted recovery branch does not match its Issue identity.");
        if (!Directory.Exists(source)) throw new IssuePreparationRejectedException($"Recoverable execution workspace is missing: {source}");
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IssuePreparationRejectedException("Recovery workspace path is a link; refusing recovery.");
        var top = (await GitAtAsync(source, ["rev-parse", "--show-toplevel"], ct)).StandardOutput.Trim();
        if (!PathEquals(top, source)) throw new IssuePreparationRejectedException("Recovery workspace is not its own Git worktree.");
        var branch = (await GitAtAsync(source, ["branch", "--show-current"], ct)).StandardOutput.Trim();
        var head = (await GitAtAsync(source, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
        if (branch != recovery.FeatureBranch || head != recovery.RecoveryBaseCommit)
            throw new IssuePreparationRejectedException("Recoverable execution workspace does not match its persisted branch and base commit; refusing to resume it.");
        foreach (var operation in new[] { "rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD" })
        {
            var gitPath = (await GitAtAsync(source, ["rev-parse", "--git-path", operation], ct)).StandardOutput.Trim();
            var path = Path.GetFullPath(gitPath, source);
            if (Directory.Exists(path) || File.Exists(path))
                throw new IssuePreparationRejectedException("Recovery workspace has an unfinished Git operation; inspect it before recovery.");
        }
        var status = (await GitAtAsync(source, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
        if (recovery.State == "IntegrationConflict" || recovery.RecoveryState == "integration-conflict" ||
            recovery.RecoveryState == "cleanup-pending" && recovery.RecoveryStatus?.StartsWith("Implementation commit ", StringComparison.Ordinal) == true)
        {
            if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace((await GitAtAsync(source, ["ls-files", "-u"], ct)).StandardOutput))
                throw new IssuePreparationRejectedException("Integration-conflict recovery workspace is not clean; refusing to reconcile it.");
        }
        else if (string.IsNullOrWhiteSpace(status) && recovery.CodexRecovery is null)
            throw new IssuePreparationRejectedException("Persisted recoverable execution workspace has no useful changes; refusing to resume it.");
        var unmerged = (await GitAtAsync(source, ["ls-files", "-u"], ct)).StandardOutput;
        if (!string.IsNullOrWhiteSpace(unmerged))
            throw new IssuePreparationRejectedException("Recoverable execution workspace contains unresolved Git index entries; refusing to resume it.");
        var worktrees = (await GitAsync(["worktree", "list", "--porcelain"], ct)).StandardOutput;
        var registeredPaths = worktrees.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)).Select(line => line[9..].TrimEnd('\r'));
        if (!registeredPaths.Contains(Path.GetFullPath(source), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            throw new IssuePreparationRejectedException("Persisted recovery path is not a registered Git worktree; refusing to resume it.");
    }

    private async Task ApplyRecoveryDeltaAsync(string source, string destination, string baseCommit, CancellationToken ct)
    {
        ValidateRecoveryPaths(source);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "codex-worker-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var index = Path.Combine(temporaryDirectory, "index");
        var patch = Path.Combine(temporaryDirectory, "recovery.patch");
        var environment = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = index };
        try
        {
            // Build a temporary index from the recorded base and stage the preserved worktree
            // into it. This captures tracked edits, deletions and untracked additions without
            // changing the preserved worktree's real index or working tree.
            var readTree = await runner.RunAsync("git", ["read-tree", baseCommit], source,
                TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct, environment);
            if (readTree.ExitCode != 0)
                throw new IssuePreparationRejectedException("Could not initialize the preserved task delta from its recorded base commit.");
            var added = await runner.RunAsync("git", ["add", "--all"], source,
                TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct, environment);
            if (added.ExitCode != 0)
                throw new IssuePreparationRejectedException($"Could not capture the preserved task delta: {Tail(added.StandardError)}");
            var diff = await runner.RunAsync("git", ["diff", "--cached", "--binary", "--full-index", $"--output={patch}", baseCommit], source,
                TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct, environment);
            if (diff.ExitCode != 0)
                throw new IssuePreparationRejectedException($"Could not capture the preserved task delta: {Tail(diff.StandardError)}");

            var applied = await runner.RunAsync("git", ["apply", "--3way", "--index", patch], destination,
                TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct);
            if (applied.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(applied.StandardError) ? applied.StandardOutput : applied.StandardError;
                throw new IssuePreparationRejectedException($"Preserved task changes conflict with the current base; the retry was stopped and the original recovery workspace remains available. {Tail(detail)}");
            }
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateRecoveryPaths(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (Path.GetFullPath(entry).Equals(Path.GetFullPath(Path.Combine(directory, ".git")),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IssuePreparationRejectedException($"Recoverable workspace contains a link that cannot be safely resumed: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
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

    public async Task<GitRecoveryInfo?> PreserveFailedIssueChangesAsync(CancellationToken ct)
    {
        try
        {
            if (_featureBranch is null || _startingCommit is null)
                throw new WorkerInfrastructureException("No worker-owned feature branch is available for safe failure cleanup.");
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            var currentCommit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (currentCommit != _startingCommit)
                throw new WorkerInfrastructureException("Refusing failed-workspace preservation because Git history changed during the Issue.");
            var status = (await GitAtAsync(ExecutionDirectory,
                ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
            if (string.IsNullOrWhiteSpace(status))
            {
                await DiscardUncommittedIssueChangesAsync(ct);
                return null;
            }

            var entries = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var changed = entries.Length;
            var staged = entries.Count(line => line.Length >= 2 &&
                (line[0] != ' ' && line[0] != '?' || line[1] != ' ' && line[1] != '?'));
            return new GitRecoveryInfo(_featureBranch, _startingCommit,
                $"{changed} changed path(s); {staged} staged path(s). Workspace retained for recovery.");
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not safely preserve failed execution work: {ex.Message}", ex); }
    }

    public async Task<GitRecoveryInfo?> PreserveIntegrationConflictAsync(CancellationToken ct)
    {
        try
        {
            if (_featureBranch is null || _executionId is null)
                throw new WorkerInfrastructureException("No worker-owned implementation worktree is available for integration recovery.");
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            var head = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            var status = (await GitAtAsync(ExecutionDirectory, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
            var unmerged = (await GitAtAsync(ExecutionDirectory, ["ls-files", "-u"], ct)).StandardOutput;
            if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(unmerged))
                throw new WorkerInfrastructureException("Integration recovery worktree is not clean after rebase recovery; preserving it for inspection.");
            return new GitRecoveryInfo(_featureBranch, head,
                $"Implementation commit {head} retained in worker worktree {_executionId.Value:N}; integration recovery is available.",
                (await GitAsync(["rev-parse", settings.BaseBranch], ct)).StandardOutput.Trim());
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not safely preserve integration recovery: {ex.Message}", ex); }
    }

    /// <summary>Removes one persisted failed-execution workspace after proving its path, branch, and base identity.</summary>
    public async Task CleanupRecoveryWorkspaceAsync(ExecutionHistoryEntry recovery, CancellationToken ct)
        => await CleanupRecoveryWorkspaceCoreAsync(recovery, force: true, ct);

    private async Task CleanupRecoveryWorkspaceCoreAsync(ExecutionHistoryEntry recovery, bool force, CancellationToken ct)
    {
        try
        {
            if (recovery.RecoveryState is not ("recoverable" or "integration-conflict" or "cleanup-pending" or "missing") ||
                string.IsNullOrWhiteSpace(recovery.RecoveryBaseCommit))
                throw new WorkerInfrastructureException("Recovery metadata is incomplete; refusing cleanup.");
            var ownership = await InspectRecoveryOwnershipAsync(recovery, ct);
            if (ownership.Error is not null)
                throw new WorkerInfrastructureException(ownership.Error.Message);
            var expectedBranch = recovery.FeatureBranch;
            var path = RecoveryWorkspacePath(recovery);
            if (ownership.DirectoryExists)
                await GitAsync(force ? ["worktree", "remove", "--force", path] : ["worktree", "remove", path], ct);

            // show-ref --verify can report an absent ref as a fatal error on some Git
            // versions. Enumerate the expected ref prefix and require an exact match
            // so cleanup remains idempotent without treating malformed refs as absent.
            var expectedRef = $"refs/heads/{expectedBranch}";
            var matchingRefs = (await GitAsync(["for-each-ref", "--format=%(refname)", expectedRef], ct))
                .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (matchingRefs.Contains(expectedRef, StringComparer.Ordinal))
            {
                var checkedOut = ParseWorktrees((await GitAsync(["worktree", "list", "--porcelain"], ct)).StandardOutput)
                    .Any(item => item.Branch == $"refs/heads/{expectedBranch}");
                var tip = (await GitAsync(["rev-parse", $"refs/heads/{expectedBranch}"], ct)).StandardOutput.Trim();
                if (checkedOut || tip != recovery.RecoveryBaseCommit)
                    throw new WorkerInfrastructureException("Recovery branch is in use or its commit changed; refusing cleanup.");
                await GitAsync(["branch", "-D", "--", expectedBranch], ct);
            }
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not safely clean recovery for execution {recovery.ExecutionId}: {ex.Message}", ex); }
    }

    public bool RecoveryWorkspaceExists(ExecutionHistoryEntry recovery) =>
        Directory.Exists(RecoveryWorkspacePath(recovery));

    public string RecoveryWorkspacePath(ExecutionHistoryEntry recovery) =>
        Path.GetFullPath(Path.Combine(worktreeRoot, (recovery.CodexRecovery?.WorkspaceExecutionId ?? recovery.ExecutionId).ToString("N")));

    private static IReadOnlyList<(string Path, string? Branch)> ParseWorktrees(string output)
    {
        var result = new List<(string Path, string? Branch)>();
        foreach (var block in output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var pathLine = lines.FirstOrDefault(line => line.StartsWith("worktree ", StringComparison.Ordinal));
            if (pathLine is null) continue;
            var branchLine = lines.FirstOrDefault(line => line.StartsWith("branch ", StringComparison.Ordinal));
            result.Add((pathLine[9..].TrimEnd('\r'), branchLine is null ? null : branchLine[7..].TrimEnd('\r')));
        }
        return result;
    }

    public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct)
        => await CommitAndIntegrateAsync(issue, validateAfterRebase, (_, _) => Task.FromResult(false), ct);

    public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase,
        Func<string, CancellationToken, Task<bool>> resolveConflict, CancellationToken ct)
        => await CommitAndIntegrateAsync(issue, validateAfterRebase, resolveConflict, (_, _) => Task.FromResult(new IntegrationRepairResult(false, false)), ct);

    public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase,
        Func<string, CancellationToken, Task<bool>> resolveConflict,
        Func<IntegrationRepairContext, CancellationToken, Task<IntegrationRepairResult>> repairIntegration,
        CancellationToken ct, Func<CancellationToken, Task>? ensureAuthority = null)
    {
        try
        {
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            var currentCommit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (currentCommit != _startingCommit) throw new WorkerInfrastructureException("Codex changed Git history; worker requires the original feature branch history.");
            if (!_implementationAlreadyCommitted)
            {
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
            }
            var commit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (settings.AutoMerge)
            {
                var implementationCommit = commit;
                // A remote integrator may advance the base while Codex is repairing. Refresh
                // before fast-forwarding and reconcile again, with a bounded handoff count.
                for (var handoff = 1; ; handoff++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ensureAuthority is not null) await ensureAuthority(ct);
                    await GitAsync(["fetch", "origin", $"refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}"], ct);
                    await GitAsync(["switch", "--", settings.BaseBranch], ct);
                    await GitAsync(["merge", "--ff-only", $"refs/remotes/origin/{settings.BaseBranch}"], ct);
                    var featureContainsBase = await GitAtAsync(ExecutionDirectory,
                        ["merge-base", "--is-ancestor", settings.BaseBranch, $"refs/heads/{_featureBranch}"], ct, [0, 1]);
                    if (featureContainsBase.ExitCode != 0)
                    {
                        await RebaseForIntegrationAsync(issue.Number, resolveConflict, ct);
                        await ValidateRebasedSourceAsync(issue.Number, implementationCommit, validateAfterRebase, repairIntegration, ct);
                    }
                    else if (_forcePostRebaseValidation)
                    {
                        await ValidateRebasedSourceAsync(issue.Number, implementationCommit, validateAfterRebase, repairIntegration, ct);
                    }
                    _forcePostRebaseValidation = false;
                    ct.ThrowIfCancellationRequested();
                    if (ensureAuthority is not null) await ensureAuthority(ct);
                    await GitAsync(["fetch", "origin", $"refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}"], ct);
                    var localBase = (await GitAsync(["rev-parse", settings.BaseBranch], ct)).StandardOutput.Trim();
                    var remoteBase = (await GitAsync(["rev-parse", $"refs/remotes/origin/{settings.BaseBranch}"], ct)).StandardOutput.Trim();
                    var containsCurrentBase = await GitAtAsync(ExecutionDirectory,
                        ["merge-base", "--is-ancestor", localBase, "HEAD"], ct, [0, 1]);
                    if (localBase == remoteBase && containsCurrentBase.ExitCode == 0) break;
                    if (handoff >= 5)
                        throw new GitIntegrationConflictException($"The integration base kept advancing for Issue #{issue.Number}; bounded reconciliation stopped and the implementation was preserved.");
                }
                commit = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
                var prepared = new GitIntegrationResult(true,
                    $"Committed as `{commit[..12]}`. Merged into `{settings.BaseBranch}`.", commit, settings.BaseBranch,
                    settings.PushCompletedBranch ? _completedBranch : settings.DeleteLocalFeatureBranch ? null : _featureBranch,
                    _resourceExecutionId, _resourceAttemptNumber);
                if (_integrationObserver is not null) await _integrationObserver(prepared, false, ct);
                var prePush = (await GitAsync(["rev-parse", settings.BaseBranch], ct)).StandardOutput.Trim();
                await GitAsync(["merge", "--ff-only", $"refs/heads/{_featureBranch}"], ct);
                await PushVerifiedAsync(settings.BaseBranch, commit, prePush, ct, ensureAuthority);
                if (_integrationObserver is not null) await _integrationObserver(prepared, true, ct);
                if (settings.PushCompletedBranch)
                {
                    try
                    {
                        await PushVerifiedAsync(_completedBranch!, commit, null, ct, ensureAuthority);
                    }
                    catch (WorkerInfrastructureException ex) when (!WorkerShutdown.IsCancellation(ex))
                    {
                        throw new GitIntegrationArchiveException(
                            $"Could not preserve Issue #{issue.Number} integration as completed branch '{_completedBranch}'. The implementation remains available for integration recovery.", ex);
                    }
                }
            }
            if (settings.DeleteLocalFeatureBranch && settings.AutoMerge)
            {
                await RemoveExecutionWorktreeAsync(ct);
                if (await GetCurrentBranchAsync(ct) != settings.BaseBranch)
                    throw new WorkerInfrastructureException("Refusing to delete feature branch while it is checked out.");
                if (settings.PushCompletedBranch)
                {
                    try
                    {
                        await GitAsync(["branch", "-m", "--", _featureBranch!, _completedBranch!], ct);
                        _featureBranch = _completedBranch;
                    }
                    catch (WorkerInfrastructureException ex) when (!WorkerShutdown.IsCancellation(ex))
                    {
                        await GitAsync(["worktree", "add", Path.GetFullPath(_executionDirectory!), _featureBranch!], ct);
                        throw new GitIntegrationArchiveException(
                            $"Could not archive Issue #{issue.Number} as completed branch '{_completedBranch}'. The implementation remains available for integration recovery.", ex);
                    }
                }
                else
                {
                    await DeleteFeatureBranchIfUnownedAsync(_featureBranch!, ct);
                }
                _executionDirectory = null;
                _executionId = null;
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
                completedBranch, _resourceExecutionId, _resourceAttemptNumber);
        }
        catch (GitIntegrationConflictException) { throw; }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Git commit/integration for Issue #{issue.Number} failed; checkout state is preserved for diagnosis: {ex.Message}", ex); }
    }

    private async Task ValidateRebasedSourceAsync(int issueNumber, string implementationCommit,
        Func<CancellationToken, Task<ValidationResult>> validate,
        Func<IntegrationRepairContext, CancellationToken, Task<IntegrationRepairResult>> repair, CancellationToken ct)
    {
        var head = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
        var diagnostics = new List<string>();
        var attempt = 0;
        var repairing = false;
        var incompleteRepair = false;
        while (true)
        {
            attempt++;
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            var current = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            var status = (await GitAtAsync(ExecutionDirectory, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
            if (current != head || !string.IsNullOrWhiteSpace(status))
                throw new WorkerInfrastructureException("Rebased validation source changed; integration stopped and workspace preserved for inspection.");
            var result = await validate(ct);
            // Verify again even after success: validation must not mutate the source being integrated.
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            current = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            status = (await GitAtAsync(ExecutionDirectory, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
            if (current != head || !string.IsNullOrWhiteSpace(status))
                throw new WorkerInfrastructureException("Rebased validation source changed; integration stopped and workspace preserved for inspection.");
            if (result.Succeeded)
            {
                if (incompleteRepair)
                    throw new GitIntegrationConflictException($"Codex could not safely complete integration repair for Issue #{issueNumber}, although validation passed. The implementation and repair edits were preserved.");
                return;
            }
            var failure = result.Failure ?? throw new WorkerInfrastructureException("Failed validation has no diagnostics.");
            diagnostics.Add($"{(repairing ? "Validation after integration repair" : $"Attempt {attempt}/2")}: {failure.ToSummary()}");
            if (!repairing && attempt < 2) continue;

            var baseCommit = (await GitAsync(["rev-parse", settings.BaseBranch], ct)).StandardOutput.Trim();
            var context = new IntegrationRepairContext(failure, settings.BaseBranch,
                _startingCommit ?? throw new WorkerInfrastructureException("Integration has no starting commit."),
                implementationCommit, baseCommit, head);
            var repaired = incompleteRepair ? new IntegrationRepairResult(false, false) : await repair(context, ct);
            if (!repaired.Attempted)
                throw new PostRebaseValidationException($"Validation after rebase failed for Issue #{issueNumber}; integration repair was unavailable, incomplete or exhausted. Integration stopped and the implementation was preserved.\n\n" +
                    string.Join("\n\n", diagnostics), failure);
            await EnsureBranchAsync(_featureBranch, ct, ExecutionDirectory);
            await EnsureWorktreeOwnedAsync(ct);
            current = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (current != head || !string.IsNullOrWhiteSpace((await GitAtAsync(ExecutionDirectory, ["ls-files", "-u"], ct)).StandardOutput))
                throw new WorkerInfrastructureException("Codex integration repair changed Git history or left unresolved index entries; workspace preserved for inspection.");
            // Commit even an incomplete repair's useful edits so verified integration recovery
            // can reuse the clean owned worktree. The original implementation remains in ancestry.
            await GitAtAsync(ExecutionDirectory, ["add", "--all"], ct);
            var staged = await GitAtAsync(ExecutionDirectory, ["diff", "--cached", "--quiet"], ct, [0, 1]);
            if (staged.ExitCode != 0)
                await GitAtAsync(ExecutionDirectory, ["commit", "-m", $"Reconcile integration for #{issueNumber}"], ct);
            head = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            incompleteRepair = !repaired.Completed;
            repairing = true;
        }
    }

    private async Task RebaseForIntegrationAsync(int issueNumber, CancellationToken ct) =>
        await RebaseForIntegrationAsync(issueNumber, (_, _) => Task.FromResult(false), ct);

    private async Task RebaseForIntegrationAsync(int issueNumber,
        Func<string, CancellationToken, Task<bool>> resolveConflict, CancellationToken ct)
    {
        var before = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
        var result = await runner.RunAsync("git", ["rebase", settings.BaseBranch], ExecutionDirectory,
            TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct);
        if (result.ExitCode == 0) return;

        var unmerged = (await GitAtAsync(ExecutionDirectory, ["ls-files", "-u"], ct)).StandardOutput;
        if (string.IsNullOrWhiteSpace(unmerged))
            throw new WorkerInfrastructureException($"Git rebase for Issue #{issueNumber} failed without a confirmed merge conflict; repository state is preserved for inspection.");

        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        if (await resolveConflict(Tail(detail), ct))
        {
            await GitAtAsync(ExecutionDirectory, ["add", "--all"], ct);
            var remaining = (await GitAtAsync(ExecutionDirectory, ["ls-files", "-u"], ct)).StandardOutput;
            if (string.IsNullOrWhiteSpace(remaining))
            {
                var continued = await runner.RunAsync("git", ["rebase", "--continue"], ExecutionDirectory,
                    TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct,
                    new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" });
                if (continued.ExitCode == 0) return;
                var unresolved = (await GitAtAsync(ExecutionDirectory, ["ls-files", "-u"], ct)).StandardOutput;
                if (!string.IsNullOrWhiteSpace(unresolved))
                    // A repair adds another Worker-owned commit. Continuing the first resolved
                    // conflict may encounter a conflict in that later commit. Abort through the
                    // verified recovery path below, retaining the whole implementation lineage.
                    detail = $"Codex resolution left conflicts unresolved. {Tail(continued.StandardError)}";

                // Git can report a nonzero continuation result after it has already finished
                // applying the commit. Treat it as complete only when the rebase state is gone
                // and the resulting feature tip contains the integration base.
                var gitPath = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "--git-path", "rebase-merge"], ct)).StandardOutput.Trim();
                var applyPath = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "--git-path", "rebase-apply"], ct)).StandardOutput.Trim();
                var rebaseInProgress = Directory.Exists(Path.GetFullPath(gitPath, ExecutionDirectory)) ||
                    Directory.Exists(Path.GetFullPath(applyPath, ExecutionDirectory));
                var containsBase = await GitAtAsync(ExecutionDirectory,
                    ["merge-base", "--is-ancestor", settings.BaseBranch, "HEAD"], ct, [0, 1]);
                var status = (await GitAtAsync(ExecutionDirectory,
                    ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
                if (!rebaseInProgress && containsBase.ExitCode == 0 && string.IsNullOrWhiteSpace(status)) return;
            }
        }
        try
        {
            await GitAtAsync(ExecutionDirectory, ["rebase", "--abort"], ct);
            var after = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            var status = (await GitAtAsync(ExecutionDirectory, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput;
            var gitPath = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "--git-path", "rebase-merge"], ct)).StandardOutput.Trim();
            var applyPath = (await GitAtAsync(ExecutionDirectory, ["rev-parse", "--git-path", "rebase-apply"], ct)).StandardOutput.Trim();
            if (after != before || !string.IsNullOrWhiteSpace(status) ||
                Directory.Exists(Path.GetFullPath(gitPath, ExecutionDirectory)) ||
                Directory.Exists(Path.GetFullPath(applyPath, ExecutionDirectory)))
                throw new WorkerInfrastructureException($"Could not verify clean recovery after the Issue #{issueNumber} rebase conflict.");
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception ex)
        {
            throw new WorkerInfrastructureException($"Could not safely abort the Issue #{issueNumber} rebase conflict: {ex.Message}", ex);
        }
        throw new GitIntegrationConflictException($"Integration conflict for Issue #{issueNumber}; the rebase was aborted and its implementation worktree was preserved. {Tail(detail)}");
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
            throw new ProjectCheckoutDirtyException($"Project checkout is dirty {phase}; refusing to proceed.");
    }

    private async Task ValidateBranchRefAsync(string branch, CancellationToken ct)
    {
        if (!await IsValidBranchRefAsync(runner, directory, branch, TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct))
            throw new WorkerInfrastructureException($"Invalid Git branch ref generated from configuration/title: '{branch}'.");
    }

    private async Task<string> SelectCompletedBranchAsync(GitHubIssue issue, int attemptNumber, Guid executionId,
        CancellationToken ct, string? expectedCommit = null)
    {
        var standard = CompletedBranchName(settings, issue);
        var preferred = attemptNumber <= 1 ? standard : $"{standard}-retry-{attemptNumber}";
        await ValidateBranchRefAsync(preferred, ct);
        if (!settings.PushCompletedBranch) return preferred;
        if (await IsCompletedBranchAvailableOrOwnedAsync(preferred, expectedCommit, ct)) return preferred;

        var executionBranch = $"{preferred}-execution-{executionId:N}";
        await ValidateBranchRefAsync(executionBranch, ct);
        if (await IsCompletedBranchAvailableOrOwnedAsync(executionBranch, expectedCommit, ct)) return executionBranch;

        throw new GitIntegrationArchiveException(
            $"Completed branch names for Issue #{issue.Number} are already owned by other commits; no historical branch was changed.",
            new InvalidOperationException("Completed branch candidates are occupied."));
    }

    private async Task<bool> IsCompletedBranchAvailableOrOwnedAsync(string branch, string? expectedCommit, CancellationToken ct)
    {
        var expectedRef = $"refs/heads/{branch}";
        var localRefs = (await GitAsync(["for-each-ref", "--format=%(refname)", expectedRef], ct))
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (localRefs.Contains(expectedRef, StringComparer.Ordinal))
        {
            var localCommit = (await GitAsync(["rev-parse", expectedRef], ct)).StandardOutput.Trim();
            if (expectedCommit is null || !string.Equals(localCommit, expectedCommit, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var remote = await GitAsync(["ls-remote", "--heads", "origin", $"refs/heads/{branch}"], ct);
        var remoteCommit = remote.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', StringSplitOptions.TrimEntries))
            .Where(fields => fields.Length == 2 && string.Equals(fields[1], expectedRef, StringComparison.Ordinal))
            .Select(fields => fields[0])
            .FirstOrDefault();
        if (remoteCommit is not null && (expectedCommit is null ||
            !string.Equals(remoteCommit, expectedCommit, StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private async Task<string> GetCurrentBranchAsync(CancellationToken ct) =>
        (await GitAsync(["branch", "--show-current"], ct)).StandardOutput.Trim();

    private async Task AcquireWorkerLockAsync(CancellationToken ct)
    {
        if (_workerLock is not null) return;
        var gitDir = (await GitAsync(["rev-parse", "--absolute-git-dir"], ct)).StandardOutput.Trim();
        try { _workerLock = new FileStream(Path.Combine(gitDir, "codex-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new WorkerInfrastructureException("Another codex-worker process holds the checkout lock; stopping.", ex); }
    }

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

    private async Task PushVerifiedAsync(string branch, string commit, string? previous, CancellationToken ct,
        Func<CancellationToken, Task>? ensureAuthority = null)
    {
        var policy = new GitHubRetryPolicy();
        await policy.ExecuteAsync("Git push", async token =>
        {
            if (ensureAuthority is not null) await ensureAuthority(token);
            return await GitAsync(["push", "origin", $"{commit}:refs/heads/{branch}"], token);
        }, ex => ex is WorkerInfrastructureException &&
            GitHubClient.ClassifyFailure(ex.Message) == GitHubFailureKind.TransientProvider, ct, async token =>
        {
            await EnsureOriginAsync(token);
            var result = await GitAsync(["ls-remote", "--heads", "origin", $"refs/heads/{branch}"], token);
            var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 1 && lines[0] == $"{commit}\trefs/heads/{branch}") return true;
            if (previous is null && lines.Length == 0 || previous is not null && lines.Length == 1 &&
                lines[0] == $"{previous}\trefs/heads/{branch}") return false;
            throw new WorkerInfrastructureException("Remote push state changed unexpectedly; reconciliation is required.");
        }, new ProcessResult(0, "", ""));
    }

    private async Task<ProcessResult> GitAsync(IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null)
    {
        var arguments = args.ToArray();
        if (arguments.FirstOrDefault() is not ("fetch" or "ls-remote"))
            return await GitAtAsync(directory, arguments, ct, allowExitCodes);
        return await new GitHubRetryPolicy().ExecuteAsync("Git remote read",
            token => GitAtAsync(directory, arguments, token, allowExitCodes),
            ex => ex is WorkerInfrastructureException &&
                GitHubClient.ClassifyFailure(ex.Message) == GitHubFailureKind.TransientProvider, ct);
    }

    private async Task<ProcessResult> GitAtAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null, bool readOnly = false)
    {
        var managedCredentials = false;
        var credentialOperation = args.FirstOrDefault() is "clone" or "ls-remote" or "fetch" or "pull" or "push";
        try
        {
            var environment = new Dictionary<string, string?>(await CodexProvisioning.NodeGitHubSetup.GitHttpsEnvironmentAsync(ct,
                requireManagedAuthentication: requireManagedAuthentication));
            managedCredentials = environment.ContainsKey("GH_CONFIG_DIR");
            if (readOnly)
            {
                environment["GIT_OPTIONAL_LOCKS"] = "0";
                environment["GIT_NO_LAZY_FETCH"] = "1";
                environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            }
            var result = await runner.RunAsync("git", args, workingDirectory, TimeSpan.FromSeconds(timeouts.GitTimeoutSeconds), ct,
                environment: environment);
            if (result.ExitCode != 0 && !(allowExitCodes?.Contains(result.ExitCode) ?? false))
            {
                var detail = managedCredentials && credentialOperation ? "Managed Git remote command failed; raw credential-helper diagnostics are withheld."
                    : string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                var transient = credentialOperation && GitHubClient.ClassifyFailure(result.StandardError) == GitHubFailureKind.TransientProvider;
                throw new WorkerInfrastructureException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}). " +
                    (transient ? "Transient provider failure (temporarily unavailable). " : "") + Tail(detail));
            }
            return result;
        }
        catch (OperationCanceledException ex) { throw new WorkerInfrastructureException($"Git operation was cancelled; repository state may be uncertain: {string.Join(' ', args)}", ex); }
        catch (WorkerInfrastructureException) { throw; }
        catch (ProcessTimeoutException) when (managedCredentials && credentialOperation)
        {
            throw new WorkerInfrastructureException($"Managed Git command timed out; repository state may be uncertain: {string.Join(' ', args)}.");
        }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Git command failed or timed out: {string.Join(' ', args)}. {ex.Message}", ex); }
    }

    private static string Tail(string value) => value.Length <= 1200 ? value : value[^1200..];
}

internal static class GitRemoteAuthenticationProbe
{
    public static async Task ValidateAsync(Func<IEnumerable<string>, CancellationToken, Task> run,
        string repository, string featurePrefix, CancellationToken cancellationToken)
    {
        var check = "remote read";
        try
        {
            await run(["ls-remote", "--exit-code", "origin", "HEAD"], cancellationToken);
            check = "dry-run Git push/write";
            var probeBranch = $"{featurePrefix}auth-check-{Guid.NewGuid():N}";
            await run(["push", "--dry-run", "--porcelain", "origin", $"HEAD:refs/heads/{probeBranch}"], cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && WorkerShutdown.IsCancellation(ex))
        {
            throw new OperationCanceledException("Git authentication check interrupted by cancellation.", ex, cancellationToken);
        }
        catch (Exception ex) when (ex is WorkerInfrastructureException or ProcessTimeoutException or InvalidOperationException)
        {
            _ = ex;
            throw new WorkerInfrastructureException($"Git repository authentication is unavailable for '{repository}' ({check} check failed).");
        }
    }
}
