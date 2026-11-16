using CodexProvisioning;
using CodexServer;
using Xunit;

namespace CodexWorker.Tests;

public sealed class WorkerReleaseCatalogTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Trait("TestType", "Unit")]
    [Fact]
    public async Task CachesLatestReleaseAndRetriesFailuresSooner()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-16T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var calls = 0;
        var fail = false;
        var catalog = new WorkerReleaseCatalog(_ =>
        {
            calls++;
            return fail ? throw new HttpRequestException("offline") : Task.FromResult("0.36.0");
        }, clock);

        Assert.Equal("0.36.0", (await catalog.GetLatestAsync(CancellationToken.None)).Version);
        clock.Now += TimeSpan.FromMinutes(14);
        Assert.Equal("0.36.0", (await catalog.GetLatestAsync(CancellationToken.None)).Version);
        Assert.Equal(1, calls);

        fail = true;
        clock.Now += TimeSpan.FromMinutes(2);
        var unavailable = await catalog.GetLatestAsync(CancellationToken.None);
        Assert.Null(unavailable.Version);
        Assert.Equal("release-catalog-unavailable", unavailable.Diagnostic);
        Assert.Equal(2, calls);

        fail = false;
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Null((await catalog.GetLatestAsync(CancellationToken.None)).Version);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal("0.36.0", (await catalog.GetLatestAsync(CancellationToken.None)).Version);
        Assert.Equal(3, calls);
    }

    [Trait("TestType", "Unit")]
    [Fact]
    public async Task RejectsInvalidCatalogVersionsAndPropagatesCallerCancellation()
    {
        var clock = new Clock(DateTimeOffset.UnixEpoch);
        var invalid = new WorkerReleaseCatalog(_ => Task.FromResult("latest"), clock);
        Assert.Equal("release-catalog-unavailable", (await invalid.GetLatestAsync(CancellationToken.None)).Diagnostic);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var waiting = new WorkerReleaseCatalog(ct => Task.FromCanceled<string>(ct), clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.GetLatestAsync(cancelled.Token));
    }

    [Trait("TestType", "Unit")]
    [Theory]
    [InlineData("0.35.2", "0.36.0", true)]
    [InlineData("0.36.0", "0.36.0", false)]
    [InlineData("0.37.0", "0.36.0", false)]
    [InlineData("0.35.2", null, false)]
    [InlineData("development", "0.36.0", false)]
    public void DescribesUpdateAvailabilityAndSupportedOperations(string current, string? latest, bool available)
    {
        var now = DateTimeOffset.UnixEpoch;
        var worker = new WorkerRegistrationResponse(2, "worker", "Worker", current, "linux", 1,
            [new("protocol", WorkerUpdateProtocol.RestartCapability)], now, now, "online", 0, 1, 1, "running", []);
        var status = WorkerReleaseCatalog.Describe(worker, latest, null, now);
        Assert.Equal(available, status.UpdateAvailable);
        Assert.Equal(latest, status.LatestVersion);
        Assert.True(status.RestartSupported);
        Assert.False(status.SelfUpdateSupported);
    }
}
