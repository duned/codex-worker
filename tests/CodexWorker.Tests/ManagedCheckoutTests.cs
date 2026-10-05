using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class ManagedCheckoutTests
{
    [Fact]
    public async Task CatalogProbeDoesNotCloneAndFirstAssignmentPublishesCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        using var repository = fixture.Repository();
        await repository.ValidateManagedRemoteReadAsync(CancellationToken.None);
        Assert.False(Directory.Exists(fixture.Checkout));

        await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);

        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
        Assert.True(GitRepository.OriginMatchesRepository(await fixture.Git(fixture.Checkout,
            "config", "--get", "remote.origin.url"), "owner/repo"));
        Assert.Empty(Directory.GetFiles(fixture.Checkout, "*.yml"));
    }

    [Fact]
    public async Task CatalogAccessFailureDoesNotExposeProcessDiagnostics()
    {
        using var fixture = await Fixture.CreateAsync();
        Directory.Move(Path.Combine(fixture.Root, "origin.git"), Path.Combine(fixture.Root, "unavailable.git"));
        using var repository = fixture.Repository();

        var error = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            repository.ValidateManagedRemoteReadAsync(CancellationToken.None));

        Assert.Equal("Git repository read authentication is unavailable for 'owner/repo'.", error.Message);
        Assert.Null(error.InnerException);
        Assert.False(Directory.Exists(fixture.Checkout));
    }

    [Fact]
    public async Task ReuseAndRestartFastForwardWithoutReplacingLocalResources()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var repository = fixture.Repository())
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        await fixture.Git(fixture.Checkout, "branch", "preserved-recovery");
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "task.txt"), "updated");
        await fixture.Git(fixture.Source, "commit", "-am", "update");
        await fixture.Git(fixture.Source, "push", "origin", "main");

        using var restarted = fixture.Repository();
        await restarted.MaterializeManagedCheckoutAsync(CancellationToken.None);
        await restarted.MaterializeManagedCheckoutAsync(CancellationToken.None);

        Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
        Assert.Contains("preserved-recovery", await fixture.Git(fixture.Checkout, "branch", "--list"));
    }

    [Fact]
    public async Task RepositoryMismatchPreservesCheckoutWithoutUpdatingIt()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var repository = fixture.Repository())
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        await fixture.Git(fixture.Checkout, "remote", "set-url", "origin", "https://github.com/other/project.git");
        var head = await fixture.Git(fixture.Checkout, "rev-parse", "HEAD");

        using var restarted = fixture.Repository();
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => restarted.MaterializeManagedCheckoutAsync(CancellationToken.None));

        Assert.Equal(head, await fixture.Git(fixture.Checkout, "rev-parse", "HEAD"));
        Assert.Equal("https://github.com/other/project.git", await fixture.Git(fixture.Checkout, "config", "--get", "remote.origin.url"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsafeExistingCheckoutIsPreserved(bool dirty)
    {
        using var fixture = await Fixture.CreateAsync();
        using (var repository = fixture.Repository())
            await repository.MaterializeManagedCheckoutAsync(CancellationToken.None);
        if (dirty) await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "task.txt"), "preserved edit");
        else await fixture.Git(fixture.Checkout, "switch", "-c", "preserved-branch");

        using var restarted = fixture.Repository();
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => restarted.MaterializeManagedCheckoutAsync(CancellationToken.None));

        if (dirty) Assert.Equal("preserved edit", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
        else Assert.Equal("preserved-branch", await fixture.Git(fixture.Checkout, "branch", "--show-current"));
    }

    [Fact]
    public async Task InterruptedCloneIsNotPublishedAndRestartUsesFreshStaging()
    {
        using var fixture = await Fixture.CreateAsync();
        // A completed clone whose validation failed is equivalent to a process
        // stopping before atomic publication: it must never become authoritative.
        using (var invalid = fixture.Repository(new GitSettings { FeaturePrefix = "invalid.." }))
            await Assert.ThrowsAsync<WorkerInfrastructureException>(() => invalid.MaterializeManagedCheckoutAsync(CancellationToken.None));
        Assert.False(Directory.Exists(fixture.Checkout));
        var abandoned = Assert.Single(Directory.GetDirectories(fixture.Root, "checkout.clone-*"));

        using var restarted = fixture.Repository();
        await restarted.MaterializeManagedCheckoutAsync(CancellationToken.None);

        Assert.True(Directory.Exists(abandoned));
        Assert.Equal("base", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "task.txt")));
    }

    [Fact]
    public async Task MultipleAssignmentsShareRepositoryGateAndOneCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        using var repository = fixture.Repository();
        using var gate = new SemaphoreSlim(1, 1);
        async Task PrepareAsync()
        {
            await gate.WaitAsync();
            try { await repository.MaterializeManagedCheckoutAsync(CancellationToken.None); }
            finally { gate.Release(); }
        }
        await Task.WhenAll(PrepareAsync(), PrepareAsync());
        Assert.Empty(Directory.GetDirectories(fixture.Root, "checkout.clone-*"));
        Assert.Equal("main", await fixture.Git(fixture.Checkout, "branch", "--show-current"));
    }

    [Fact]
    public async Task CancellationDoesNotPublishCheckout()
    {
        using var fixture = await Fixture.CreateAsync();
        using var repository = fixture.Repository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.MaterializeManagedCheckoutAsync(cancellation.Token));
        Assert.False(Directory.Exists(fixture.Checkout));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? _previousGitConfiguration = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cw-managed-checkout", Guid.NewGuid().ToString("N"));
        public string Checkout => Path.Combine(Root, "checkout");
        public string Source => Path.Combine(Root, "source");
        public GitRepository Repository(GitSettings? settings = null) => new(new ProcessRunner(), Checkout,
            "owner/repo", settings ?? new GitSettings(), new WorkerSettings(), Path.Combine(Root, "worktrees"));

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture.Source);
                var bare = Path.Combine(fixture.Root, "origin.git");
                await fixture.Git(fixture.Root, "init", "--bare", "--initial-branch=main", bare);
                await fixture.Git(fixture.Source, "init", "-b", "main");
                await fixture.Git(fixture.Source, "config", "user.name", "Worker Tests");
                await fixture.Git(fixture.Source, "config", "user.email", "worker@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(fixture.Source, "task.txt"), "base");
                await fixture.Git(fixture.Source, "add", "task.txt");
                await fixture.Git(fixture.Source, "commit", "-m", "base");
                await fixture.Git(fixture.Source, "remote", "add", "origin", bare);
                await fixture.Git(fixture.Source, "push", "origin", "main");
                var configuration = Path.Combine(fixture.Root, "gitconfig");
                await fixture.Git(fixture.Root, "config", "--file", configuration,
                    $"url.file://{bare}.insteadOf", "https://github.com/owner/repo.git");
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", configuration);
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public async Task<string> Git(string directory, params string[] arguments)
        {
            var result = await new ProcessRunner().RunAsync("git", arguments, directory, TimeSpan.FromSeconds(15));
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput.Trim();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", _previousGitConfiguration);
            Directory.Delete(Root, recursive: true);
        }
    }
}
