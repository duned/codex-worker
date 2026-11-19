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
    [InlineData("safe", "cleaned", "integrated")]
    [InlineData("dirty", "refused", "recovery-changes-required")]
    [InlineData("recoverable", "refused", "recovery-changes-missing")]
    [InlineData("authoritative", "refused", "authoritative-recovery")]
    [InlineData("authoritative-pending", "refused", "authoritative-recovery")]
    [InlineData("uncertain", "refused", "uncertain-execution")]
    [InlineData("active", "refused", "execution-active")]
    [InlineData("branch", "refused", "worktree-registration-mismatch")]
    [InlineData("head", "refused", "worktree-head-mismatch")]
    [InlineData("path", "refused", "missing-registered-worktree")]
    [InlineData("unmerged", "refused", "unmerged-commit-required")]
    [InlineData("race", "refused", "recovery-changes-required")]
    [InlineData("failure", "failed", "cleanup-failed")]
    [InlineData("partial-failure", "failed", "cleanup-failed")]
    [InlineData("already-clean", "already-clean", "already-clean")]
    public async Task OperatorCleanupRevalidatesAndPreservesUnsafeResources(string scenario, string outcome, string reason)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        var head = await fixture.GitAt(workspace, "rev-parse", "HEAD");
        var started = DateTimeOffset.UtcNow.AddDays(-40);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(workspace, "branch", "--show-current"), "main", started, started.AddMinutes(1), "Failed", 1,
            "primary outcome", "failed", 0, [], null, null, null, "task failed", "cleanup-pending", head);
        switch (scenario)
        {
            case "dirty": await File.WriteAllTextAsync(Path.Combine(workspace, "useful.txt"), "preserve"); break;
            case "recoverable": entry = entry with { RecoveryState = "recoverable" }; break;
            case "authoritative": entry = entry with { State = "IntegrationConflict", RecoveryState = "integration-conflict" }; break;
            case "authoritative-pending": entry = entry with { State = "IntegrationConflict" }; break;
            case "uncertain": entry = entry with { State = "InfrastructureFailure" }; break;
            case "active": entry = entry with { State = "Implementing", CompletedAtUtc = null }; break;
            case "branch": await fixture.GitAt(workspace, "switch", "-c", "unrelated-branch"); break;
            case "head":
            case "unmerged":
                await File.WriteAllTextAsync(Path.Combine(workspace, "useful.txt"), "preserve");
                await fixture.GitAt(workspace, "add", "useful.txt");
                await fixture.GitAt(workspace, "commit", "-m", "unmerged work");
                if (scenario == "unmerged") entry = entry with { RecoveryBaseCommit = await fixture.GitAt(workspace, "rev-parse", "HEAD") };
                break;
            case "path": Directory.Delete(workspace, recursive: true); break;
            case "failure": await fixture.Git("worktree", "lock", workspace); break;
            case "already-clean":
                await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);
                entry = entry with { RecoveryState = "expired-cleaned" };
                break;
        }
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "history.db"));
        await history.CreateAsync(entry);
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)]);
        var service = new ExecutionCleanupService(history, registry, c => fixture.CreateRepository(c.Git));
        var dryRun = Assert.Single(await service.RunAsync(new(ExecutionId: id), CancellationToken.None));
        Assert.Equal(entry.RecoveryState, (await history.ReadExecutionAsync(id))?.RecoveryState);
        if (scenario == "race")
        {
            Assert.Equal("dry-run", dryRun.Outcome);
            await File.WriteAllTextAsync(Path.Combine(workspace, "new-useful.txt"), "changed since inspection");
        }
        string? branchLock = null;
        if (scenario == "partial-failure")
        {
            branchLock = Path.Combine(fixture.Checkout, ".git", "refs", "heads", entry.FeatureBranch + ".lock");
            await File.WriteAllTextAsync(branchLock, "simulate another ref writer");
        }
        registry.DrainWorker();
        var result = Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Inspection.ReasonCode);
        var saved = await history.ReadExecutionAsync(id);
        Assert.NotNull(saved);
        Assert.Equal(entry.State, saved.State);
        Assert.Equal(entry.ImplementationSummary, saved.ImplementationSummary);
        Assert.Equal(entry.FailureReason, saved.FailureReason);
        Assert.Equal(outcome is "cleaned" or "already-clean" ? "operator-cleaned" : entry.RecoveryState, saved.RecoveryState);
        if (outcome is "cleaned" or "already-clean")
        {
            Assert.False(Directory.Exists(workspace));
            Assert.Equal(string.Empty, await fixture.Git("branch", "--list", entry.FeatureBranch));
            Assert.Equal("already-clean", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None)).Outcome);
            var preview = Assert.Single(await service.RunAsync(new(ExecutionId: id, Action: "archive"), CancellationToken.None));
            Assert.Equal("dry-run", preview.Outcome);
            Assert.Null(await history.ReadArchiveAuditAsync(id));
            Assert.Equal("archived", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true, Action: "archive"), CancellationToken.None)).Outcome);
            Assert.Equal("already-archived", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true, Action: "archive"), CancellationToken.None)).Outcome);
            Assert.NotNull(await history.ReadExecutionAsync(id));
            Assert.Empty(await history.ReadRecentAsync(20));
        }
        else if (scenario == "partial-failure")
        {
            Assert.False(Directory.Exists(workspace));
            Assert.NotEqual(string.Empty, await fixture.Git("branch", "--list", entry.FeatureBranch));
            Assert.NotNull(branchLock);
            File.Delete(branchLock);
            Assert.Equal("cleaned", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None)).Outcome);
        }
        else if (scenario != "path") Assert.True(Directory.Exists(workspace));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorCleanupSelectsBoundedHistoryAndOnlyRemovesOlderEligibleAttempt(bool stale)
    {
        using var fixture = await seed.CreateAsync();
        using var olderGit = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var newerGit = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var unrelatedGit = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await olderGit.InitializeAsync(CancellationToken.None);
        var oldId = Guid.NewGuid();
        await olderGit.StartIssueAsync(oldId, fixture.Issue, CancellationToken.None);
        var head = await fixture.GitAt(olderGit.ExecutionDirectory, "rev-parse", "HEAD");
        var started = DateTimeOffset.UtcNow.AddDays(-10);
        var older = new ExecutionHistoryEntry(oldId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(olderGit.ExecutionDirectory, "branch", "--show-current"), "main", started, started.AddMinutes(1), "IntegrationConflict", 1,
            "original outcome", null, 0, [], head, null, null, null, "integration-conflict", head);
        var newId = Guid.NewGuid();
        await newerGit.StartIssueAsync(newId, fixture.Issue, older, false, 2, CancellationToken.None);
        var newer = older with { ExecutionId = newId, FeatureBranch = older.FeatureBranch + "-retry-2", AttemptNumber = 2,
            RetryOfExecutionId = oldId, StartedAtUtc = started.AddHours(1), CompletedAtUtc = started.AddHours(2) };
        await unrelatedGit.StartIssueAsync(Guid.NewGuid(), fixture.Issue with { Number = 99 }, CancellationToken.None);
        var unrelatedBranch = await fixture.GitAt(unrelatedGit.ExecutionDirectory, "branch", "--show-current");
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "history.db"));
        await history.CreateAsync(older);
        await history.CreateAsync(newer);
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)]);
        var service = new ExecutionCleanupService(history, registry, c => fixture.CreateRepository(c.Git));
        registry.DrainWorker();
        var request = stale ? new ExecutionCleanupRequest(Stale: true, Limit: 1, Apply: true) :
            new ExecutionCleanupRequest(IssueNumber: fixture.Issue.Number, Apply: true);
        var results = await service.RunAsync(request, CancellationToken.None);
        Assert.Equal("cleaned", results[0].Outcome);
        Assert.Equal(oldId, results[0].Inspection.ExecutionId);
        if (!stale) Assert.Equal("authoritative-recovery", results[1].Inspection.ReasonCode);
        else
        {
            Assert.Single(results);
            var next = Assert.Single(await service.RunAsync(request, CancellationToken.None));
            Assert.Equal(newId, next.Inspection.ExecutionId);
            Assert.Equal("authoritative-recovery", next.Inspection.ReasonCode);
        }
        Assert.False(Directory.Exists(olderGit.ExecutionDirectory));
        Assert.True(Directory.Exists(newerGit.ExecutionDirectory));
        Assert.True(Directory.Exists(unrelatedGit.ExecutionDirectory));
        Assert.NotEqual(string.Empty, await fixture.Git("branch", "--list", newer.FeatureBranch));
        Assert.NotEqual(string.Empty, await fixture.Git("branch", "--list", unrelatedBranch));
        Assert.Equal("IntegrationConflict", (await history.ReadExecutionAsync(oldId))?.State);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task OperatorCleanupReadsHistoryAfterAcquiringRepositoryGateAndHoldsDrain()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var head = await fixture.GitAt(git.ExecutionDirectory, "rev-parse", "HEAD");
        var now = DateTimeOffset.UtcNow;
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(git.ExecutionDirectory, "branch", "--show-current"), "main", now, now, "Failed", 1,
            null, null, 0, [], null, null, null, null, "cleanup-pending", head);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "history.db"));
        await history.CreateAsync(entry);
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)]);
        using var gate = new SemaphoreSlim(0, 1);
        var gates = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        Assert.True(gates.TryAdd("owner/repo", gate));
        var service = new ExecutionCleanupService(history, registry, c => fixture.CreateRepository(c.Git), gates);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None));
        registry.DrainWorker();
        var pending = service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.False(registry.CancelWorkerDrain());
        await history.UpdateRecoveryAsync(id, "integration-conflict");
        gate.Release();
        var result = Assert.Single(await pending);
        Assert.Equal("authoritative-recovery", result.Inspection.ReasonCode);
        Assert.True(Directory.Exists(git.ExecutionDirectory));
        Assert.True(registry.CancelWorkerDrain());
    }
}
