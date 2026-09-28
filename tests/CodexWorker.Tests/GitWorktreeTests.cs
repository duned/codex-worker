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
