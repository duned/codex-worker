using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerCapabilityDiscoveryTests
{
    [Fact]
    public async Task DiscoversAvailableToolsAndParsesTheirVersions()
    {
        var discovery = new WorkerCapabilityDiscovery((executable, _, _, timeout, _) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(3), timeout);
            return Task.FromResult(executable switch
            {
                "dotnet" => new ProcessResult(0, "10.0.112\n", ""),
                "node" => new ProcessResult(0, "v22.3.0\n", ""),
                "git" => new ProcessResult(0, "git version 2.43.0\n", ""),
                "gh" => new ProcessResult(0, "gh version 2.55.0\n", ""),
                _ => new ProcessResult(127, "", "not installed")
            });
        });

        var capabilities = await discovery.DiscoverAsync();

        Assert.Contains(capabilities, item => item.Type == "runtime" && item.Name == "dotnet" && item.Version == "10.0.112");
        Assert.Contains(capabilities, item => item.Type == "runtime" && item.Name == "node" && item.Version == "22.3.0");
        Assert.Contains(capabilities, item => item.Type == "tool" && item.Name == "git" && item.Version == "2.43.0");
        Assert.Contains(capabilities, item => item.Type == "tool" && item.Name == "github-cli" && item.Version == "2.55.0");
        Assert.DoesNotContain(capabilities, item => item.Name == "docker" || item.Name == "postgresql");
        Assert.Contains(capabilities, item => item.Type == "integration" && item.Name == "github-issues");
    }

    [Fact]
    public async Task MissingExecutablesAreAbsentWithoutFailingDiscovery()
    {
        var discovery = new WorkerCapabilityDiscovery((_, _, _, _, _) =>
            Task.FromException<ProcessResult>(new FileNotFoundException("not installed")));

        var capabilities = await discovery.DiscoverAsync();

        var capability = Assert.Single(capabilities);
        Assert.Equal(new WorkerCapabilityContract("integration", "github-issues"), capability);
    }

    [Fact]
    public async Task ServicePathResolutionFailuresAreUnavailableCapabilities()
    {
        var runner = new ProcessRunner();
        var discovery = new WorkerCapabilityDiscovery((executable, arguments, directory, timeout, token) =>
            runner.RunAsync(executable, arguments, directory, timeout, token,
                environment: new Dictionary<string, string?> { ["PATH"] = "/nonexistent-worker-tools" }));

        Assert.Equal(new WorkerCapabilityContract("integration", "github-issues"),
            Assert.Single(await discovery.DiscoverAsync()));
    }

    [Fact]
    public async Task CachedCapabilitiesRefreshAfterConfiguredInterval()
    {
        var calls = 0;
        var discovery = new WorkerCapabilityDiscovery((_, _, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(1, "", "not installed"));
        }, TimeSpan.Zero);

        await discovery.GetCachedAsync();
        await discovery.GetCachedAsync();

        Assert.Equal(14, calls);
    }

    [Theory]
    [InlineData("v20.1.2", "20.1.2")]
    [InlineData("git version 2.43.0.windows.1", "2.43.0")]
    [InlineData("tool version unavailable", null)]
    public void VersionParserReturnsFirstNumericVersion(string text, string? expected) =>
        Assert.Equal(expected, WorkerCapabilityDiscovery.ParseVersion(text));
}
