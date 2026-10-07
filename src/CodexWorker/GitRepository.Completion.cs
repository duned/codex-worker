namespace CodexWorker;

public sealed partial class GitRepository
{
    public async Task<bool> VerifyRemoteIntegrationAsync(ExecutionHistoryEntry entry, CancellationToken ct)
    {
        if (!settings.AutoMerge || entry.Repository != repository || entry.BaseBranch != settings.BaseBranch ||
            entry.IntegrationBranch != settings.BaseBranch || entry.CommitSha is not { } commit ||
            !IsCommitId(commit))
            throw new WorkerInfrastructureException("Exact integration commit/base provenance is missing or incompatible; manual reconciliation is required.");
        await EnsureOriginAsync(ct);
        await ValidateBranchRefAsync(settings.BaseBranch, ct);
        // Fetch only. Never merge, push, reset or substitute a local workspace for remote proof.
        await GitAsync(["fetch", "origin", $"refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}"], ct);
        var exists = await GitAtAsync(directory, ["cat-file", "-e", $"{commit}^{{commit}}"], ct, [0, 1, 128], readOnly: true);
        if (exists.ExitCode != 0) return false;
        return (await GitAtAsync(directory, ["merge-base", "--is-ancestor", commit,
            $"refs/remotes/origin/{settings.BaseBranch}"], ct, [0, 1], readOnly: true)).ExitCode == 0;
    }

    public async Task FinishIntegratedExecutionAsync(ExecutionHistoryEntry entry, CancellationToken ct)
    {
        if (!await VerifyRemoteIntegrationAsync(entry, ct))
            throw new WorkerInfrastructureException("Remote no longer contains the validated integration commit; preserve completion resources.");
        var integration = ExecutionCompletion.Read(entry).Report.Integration;
        if (integration?.ResourceExecutionId is not { } resourceId || integration.ResourceAttemptNumber is not > 0)
            throw new WorkerInfrastructureException("Integration resource ownership is missing; preserve completion resources.");
        var owned = entry with { ExecutionId = resourceId, AttemptNumber = integration.ResourceAttemptNumber.Value,
            RecoveryState = "cleanup-pending", RecoveryBaseCommit = entry.CommitSha };
        var ownership = await InspectRecoveryOwnershipAsync(owned, ct);
        if (ownership.Error is not null) throw new WorkerInfrastructureException(ownership.Error.Message);
        if (settings.PushCompletedBranch)
        {
            var completed = integration.CompletedBranch;
            var standard = CompletedBranchName(settings, new GitHubIssue(entry.IssueNumber, entry.IssueTitle, "", entry.StartedAtUtc));
            var preferred = owned.AttemptNumber <= 1 ? standard : $"{standard}-retry-{owned.AttemptNumber}";
            if (completed is null || completed != preferred && completed != $"{preferred}-execution-{resourceId:N}")
                throw new WorkerInfrastructureException("Completed branch identity is incompatible; preserve completion resources.");
            await ValidateBranchRefAsync(completed, ct);
            var remote = (await GitAsync(["ls-remote", "--heads", "origin", $"refs/heads/{completed}"], ct)).StandardOutput;
            var lines = remote.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
            {
                // The configured archive is a pending completion operation, not a base retry.
                // Push only the proven commit to its persisted, Worker-owned archive name.
                await GitAsync(["push", "origin", $"{entry.CommitSha}:refs/heads/{completed}"], ct);
                remote = (await GitAsync(["ls-remote", "--heads", "origin", $"refs/heads/{completed}"], ct)).StandardOutput;
                lines = remote.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            if (lines.Length != 1 || lines[0] != $"{entry.CommitSha}\trefs/heads/{completed}")
                throw new WorkerInfrastructureException("Completed branch archive is unverified; reconcile that exact archive manually before cleanup.");
            if (ownership.DirectoryExists)
                await GitAsync(["worktree", "remove", RecoveryWorkspacePath(owned)], ct);
            if (settings.DeleteLocalFeatureBranch && ownership.BranchExists)
            {
                var existing = (await GitAsync(["for-each-ref", "--format=%(refname)", $"refs/heads/{completed}"], ct)).StandardOutput.Trim();
                if (existing.Length != 0)
                    throw new WorkerInfrastructureException("Completed local archive already exists alongside the feature branch; manual reconciliation is required.");
                await GitAsync(["branch", "-m", "--", entry.FeatureBranch, completed], ct);
            }
        }
        else if (settings.DeleteLocalFeatureBranch)
            await CleanupRecoveryWorkspaceCoreAsync(owned, force: false, ct);
        else if (ownership.DirectoryExists)
            await GitAsync(["worktree", "remove", RecoveryWorkspacePath(owned)], ct);
    }
}
