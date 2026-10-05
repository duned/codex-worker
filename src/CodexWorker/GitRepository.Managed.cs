namespace CodexWorker;

public sealed partial class GitRepository
{
    // Catalog eligibility is read-only and does not require a checkout. Full checkout
    // and dry-run write validation still run before an assigned execution is claimed.
    public async Task ValidateManagedRemoteReadAsync(CancellationToken ct)
    {
        try
        {
            await GitAtAsync(Path.GetTempPath(),
                ["ls-remote", "--exit-code", $"https://github.com/{repository}.git", "HEAD"], ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && WorkerShutdown.IsCancellation(ex))
        {
            throw new OperationCanceledException("Managed repository access check was cancelled.", ct);
        }
        catch (WorkerInfrastructureException)
        {
            // Credential-helper output must not reach catalog status or dashboards.
            throw new WorkerInfrastructureException($"Git repository read authentication is unavailable for '{repository}'.");
        }
    }

    /// <summary>Publishes only a complete clone; existing checkouts are never replaced.</summary>
    /// <remarks>The caller must hold the same repository gate used by execution/integration.</remarks>
    public async Task MaterializeManagedCheckoutAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(directory))
        {
            // Verification precedes any update. InitializeAsync retains the canonical
            // checkout lock and performs a clean, base-branch-only fast-forward update.
            await ValidateStartupReadOnlyAsync(ct);
            await InitializeAsync(ct);
            return;
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(directory))
            ?? throw new WorkerInfrastructureException("Managed checkout has no parent directory.");
        Directory.CreateDirectory(parent);
        // Also exclude another Worker process during first-clone publication, before
        // the checkout's normal Git lock exists. Keep the lock file across restarts.
        using var materializationLock = new FileStream(directory + ".materialization.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.Exists(directory))
        {
            await ValidateStartupReadOnlyAsync(ct);
            await InitializeAsync(ct);
            return;
        }
        var staging = directory + ".clone-" + Guid.NewGuid().ToString("N");
        // Interrupted staging directories are never treated as canonical checkouts or
        // recovery worktrees. Preserve them for inspection; a restart uses a fresh path.
        await GitAtAsync(parent, ["clone", "--origin", "origin", "--branch", settings.BaseBranch,
            "--", $"https://github.com/{repository}.git", staging], ct);
        using (var candidate = new GitRepository(runner, staging, repository, settings, timeouts, worktreeRoot))
            await candidate.ValidateStartupReadOnlyAsync(ct);
        ct.ThrowIfCancellationRequested();
        Directory.Move(staging, directory);
        await InitializeAsync(ct);
    }
}
