using System.Text.Json;

namespace CodexWorker;

public sealed record InterruptedWorkspaceInspection(string? Blocker = null, string? Head = null,
    string? RemoteBase = null, bool HasChanges = false, bool NotIntegrated = false, bool NoWorkspace = false);

public sealed partial class GitRepository
{
    public async Task<InterruptedWorkspaceInspection> InspectInterruptedExecutionAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        var beforeWorkspace = BeforeWorkspaceCreation(source);
        if (source.ServerExecutionId is not null || source.AssignmentId is not null || source.OwnershipGeneration is not null ||
            source.CompletionJson is not null || source.CommitSha is not null || source.IntegrationBranch is not null ||
            source.CompletedBranch is not null || source.Repository != repository || source.BaseBranch != settings.BaseBranch ||
            (!beforeWorkspace && !IsCommitId(source.RecoveryBaseCommit)))
            return new("integration-or-ownership-metadata-unproven");
        try
        {
            var path = RecoveryWorkspacePath(source);
            var processBlocker = await InspectExecutionProcessesAsync(source, ct);
            if (processBlocker is not null) return new(processBlocker);
            var exists = Directory.Exists(path);
            if (beforeWorkspace)
            {
                if (exists) return new("preparation-workspace-ownership-unproven");
                var remoteBeforeWork = await GetIntegrationBaseAsync(ct);
                if (!IsCommitId(remoteBeforeWork)) return new("authoritative-base-unavailable");
                var unused = await InspectRecoveryOwnershipAsync(source with { RecoveryBaseCommit = remoteBeforeWork }, ct);
                if (unused.Error is { } unusedError) return new(unusedError.Code);
                if (unused.DirectoryExists || unused.BranchExists) return new("preparation-resources-unproven");
                return new(null, remoteBeforeWork, remoteBeforeWork, false, true, true);
            }
            if (!IsCommitId(source.RecoveryBaseCommit)) return new("integration-or-ownership-metadata-unproven");
            InterruptedWorkspaceInspection? receipt = null;
            if (!exists && source.RecoveryState == "standalone-retiring")
            {
                try { receipt = JsonSerializer.Deserialize<InterruptedWorkspaceInspection>(source.RecoveryStatus ?? "null"); }
                catch (JsonException) { return new("retirement-receipt-invalid"); }
            }
            if (!exists && (receipt is not { NotIntegrated: true, HasChanges: false } || !IsCommitId(receipt.Head)))
                return new("owned-workspace-missing");
            var head = exists ? (await InspectionGitAtAsync(path, ["rev-parse", "HEAD"], ct)).StandardOutput.Trim() : receipt?.Head;
            if (!IsCommitId(head)) return new("workspace-head-invalid");
            var ownership = await InspectRecoveryOwnershipAsync(source with { RecoveryBaseCommit = head }, ct);
            if (ownership.Error is { } error) return new(error.Code);
            var status = exists ? (await InspectionGitAtAsync(path, ["status", "--porcelain=v1", "--untracked-files=all"], ct)).StandardOutput : "";
            var dirty = !string.IsNullOrWhiteSpace(status);
            // Reuse the established checks for preserved edits or clean committed work,
            // including unfinished operations and unresolved index entries.
            var owner = source with { RecoveryBaseCommit = head, State = dirty ? "Cancelled" : "IntegrationConflict",
                RecoveryState = dirty ? null : "integration-conflict" };
            if (exists)
            {
                var common = (await InspectionGitAsync(["rev-parse", "--git-common-dir"], ct)).StandardOutput.Trim();
                var workspaceCommon = (await InspectionGitAtAsync(path, ["rev-parse", "--git-common-dir"], ct)).StandardOutput.Trim();
                if (!PathEquals(Path.GetFullPath(common, directory), Path.GetFullPath(workspaceCommon, path)))
                    return new("worktree-repository-mismatch");
                await ValidateRecoveryWorkspaceAsync(path, owner, ct);
            }
            if (!exists && !ownership.BranchExists) return new("owned-feature-branch-missing");
            string? remote;
            try { remote = await GetIntegrationBaseAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is WorkerInfrastructureException or ProcessTimeoutException or IOException)
            { return new("authoritative-remote-unavailable"); }
            if (!IsCommitId(remote)) return new("authoritative-base-unavailable");
            if ((await InspectionGitAsync(["merge-base", "--is-ancestor", source.RecoveryBaseCommit, remote], ct, [0, 1])).ExitCode != 0)
                return new("original-base-not-on-authoritative-remote");
            // Rebase can change HEAD while preserving its registered owner. Its starting base
            // must remain in the ancestry; never accept a replaced/unrelated feature history.
            if ((await InspectionGitAsync(["merge-base", "--is-ancestor", source.RecoveryBaseCommit, head], ct, [0, 1])).ExitCode != 0)
                return new("workspace-history-diverged");
            var integrated = (await InspectionGitAsync(["merge-base", "--is-ancestor", head, remote], ct, [0, 1])).ExitCode == 0;
            if (integrated && (!dirty || head != source.RecoveryBaseCommit))
                return new("integration-present-without-validated-provenance", head, remote, dirty);
            if (integrated && (await InspectionGitAtAsync(path, ["diff", "--quiet", remote, "--"], ct, [0, 1])).ExitCode == 0 &&
                string.IsNullOrWhiteSpace((await InspectionGitAtAsync(path, ["ls-files", "--others", "--exclude-standard"], ct)).StandardOutput))
                return new("workspace-work-already-on-remote", head, remote, dirty);
            return new(null, head, remote, dirty, true);
        }
        catch (WorkerInfrastructureException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (ex is WorkerInfrastructureException or ProcessTimeoutException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new("workspace-inspection-unavailable");
        }
    }

