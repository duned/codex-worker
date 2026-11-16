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
    [Trait("ValidationProfile", "FastCandidate")]
    [Fact]
    public async Task ExecutionWorktreeIsIsolatedAndRemovedAfterTaskFailureCleanup()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.WorktreeRoot, id.ToString("N")), git.ExecutionDirectory);
        Assert.NotEqual(fixture.Checkout, git.ExecutionDirectory);
        Assert.True(Directory.Exists(git.ExecutionDirectory));
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "generated.txt"), "task output");
        await git.VerifyCodexStateAsync(CancellationToken.None);
        using var sibling = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await sibling.StartIssueAsync(Guid.NewGuid(), fixture.Issue with { Number = 18, Title = "Second task" }, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(sibling.ExecutionDirectory, "diagnostic.txt"), "still active");
        await git.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreeRoot, id.ToString("N"))));
        Assert.False(File.Exists(Path.Combine(fixture.Checkout, "generated.txt")));
        Assert.Equal("still active", await File.ReadAllTextAsync(Path.Combine(sibling.ExecutionDirectory, "diagnostic.txt")));
        Assert.Contains(sibling.ExecutionDirectory, await fixture.Git("worktree", "list", "--porcelain"));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task FailedExecutionWithChangesRetainsOwnedWorktreeAndNeverIntegratesOrPushes()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true, PushCompletedBranch = true });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var directory = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "partial.txt"), "useful partial work");

        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);

        Assert.NotNull(recovery);
        Assert.Equal("feature/example-task-17", recovery.Branch);
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.True(Directory.Exists(directory));
        Assert.Equal("useful partial work", await File.ReadAllTextAsync(Path.Combine(directory, "partial.txt")));
        Assert.DoesNotContain("partial.txt", await fixture.Git("ls-tree", "-r", "--name-only", "main"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "completed/example-task-17"));
        Assert.Contains($"worktree {directory}", await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task FailedExecutionWithoutChangesRemovesWorktreeAndBranch()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var directory = git.ExecutionDirectory;

        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);

        Assert.Null(recovery);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RetryResumeCopiesVerifiedPartialFilesIntoNewOwnedWorktree()
    {
        using var fixture = await seed.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        var previousDirectory = first.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(previousDirectory, "partial.txt"), "useful partial work");
        File.Delete(Path.Combine(previousDirectory, "base.txt"));
        var recovery = await first.PreserveFailedIssueChangesAsync(CancellationToken.None);
        Assert.NotNull(recovery);
        var previous = new ExecutionHistoryEntry(firstId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, "partial implementation",
            null, 0, [], null, null, null, "validation failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);

        using var retry = first.CreateExecutionRepository();
        var retryId = Guid.NewGuid();
        await retry.StartIssueAsync(retryId, fixture.Issue, previous, true, 2, CancellationToken.None);

        Assert.NotEqual(previousDirectory, retry.ExecutionDirectory);
        Assert.Equal("useful partial work", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "partial.txt")));
        Assert.False(File.Exists(Path.Combine(retry.ExecutionDirectory, "base.txt")));
        Assert.Equal("feature/example-task-17-retry-2", await fixture.GitAt(retry.ExecutionDirectory, "branch", "--show-current"));
        Assert.True(Directory.Exists(previousDirectory));
        await retry.DiscardUncommittedIssueChangesAsync(CancellationToken.None);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RetryResumeReappliesOnlyPreservedDeltaOnAdvancedBaseBeforeIntegration()
    {
        using var fixture = await seed.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var originalId = Guid.NewGuid();
        await original.StartIssueAsync(originalId, fixture.Issue, CancellationToken.None);
        var originalDirectory = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(originalDirectory, "task.txt"), "recovered task change");
        var recovery = await original.PreserveFailedIssueChangesAsync(CancellationToken.None);
        Assert.NotNull(recovery);
        var previous = new ExecutionHistoryEntry(originalId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, "partial implementation",
            null, 0, [], null, null, null, "validation failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);

        await fixture.AddAndPushAsync("cw", "#!/bin/sh\nprintf 'new executable\\n'\n", executable: true);
        await fixture.AddAndPushAsync("base.txt", "newer source modification");
        await fixture.AddAndPushAsync("src/example.cs", "newer source change");
        await fixture.AddAndPushAsync("tests/example.cs", "newer test change");
        var baseBeforeRetry = await fixture.Git("rev-parse", "main");

        using var retry = original.CreateExecutionRepository();
        await retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, true, 2, CancellationToken.None);
        Assert.Equal("newer source change", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "src", "example.cs")));
        Assert.Equal("newer test change", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "tests", "example.cs")));
        Assert.Equal("newer source modification", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "base.txt")));
        Assert.Equal("recovered task change", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "task.txt")));
        Assert.True(File.Exists(Path.Combine(retry.ExecutionDirectory, "cw")));
        Assert.Equal(baseBeforeRetry, await fixture.GitAt(retry.ExecutionDirectory, "rev-parse", "HEAD"));

        // Normal execution validation runs against the prepared source before integration.
        var validatedHead = await fixture.GitAt(retry.ExecutionDirectory, "rev-parse", "HEAD");
        Assert.Equal(baseBeforeRetry, validatedHead);
        var result = await retry.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        Assert.True(result.HasChanges);
        Assert.Equal("newer source change", await fixture.Git("show", "main:src/example.cs"));
        Assert.Equal("newer test change", await fixture.Git("show", "main:tests/example.cs"));
        Assert.Equal("newer source modification", await fixture.Git("show", "main:base.txt"));
        Assert.Equal("recovered task change", await fixture.Git("show", "main:task.txt"));
        Assert.Equal("#!/bin/sh\nprintf 'new executable\\n'\n", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "cw")));
        Assert.StartsWith("100755", await fixture.Git("ls-tree", "main", "cw"));
        Assert.Equal(baseBeforeRetry, await fixture.Git("rev-parse", "main^"));
        Assert.Equal("task.txt", await fixture.Git("diff", "--name-only", baseBeforeRetry, "main"));
        Assert.NotEmpty(validatedHead);
        Assert.True(Directory.Exists(originalDirectory));
        Assert.Equal("recovered task change", await File.ReadAllTextAsync(Path.Combine(originalDirectory, "task.txt")));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RetryResumeConflictStopsAndPreservesOriginalRecoveryWorkspace()
    {
        using var fixture = await seed.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var originalId = Guid.NewGuid();
        await original.StartIssueAsync(originalId, fixture.Issue, CancellationToken.None);
        var originalDirectory = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(originalDirectory, "base.txt"), "task replacement");
        var recovery = await original.PreserveFailedIssueChangesAsync(CancellationToken.None);
        Assert.NotNull(recovery);
        var previous = new ExecutionHistoryEntry(originalId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, "partial implementation",
            null, 0, [], null, null, null, "validation failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);
        await fixture.AddAndPushAsync("base.txt", "incompatible newer base change");

        using var retry = original.CreateExecutionRepository();
        var conflict = await Assert.ThrowsAsync<IssuePreparationRejectedException>(() =>
            retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, true, 2, CancellationToken.None));

        Assert.Contains("conflict", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("task replacement", await File.ReadAllTextAsync(Path.Combine(originalDirectory, "base.txt")));
        Assert.Equal("incompatible newer base change", await fixture.Git("show", "main:base.txt"));
        Assert.True(Directory.Exists(originalDirectory));
        Assert.DoesNotContain("retry-2", await fixture.Git("branch", "--list"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RetryRestartUsesCurrentBaseWithoutCopyingPreviousPartialFiles()
    {
        using var fixture = await seed.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(first.ExecutionDirectory, "partial.txt"), "do not use");
        var recovery = await first.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var previous = new ExecutionHistoryEntry(firstId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery!.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);

        using var retry = first.CreateExecutionRepository();
        await retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, false, 2, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(retry.ExecutionDirectory, "partial.txt")));
        await retry.DiscardUncommittedIssueChangesAsync(CancellationToken.None);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("ignored", false)]
    [InlineData("unignored", true)]
    [InlineData("tracked", true)]
    [InlineData("ancestor", true)]
    public async Task ResumeUsesGitPathsAndRejectsCaptureLinks(string scenario, bool rejected)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        await fixture.AddAndPushAsync("generated/tracked.txt", "tracked source");
        await fixture.AddAndPushAsync(".gitignore", "generated/\n");
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await original.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var workspace = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "preserved source");
        var generated = Path.Combine(workspace, "generated");
        await File.WriteAllTextAsync(Path.Combine(generated, "tracked.txt"), "changed tracked source");
        var bin = Path.Combine(generated, "node_modules", ".bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "esbuild"), "/outside/missing/esbuild");
        Directory.CreateSymbolicLink(Path.Combine(bin, "external-tree"), "/outside/missing/tree");
        await File.WriteAllTextAsync(Path.Combine(generated, "staged-source.txt"), "staged source");
        await fixture.GitAt(workspace, "add", "--force", "generated/staged-source.txt");
        if (scenario == "unignored") File.CreateSymbolicLink(Path.Combine(workspace, "unsafe.txt"), "/etc/passwd");
        if (scenario == "tracked")
        {
            File.Delete(Path.Combine(generated, "tracked.txt"));
            File.CreateSymbolicLink(Path.Combine(generated, "tracked.txt"), "/etc/passwd");
        }
        if (scenario == "ancestor")
        {
            Directory.Move(generated, generated + "-saved");
            Directory.CreateSymbolicLink(generated, generated + "-saved");
        }
        var recovery = Assert.IsType<GitRecoveryInfo>(await original.PreserveFailedIssueChangesAsync(CancellationToken.None));
        var previous = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);
        using var retry = original.CreateExecutionRepository();
        if (rejected)
            await Assert.ThrowsAsync<IssuePreparationRejectedException>(() => retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue,
                previous, true, 2, CancellationToken.None));
        else
        {
            await retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, true, 2, CancellationToken.None);
            Assert.Equal("preserved source", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "partial.txt")));
            Assert.Equal("changed tracked source", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "generated", "tracked.txt")));
            Assert.Equal("staged source", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "generated", "staged-source.txt")));
            Assert.False(Directory.Exists(Path.Combine(retry.ExecutionDirectory, "generated", "node_modules")));
        }
        Assert.Equal("preserved source", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
        Assert.True(Directory.Exists(workspace));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ResumeRejectsMissingOrUnsafePersistedRecoveryMetadata()
    {
        using var fixture = await seed.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(first.ExecutionDirectory, "partial.txt"), "useful partial work");
        var recovery = await first.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var previous = new ExecutionHistoryEntry(firstId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery!.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "uncertain", recovery.BaseCommit, recovery.StatusSummary);

        using var retry = first.CreateExecutionRepository();
        var retryId = Guid.NewGuid();
        await Assert.ThrowsAsync<IssuePreparationRejectedException>(() => retry.StartIssueAsync(retryId, fixture.Issue,
            previous, true, 2, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreeRoot, retryId.ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(fixture.WorktreeRoot, firstId.ToString("N"))));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExistingHistoricalNumberFirstBranchDoesNotBlockOrGetRemovedWithNewExecution()
    {
        using var fixture = await seed.CreateAsync();
        const string historicalBranch = "feature/17-example-task";
        await fixture.Git("branch", historicalBranch, "main");
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);

        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        Assert.Equal("feature/example-task-17", await fixture.GitAt(git.ExecutionDirectory, "branch", "--show-current"));
        await git.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        Assert.Contains(historicalBranch, await fixture.Git("branch", "--list", historicalBranch));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
    }
}
