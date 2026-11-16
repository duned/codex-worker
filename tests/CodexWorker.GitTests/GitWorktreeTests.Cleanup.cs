using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed partial class GitWorktreeTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("active", "active", "execution-active")]
    [InlineData("dirty", "keep", "recovery-changes-required")]
    [InlineData("ignored-changes", "keep", "recovery-changes-required")]
    [InlineData("integrated", "safe", "integrated")]
    [InlineData("local-only-integration", "keep", "unmerged-commit-required")]
    [InlineData("remote-ahead", "review", "authoritative-base-unavailable")]
    [InlineData("claimed-recovery", "active", "recovery-or-attempt-active")]
    [InlineData("missing-registered", "review", "missing-registered-worktree")]
    [InlineData("branch-mismatch", "review", "worktree-registration-mismatch")]
    [InlineData("head-mismatch", "review", "worktree-head-mismatch")]
    [InlineData("unknown-recovery", "review", "unknown-recovery-state")]
    [InlineData("stale-recovery", "review", "incomplete-recovery-metadata")]
    [InlineData("clean-recoverable", "review", "recovery-changes-missing")]
    [InlineData("superseded", "keep", "superseded-unmerged-changes")]
    [InlineData("newer-active", "active", "recovery-or-attempt-active")]
    [InlineData("unmerged", "keep", "unmerged-commit-required")]
    [InlineData("unknown-commit", "review", "commit-unavailable")]
    [InlineData("already-clean", "safe", "already-clean")]
    [InlineData("resumed-cleaned", "safe", "already-clean")]
    [InlineData("discarded", "safe", "already-clean")]
    [InlineData("cleaned-no-changes", "safe", "already-clean")]
    [InlineData("missing-resources", "review", "missing-recovery-resources")]
    [InlineData("missing-parent", "review", "lineage-inconsistent")]
    [InlineData("fresh-after-conflict", "safe", "integrated")]
    [InlineData("remote-unavailable", "review", "inspection-unavailable")]
    public async Task CleanupInspectionCorrelatesHistoryAndGitWithoutMutation(string scenario, string decision, string reason)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        var head = await fixture.GitAt(workspace, "rev-parse", "HEAD");
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(workspace, "branch", "--show-current"), "main", started, started.AddMinutes(1), "Failed", 1,
            null, null, 0, [], null, null, null, "failed", "recoverable", head);
        var history = new List<ExecutionHistoryEntry>();
        switch (scenario)
        {
            case "active": entry = entry with { State = "Implementing", CompletedAtUtc = null }; break;
            case "claimed-recovery": entry = entry with { IntegrationRecoveryClaim = Guid.NewGuid() }; break;
            case "ignored-changes":
                entry = entry with { RecoveryState = "integration-conflict" };
                await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), "private.txt\n");
                await fixture.GitAt(workspace, "add", ".gitignore");
                await fixture.GitAt(workspace, "commit", "-m", "ignore local resource");
                entry = entry with { RecoveryBaseCommit = await fixture.GitAt(workspace, "rev-parse", "HEAD") };
                await fixture.Git("merge", "--ff-only", entry.FeatureBranch);
                await fixture.Git("push", "origin", "main");
                await File.WriteAllTextAsync(Path.Combine(workspace, "private.txt"), "useful ignored resource");
                break;
            case "dirty":
            case "superseded":
            case "newer-active":
                await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "useful recovery work");
                if (scenario != "dirty")
                    history.Add(entry with { ExecutionId = Guid.NewGuid(), RetryOfExecutionId = id, AttemptNumber = 2,
                        StartedAtUtc = started.AddHours(1), State = scenario == "newer-active" ? "Implementing" : "Failed",
                        CompletedAtUtc = scenario == "newer-active" ? null : started.AddHours(2) });
                break;
            case "integrated":
            case "local-only-integration":
            case "unmerged":
            case "head-mismatch":
                await File.WriteAllTextAsync(Path.Combine(workspace, "implemented.txt"), "implementation");
                await fixture.GitAt(workspace, "add", "implemented.txt");
                await fixture.GitAt(workspace, "commit", "-m", "retained implementation");
                if (scenario != "head-mismatch")
                    entry = entry with { RecoveryBaseCommit = await fixture.GitAt(workspace, "rev-parse", "HEAD"),
                        RecoveryState = "integration-conflict" };
                if (scenario is "integrated" or "local-only-integration")
                {
                    await fixture.Git("merge", "--ff-only", entry.FeatureBranch);
                    if (scenario == "integrated") await fixture.Git("push", "origin", "main");
                }
                break;
            case "remote-ahead":
                entry = entry with { RecoveryState = "integration-conflict" };
                var origin = Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git"));
                var tree = await fixture.GitAt(origin, "rev-parse", "main^{tree}");
                var remoteCommit = await fixture.GitAt(origin, "-c", "user.name=Worker Tests", "-c", "user.email=worker-tests@example.invalid",
                    "commit-tree", tree, "-p", head, "-m", "remote advance");
                await fixture.GitAt(origin, "update-ref", "refs/heads/main", remoteCommit);
                break;
            case "missing-registered": Directory.Delete(workspace, recursive: true); break;
            case "branch-mismatch": await fixture.GitAt(workspace, "switch", "-c", "unexpected"); break;
            case "unknown-recovery": entry = entry with { RecoveryState = "unknown" }; break;
            case "stale-recovery": entry = entry with { RecoveryState = null }; break;
            case "unknown-commit": entry = entry with { CommitSha = new string('f', 40), RecoveryState = "integration-conflict" }; break;
            case "missing-parent": entry = entry with { RetryOfExecutionId = Guid.NewGuid() }; break;
            case "fresh-after-conflict":
                history.Add(entry with { ExecutionId = Guid.NewGuid(), State = "IntegrationConflict", RecoveryState = "integration-conflict" });
                await fixture.GitAt(workspace, "branch", "-m", entry.FeatureBranch + "-retry-2");
                entry = entry with { AttemptNumber = 2, FeatureBranch = entry.FeatureBranch + "-retry-2", RecoveryState = "cleanup-pending" };
                break;
            case "remote-unavailable":
                entry = entry with { RecoveryState = "integration-conflict" };
                await fixture.Git("config", "--remove-section", "url.file://" + Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git")));
                await fixture.Git("config", "url.file:///nonexistent-cw-origin.insteadOf", "https://github.com/owner/repo");
                break;
            case "already-clean":
            case "resumed-cleaned":
            case "discarded":
            case "cleaned-no-changes":
            case "missing-resources":
                await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);
                if (scenario != "missing-resources") entry = entry with { RecoveryState = scenario == "already-clean" ? "expired-cleaned" : scenario };
                break;
        }
        history.Add(entry);
        var refsBefore = await fixture.Git("show-ref");
        var registrationsBefore = await fixture.Git("worktree", "list", "--porcelain");
        var indexPath = Directory.Exists(workspace) ? Path.GetFullPath(await fixture.GitAt(workspace, "rev-parse", "--git-path", "index"), workspace) : null;
        var indexBefore = indexPath is null ? null : await File.ReadAllBytesAsync(indexPath);

        var result = await git.InspectCleanupAsync(entry, history, CancellationToken.None);

        Assert.Equal(decision, result.Decision);
        Assert.Equal(reason, result.ReasonCode);
        Assert.InRange(result.Message.Length, 1, 250);
        if (scenario is "superseded" or "newer-active") Assert.Equal(history[0].ExecutionId, result.NewerExecutionId);
        if (scenario == "integrated") Assert.Equal(await fixture.Git("rev-parse", "main"), result.AuthoritativeBaseCommit);
        Assert.Equal(refsBefore, await fixture.Git("show-ref"));
        Assert.Equal(registrationsBefore, await fixture.Git("worktree", "list", "--porcelain"));
        if (indexPath is not null) Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(result, await git.InspectCleanupAsync(entry, history, CancellationToken.None));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RecoveryCleanupRemovesOnlyThePersistedWorkerOwnedWorkspaceAndBranch()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var active = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var activeId = Guid.NewGuid();
        await active.StartIssueAsync(activeId, fixture.Issue with { Number = 18, Title = "Active execution" }, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(active.ExecutionDirectory, "active.txt"), "keep active");
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "partial.txt"), "recoverable");
        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery!.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "cleanup-pending", recovery.BaseCommit, recovery.StatusSummary);

        await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);
        await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreeRoot, id.ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(fixture.WorktreeRoot, activeId.ToString("N"))));
        Assert.Equal("keep active", await File.ReadAllTextAsync(Path.Combine(active.ExecutionDirectory, "active.txt")));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", recovery.Branch));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RecoveryCleanupRefusesBranchNotMatchingExecutionIdentity()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "partial.txt"), "recoverable");
        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            "unrelated/branch", "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "cleanup-pending", recovery!.BaseCommit, recovery.StatusSummary);

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None));

        Assert.True(Directory.Exists(git.ExecutionDirectory));
        Assert.Contains(id.ToString("N"), await fixture.Git("worktree", "list", "--porcelain"));
    }
}
