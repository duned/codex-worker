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
    [InlineData("done/feature/")]
    [InlineData("complete/feature/")]
    public async Task ArchivedBranchSelectionPreservesOtherProjectsAndRejectsStaleTips(string prefix)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = prefix });
        var branch = prefix + "example-task-17";
        var second = prefix + "example-task-18";
        var tip = await fixture.Git("rev-parse", "HEAD");
        foreach (var name in new[] { branch, second, "feature/active", "unrelated" })
            await fixture.Git("branch", name);
        var now = DateTimeOffset.UtcNow;
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [], tip, "main", branch, null);
        var other = entry with { ExecutionId = Guid.NewGuid(), Project = "other", IssueNumber = 18, CompletedBranch = second };
        var preview = await git.CleanupCompletedBranchesAsync("sample", [entry, other], 30, false, now, CancellationToken.None, archivedOnly: true);
        Assert.Equal(2, preview.Branches.Count);
        Assert.Equal("eligible", preview.Branches.Single(b => b.Branch == branch).Decision);
        Assert.Equal(now.AddDays(-40), preview.Branches.Single(b => b.Branch == branch).CompletedAtUtc);
        Assert.Equal("review", preview.Branches.Single(b => b.Branch == second).Decision);
        var stale = await git.CleanupCompletedBranchesAsync("sample", [entry], 30, true, now, CancellationToken.None,
            branches: [new(branch, new string('0', 40))], archivedOnly: true);
        Assert.Equal("review", Assert.Single(stale.Branches).Decision);
        var applied = await git.CleanupCompletedBranchesAsync("sample", [entry, other], 30, true, now, CancellationToken.None,
            branches: [new(branch, tip), new(second, tip)], archivedOnly: true);
        Assert.Equal(1, applied.Deleted);
        Assert.Equal("review", applied.Branches.Single(b => b.Branch == second).Decision);
        Assert.Equal(tip, await fixture.Git("rev-parse", second));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("integrated", "eligible")]
    [InlineData("flat-prefix", "eligible")]
    [InlineData("new", "skipped")]
    [InlineData("unmerged", "review")]
    [InlineData("mismatch", "review")]
    [InlineData("worktree", "review")]
    [InlineData("orphan", "review")]
    [InlineData("local-only", "eligible")]
    [InlineData("remote-only", "eligible")]
    [InlineData("stale-base", "eligible")]
    [InlineData("ambiguous", "review")]
    [InlineData("claimed", "review")]
    public async Task CompletedBranchCleanupRequiresHistoryAgeExactTipsAndIntegration(string scenario, string decision)
    {
        using var fixture = await seed.CreateAsync();
        var prefix = scenario == "flat-prefix" ? "done-" : "done/";
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = prefix });
        var branch = prefix + "example-task-17";
        var integrated = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("branch", branch);
        if (scenario is "unmerged" or "mismatch" or "stale-base")
        {
            await fixture.Git("checkout", branch);
            await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "unmerged.txt"), "unmerged");
            await fixture.Git("add", "unmerged.txt");
            await fixture.Git("commit", "-m", "unmerged");
            await fixture.Git("checkout", "main");
        }
        var tip = await fixture.Git("rev-parse", branch);
        if (scenario != "local-only")
            await fixture.Git("push", "origin", scenario == "mismatch" ? $"{integrated}:refs/heads/{branch}" : branch);
        if (scenario == "stale-base") await fixture.Git("push", "origin", $"{tip}:refs/heads/main");
        if (scenario == "remote-only") await fixture.Git("branch", "-d", branch);
        if (scenario == "worktree") await fixture.Git("worktree", "add", Path.Combine(fixture.WorktreeRoot, "checked-out"), branch);
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var completed = scenario == "new" ? now.AddDays(-1) : now.AddDays(-40);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", completed.AddMinutes(-1), completed, "Completed", 1, null, null, 0, [],
            tip, "main", branch, null, IntegrationRecoveryClaim: scenario == "claimed" ? Guid.NewGuid() : null);
        IReadOnlyList<ExecutionHistoryEntry> history = scenario switch
        {
            "orphan" => [],
            "ambiguous" => [entry, entry with { ExecutionId = Guid.NewGuid() }],
            _ => [entry]
        };
        var preview = await git.CleanupCompletedBranchesAsync("sample", history, 30, false, now, CancellationToken.None);
        Assert.Equal(decision, Assert.Single(preview.Branches).Decision);
        Assert.False(preview.Applied);
        // Commit timestamps are current; the old lifecycle timestamp is the authoritative age.
        var applied = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None);
        if (decision == "eligible")
        {
            var deleted = Assert.Single(applied.Branches);
            Assert.Equal("deleted", deleted.Decision);
            Assert.Equal(scenario != "remote-only", deleted.LocalDeleted);
            Assert.Equal(scenario != "local-only", deleted.RemoteDeleted);
            Assert.Equal(string.Empty, await fixture.Git("branch", "--list", branch));
            Assert.Equal(string.Empty, await fixture.Git("ls-remote", "--heads", "origin", "refs/heads/" + branch));
            var repeated = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None);
            Assert.Empty(repeated.Branches);
        }
        else
        {
            Assert.Equal(decision, Assert.Single(applied.Branches).Decision);
            Assert.Equal(0, applied.Deleted);
            Assert.Equal(tip, await fixture.Git("rev-parse", branch));
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedBranchMaintenanceApiAcceptsCompletedDrainAndAllowsResume(bool initiallyActive)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "maintenance-history.db"));
        using var gate = new SemaphoreSlim(1, 1);
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout }
        };
        var model = new WorkerRuntimeReadModel(new GlobalWorkerConfiguration(), [("project.yml", config)], history) { State = "running" };
        model.CompletedBranchMaintenance.Register(config, git, gate);
        const string branch = "done/example-task-17";
        await fixture.Git("branch", branch);
        await fixture.Git("push", "origin", branch);
        var tip = await fixture.Git("rev-parse", branch);
        var now = DateTimeOffset.UtcNow;
        await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [],
            tip, "main", branch, null));
        var app = await ManagementApi.StartAsync(model, new ManagementApiSettings { ListenUrl = "http://127.0.0.1:0" }, CancellationToken.None);
        Assert.NotNull(app);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            Assert.NotNull(addresses);
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
            var request = new CompletedBranchCleanupRequest(fixture.Checkout, 1, Apply: true, Limit: 3);
            using var beforeDrain = await client.PostAsJsonAsync("/api/maintenance/completed-branches", request);
            Assert.Equal(HttpStatusCode.Conflict, beforeDrain.StatusCode);
            if (initiallyActive) Assert.True(model.Registry.TryReserve("sample"));
            using var drain = await client.PostAsync("/api/worker/drain", null);
            Assert.Equal(HttpStatusCode.OK, drain.StatusCode);
            if (initiallyActive)
            {
                using var pendingDrain = await client.PostAsJsonAsync("/api/maintenance/completed-branches", request);
                Assert.Equal(HttpStatusCode.Conflict, pendingDrain.StatusCode);
                model.Registry.Release("sample");
            }
            using var status = JsonDocument.Parse(await client.GetStringAsync("/api/worker/drain"));
            Assert.Equal("drained", status.RootElement.GetProperty("state").GetString());
            Assert.True(status.RootElement.GetProperty("drainComplete").GetBoolean());
            Assert.Equal("drained", model.State);
            Assert.Equal("drained", model.HeartbeatStatus(new WorkerHeartbeatStatus(0, [], "running")).State);
            using var applied = await client.PostAsJsonAsync("/api/maintenance/completed-branches", request);
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            using var result = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
            Assert.Equal(1, result.RootElement.GetProperty("deleted").GetInt32());
            using var cancel = await client.PostAsync("/api/worker/drain/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            Assert.Equal("running", model.State);
            Assert.True(model.Registry.TryReserve("sample"));
            model.Registry.Release("sample");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task StandaloneBranchMaintenanceHonorsBindingDrainGateAuthorityAndPartialResults()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/feature/" });
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "maintenance-history.db"));
        using var gate = new SemaphoreSlim(1, 1);
        var config = new WorkerConfiguration { Project = new ProjectSettings
            { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var registry = new ProjectRuntimeRegistry([("project.yml", config)]);
        var service = new CompletedBranchMaintenanceService(registry, history);
        service.Register(config, git, gate);
        const string branch = "done/feature/example-task-17";
        const string active = "done/feature/example-task-18";
        var tip = await fixture.Git("rev-parse", "HEAD");
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, issue) in new[] { (branch, 17), (active, 18) })
        {
            await fixture.Git("branch", name);
            await fixture.Git("push", "origin", name);
            await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", issue, "Example task",
                $"feature/example-task-{issue}", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [],
                tip, "main", name, null));
        }
        await fixture.Git("worktree", "add", Path.Combine(fixture.WorktreeRoot, "active-archive"), active);
        var request = new CodexProvisioning.ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"),
            "obs_0123456789abcdef0123456789abcdef", null, null, null, null, Action: "archived-branches", Apply: true,
            Project: "sample", Repository: "owner/repo", OlderThanDays: 30, Branches: [new(branch, tip), new(active, tip)]);
        Task<CodexProvisioning.ExecutionMaintenanceReport> Run(CodexProvisioning.ExecutionMaintenanceRequest scope, CancellationToken ct = default,
            bool authorized = true) => service.RunManagedAsync(scope, _ => Task.FromResult(authorized), ct, standalone: true);
        Assert.Equal("project-unavailable", (await Run(request with { Repository = "owner/other" })).Reason);
        Assert.Equal("worker-drain-required", (await Run(request)).Reason);
        Assert.True(registry.TryReserve("sample"));
        registry.DrainWorker();
        Assert.Equal("worker-drain-required", (await Run(request)).Reason);
        registry.Release("sample");
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = Run(request, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        gate.Release();
        Assert.Equal("server-authority-rejected", (await Run(request, authorized: false)).Reason);
        var stale = await Run(request with { Branches = [new(branch, new string('0', 40))] });
        Assert.Equal("review", Assert.Single(stale.Branches ?? []).Decision);
        var result = await Run(request);
        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal("deleted", Assert.Single(result.Branches ?? [], b => b.Branch == branch).Decision);
        Assert.Equal("review", Assert.Single(result.Branches ?? [], b => b.Branch == active).Decision);
        Assert.Equal(tip, await fixture.Git("rev-parse", active));
        Assert.True(registry.CancelWorkerDrain());
        Assert.True(registry.TryReserve("sample"));
        registry.Release("sample");
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CompletedBranchMaintenanceUsesPersistedHistoryAndRepositoryGate()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "maintenance-history.db"));
        using var gate = new SemaphoreSlim(1, 1);
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
            Codex = new CodexSettings { InstructionsFile = Path.Combine(fixture.Checkout, "base.txt") }
        };
        var registry = new ProjectRuntimeRegistry([("project.yml", config)]);
        var service = new CompletedBranchMaintenanceService(registry, history);
        service.Register(config, git, gate);
        const string branch = "done/example-task-17";
        await fixture.Git("branch", branch);
        await fixture.Git("push", "origin", branch);
        var tip = await fixture.Git("rev-parse", branch);
        var now = DateTimeOffset.UtcNow;
        await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [],
            tip, "main", branch, null));
        var request = new CompletedBranchCleanupRequest(fixture.Checkout, 30, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(request, CancellationToken.None));
        Assert.True(registry.TryReserve("sample"));
        registry.DrainWorker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(request, CancellationToken.None));
        registry.Release("sample");
        Assert.False(registry.TryReserve("sample"));
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = service.CleanupAsync(request, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        Assert.False(registry.CancelWorkerDrain());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(tip, await fixture.Git("rev-parse", branch));
        gate.Release();
        var result = await service.CleanupAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(1, result.Deleted);
        Assert.Null(await service.CleanupAsync(request with { RepositoryDirectory = fixture.WorktreeRoot }, CancellationToken.None));
        registry.ReplaceConfiguration([("project.yml", new WorkerConfiguration { Project = config.Project, Codex = config.Codex })]);
        Assert.Null(await service.CleanupAsync(request, CancellationToken.None));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CompletedBranchCleanupBoundsTheSameEligibleSetForPreviewAndApply()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        var tip = await fixture.Git("rev-parse", "HEAD");
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new List<ExecutionHistoryEntry>();
        foreach (var number in new[] { 17, 18 })
        {
            var branch = $"done/example-task-{number}";
            await fixture.Git("branch", branch);
            await fixture.Git("push", "origin", branch);
            history.Add(new(Guid.NewGuid(), "sample", "owner/repo", number, "Example task",
                $"feature/example-task-{number}", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1,
                null, null, 0, [], tip, "main", branch, null));
        }
        var preview = await git.CleanupCompletedBranchesAsync("sample", history, 30, false, now, CancellationToken.None, limit: 1);
        Assert.Equal(1, preview.Eligible);
        Assert.Equal(1, preview.Skipped);
        var applied = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None, limit: 1);
        Assert.Equal(1, applied.Deleted);
        Assert.Equal(preview.Branches.Select(b => b.Branch), applied.Branches.Select(b => b.Branch));
        Assert.Equal("deleted", applied.Branches[0].Decision);
        Assert.Equal("skipped", applied.Branches[1].Decision);
        Assert.Equal(tip, await fixture.Git("rev-parse", "done/example-task-18"));
        Assert.Contains(tip, await fixture.Git("ls-remote", "--heads", "origin", "refs/heads/done/example-task-18"), StringComparison.Ordinal);
        var remaining = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None, limit: 1);
        Assert.Equal(1, remaining.Deleted);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CompletedBranchCleanupRemoteLeasePreservesRefChangedDuringPush()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        const string branch = "done/example-task-17";
        var tip = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("branch", branch);
        await fixture.Git("push", "origin", branch);
        await fixture.AdvanceBaseAsync();
        var changedTip = await fixture.Git("rev-parse", "HEAD");
        var remotePath = Path.Combine(Path.GetDirectoryName(fixture.Checkout) ?? "", "origin.git");
        var hook = Path.Combine(fixture.Checkout, ".git", "hooks", "pre-push");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ngit --git-dir='{remotePath}' update-ref refs/heads/{branch} {changedTip} {tip}\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var now = DateTimeOffset.UtcNow;
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [],
            tip, "main", branch, null);
        var result = await git.CleanupCompletedBranchesAsync("sample", [entry], 30, true, now, CancellationToken.None);
        Assert.Equal("review", Assert.Single(result.Branches).Decision);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(tip, await fixture.Git("rev-parse", branch));
        Assert.StartsWith(changedTip, await fixture.Git("ls-remote", "--heads", "origin", "refs/heads/" + branch));
    }

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
