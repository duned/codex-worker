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
        public void AddProject(string file, string name, string repo, string directory)
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
                """);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
