namespace CodexWorker;

/// <summary>A point-in-time decision, never authorization to mutate without reinspection under the repository gate.</summary>
public sealed record ExecutionCleanupInspection(Guid ExecutionId, string Decision, string ReasonCode, string Message,
    string? AuthoritativeBase = null, string? AuthoritativeBaseCommit = null, Guid? NewerExecutionId = null);

public sealed partial class GitRepository
{
    public async Task<ExecutionCleanupInspection> InspectOperatorCleanupAsync(ExecutionHistoryEntry entry,
        IReadOnlyList<ExecutionHistoryEntry> history, CancellationToken ct)
    {
        var result = await InspectCleanupAsync(entry, history, ct);
        if (result.Decision != "safe") return result;
        if (entry.State is "InfrastructureFailure" or "Cancelled")
            return result with { Decision = "review", ReasonCode = "uncertain-execution", Message = "Interrupted or infrastructure-failed execution requires reconciliation; preserve its resources." };
        if (result.ReasonCode == "already-clean") return result;
        if (result.NewerExecutionId is null && (entry.RecoveryState is "recoverable" or "integration-conflict" or "missing" ||
            entry.State == "IntegrationConflict" && entry.RecoveryState != "integration-recovered"))
            return result with { Decision = "keep", ReasonCode = "authoritative-recovery", Message = "This is the current recovery attempt; preserve it until recovery is superseded or reconciled." };
        return result;
    }

    /// <summary>Caller must hold a drained Worker maintenance reservation through history recording.</summary>
    public async Task<ExecutionCleanupInspection> CleanupStaleExecutionAsync(ExecutionHistoryEntry entry,
        IReadOnlyList<ExecutionHistoryEntry> history, CancellationToken ct)
    {
        var inspection = await InspectOperatorCleanupAsync(entry, history, ct);
        if (inspection.Decision != "safe" || inspection.ReasonCode == "already-clean") return inspection;
        // Never force-remove operator-selected worktrees. Git also refuses new local edits or locks.
        await CleanupRecoveryWorkspaceCoreAsync(entry with { RecoveryState = "cleanup-pending" }, force: false, ct);
        return inspection;
    }

    private sealed record OwnershipError(string Code, string Message);
    private sealed record RecoveryOwnership(bool DirectoryExists, bool BranchExists, OwnershipError? Error = null);

    // Shared by inspection and the existing explicitly requested recovery discard operation.
    private async Task<RecoveryOwnership> InspectRecoveryOwnershipAsync(ExecutionHistoryEntry entry, CancellationToken ct)
    {
        RecoveryOwnership Reject(string code, string message) => new(false, false, new(code, message));
        var expectedBranch = FeatureBranchName(settings, new GitHubIssue(entry.IssueNumber, entry.IssueTitle, "", entry.StartedAtUtc));
        var workspaceAttempt = entry.CodexRecovery?.WorkspaceAttemptNumber ?? entry.AttemptNumber;
        if (workspaceAttempt > 1) expectedBranch += $"-retry-{workspaceAttempt}";
        if (entry.ExecutionId == Guid.Empty || entry.IssueNumber < 1 || entry.AttemptNumber < 1 ||
            entry.Repository != repository || entry.BaseBranch != settings.BaseBranch || entry.FeatureBranch != expectedBranch)
            return Reject("ownership-metadata-mismatch", "Persisted repository, base, branch or Issue identity differs from configured ownership.");
        var root = Path.GetFullPath(worktreeRoot);
        var path = RecoveryWorkspacePath(entry);
        if (IsWithin(Path.GetFullPath(directory), root) || !IsWithin(root, path) || PathEquals(root, path))
            return Reject("unsafe-worktree-path", "Worktree path is outside the managed root or the root overlaps the checkout.");
        var registrations = ParseWorktrees((await InspectionGitAsync(["worktree", "list", "--porcelain"], ct)).StandardOutput);
        var registered = registrations.Where(item => PathEquals(item.Path, path)).ToArray();
        var exists = Directory.Exists(path);
        if (!exists && (registered.Length != 0 || File.Exists(path)))
            return Reject("missing-registered-worktree", "Workspace is missing but its Git registration or a filesystem entry remains.");
        if (exists)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || registered.Length != 1 ||
                registered[0].Branch != $"refs/heads/{expectedBranch}")
                return Reject("worktree-registration-mismatch", "Workspace is linked or not registered to its persisted branch.");
            var top = (await InspectionGitAtAsync(path, ["rev-parse", "--show-toplevel"], ct)).StandardOutput.Trim();
            var branch = (await InspectionGitAtAsync(path, ["branch", "--show-current"], ct)).StandardOutput.Trim();
            var head = (await InspectionGitAtAsync(path, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim();
            if (!PathEquals(top, path) || branch != expectedBranch || head != entry.RecoveryBaseCommit)
                return Reject("worktree-head-mismatch", "Workspace branch or HEAD differs from persisted ownership metadata.");
        }
        var expectedRef = $"refs/heads/{expectedBranch}";
        var refs = (await InspectionGitAsync(["for-each-ref", "--format=%(refname)", expectedRef], ct)).StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var branchExists = refs.Contains(expectedRef, StringComparer.Ordinal);
        if (branchExists)
        {
            var tip = (await InspectionGitAsync(["rev-parse", expectedRef], ct)).StandardOutput.Trim();
            if (tip != entry.RecoveryBaseCommit || registrations.Any(item => item.Branch == expectedRef && !PathEquals(item.Path, path)))
                return Reject("branch-head-mismatch", "Branch tip changed or the branch is checked out in another workspace.");
        }
        return new(exists, branchExists);
    }

