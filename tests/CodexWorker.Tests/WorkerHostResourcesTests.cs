using CodexProvisioning;
using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class WorkerHostResourcesTests
{
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

    [Fact]
    public async Task CollectionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexWorker.WorkerHostResourceCollector.CollectAsync(cancellation.Token));
    }

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
