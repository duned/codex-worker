using CodexProvisioning;
using CodexServer;

namespace CodexWorker.Tests;

public sealed class ServerClockBoundaryTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void NodeObservationsRemainFreshThroughSixMinutes(long ticks, bool stale)
    {
        var clock = new Clock();
        var observed = clock.Now.AddMinutes(-6).AddTicks(-ticks);
        var states = CapabilityCatalog.Definitions.Select(definition => CapabilityCatalog.Unknown(definition) with
        {
            Installation = InstallationState.Installed,
            Health = CapabilityHealth.Healthy,
            Authentication = RequirementState.Satisfied,
            Configuration = RequirementState.Satisfied,
            DetectedAtUtc = observed
        }).ToArray();
        var worker = new WorkerRegistrationResponse(2, "worker", "Worker", "1", "linux", 1, [],
            observed, clock.Now, "online", 0, 1, 1, "running", [], CapabilityInventory: states);

        var node = NodeProvisioning.Describe(worker, [], clock);

        Assert.Equal(stale, node.ObservationsStale);
        Assert.Equal(stale ? "not-ready" : "ready", node.ExecutionReadiness);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void JournalDeadlineControlsPollingCompletionAndCache(long ticks, bool valid)
    {
        var clock = new Clock();
        var requests = new WorkerJournalRequests(clock);
        requests.Read("pending", 1);
        requests.Read("cached", 1);
        var pending = Assert.IsType<WorkerJournalRequest>(requests.Pending("pending"));
        var cached = Assert.IsType<WorkerJournalRequest>(requests.Pending("cached"));
        Assert.True(requests.Complete("cached", cached.Id, new("ready", ["line"])));
        clock.Now = clock.Now.AddSeconds(90).AddTicks(ticks);

        Assert.Equal(valid, requests.Pending("pending") is not null);
        Assert.Equal(valid, requests.Complete("pending", pending.Id, new("ready", [])));
        Assert.Equal(valid ? "ready" : "pending", requests.Read("cached", 1).State);
        requests.Read("pending", 1);
        if (!valid) Assert.NotEqual(pending.Id, requests.Pending("pending")?.Id);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2040-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