    public Task<ExecutionCleanupInspection> InspectCleanupAsync(ExecutionHistoryEntry entry,
        IReadOnlyList<ExecutionHistoryEntry> history, CancellationToken ct) => InspectCleanupCoreAsync(entry, history, false, ct);

    internal async Task<ExecutionCleanupInspection> InspectAcknowledgedCleanupAsync(ExecutionHistoryEntry entry,
        IReadOnlyList<ExecutionHistoryEntry> history, CancellationToken ct)
    {
        var inspection = await InspectCleanupCoreAsync(entry, history, true, ct);
        return inspection.Decision == "safe" && inspection.ReasonCode != "already-clean"
            ? inspection with { Decision = "review", ReasonCode = "acknowledged-resources-remain", Message = "Acknowledgment retains resources; use the existing recovery protocol before cleanup." }
            : inspection;
    }

    private async Task<ExecutionCleanupInspection> InspectCleanupCoreAsync(ExecutionHistoryEntry entry,
        IReadOnlyList<ExecutionHistoryEntry> history, bool acknowledged, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var related = history.Where(e => e.Repository == entry.Repository && e.Project == entry.Project && e.IssueNumber == entry.IssueNumber).ToArray();
        var newer = related.Where(e => e.ExecutionId != entry.ExecutionId &&
            (e.AttemptNumber > entry.AttemptNumber || e.StartedAtUtc > entry.StartedAtUtc || e.RetryOfExecutionId == entry.ExecutionId))
            .OrderByDescending(e => e.AttemptNumber).ThenByDescending(e => e.StartedAtUtc).FirstOrDefault();
        ExecutionCleanupInspection Result(string decision, string code, string message, string? baseCommit = null) =>
            new(entry.ExecutionId, decision, code, message, $"origin:refs/heads/{settings.BaseBranch}", baseCommit, newer?.ExecutionId);
        static bool Terminal(ExecutionHistoryEntry e) => e.CompletedAtUtc is not null &&
            e.State is "Completed" or "Blocked" or "Failed" or "IntegrationConflict" or "InfrastructureFailure" or "Cancelled" or "Superseded";
        if (history.Count(e => e.ExecutionId == entry.ExecutionId) != 1 || !history.Contains(entry))
            return Result("review", "history-inconsistent", "Selected execution does not match one authoritative history entry.");
        if (!Enum.TryParse<ExecutionState>(entry.State, out var state) || !Enum.IsDefined(state))
            return Result("review", "unknown-execution-state", "Execution state is unknown; preserve resources for review.");
        if (!Terminal(entry)) return Result("active", "execution-active", "Execution has no confirmed terminal state.");
        if (entry.IntegrationRecoveryClaim is not null || related.Any(e => e.ExecutionId != entry.ExecutionId && !Terminal(e)))
            return Result("active", "recovery-or-attempt-active", "Recovery is claimed or another attempt for this Issue is active.");
        if (entry.RetryOfExecutionId is { } parentId && !related.Any(e => e.ExecutionId == parentId && e.AttemptNumber == entry.AttemptNumber - 1) ||
            entry.RetryOfExecutionId is null && (entry.Resumed || entry.AttemptNumber > 1 &&
                !related.Any(e => e.ExecutionId != entry.ExecutionId && e.AttemptNumber == entry.AttemptNumber - 1)) ||
            related.Any(e => e.ExecutionId != entry.ExecutionId && e.AttemptNumber == entry.AttemptNumber))
            return Result("review", "lineage-inconsistent", "Attempt lineage is missing or inconsistent.");
        if (entry.RecoveryState is "codex-interrupted" or "codex-resuming" or "codex-recovery-exhausted" or "codex-recovery-inspection-required")
            return Result("keep", "codex-interruption-recovery", "Codex interruption resources are retained for continuation or inspection; automatic cleanup is not authorized.");
        if (!(acknowledged && entry.RecoveryState == "operator-acknowledged") && entry.RecoveryState is not (null or "recoverable" or "integration-conflict" or "cleanup-pending" or "missing" or
            "expired-cleaned" or "resumed-cleaned" or "discarded" or "cleaned-no-changes" or "superseded" or "integration-recovered" or "codex-recovered" or "codex-recovery-finished" or "operator-cleaned" or "completion-reconciled"))
            return Result("review", "unknown-recovery-state", "Recovery metadata is unknown or requires reconciliation.");
        try
        {
            var ownership = await InspectRecoveryOwnershipAsync(entry, ct);
            if (ownership.Error is { } error) return Result("review", error.Code, error.Message);
            if (!ownership.DirectoryExists && !ownership.BranchExists)
            {
                if (acknowledged && entry.RecoveryState == "operator-acknowledged" || ExecutionCompletion.IsSettled(entry) || entry.RecoveryState is null or "expired-cleaned" or "resumed-cleaned" or "discarded" or "cleaned-no-changes" or
                    "superseded" or "integration-recovered" or "codex-recovered" or "codex-recovery-finished" or "cleanup-pending" or "operator-cleaned")
                    return Result("safe", "already-clean", "No managed workspace, Git registration or feature branch remains.");
                return Result("review", "missing-recovery-resources", "History still requires recovery resources that no longer exist.");
            }
            if (!ownership.DirectoryExists && entry.RecoveryState is "recoverable" or "integration-conflict" or "missing")
                return Result("review", "missing-recovery-workspace", "Recovery still requires a workspace but only its branch remains.");
            if (entry.RecoveryState is null || !IsCommitId(entry.RecoveryBaseCommit) ||
                entry.CommitSha is not null && !IsCommitId(entry.CommitSha))
                return Result("review", "incomplete-recovery-metadata", "Retained resources lack valid persisted commit metadata.");
            if (ownership.DirectoryExists)
            {
                foreach (var operation in new[] { "rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD" })
                {
                    var gitPath = (await InspectionGitAtAsync(RecoveryWorkspacePath(entry), ["rev-parse", "--git-path", operation], ct)).StandardOutput.Trim();
                    var path = Path.GetFullPath(gitPath, RecoveryWorkspacePath(entry));
                    if (Directory.Exists(path) || File.Exists(path))
                        return Result("review", "unfinished-git-operation", "Workspace contains an unfinished Git operation.");
                }
                if (!string.IsNullOrWhiteSpace((await InspectionGitAtAsync(RecoveryWorkspacePath(entry), ["ls-files", "-u"], ct)).StandardOutput))
                    return Result("review", "unresolved-index", "Workspace has unresolved index entries; preserve it for review.");
                var status = (await InspectionGitAtAsync(RecoveryWorkspacePath(entry), ["status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching"], ct)).StandardOutput;
                if (!string.IsNullOrWhiteSpace(status))
                    return Result("keep", newer is null ? "recovery-changes-required" : "superseded-unmerged-changes",
                        "Workspace contains uncommitted changes; integration cannot prove they are disposable.");
                if (entry.RecoveryState == "recoverable")
                    return Result("review", "recovery-changes-missing", "Recoverable history expects useful workspace changes but the workspace is clean.");
            }
            var baseRef = $"refs/heads/{settings.BaseBranch}";
            await EnsureOriginAsync(ct);
            // Query the remote ref without fetching or changing local refs. Cached origin/* is not proof of freshness.
            var remote = (await InspectionGitAsync(["ls-remote", "--exit-code", "origin", baseRef], ct)).StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var fields = remote.Length == 1 ? remote[0].Split('\t') : [];
            if (fields.Length != 2 || fields[1] != baseRef || !IsCommitId(fields[0]))
                return Result("review", "authoritative-base-unavailable", "Current authoritative remote base could not be identified.");
            var baseCommit = fields[0];
            if ((await InspectionGitAsync(["cat-file", "-e", $"{baseCommit}^{{commit}}"], ct, [0, 1, 128])).ExitCode != 0)
                return Result("review", "authoritative-base-unavailable", "Current remote base is not available locally; Worker must refresh it before integration can be proved.", baseCommit);
            foreach (var commit in new[] { entry.RecoveryBaseCommit, entry.CommitSha }.OfType<string>().Distinct())
            {
                var known = await InspectionGitAsync(["cat-file", "-e", $"{commit}^{{commit}}"], ct, [0, 1, 128]);
                if (known.ExitCode != 0) return Result("review", "commit-unavailable", "A persisted commit cannot be resolved locally.", baseCommit);
                var reachable = await InspectionGitAsync(["merge-base", "--is-ancestor", commit, baseCommit], ct, [0, 1]);
                if (reachable.ExitCode != 0)
                    return Result("keep", newer is null ? "unmerged-commit-required" : "superseded-unmerged-commit",
                        "A retained commit is not reachable from the authoritative base; preserve it for recovery.", baseCommit);
            }
            return Result("safe", "integrated", "Clean retained commits are reachable from the freshly observed authoritative origin base.", baseCommit);
        }
        catch (WorkerInfrastructureException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (ex is WorkerInfrastructureException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Never expose raw process diagnostics through the inspection API.
            return Result("review", "inspection-unavailable", "Git ownership or integration proof could not be read; preserve resources for review.");
        }
    }

    private Task<ProcessResult> InspectionGitAsync(IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null) =>
        InspectionGitAtAsync(directory, args, ct, allowExitCodes);

    private Task<ProcessResult> InspectionGitAtAsync(string path, IEnumerable<string> args, CancellationToken ct, int[]? allowExitCodes = null) =>
        GitAtAsync(path, args, ct, allowExitCodes, readOnly: true);

    internal static bool IsCommitId(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
