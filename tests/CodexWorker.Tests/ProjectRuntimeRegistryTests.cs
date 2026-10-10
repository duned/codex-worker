using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProjectRuntimeRegistryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrainingMaintenanceRestoresPriorDrainAndDoesNotInterruptWork(bool previouslyDrained)
    {
        var registry = new ProjectRuntimeRegistry([("alpha.yml", Config("alpha"))]);
        Assert.True(registry.TryReserve("alpha"));
        if (previouslyDrained) registry.DrainWorker();
        using (var reservation = registry.TryBeginDrainingMaintenance())
        {
            Assert.NotNull(reservation);
            Assert.False(registry.WorkerDrainComplete);
            Assert.False(registry.TryReserve("alpha"));
            Assert.Null(registry.TryBeginDrainingMaintenance());
            Assert.False(registry.CancelWorkerDrain());
        }
        Assert.Equal(previouslyDrained, registry.WorkerDraining);
        Assert.Equal(1, registry.Lifecycle.Snapshot.ActiveExecutions);
        registry.Release("alpha");
    }

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
    public void UnavailableProjectCannotBeReservedWhileIndependentProjectRemainsSchedulable()
    {
        var alpha = Config("alpha");
        var beta = Config("beta");
        var registry = new ProjectRuntimeRegistry([("alpha.yml", alpha), ("beta.yml", beta)]);

        Assert.True(registry.TryReserve("alpha"));
        var status = registry.MarkUnavailable("alpha", "checkout is dirty");
        registry.Release("alpha");

        Assert.Equal(ProjectLifecycleState.Unavailable, status!.State);
        Assert.Equal("checkout is dirty", status.UnavailableReason);
        Assert.False(registry.TryReserve("alpha"));
        Assert.True(registry.TryReserve("beta"));
        registry.Release("beta");
    }

    [Fact]
    public void IdleWorkerDrainImmediatelyUpdatesTheAuthoritativeLifecycle()
    {
        var registry = new ProjectRuntimeRegistry([("alpha.yml", Config("alpha"))]);
        Assert.False(registry.WorkerDrainComplete);
        registry.DrainWorker();
        Assert.True(registry.WorkerDraining);
        Assert.True(registry.WorkerDrainComplete);
        Assert.Equal("drained", registry.Lifecycle.Snapshot.State);
        Assert.Equal(0, registry.Lifecycle.Snapshot.ActiveExecutions);
        Assert.False(registry.TryReserve("alpha"));
        Assert.True(registry.CancelWorkerDrain());
        Assert.False(registry.WorkerDraining);
        Assert.Equal("ready", registry.Lifecycle.Snapshot.State);
        Assert.True(registry.TryReserve("alpha"));
        registry.Release("alpha");
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
        Assert.Equal("drain-requested", registry.Lifecycle.Snapshot.State);
        Assert.Equal(1, registry.Lifecycle.Snapshot.ActiveExecutions);
        registry.Release("alpha");
        Assert.True(registry.WorkerDrainComplete);
        Assert.Equal("drained", registry.Lifecycle.Snapshot.State);
        Assert.Equal(0, registry.Lifecycle.Snapshot.ActiveExecutions);
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

    [Fact]
    public void MaintenanceRequiresDrainAndPinsDrainAndConfigurationUntilReleased()
    {
        var config = Config("alpha");
        var registry = new ProjectRuntimeRegistry([("alpha.yml", config)]);
        Assert.Null(registry.TryBeginMaintenance());
        Assert.True(registry.TryReserve("alpha"));
        registry.DrainWorker();
        Assert.Null(registry.TryBeginMaintenance());
        registry.Release("alpha");
        using (var maintenance = registry.TryBeginMaintenance())
        {
            Assert.NotNull(maintenance);
            Assert.Null(registry.TryBeginMaintenance());
            Assert.False(registry.TryReserve("alpha"));
            Assert.False(registry.CancelWorkerDrain());
            Assert.False(registry.TryBeginRemoval("alpha"));
            Assert.Throws<ProjectConfigurationConflictException>(() => registry.ReplaceConfiguration([("alpha.yml", config)]));
        }
        Assert.True(registry.CancelWorkerDrain());
        Assert.True(registry.TryReserve("alpha"));
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
