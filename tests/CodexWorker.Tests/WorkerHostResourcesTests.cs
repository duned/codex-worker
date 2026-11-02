using CodexProvisioning;
using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class WorkerHostResourcesTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("app", "app", true)]
    [InlineData("app", "app/work", true)]
    [InlineData("app", "application", false)]
    [InlineData("app/work", "app", false)]
    [InlineData(".", "app", true)]
    public void DiskMountMustContainDirectory(string mount, string directory, bool expected)
    {
        var root = Path.GetTempPath();
        Assert.Equal(expected, CodexWorker.WorkerHostResourceCollector.ContainsDirectory(
            Path.Combine(root, mount), Path.Combine(root, directory)));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("")]
    [InlineData("cpu 1 2 3")]
    [InlineData("intr 1 2 3 4 5")]
    [InlineData("cpu 1 2 3 -4 5")]
    public void MissingOrInvalidCpuCountersAreUnavailable(string input)
    {
        Assert.Throws<IOException>(() => CodexWorker.WorkerHostResourceCollector.ParseCpu(
            input.Length == 0 ? [] : [input]));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void CpuCountersExcludeGuestTimeAndCountOnlyProcessorRows()
    {
        var sample = CodexWorker.WorkerHostResourceCollector.ParseCpu([
            "cpu 10 20 30 40 50 60 70 80 90 100", "cpu0 1 2 3", "cpu1 1 2 3", "intr 123"]);
        Assert.Equal(360, sample.Total);
        Assert.Equal(90, sample.Idle);
        Assert.Equal(2, sample.Count);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void OlderHeartbeatCanOmitResources()
    {
        var heartbeat = JsonSerializer.Deserialize<CodexWorker.WorkerHeartbeatContract>("""
            {"ContractVersion":2,"WorkerId":"worker","WorkerVersion":"0.15.0","LifecycleState":"Ready",
             "ActiveExecutions":0,"MaximumCapacity":1,"Capabilities":[],"ActiveProjects":[]}
            """);
        Assert.NotNull(heartbeat);
        Assert.Null(heartbeat.HostResources);
        Assert.True(WorkerHostResources.Valid(null));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ResourcesRoundTripThroughServerHeartbeatContract()
    {
        var resources = new WorkerHostResources(DateTimeOffset.UnixEpoch, 8, 1024, 512, 2048, 1024, 25, 50, 1);
        var heartbeat = new CodexWorker.WorkerHeartbeatContract(2, "worker", "0.15.0", "Ready", 0, 1, [], [], HostResources: resources);
        var received = JsonSerializer.Deserialize<CodexServer.WorkerHeartbeatRequest>(JsonSerializer.Serialize(heartbeat));
        Assert.NotNull(received);
        Assert.Equal(resources, received.HostResources);
        Assert.True(WorkerHostResources.Valid(received.HostResources));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CollectionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexWorker.WorkerHostResourceCollector.CollectAsync(cancellation.Token));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPercentagesAreRejected(double percent)
    {
        Assert.False(WorkerHostResources.Valid(new(DateTimeOffset.UnixEpoch, CpuUsagePercent: percent, SampleSeconds: 1)));
        Assert.False(WorkerHostResources.Valid(new(DateTimeOffset.UnixEpoch, MemoryUsagePercent: percent)));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void CpuRequiresMeaningfulSampleAndCapacityBoundsAreChecked()
    {
        Assert.False(WorkerHostResources.Valid(new(DateTimeOffset.UnixEpoch, CpuUsagePercent: 50)));
        Assert.False(WorkerHostResources.Valid(new(DateTimeOffset.UnixEpoch, CpuUsagePercent: 50, SampleSeconds: .1)));
        Assert.False(WorkerHostResources.Valid(new(DateTimeOffset.UnixEpoch, TotalMemoryBytes: 100, UsedMemoryBytes: 101)));
        Assert.False(WorkerHostResources.Valid(new(DateTimeOffset.UnixEpoch, DiskTotalBytes: 100, DiskAvailableBytes: 101)));
        Assert.True(WorkerHostResources.Valid(new(DateTimeOffset.UnixEpoch, CpuUsagePercent: 100, SampleSeconds: 1)));
    }
}