    private static bool BeforeWorkspaceCreation(ExecutionHistoryEntry source) =>
        source.State is "Created" or "Claimed" or "Preparing" && source.RecoveryBaseCommit is null &&
        source.ImplementationSummary is null && source.ValidationOutcome is null;

    public Task<string?> InspectExecutionProcessesAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
        InterruptedWorkspaceProcesses.CheckAsync(RecoveryWorkspacePath(source),
            BeforeWorkspaceCreation(source) ||
            source.State == "Cancelled" && source.FailureReason?.StartsWith("Execution interrupted by Worker shutdown", StringComparison.Ordinal) == true,
            source.CodexRecovery?.ProcessScope, ct);

    public async Task StartInterruptedExecutionAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        var inspection = await InspectInterruptedExecutionAsync(source, ct);
        if (inspection.Blocker is not null || !inspection.NotIntegrated || inspection.Head is null)
            throw new WorkerInfrastructureException("Interrupted execution preparation blocked: " + inspection.Blocker);
        if (inspection.NoWorkspace)
            throw new WorkerInfrastructureException("An unstarted execution must use normal workspace preparation.");
        await EnsureCleanAsync("before interrupted execution continuation", ct);
        _executionDirectory = RecoveryWorkspacePath(source);
        _featureBranch = source.FeatureBranch;
        _startingCommit = inspection.Head;
        _executionId = source.CodexRecovery?.WorkspaceExecutionId ?? source.ExecutionId;
        _resourceExecutionId = source.ExecutionId;
        _resourceAttemptNumber = source.AttemptNumber;
        _implementationAlreadyCommitted = !inspection.HasChanges;
        _forcePostRebaseValidation = true;
        _completedBranch = await SelectCompletedBranchAsync(new(source.IssueNumber, source.IssueTitle,
            source.OriginalIssueBody ?? "", source.StartedAtUtc), source.AttemptNumber, source.ExecutionId, ct);
    }

    public async Task RetireInterruptedExecutionAsync(ExecutionHistoryEntry source, CancellationToken ct)
    {
        var inspection = await InspectInterruptedExecutionAsync(source, ct);
        if (inspection.Blocker is not null || !inspection.NotIntegrated || inspection.HasChanges)
            throw new WorkerInfrastructureException("Interrupted retirement blocked: " + (inspection.Blocker ?? "uncommitted-work-retained"));
        // Keep the feature ref so useful commits remain reachable. Only this attempt's
        // clean, registered worktree is removed; history retains its original outcome.
        if (Directory.Exists(RecoveryWorkspacePath(source)))
            await GitAsync(["worktree", "remove", RecoveryWorkspacePath(source)], ct);
    }
}
