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
        var stage = "checkout-inspection";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(directory))
            {
                // Verification precedes any update. InitializeAsync retains the canonical
                // checkout lock and performs a clean, base-branch-only fast-forward update.
                stage = "checkout-inspection";
                await ValidateStartupReadOnlyAsync(ct);
                stage = "checkout-initialization";
                await InitializeAsync(ct);
                return;
            }

            stage = "checkout-path-and-lock";
            var parent = Path.GetDirectoryName(Path.GetFullPath(directory))
                ?? throw new WorkerInfrastructureException("Managed checkout has no parent directory.");
            Directory.CreateDirectory(parent);
            // Also exclude another Worker process during first-clone publication, before
            // the checkout's normal Git lock exists. Keep the lock file across restarts.
            using var materializationLock = new FileStream(directory + ".materialization.lock",
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (Directory.Exists(directory))
            {
                stage = "checkout-inspection";
                await ValidateStartupReadOnlyAsync(ct);
                stage = "checkout-initialization";
                await InitializeAsync(ct);
                return;
            }
            var staging = directory + ".clone-" + Guid.NewGuid().ToString("N");
            // Interrupted staging directories are never treated as canonical checkouts or
            // recovery worktrees. Preserve them for inspection; a restart uses a fresh path.
            stage = "checkout-clone";
            await GitAtAsync(parent, ["clone", "--origin", "origin", "--branch", settings.BaseBranch,
                "--", $"https://github.com/{repository}.git", staging], ct);
            stage = "checkout-inspection";
            using (var candidate = new GitRepository(runner, staging, repository, settings, timeouts, worktreeRoot, requireManagedAuthentication))
                await candidate.ValidateStartupReadOnlyAsync(ct);
            ct.ThrowIfCancellationRequested();
            stage = "checkout-publication";
            Directory.Move(staging, directory);
            stage = "checkout-initialization";
            await InitializeAsync(ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && WorkerShutdown.IsCancellation(ex))
        {
            throw new OperationCanceledException("Managed checkout preparation cancelled.", ct);
        }
        catch (Exception)
        {
            // Never retain raw Git output or credential helper exceptions in reports.
            throw new ManagedCheckoutPreparationException(stage);
        }
    }
}

public sealed class ManagedCheckoutPreparationException(string stage)
    : WorkerInfrastructureException($"Managed checkout preparation failed at {stage}; inspect checkout safety, access and node configuration.")
{
    public string DiagnosticCode { get; } = $"project-{stage}-failed";
}
