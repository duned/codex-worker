using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitWorktreeTests
{
    [Fact]
    public async Task ExecutionWorktreeIsIsolatedAndRemovedAfterTaskFailureCleanup()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.WorktreeRoot, id.ToString("N")), git.ExecutionDirectory);
        Assert.NotEqual(fixture.Checkout, git.ExecutionDirectory);
        Assert.True(Directory.Exists(git.ExecutionDirectory));
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "generated.txt"), "task output");
        await git.VerifyCodexStateAsync(CancellationToken.None);
        await git.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreeRoot, id.ToString("N"))));
        Assert.False(File.Exists(Path.Combine(fixture.Checkout, "generated.txt")));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/17-example-task"));
    }

    [Fact]
    public async Task SuccessfulIntegrationCommitsFromWorktreeAndPreservesFeatureBranch()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false, DeleteLocalFeatureBranch = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var executionDirectory = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(executionDirectory, "implemented.txt"), "implementation");
        await git.VerifyCodexStateAsync(CancellationToken.None);

        var result = await git.CommitAndIntegrateAsync(fixture.Issue, CancellationToken.None);

        Assert.True(result.HasChanges);
        Assert.False(Directory.Exists(executionDirectory));
        Assert.False(File.Exists(Path.Combine(fixture.Checkout, "implemented.txt")));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.Contains("feature/17-example-task", await fixture.Git("branch", "--list", "feature/17-example-task"));
        Assert.Equal("implementation", await fixture.Git("show", "feature/17-example-task:implemented.txt"));
    }

    [Fact]
    public async Task DistinctExecutionsUseDistinctDirectoriesAndOccupiedPathsArePreserved()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Fact]
    public async Task SimultaneousCleanupRemovesOnlyEachExecutionAndKeepsGitMetadataValid()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/17-example-task"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/18-second-task"));
    }

    [Fact]
    public async Task SimultaneousSuccessfulIntegrationsCleanOnlyTheirOwnWorktrees()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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
            first.CommitAndIntegrateAsync(firstIssue, CancellationToken.None),
            second.CommitAndIntegrateAsync(secondIssue, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.HasChanges));
        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
        var worktrees = await fixture.Git("worktree", "list", "--porcelain");
        Assert.DoesNotContain(firstId.ToString("N"), worktrees);
        Assert.DoesNotContain(secondId.ToString("N"), worktrees);
        Assert.Equal("first", await fixture.Git("show", "feature/17-example-task:first.txt"));
        Assert.Equal("second", await fixture.Git("show", "feature/18-second-task:second.txt"));
    }

    [Fact]
    public async Task CleaningExecutionADoesNotRemoveExecutionB()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await second.StartIssueAsync(secondId, fixture.Issue with { Number = 18, Title = "Second task" }, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "diagnostic.txt"), "still active");

        await first.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        Assert.False(Directory.Exists(firstDirectory));
        Assert.True(Directory.Exists(secondDirectory));
        Assert.Equal("still active", await File.ReadAllTextAsync(Path.Combine(secondDirectory, "diagnostic.txt")));
        Assert.Contains(secondDirectory, await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task CancellationBeforeCleanupPreservesThisAndOtherExecutionWorkspaces()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Fact]
    public async Task UnexpectedWorktreeBranchPreservesExecutionState()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _root;
        private RepositoryFixture(string root)
        {
            _root = root;
            Checkout = Path.Combine(root, "checkout");
            WorktreeRoot = Path.Combine(root, "managed-worktrees");
        }
        public string Checkout { get; }
        public string WorktreeRoot { get; }
        public GitHubIssue Issue { get; } = new(17, "Example task", "Implement this request", DateTimeOffset.UtcNow);
        public GitRepository CreateRepository(GitSettings settings) => new(new ProcessRunner(), Checkout, "owner/repo", settings,
            new WorkerSettings(), WorktreeRoot);

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var fixture = new RepositoryFixture(Path.Combine(Path.GetTempPath(), "codex-worker-worktree-tests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(fixture.Checkout);
            var bare = Path.Combine(fixture._root, "origin.git");
            await RunGit(fixture._root, "init", "--bare", bare);
            await RunGit(fixture.Checkout, "init", "-b", "main");
            await RunGit(fixture.Checkout, "config", "user.name", "Codex Worker Tests");
            await RunGit(fixture.Checkout, "config", "user.email", "worker-tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "base.txt"), "base");
            await RunGit(fixture.Checkout, "add", "base.txt");
            await RunGit(fixture.Checkout, "commit", "-m", "base");
            await RunGit(fixture.Checkout, "remote", "add", "origin", "https://github.com/owner/repo");
            await RunGit(fixture.Checkout, "config", $"url.file://{bare}.insteadOf", "https://github.com/owner/repo");
            await RunGit(fixture.Checkout, "push", "-u", "origin", "main");
            return fixture;
        }

        public async Task<string> Git(params string[] args) => await RunGit(Checkout, args);
        public async Task<string> GitAt(string directory, params string[] args) => await RunGit(directory, args);
        public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

        private static async Task<string> RunGit(string directory, params string[] args)
        {
            var result = await new ProcessRunner().RunAsync("git", args, directory, TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
            return result.StandardOutput.Trim();
        }
    }
}
