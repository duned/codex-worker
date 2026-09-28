using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProjectRuntimeRegistryTests
{
    [Fact]
    public void LifecycleTransitionsAreIdempotentAndDrainCompletesWhenLastExecutionReleases()
    {
        var events = new RuntimeEventLog();
        var registry = new ProjectRuntimeRegistry([("alpha.yml", Config("alpha"))], events);

        Assert.Equal(ProjectLifecycleState.Enabled, registry.Get("alpha")!.State);
        Assert.True(registry.TryReserve("alpha"));
        Assert.Equal(ProjectLifecycleState.Disabled, registry.Disable("alpha")!.State);
        Assert.Equal(ProjectLifecycleState.Disabled, registry.Disable("alpha")!.State);
        Assert.False(registry.TryReserve("alpha"));
        Assert.Equal(ProjectLifecycleState.Enabled, registry.Enable("alpha")!.State);
        Assert.True(registry.TryReserve("alpha"));
        Assert.False(registry.Drain("alpha")!.DrainComplete);
        Assert.False(registry.TryReserve("alpha"));
        registry.Release("alpha");
        Assert.False(registry.Get("alpha")!.DrainComplete);
        registry.Release("alpha");
        Assert.True(registry.Get("alpha")!.DrainComplete);
        Assert.Equal(1, events.ReadRecent().Count(x => x.Type == "project.drain.completed"));
    }

    [Fact]
    public void WorkerDrainBlocksAllProjectsAndCompletesAfterReservationsRelease()
    {
        var registry = new ProjectRuntimeRegistry([("alpha.yml", Config("alpha")), ("beta.yml", Config("beta"))]);
        Assert.True(registry.TryReserve("alpha"));
        registry.DrainWorker();
        registry.DrainWorker();
        Assert.False(registry.TryReserve("beta"));
        Assert.False(registry.WorkerDrainComplete);
        registry.Release("alpha");
        Assert.True(registry.WorkerDrainComplete);
    }

    [Fact]
    public void InvalidReplacementPreservesSnapshotAndValidReplacementIsAtomic()
    {
        var original = Config("alpha", "owner/alpha");
        var registry = new ProjectRuntimeRegistry([("alpha.yml", original)]);
        Assert.Throws<InvalidDataException>(() => registry.ReplaceConfiguration([("a.yml", Config("alpha")), ("b.yml", Config("alpha"))]));
        Assert.Same(original, registry.Snapshot().Single().Configuration);

        var replacement = Config("alpha", "owner/renamed");
        registry.ReplaceConfiguration([("alpha.yml", replacement)]);
        Assert.Same(replacement, registry.Snapshot().Single().Configuration);
    }

    [Fact]
    public void ActiveExecutionPreventsChangingRepositoryOrCheckoutButKeepsOldConfiguration()
    {
        var original = Config("alpha", "owner/alpha");
        var registry = new ProjectRuntimeRegistry([("alpha.yml", original)]);
        Assert.True(registry.TryReserve("alpha"));
        Assert.Throws<ProjectConfigurationConflictException>(() => registry.ReplaceConfiguration([("alpha.yml", Config("alpha", "owner/new"))]));
        Assert.Same(original, registry.Snapshot().Single().Configuration);
        registry.Release("alpha");
    }

    [Fact]
    public void ActiveExecutionsKeepTheOldConfigurationWhileReplacementBecomesCurrent()
    {
        var original = Config("alpha");
        var registry = new ProjectRuntimeRegistry([("alpha.yml", original)]);
        Assert.True(registry.TryReserve("alpha"));
        var replacement = Config("alpha");
        replacement.Validation.Commands = ["dotnet test --no-restore"];

        registry.ReplaceConfiguration([("alpha.yml", replacement)]);

        Assert.Empty(original.Validation.Commands);
        Assert.Equal("dotnet test --no-restore", registry.Snapshot().Single().Configuration.Validation.Commands.Single());
        registry.Release("alpha");
    }

    [Fact]
    public void ReservationFailsWhenTheSchedulerHasAStaleConfigurationSnapshot()
    {
        var original = Config("alpha");
        var registry = new ProjectRuntimeRegistry([("alpha.yml", original)]);
        var replacement = Config("alpha");
        registry.ReplaceConfiguration([("alpha.yml", replacement)]);
        Assert.False(registry.TryReserve("alpha", original));
        Assert.True(registry.TryReserve("alpha", replacement));
        registry.Release("alpha");
    }

    private static WorkerConfiguration Config(string name, string repository = "owner/repo") => new()
    {
        Project = new ProjectSettings { Name = name, Repository = repository, Directory = Path.GetTempPath() },
        Git = new GitSettings { BaseBranch = "main", FeaturePrefix = "feature/", CompletedPrefix = "done/" },
        GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" },
        Codex = new CodexSettings { InstructionsFile = typeof(ProjectRuntimeRegistryTests).Assembly.Location }
    };
}
