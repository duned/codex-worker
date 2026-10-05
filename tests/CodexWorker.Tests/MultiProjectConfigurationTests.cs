using CodexWorker;

namespace CodexWorker.Tests;

public sealed class MultiProjectConfigurationTests
{
    [Fact]
    public void LoadsGlobalConfigAndResolvesProjectsRelativeToWorkerFile()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "worker.yml");
        File.WriteAllText(path, "worker:\n  pollingSeconds: 23\n  preflightTimeoutSeconds: 45\nprojects:\n  directory: ./projects\ntelegram:\n  enabled: false\n");
        var config = GlobalWorkerConfiguration.Load(path);
        Assert.Equal(23, config.Worker.PollingSeconds);
        Assert.Equal(45, config.Worker.PreflightTimeoutSeconds);
        Assert.Equal(1, config.Worker.MaxParallelTasks);
        Assert.Equal(Path.Combine(fixture.Root, "projects"), config.Projects.Directory);
    }

    [Fact]
    public void ManagedOwnershipIgnoresLocalYamlAndDoesNotRequireProjectsDirectory()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Projects, "invalid.yml"), "invalid: configuration");
        var global = new GlobalWorkerConfiguration { Projects = new() { Directory = fixture.Projects, Ownership = "managed" } };
        Assert.Empty(ProjectConfigurationDiscovery.LoadForWorker(global));
        global.Projects.Directory = Path.Combine(fixture.Root, "absent");
        Assert.Empty(ProjectConfigurationDiscovery.LoadForWorker(global));
        global.Projects.Ownership = "standalone";
        Assert.Throws<InvalidDataException>(() => ProjectConfigurationDiscovery.LoadForWorker(global));
    }

    [Fact]
    public void ManagedRuntimeDefaultsResolveAtGlobalLayerAndRejectUnknownKeys()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "worker.yml");
        File.WriteAllText(path, "managedProjects:\n  checkoutDirectory: ./runtime\n  worker:\n    maxParallelTasks: 2\n  validation:\n    commands: [dotnet test]\n");
        var global = GlobalWorkerConfiguration.Load(path);
        Assert.Equal(Path.Combine(fixture.Root, "runtime"), global.ManagedProjects.CheckoutDirectory);
        Assert.Equal(2, global.ManagedProjects.Worker.MaxParallelTasks);
        Assert.Equal(["dotnet test"], global.ManagedProjects.Validation.Commands);
        File.WriteAllText(path, "managedProjects:\n  repository: owner/repo\n");
        Assert.Throws<InvalidDataException>(() => GlobalWorkerConfiguration.Load(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void RejectsUnsupportedGlobalParallelism(int parallelism)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "worker.yml");
        File.WriteAllText(path, $"worker:\n  maxParallelTasks: {parallelism}\nprojects:\n  directory: ./projects\n");
        Assert.Throws<InvalidDataException>(() => GlobalWorkerConfiguration.Load(path));
    }

    [Fact]
    public void LoadsSupportedGlobalParallelism()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "worker.yml");
        File.WriteAllText(path, "worker:\n  maxParallelTasks: 3\nprojects:\n  directory: ./projects\n");
        Assert.Equal(3, GlobalWorkerConfiguration.Load(path).Worker.MaxParallelTasks);
    }

    [Fact]
    public void ProjectParallelismDefaultsToOneAndCanBeConfigured()
    {
        using var fixture = new Fixture();
        fixture.AddProject("default.yml", "Default", "owner/default", "default");
        fixture.AddProject("parallel.yml", "Parallel", "owner/parallel", "parallel", maxParallelTasks: 2);

        var projects = ProjectConfigurationDiscovery.Load(fixture.Projects);

        Assert.Equal(1, projects.Single(x => x.Configuration.Project.Name == "Default").Configuration.Worker.MaxParallelTasks);
        Assert.Equal(2, projects.Single(x => x.Configuration.Project.Name == "Parallel").Configuration.Worker.MaxParallelTasks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void RejectsUnsupportedProjectParallelism(int parallelism)
    {
        using var fixture = new Fixture();
        fixture.AddProject("invalid.yml", "Invalid", "owner/invalid", "invalid", maxParallelTasks: parallelism);

        var error = Assert.Throws<InvalidDataException>(() => ProjectConfigurationDiscovery.Load(fixture.Projects));

        Assert.Contains("worker.maxParallelTasks", error.Message);
    }

    [Fact]
    public void DiscoversYamlDeterministicallyAndIgnoresOtherFiles()
    {
        using var fixture = new Fixture();
        fixture.AddProject("zeta.yml", "Zeta", "owner/zeta", "zeta");
        fixture.AddProject("notes.txt", "Ignored", "owner/ignored", "ignored");
        fixture.AddProject("alpha.yaml", "Alpha", "owner/alpha", "alpha");
        var projects = ProjectConfigurationDiscovery.Load(fixture.Projects);
        Assert.Equal(new[] { "alpha.yaml", "zeta.yml" }, projects.Select(x => Path.GetFileName(x.Path)));
    }

    [Fact]
    public void RejectsEmptyProjectsDirectory()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => ProjectConfigurationDiscovery.Load(fixture.Projects));
    }

    [Fact]
    public void ManagedWorkerCanDiscoverAnEmptyProjectsDirectory()
    {
        using var fixture = new Fixture();
        var global = new GlobalWorkerConfiguration
        {
            Projects = new ProjectsSettings { Directory = fixture.Projects, Ownership = "managed" },
            Server = new WorkerServerSettings { Enabled = true, Url = "https://server.example" }
        };

        var projects = ProjectConfigurationDiscovery.LoadForWorker(global);
        var scheduler = new ProjectScheduler(projects.Count);

        Assert.Empty(projects);
        Assert.Empty(scheduler.ScanOrder());
        scheduler.Reconfigure(0);
        Assert.Equal(0, scheduler.ProjectCount);

        fixture.AddProject("later.yml", "Later", "owner/later", "later");

        Assert.Empty(ProjectConfigurationDiscovery.LoadForWorker(global));
        global.Projects.Ownership = "standalone";
        Assert.Equal("Later", Assert.Single(ProjectConfigurationDiscovery.LoadForWorker(global)).Configuration.Project.Name);
    }

    [Fact]
    public void MalformedProjectNamesSourceFile()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Projects, "broken.yml"), "project: [bad");
        var error = Assert.Throws<InvalidDataException>(() => ProjectConfigurationDiscovery.Load(fixture.Projects));
        Assert.Contains("broken.yml", error.Message);
    }

    [Theory]
    [InlineData("same", "owner/a", "a", "same", "owner/b", "b")]
    [InlineData("a", "owner/same", "a", "b", "owner/same", "b")]
    [InlineData("a", "owner/a", "same", "b", "owner/b", "same")]
    public void RejectsDuplicateNamesRepositoriesAndCheckoutDirectories(string name1, string repo1, string dir1, string name2, string repo2, string dir2)
    {
        using var fixture = new Fixture();
        fixture.AddProject("a.yml", name1, repo1, dir1);
        fixture.AddProject("b.yml", name2, repo2, dir2);
        Assert.Throws<InvalidDataException>(() => ProjectConfigurationDiscovery.Load(fixture.Projects));
    }

    [Fact]
    public void RoundRobinStartsAtZeroAndAdvancesAfterSelection()
    {
        var scheduler = new ProjectScheduler(3);
        Assert.Equal(new[] { 0, 1, 2 }, scheduler.ScanOrder());
        scheduler.Selected(0);
        Assert.Equal(new[] { 1, 2, 0 }, scheduler.ScanOrder());
        scheduler.Selected(1);
        Assert.Equal(new[] { 2, 0, 1 }, scheduler.ScanOrder());
    }

    [Fact]
    public void RoundRobinSkipsProjectsAtCapacityAndUsesAvailableProjectCapacityFairly()
    {
        var scheduler = new ProjectScheduler(3);
        var limits = new[] { 1, 1, 2 };
        var active = new[] { 1, 0, 0 };
        const int globalLimit = 4;

        var first = scheduler.ScanOrder().First(index => active.Sum() < globalLimit && active[index] < limits[index]);
        scheduler.Selected(first);
        active[first]++;
        var second = scheduler.ScanOrder().First(index => active.Sum() < globalLimit && active[index] < limits[index]);
        scheduler.Selected(second);
        active[second]++;
        var third = scheduler.ScanOrder().First(index => active.Sum() < globalLimit && active[index] < limits[index]);
        scheduler.Selected(third);
        active[third]++;

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, third);
        Assert.Equal(2, active[2]);
        Assert.Equal(globalLimit, active.Sum());
        Assert.All(active.Zip(limits), pair => Assert.True(pair.First <= pair.Second));
    }

    [Fact]
    public void CompletingOrFailingExecutionRestoresItsProjectCapacity()
    {
        var scheduler = new ProjectScheduler(2);
        var limits = new[] { 1, 2 };
        var active = new[] { 1, 2 };

        Assert.DoesNotContain(scheduler.ScanOrder(), index => active[index] < limits[index]);

        // Any terminal execution (success or safe task failure) leaves the active set.
        active[0]--;
        Assert.Contains(scheduler.ScanOrder(), index => active[index] < limits[index]);
        scheduler.Selected(0);
        active[1]--;
        Assert.Contains(scheduler.ScanOrder(), index => active[index] < limits[index]);
    }

    [Fact]
    public async Task ScanSkipsEmptyQueuesAndAdvancesAfterOneSequentialSelection()
    {
        var scheduler = new ProjectScheduler(3);
        var ready = new[] { false, true, true };
        var active = 0;
        var maximumActive = 0;
        var first = await scheduler.ScanAsync(async index =>
        {
            active++;
            maximumActive = Math.Max(maximumActive, active);
            await Task.Yield();
            active--;
            return ready[index];
        });
        Assert.Equal(1, first);
        Assert.Equal(1, maximumActive);
        Assert.Equal(new[] { 2, 0, 1 }, scheduler.ScanOrder());

        ready[1] = false;
        var second = await scheduler.ScanAsync(index => Task.FromResult(ready[index]));
        Assert.Equal(2, second);
        Assert.Equal(new[] { 0, 1, 2 }, scheduler.ScanOrder());
    }

    [Fact]
    public async Task ScanReturnsIdleWhenEveryProjectIsEmpty()
    {
        var scheduler = new ProjectScheduler(2);
        var visits = new List<int>();
        var selected = await scheduler.ScanAsync(index => { visits.Add(index); return Task.FromResult(false); });
        Assert.Null(selected);
        Assert.Equal(new[] { 0, 1 }, visits);
        Assert.Equal(0, scheduler.NextIndex);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codex-worker-multi-" + Guid.NewGuid().ToString("N"));
        public string Projects { get; }
        public Fixture() { Projects = Path.Combine(Root, "projects"); Directory.CreateDirectory(Projects); }
        public void AddProject(string file, string name, string repo, string directory, int? maxParallelTasks = null)
        {
            var checkout = Path.Combine(Root, directory);
            Directory.CreateDirectory(checkout);
            File.WriteAllText(Path.Combine(checkout, "AGENTS.md"), "instructions");
            File.WriteAllText(Path.Combine(Projects, file), $"""
                project:
                  name: {name}
                  repository: {repo}
                  directory: ../{directory}
                git:
                  baseBranch: main
                  featurePrefix: feature/
                  completedPrefix: done/feature/
                github:
                  readyLabel: ready
                  workingLabel: working
                  blockedLabel: blocked
                  failedLabel: failed
                  doneLabel: done
                codex:
                  instructionsFile: AGENTS.md
                  model: gpt-6-luna
                  reasoningEffort: high
                validation:
                  commands:
                    - dotnet test
                {(maxParallelTasks is null ? "" : $"worker:\n  maxParallelTasks: {maxParallelTasks}\n")}
                """);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
