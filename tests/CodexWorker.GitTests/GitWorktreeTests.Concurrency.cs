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
    [Fact]
    public async Task DistinctExecutionsUseDistinctDirectoriesAndOccupiedPathsArePreserved()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await git.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        var firstDirectory = git.ExecutionDirectory;
        await git.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        var secondId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(fixture.WorktreeRoot, secondId.ToString("N")));
        var exception = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            git.StartIssueAsync(secondId, fixture.Issue, CancellationToken.None));

        Assert.Contains("already exists", exception.Message);
        Assert.NotEqual(firstDirectory, Path.Combine(fixture.WorktreeRoot, secondId.ToString("N")));
        Assert.True(Directory.Exists(Path.Combine(fixture.WorktreeRoot, secondId.ToString("N"))));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task SimultaneousCleanupRemovesOnlyEachExecutionAndKeepsGitMetadataValid()
    {
        using var fixture = await seed.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        var secondIssue = fixture.Issue with { Number = 18, Title = "Second task" };
        await second.StartIssueAsync(secondId, secondIssue, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "second.txt"), "second");

        await Task.WhenAll(
            first.DiscardUncommittedIssueChangesAsync(CancellationToken.None),
            second.DiscardUncommittedIssueChangesAsync(CancellationToken.None));

        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
        Assert.DoesNotContain(firstId.ToString("N"), await fixture.Git("worktree", "list", "--porcelain"));
        Assert.DoesNotContain(secondId.ToString("N"), await fixture.Git("worktree", "list", "--porcelain"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/second-task-18"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task PreservingOneFailedExecutionLeavesConcurrentExecutionUntouched()
    {
        using var fixture = await seed.CreateAsync();
        using var failed = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var active = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await failed.InitializeAsync(CancellationToken.None);
        var failedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await failed.StartIssueAsync(failedId, fixture.Issue, CancellationToken.None);
        await active.StartIssueAsync(activeId, fixture.Issue with { Number = 18, Title = "Active task" }, CancellationToken.None);
        var failedDirectory = failed.ExecutionDirectory;
        var activeDirectory = active.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(failedDirectory, "partial.txt"), "partial");
        await File.WriteAllTextAsync(Path.Combine(activeDirectory, "active.txt"), "active");

        var recovery = await failed.PreserveFailedIssueChangesAsync(CancellationToken.None);

        Assert.NotNull(recovery);
        Assert.True(Directory.Exists(failedDirectory));
        Assert.True(Directory.Exists(activeDirectory));
        Assert.Equal("active", await File.ReadAllTextAsync(Path.Combine(activeDirectory, "active.txt")));
        var worktrees = await fixture.Git("worktree", "list", "--porcelain");
        Assert.Contains($"worktree {failedDirectory}", worktrees);
        Assert.Contains($"worktree {activeDirectory}", worktrees);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task SimultaneousSuccessfulIntegrationsCleanOnlyTheirOwnWorktrees()
    {
        using var fixture = await seed.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false, DeleteLocalFeatureBranch = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false, DeleteLocalFeatureBranch = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstIssue = fixture.Issue;
        var secondIssue = fixture.Issue with { Number = 18, Title = "Second task" };
        await first.StartIssueAsync(firstId, firstIssue, CancellationToken.None);
        await second.StartIssueAsync(secondId, secondIssue, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "second.txt"), "second");

        var results = await Task.WhenAll(
            first.CommitAndIntegrateAsync(firstIssue, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None),
            second.CommitAndIntegrateAsync(secondIssue, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));

        Assert.All(results, result => Assert.True(result.HasChanges));
        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
        var worktrees = await fixture.Git("worktree", "list", "--porcelain");
        Assert.DoesNotContain(firstId.ToString("N"), worktrees);
        Assert.DoesNotContain(secondId.ToString("N"), worktrees);
        Assert.Equal("first", await fixture.Git("show", "feature/example-task-17:first.txt"));
        Assert.Equal("second", await fixture.Git("show", "feature/second-task-18:second.txt"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task CancellationBeforeCleanupPreservesThisAndOtherExecutionWorkspaces()
    {
        using var fixture = await seed.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await second.StartIssueAsync(secondId, fixture.Issue with { Number = 18, Title = "Second task" }, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => first.DiscardUncommittedIssueChangesAsync(cancelled.Token));

        Assert.True(Directory.Exists(firstDirectory));
        Assert.True(Directory.Exists(secondDirectory));
        Assert.Contains(firstDirectory, await fixture.Git("worktree", "list", "--porcelain"));
        Assert.Contains(secondDirectory, await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task UnexpectedWorktreeBranchPreservesExecutionState()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var executionDirectory = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(executionDirectory, "diagnostic.txt"), "keep for inspection");
        await fixture.GitAt(executionDirectory, "switch", "-c", "unexpected");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => git.DiscardUncommittedIssueChangesAsync(CancellationToken.None));

        Assert.True(Directory.Exists(executionDirectory));
        Assert.Equal("keep for inspection", await File.ReadAllTextAsync(Path.Combine(executionDirectory, "diagnostic.txt")));
    }
}
