namespace CodexWorker;

public sealed record CompletedBranchCleanupRequest(string RepositoryDirectory, int OlderThanDays, bool Apply = false, int Limit = 20);
public sealed record CompletedBranchCleanupItem(string Branch, string Decision, string Reason, bool LocalDeleted = false,
    bool RemoteDeleted = false, string? Tip = null, DateTimeOffset? CompletedAtUtc = null);
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
        IReadOnlyList<ExecutionHistoryEntry> history, int olderThanDays, bool apply, DateTimeOffset now, CancellationToken ct, int limit = 20, IReadOnlyList<CodexProvisioning.ArchivedBranchSelection>? branches = null, bool archivedOnly = false, int offset = 0)
    {
        if (olderThanDays is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(olderThanDays));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
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
            var archivePrefixes = archivedOnly ? new[] { "done/feature/", "complete/feature/" } : new[] { settings.CompletedPrefix };
            var fetch = new List<string> { "fetch", "--no-tags", "origin", $"+refs/heads/{settings.BaseBranch}:refs/remotes/origin/{settings.BaseBranch}" };
            fetch.AddRange(archivePrefixes.Select(p => $"+refs/heads/{p}*:refs/remotes/origin/{p}*"));
            await GitAsync(fetch.ToArray(), ct);
            var remote = await ReadCleanupRemoteAsync(ct);
            var baseRef = "refs/heads/" + settings.BaseBranch;
            if (!remote.TryGetValue(baseRef, out var baseTip) ||
                (await InspectionGitAsync(["rev-parse", "refs/remotes/origin/" + settings.BaseBranch], ct)).StandardOutput.Trim() != baseTip)
                throw new InvalidDataException("Authoritative base changed during refresh; retry inspection.");
            var local = ParseCleanupRefs((await InspectionGitAsync(["for-each-ref", "--format=%(objectname)%09%(refname)",
                "refs/heads/"], ct)).StandardOutput);
            var prefix = "refs/heads/" + settings.CompletedPrefix;
            var deletionStopped = false;
            var selected = 0;
            foreach (var reference in local.Keys.Concat(remote.Keys).Where(r => archivedOnly ? CodexProvisioning.ExecutionMaintenanceProtocol.ArchivedBranch(r["refs/heads/".Length..]) : r.StartsWith(prefix, StringComparison.Ordinal))
                         .Where(r => branches is null || branches.Any(b => "refs/heads/" + b.Branch == r))
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Skip(archivedOnly ? offset : 0).Take(archivedOnly ? limit : int.MaxValue))
            {
                ct.ThrowIfCancellationRequested();
                var branch = reference["refs/heads/".Length..];
                local.TryGetValue(reference, out var localTip);
                remote.TryGetValue(reference, out var remoteTip);
                var tip = localTip ?? remoteTip;
                var correlated = history.Where(e => e.Project == project && e.Repository == repository && e.CompletedBranch == branch).ToArray();
                var completedAt = correlated.Length == 1 ? correlated[0].CompletedAtUtc : null;
                CompletedBranchCleanupItem Item(string decision, string reason) => new(branch, decision, reason, Tip: tip, CompletedAtUtc: completedAt);
                if (branches is not null && branches.Single(b => b.Branch == branch).ExpectedTip != tip)
                { results.Add(Item("review", "Expected tip changed since preview.")); continue; }
                if (deletionStopped)
                {
                    results.Add(Item("review", "Cleanup stopped after an uncertain deletion; refs preserved.")); continue;
                }
                var entries = correlated;
                var expectedBranch = entries.Length == 1
                    ? CompletedBranchName(settings, new GitHubIssue(entries[0].IssueNumber, entries[0].IssueTitle, "", entries[0].StartedAtUtc))
                    : "";
                if (archivedOnly && entries.Length == 1)
                    expectedBranch = branch[..(branch.StartsWith("done/", StringComparison.Ordinal) ? "done/feature/".Length : "complete/feature/".Length)] + expectedBranch[settings.CompletedPrefix.Length..];
                if (entries.Length == 1 && entries[0].AttemptNumber > 1) expectedBranch += $"-retry-{entries[0].AttemptNumber}";
                if (branch == settings.BaseBranch || entries.Length != 1 || entries[0].ExecutionId == Guid.Empty ||
                    entries[0].IssueNumber < 1 || entries[0].AttemptNumber < 1 ||
                    (branch != expectedBranch && branch != $"{expectedBranch}-execution-{entries[0].ExecutionId:N}") ||
                    entries[0].State != "Completed" || entries[0].IntegrationRecoveryClaim is not null ||
                    entries[0].CompletedAtUtc is null || entries[0].CompletedAtUtc < entries[0].StartedAtUtc || entries[0].BaseBranch != settings.BaseBranch ||
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
                if (selected >= limit)
                {
                    results.Add(Item("skipped", "Eligible branch exceeds this request's cleanup limit.")); continue;
                }
                selected++;
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
                            results.Add(new(branch, "review", "Worktree changed; local ref preserved.", false, remoteDeleted, tip, completedAt)); continue;
                        }
                        // Atomic old-value check protects against a local ref change after inspection.
                        await GitAsync(["update-ref", "-d", reference, localTip], ct);
                    }
                    results.Add(new(branch, "deleted", "Removed verified integrated refs.", localTip is not null, remoteDeleted, tip, completedAt));
                }
                catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested)
                {
                    deletionStopped = true;
                    results.Add(new(branch, "review", "Deletion rejected or uncertain; inspect remaining refs before retrying.", false, remoteDeleted, tip, completedAt));
                }
            }
            if (branches is not null)
                foreach (var missing in branches.Where(b => results.All(r => r.Branch != b.Branch)))
                    results.Add(new(missing.Branch, "review", "Ref missing or outside configured archive prefix."));
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
