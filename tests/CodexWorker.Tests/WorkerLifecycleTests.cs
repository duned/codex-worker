using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerLifecycleTests
{
    [Fact]
    public void DrainCannotFinishUntilActiveExecutionsHaveReleased()
    {
        var lifecycle = new WorkerLifecycle();
        lifecycle.SetReady();

        lifecycle.RequestDrain();
        lifecycle.SetActiveExecutions(2);

        Assert.Equal("drain-requested", lifecycle.Snapshot.State);
        Assert.True(lifecycle.Snapshot.DrainRequested);
        Assert.Equal(2, lifecycle.Snapshot.ActiveExecutions);

        lifecycle.SetActiveExecutions(0);

        Assert.Equal("drained", lifecycle.Snapshot.State);
        Assert.Equal(0, lifecycle.Snapshot.ActiveExecutions);
    }

    [Fact]
    public void PendingDrainCanBeCancelledAfterDrainTimeout()
    {
        var lifecycle = new WorkerLifecycle();
        lifecycle.SetReady();
        lifecycle.RequestDrain();

        Assert.Throws<InvalidOperationException>(() => lifecycle.CancelDrain(1));
        lifecycle.CancelDrain(0);

        Assert.Equal("ready", lifecycle.Snapshot.State);
        Assert.False(lifecycle.Snapshot.DrainRequested);
    }

    [Fact]
    public void UpdateAndRestartFailuresNeverReturnWorkerToReady()
    {
        var lifecycle = new WorkerLifecycle();
        lifecycle.SetReady();
        lifecycle.RequestDrain();
        lifecycle.SetDrained(0);

        lifecycle.BeginUpdate();
        lifecycle.RecordUpdateResult("package update failed", succeeded: false);
        Assert.Equal("update-failed", lifecycle.Snapshot.State);
        Assert.Equal("package update failed", lifecycle.Snapshot.LastUpdateResult);
        Assert.Throws<InvalidOperationException>(lifecycle.BeginRestart);

        lifecycle.RequestDrain();
        lifecycle.SetDrained(0);
        lifecycle.BeginUpdate();
        lifecycle.RecordUpdateResult("updated", succeeded: true);
        lifecycle.BeginRestart();
        lifecycle.RecordRestartFailure("service manager could not restart worker");

        Assert.Equal("restart-failed", lifecycle.Snapshot.State);
        Assert.Equal("service manager could not restart worker", lifecycle.Snapshot.ReconnectReadinessResult);
    }

    [Fact]
    public void ReadinessRejectsCapabilityRegressionAndIncompatibleConfiguration()
    {
        var lifecycle = new WorkerLifecycle();
        var required = new[] { new WorkerCapabilityContract("runtime", "git") };

        Assert.False(lifecycle.CompleteReadiness(required, [], configurationCompatible: true));
        Assert.Equal("capability-regression", lifecycle.Snapshot.State);
        Assert.False(lifecycle.CompleteReadiness(required, required, configurationCompatible: false, "invalid server configuration"));
        Assert.Equal("configuration-incompatible", lifecycle.Snapshot.State);
        Assert.Equal("invalid server configuration", lifecycle.Snapshot.ReconnectReadinessResult);
        Assert.True(lifecycle.CompleteReadiness(required, required, configurationCompatible: true));
        Assert.Equal("ready", lifecycle.Snapshot.State);
    }
}
