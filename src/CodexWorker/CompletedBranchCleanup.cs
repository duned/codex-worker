namespace CodexWorker;

public sealed record CompletedBranchCleanupRequest(string RepositoryDirectory, int OlderThanDays, bool Apply = false);
public sealed record CompletedBranchCleanupItem(string Branch, string Decision, string Reason, bool LocalDeleted = false,
    bool RemoteDeleted = false);
public sealed record CompletedBranchCleanupResult(bool Applied, IReadOnlyList<CompletedBranchCleanupItem> Branches)
{
    public int Deleted => Branches.Count(b => b.LocalDeleted || b.RemoteDeleted);
    public int Skipped => Branches.Count(b => b.Decision == "skipped");
    public int ReviewRequired => Branches.Count(b => b.Decision == "review");
    public int Eligible => Branches.Count(b => b.Decision == "eligible");
}

public sealed partial class GitRepository
{
    /// <summary>Caller holds the Worker's repository gate throughout inspection and mutation.</summary>
    public async Task<CompletedBranchCleanupResult> CleanupCompletedBranchesAsync(string project,
        IReadOnlyList<ExecutionHistoryEntry> history, int olderThanDays, bool apply, DateTimeOffset now, CancellationToken ct)
    {
        if (olderThanDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(olderThanDays));
        var results = new List<CompletedBranchCleanupItem>();
        try
        {
            await EnsureOriginAsync(ct);
            await ValidateBranchRefAsync(settings.BaseBranch, ct);
            if (string.IsNullOrWhiteSpace(settings.CompletedPrefix))
                throw new InvalidDataException("Completed prefix must be nonempty.");
            await ValidateBranchRefAsync(settings.CompletedPrefix + "cleanup-probe", ct);
            var top = (await InspectionGitAsync(["rev-parse", "--show-toplevel"], ct)).StandardOutput.Trim();
            if (!PathEquals(Path.GetFullPath(top), Path.GetFullPath(directory)))
                throw new InvalidDataException("Configured checkout is not the repository root.");
            // Fetch only into remote-tracking refs; the canonical checkout/base is never reset.
            await GitAsync(["fetch", "--no-tags", "origin", $"+refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}",
                $"+refs/heads/{settings.CompletedPrefix}*:refs/remotes/origin/{settings.CompletedPrefix}*"], ct);
            var remote = await ReadCleanupRemoteAsync(ct);
            var baseRef = "refs/heads/" + settings.BaseBranch;
            if (!remote.TryGetValue(baseRef, out var baseTip) ||
                (await InspectionGitAsync(["rev-parse", "refs/remotes/origin/" + settings.BaseBranch], ct)).StandardOutput.Trim() != baseTip)
                throw new InvalidDataException("Authoritative base changed during refresh; retry inspection.");
            var local = ParseCleanupRefs((await InspectionGitAsync(["for-each-ref", "--format=%(objectname)%09%(refname)",
                "refs/heads/"], ct)).StandardOutput);
            var prefix = "refs/heads/" + settings.CompletedPrefix;
            var deletionStopped = false;
            foreach (var reference in local.Keys.Concat(remote.Keys).Where(r => r.StartsWith(prefix, StringComparison.Ordinal))
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var branch = reference["refs/heads/".Length..];
                local.TryGetValue(reference, out var localTip);
                remote.TryGetValue(reference, out var remoteTip);
                var tip = localTip ?? remoteTip;
                CompletedBranchCleanupItem Item(string decision, string reason) => new(branch, decision, reason);
                if (deletionStopped)
                {
                    results.Add(Item("review", "Cleanup stopped after an uncertain deletion; refs preserved.")); continue;
                }
                var entries = history.Where(e => e.Project == project && e.Repository == repository && e.CompletedBranch == branch).ToArray();
                var expectedBranch = entries.Length == 1
                    ? CompletedBranchName(settings, new GitHubIssue(entries[0].IssueNumber, entries[0].IssueTitle, "", entries[0].StartedAtUtc))
                    : "";
                if (entries.Length == 1 && entries[0].AttemptNumber > 1) expectedBranch += $"-retry-{entries[0].AttemptNumber}";
                if (branch == settings.BaseBranch || entries.Length != 1 || entries[0].ExecutionId == Guid.Empty ||
                    entries[0].IssueNumber < 1 || entries[0].AttemptNumber < 1 ||
                    (branch != expectedBranch && branch != $"{expectedBranch}-execution-{entries[0].ExecutionId:N}") ||
                    entries[0].State != "Completed" || entries[0].CompletedAtUtc is null || entries[0].CompletedAtUtc < entries[0].StartedAtUtc || entries[0].BaseBranch != settings.BaseBranch ||
                    !IsCommitId(entries[0].CommitSha) || entries[0].CommitSha != tip)
                {
                    results.Add(Item("review", "No unique completed execution with matching base and exact commit.")); continue;
                }
                if (localTip is not null && remoteTip is not null && localTip != remoteTip)
                {
                    results.Add(Item("review", "Local and remote tips differ.")); continue;
                }
                if (entries[0].CompletedAtUtc > now.AddDays(-olderThanDays))
                {
                    results.Add(Item("skipped", "Completion is newer than retention threshold.")); continue;
                }
                if (await CleanupBranchCheckedOutAsync(reference, ct))
                {
                    results.Add(Item("review", "Branch is checked out in a worktree.")); continue;
                }
                if ((await InspectionGitAsync(["merge-base", "--is-ancestor", tip ?? "", baseTip], ct, [0, 1, 128])).ExitCode != 0)
                {
                    results.Add(Item("review", "Tip is not provably reachable from authoritative base.")); continue;
                }
                if (!apply) { results.Add(Item("eligible", "Exact tip is integrated and retention has elapsed.")); continue; }
                // Re-read both refs and the base. An explicit lease is a compare-and-delete on origin,
                // including changes racing this re-read; never use an unconditional remote deletion.
                var freshRemote = await ReadCleanupRemoteAsync(ct);
                var freshLocal = ParseCleanupRefs((await InspectionGitAsync(["for-each-ref", "--format=%(objectname)%09%(refname)", reference], ct)).StandardOutput);
                freshLocal.TryGetValue(reference, out var currentLocal);
                freshRemote.TryGetValue(reference, out var currentRemote);
                if (currentLocal != localTip || currentRemote != remoteTip || freshRemote.GetValueOrDefault(baseRef) != baseTip ||
                    await CleanupBranchCheckedOutAsync(reference, ct))
                {
                    results.Add(Item("review", "Ref, base or worktree changed after inspection.")); continue;
                }
                var remoteDeleted = false;
                try
                {
                    await EnsureOriginAsync(ct);
                    if (remoteTip is not null)
                    {
                        await GitAsync(["push", "--porcelain", $"--force-with-lease={reference}:{remoteTip}", "origin", ":" + reference], ct);
                        remoteDeleted = true;
                    }
                    if (localTip is not null)
                    {
                        if (await CleanupBranchCheckedOutAsync(reference, ct))
                        {
                            results.Add(new(branch, "review", "Worktree changed; local ref preserved.", false, remoteDeleted)); continue;
                        }
                        // Atomic old-value check protects against a local ref change after inspection.
                        await GitAsync(["update-ref", "-d", reference, localTip], ct);
                    }
                    results.Add(new(branch, "deleted", "Removed verified integrated refs.", localTip is not null, remoteDeleted));
                }
                catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested)
                {
                    deletionStopped = true;
                    results.Add(new(branch, "review", "Deletion rejected or uncertain; inspect remaining refs before retrying.", false, remoteDeleted));
                }
            }
            return new(apply, results);
        }
        catch (WorkerInfrastructureException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private async Task<bool> CleanupBranchCheckedOutAsync(string reference, CancellationToken ct)
    {
        var output = (await InspectionGitAsync(["worktree", "list", "--porcelain"], ct)).StandardOutput;
        if (output.Contains("[output truncated]", StringComparison.Ordinal))
            throw new InvalidDataException("Incomplete worktree listing; cleanup refused.");
        return output.Split('\n').Any(line => line == "branch " + reference);
    }

    private async Task<Dictionary<string, string>> ReadCleanupRemoteAsync(CancellationToken ct) =>
        ParseCleanupRefs((await InspectionGitAsync(["ls-remote", "--heads", "origin"], ct)).StandardOutput);

    private static Dictionary<string, string> ParseCleanupRefs(string output)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length != 2 || !IsCommitId(fields[0]) || !fields[1].StartsWith("refs/heads/", StringComparison.Ordinal) ||
                !refs.TryAdd(fields[1], fields[0]))
                throw new InvalidDataException("Incomplete or invalid Git ref listing; cleanup refused.");
        }
        return refs;
    }
}
