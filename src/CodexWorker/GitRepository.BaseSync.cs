namespace CodexWorker;

/// <summary>Definitive reasons the remote refused a push. A rejected ref was not updated on the remote.</summary>
public enum GitPushRejection { NonFastForward, WorkflowScope, ProtectedBranch, PermissionDenied, Other }

/// <summary>The remote explicitly rejected a push, so the remote ref is known to be unchanged.
/// The message is product-owned text; raw remote output is never included.</summary>
public sealed class GitPushRejectedException(GitPushRejection reason, string message) : WorkerInfrastructureException(message)
{
    public GitPushRejection Reason { get; } = reason;
}

public sealed partial class GitRepository
{
    /// <summary>
    /// Aligns the canonical local base branch with the authoritative remote base. A local base can be left
    /// ahead of the remote when a push was rejected after the local fast-forward; leaving it there would make
    /// later executions start from (and integrate) unpublished work and prevent integration from ever converging.
    /// Local-only commits are discarded from the base branch only when another local branch still preserves them.
    /// </summary>
    internal async Task SyncBaseWithRemoteAsync(CancellationToken ct)
    {
        var remoteRef = $"refs/remotes/origin/{settings.BaseBranch}";
        await GitAsync(["fetch", "origin", $"refs/heads/{settings.BaseBranch}:{remoteRef}"], ct);
        await GitAsync(["switch", "--", settings.BaseBranch], ct);
        var local = (await GitAsync(["rev-parse", $"refs/heads/{settings.BaseBranch}"], ct)).StandardOutput.Trim();
        var remote = (await GitAsync(["rev-parse", remoteRef], ct)).StandardOutput.Trim();
        if (local == remote) return;
        var localIsBehind = await GitAsync(["merge-base", "--is-ancestor", local, remote], ct, [0, 1]);
        if (localIsBehind.ExitCode == 0)
        {
            await GitAsync(["merge", "--ff-only", remoteRef], ct);
            return;
        }
        // The local base holds commits the remote does not have. They must be preserved by another
        // local branch (normally the execution feature branch whose push was rejected) before realignment.
        var unpreserved = (await GitAsync(["rev-list", "--max-count=1", $"refs/heads/{settings.BaseBranch}",
            "--not", remoteRef, $"--exclude={settings.BaseBranch}", "--branches"], ct)).StandardOutput.Trim();
        if (unpreserved.Length > 0)
            throw new WorkerInfrastructureException(
                $"Local '{settings.BaseBranch}' contains unpublished commits that no other branch preserves; inspect the checkout before integrating.");
        await EnsureCleanAsync($"before realigning '{settings.BaseBranch}' with the remote base", ct);
        await GitAsync(["reset", "--keep", remoteRef], ct);
        var realigned = (await GitAsync(["rev-parse", $"refs/heads/{settings.BaseBranch}"], ct)).StandardOutput.Trim();
        if (realigned != remote)
            throw new WorkerInfrastructureException($"Could not realign local '{settings.BaseBranch}' with the remote base; checkout preserved.");
    }

    /// <summary>Classifies an explicit push rejection from Git/remote output. Returns null when the
    /// output does not prove the remote ref was left unchanged.</summary>
    internal static GitPushRejectedException? ClassifyPushRejection(string output, string branch)
    {
        static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
        var rejected = Has(output, "! [rejected]") || Has(output, "! [remote rejected]");
        if (Has(output, "without `workflow` scope") || Has(output, "without 'workflow' scope") ||
            Has(output, "refusing to allow") && Has(output, "workflow"))
            return new GitPushRejectedException(GitPushRejection.WorkflowScope,
                $"GitHub rejected the push to '{branch}' because the change creates or updates GitHub Actions workflow files " +
                "and the Worker's GitHub credential lacks the 'workflow' scope (or 'Workflows: write' permission). " +
                "Re-run the GitHub login for this Worker (it now requests the 'workflow' scope) and retry the Issue.");
        if (Has(output, "GH006") || Has(output, "protected branch"))
            return new GitPushRejectedException(GitPushRejection.ProtectedBranch,
                $"GitHub rejected the push because '{branch}' is a protected branch that the Worker credential cannot update directly.");
        if (Has(output, "GH013") || Has(output, "repository rule violation"))
            return new GitPushRejectedException(GitPushRejection.ProtectedBranch,
                $"GitHub rejected the push to '{branch}' because of repository rule violations.");
        if (rejected && (Has(output, "non-fast-forward") || Has(output, "fetch first") || Has(output, "stale info")))
            return new GitPushRejectedException(GitPushRejection.NonFastForward,
                $"The remote '{branch}' advanced before the push (non-fast-forward); nothing was pushed.");
        if (Has(output, "permission to") && Has(output, "denied") || Has(output, "returned error: 403"))
            return new GitPushRejectedException(GitPushRejection.PermissionDenied,
                $"GitHub denied write access for the push to '{branch}'; check the Worker's GitHub credential permissions.");
        return rejected
            ? new GitPushRejectedException(GitPushRejection.Other, $"The remote rejected the push to '{branch}'; nothing was pushed.")
            : null;
    }
}
