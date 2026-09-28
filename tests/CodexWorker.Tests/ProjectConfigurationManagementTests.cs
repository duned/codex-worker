using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProjectConfigurationManagementTests
{
    [Fact]
    public async Task CreateUpdateRemoveReloadAndNeverExposeEnvironmentValues()
    {
        using var fixture = new Fixture();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Root, "history.db"));
        var service = fixture.Service(history);

        var created = await service.CreateAsync(fixture.Configuration("alpha", "owner/alpha"), CancellationToken.None);
        Assert.Equal("alpha", created.Name);
        await service.CreateAsync(fixture.Configuration("beta", "owner/beta"), CancellationToken.None);
        Assert.Equal(2, (await service.ListAsync(CancellationToken.None)).Count);

        var persisted = await new LocalYamlProjectConfigurationProvider(fixture.Projects).ReadAllAsync(CancellationToken.None);
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, item => Assert.DoesNotContain("secret-value", File.ReadAllText(item.Path)));

        var reloaded = new ProjectConfigurationService(new LocalYamlProjectConfigurationProvider(fixture.Projects), history, fixture.Projects);
        Assert.Equal("owner/alpha", (await reloaded.GetAsync("alpha", CancellationToken.None))!.Repository);
        var updated = await reloaded.UpdateAsync("alpha", fixture.Configuration("alpha", "owner/renamed"), CancellationToken.None);
        Assert.Equal("owner/renamed", updated.Repository);
        Assert.True(await reloaded.RemoveAsync("alpha", CancellationToken.None));
        Assert.Equal(new[] { "beta" }, (await reloaded.ListAsync(CancellationToken.None)).Select(project => project.Name));
    }

    [Fact]
    public async Task InvalidUpdateAndUniquenessConflictLeaveExistingYamlUntouched()
    {
        using var fixture = new Fixture();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Root, "history.db"));
        var service = fixture.Service(history);
        await service.CreateAsync(fixture.Configuration("alpha", "owner/alpha"), CancellationToken.None);
        await service.CreateAsync(fixture.Configuration("beta", "owner/beta"), CancellationToken.None);
        var before = Directory.GetFiles(fixture.Projects, "*.yml").ToDictionary(Path.GetFileName, File.ReadAllText);

        var invalid = fixture.Configuration("alpha", "not-a-repository");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync("alpha", invalid, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync("alpha", fixture.Configuration("alpha", "owner/beta"), CancellationToken.None));
        Assert.Equal(before, Directory.GetFiles(fixture.Projects, "*.yml").ToDictionary(Path.GetFileName, File.ReadAllText));
    }

    [Fact]
    public async Task ActiveExecutionPreventsRemovalAndLastConfigurationCannotBeRemoved()
    {
        using var fixture = new Fixture();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Root, "history.db"));
        var service = fixture.Service(history);
        await service.CreateAsync(fixture.Configuration("alpha", "owner/alpha"), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveAsync("alpha", CancellationToken.None));

        await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "alpha", "owner/alpha", 1, "issue",
            "feature/1", "main", DateTimeOffset.UtcNow, null, "Processing", null, null, null, 0, [], null, null, null, null));
        await Assert.ThrowsAsync<ProjectConfigurationConflictException>(() => service.RemoveAsync("alpha", CancellationToken.None));
    }

    [Fact]
    public async Task ConfigurationMutationReplacesRuntimeSnapshotOnlyAfterValidation()
    {
        using var fixture = new Fixture();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Root, "history.db"));
        var registry = new ProjectRuntimeRegistry([]);
        var service = new ProjectConfigurationService(new LocalYamlProjectConfigurationProvider(fixture.Projects), history, fixture.Projects, registry);
        await service.CreateAsync(fixture.Configuration("alpha", "owner/alpha"), CancellationToken.None);
        Assert.Equal("owner/alpha", registry.Snapshot().Single().Configuration.Project.Repository);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.UpdateAsync("alpha", fixture.Configuration("alpha", "invalid"), CancellationToken.None));
        Assert.Equal("owner/alpha", registry.Snapshot().Single().Configuration.Project.Repository);

        await service.UpdateAsync("alpha", fixture.Configuration("alpha", "owner/renamed"), CancellationToken.None);
        Assert.Equal("owner/renamed", registry.Snapshot().Single().Configuration.Project.Repository);

        var accepted = registry.Snapshot().Single().Configuration;
        var path = Directory.GetFiles(fixture.Projects, "*.yml").Single();
        await File.WriteAllTextAsync(path, "project: [incomplete");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReloadAsync(CancellationToken.None));
        Assert.Same(accepted, registry.Snapshot().Single().Configuration);
    }

    [Fact]
    public async Task FileWatcherDebouncesAndAppliesManualYamlChangesThroughValidatedReload()
    {
        using var fixture = new Fixture();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Root, "history.db"));
        var registry = new ProjectRuntimeRegistry([]);
        var provider = new LocalYamlProjectConfigurationProvider(fixture.Projects);
        var service = new ProjectConfigurationService(provider, history, fixture.Projects, registry);
        await service.CreateAsync(fixture.Configuration("alpha", "owner/alpha"), CancellationToken.None);
        await using var watcher = new ProjectConfigurationWatcher(fixture.Projects, service, registry, TimeSpan.FromMilliseconds(10));
        var previousVersion = registry.Version;

        await provider.WriteAsync(fixture.Configuration("alpha", "owner/renamed"), CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await registry.WaitForChangeAsync(previousVersion, timeout.Token);
        Assert.Equal("owner/renamed", registry.Snapshot().Single().Configuration.Project.Repository);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "project-config-management-" + Guid.NewGuid().ToString("N"));
        public string Projects { get; }
        public Fixture()
        {
            Projects = Path.Combine(Root, "projects");
            Directory.CreateDirectory(Projects);
            File.WriteAllText(Path.Combine(Root, "secrets.env"), "TOKEN=secret-value\n");
        }
        public ProjectConfigurationService Service(ExecutionHistoryStore history) =>
            new(new LocalYamlProjectConfigurationProvider(Projects), history, Projects);
        public WorkerConfiguration Configuration(string name, string repository)
        {
            var checkout = Path.Combine(Root, "checkout-" + name);
            Directory.CreateDirectory(checkout);
            File.WriteAllText(Path.Combine(checkout, "AGENTS.md"), "instructions");
            return new WorkerConfiguration
            {
                Project = new ProjectSettings { Name = name, Repository = repository, Directory = checkout },
                GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" },
                Codex = new CodexSettings { InstructionsFile = Path.Combine(checkout, "AGENTS.md") },
                Environment = new ProjectEnvironmentSettings { File = Path.Combine(Root, "secrets.env"), Variables = new Dictionary<string, string> { ["TOKEN"] = "secret-value" } }
            };
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
