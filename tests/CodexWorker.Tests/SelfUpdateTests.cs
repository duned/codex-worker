namespace CodexWorker.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using CodexProvisioning;
using Xunit;

public sealed class SelfUpdateTests
{
    [Theory]
    [InlineData("server")]
    [InlineData("worker")]
    public async Task BothComponentsUseSharedOrchestration(string name)
    {
        var operations = new FakeOperations("0.18.7");
        var output = new StringWriter();
        var exit = await new SelfUpdateCli(operations, new StringReader("y\n"), output, new StringWriter(), true)
            .RunAsync(Component(name), []);
        Assert.Equal(0, exit);
        Assert.Equal(1, operations.InstallCount);
        Assert.Equal(name, operations.InstalledComponent);
        Assert.Contains("[Y/n]", output.ToString());
        Assert.Contains("updated successfully", output.ToString());
    }

    [Theory]
    [InlineData("0.18.6", "0.18.6", false)]
    [InlineData("0.18.7", "0.18.6", false)]
    [InlineData("0.9.0", "0.10.0", true)]
    [InlineData("1.0.0-rc.10", "1.0.0", true)]
    [InlineData("1.0.0+build.1", "1.0.0", false)]
    public async Task CheckUsesSemanticComparisonAndNeverInstalls(string current, string latest, bool available)
    {
        var operations = new FakeOperations(latest);
        var output = new StringWriter();
        Assert.Equal(0, await new SelfUpdateCli(operations, new StringReader(""), output, new StringWriter(), false)
            .RunAsync(Component() with { CurrentVersion = current }, ["--check", "--json"]));
        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement;
        Assert.Equal(1, result.GetProperty("contractVersion").GetInt32());
        Assert.Equal("server", result.GetProperty("component").GetString());
        Assert.Equal(current, result.GetProperty("currentVersion").GetString());
        Assert.Equal(latest, result.GetProperty("latestVersion").GetString());
        Assert.Equal(available, result.GetProperty("updateAvailable").GetBoolean());
        Assert.False(result.GetProperty("updateAttempted").GetBoolean());
        Assert.Equal(current, result.GetProperty("finalVersion").GetString());
        Assert.Equal(0, operations.InstallCount);
    }

    [Theory]
    [InlineData("n\n")]
    [InlineData("")]
    public async Task DeclineOrEndOfInputDoesNotInstall(string answer)
    {
        var operations = new FakeOperations("0.18.7");
        var output = new StringWriter();
        Assert.Equal(0, await new SelfUpdateCli(operations, new StringReader(answer), output, new StringWriter(), true)
            .RunAsync(Component(), []));
        Assert.Equal(0, operations.InstallCount);
        Assert.Contains("declined", output.ToString());
    }

    [Fact]
    public async Task EmptyAnswerAcceptsDefault()
    {
        var operations = new FakeOperations("0.18.7");
        Assert.Equal(0, await new SelfUpdateCli(operations, new StringReader("\n"), new StringWriter(), new StringWriter(), true)
            .RunAsync(Component(), []));
        Assert.Equal(1, operations.InstallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonIsNonInteractiveAndYesControlsInstallation(bool yes)
    {
        var operations = new FakeOperations("0.18.7");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await new SelfUpdateCli(operations, new StringReader(""), output, error, false)
            .RunAsync(Component(), yes ? ["--yes", "--json"] : ["--json"]));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(yes ? "updated" : "available", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(yes, document.RootElement.GetProperty("updateAttempted").GetBoolean());
        Assert.Equal(yes ? 1 : 0, operations.InstallCount);
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailuresAreStructuredAndDoNotClaimSuccess(bool installerFailure)
    {
        var operations = new FakeOperations("0.18.7") { FailLookup = !installerFailure, FailInstall = installerFailure };
        var output = new StringWriter();
        Assert.Equal(1, await new SelfUpdateCli(operations, new StringReader(""), output, new StringWriter(), false)
            .RunAsync(Component(), ["--yes", "--json"]));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(installerFailure, document.RootElement.GetProperty("updateAttempted").GetBoolean());
        Assert.DoesNotContain("sensitive", output.ToString());
        Assert.Equal(installerFailure ? 1 : 0, operations.InstallCount);
        if (installerFailure) Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("finalVersion").ValueKind);
    }

    [Fact]
    public async Task MismatchedFinalVersionFailsVerification()
    {
        var output = new StringWriter();
        var operations = new FakeOperations("0.18.7") { FinalVersion = "0.18.6" };
        Assert.Equal(1, await new SelfUpdateCli(operations, new StringReader(""), output, new StringWriter(), false)
            .RunAsync(Component(), ["--yes", "--json"]));
        Assert.Contains("\"status\":\"failed\"", output.ToString());
    }

    [Fact]
    public async Task DiscoveryExcludesDraftPrereleaseInvalidAndIncompleteReleases()
    {
        var releases = new[]
        {
            Release("v0.19.0", draft: true), Release("v0.20.0", prerelease: true),
            Release("v0.21.0-rc.1"), Release("not-a-version"), Release("v0.22.0", package: false),
            Release("v0.9.0"), Release("v0.18.7")
        };
        using var http = new HttpClient(new StubHandler(JsonSerializer.Serialize(releases)));
        Assert.Equal("0.18.7", (await new PackagedSelfUpdate(http).FindReleaseAsync(Component(), CancellationToken.None)).Version);
    }

    [Fact]
    public async Task NoValidReleaseAndNetworkFailureLeaveInstallationUntouched()
    {
        using var http = new HttpClient(new StubHandler("[]"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PackagedSelfUpdate(http).FindReleaseAsync(Component(), CancellationToken.None));
        using var failing = new HttpClient(new StubHandler("", HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => new PackagedSelfUpdate(failing).FindReleaseAsync(Component(), CancellationToken.None));
    }

    private static object Release(string tag, bool draft = false, bool prerelease = false, bool package = true) => new
    {
        tag_name = tag, draft, prerelease,
        assets = package ? new[] { new { name = $"codex-server-{tag[1..]}-linux-x64.tar.gz" }, new { name = "checksums.txt" } } : []
    };
    private static UpdateComponent Component(string name = "server") => new(name, name == "server" ? "Codex Server" : "Codex Worker", "0.18.6", "/unused");

    private sealed class FakeOperations(string latest) : ISelfUpdateOperations
    {
        public int InstallCount { get; private set; }
        public string? InstalledComponent { get; private set; }
        public bool FailLookup { get; init; }
        public bool FailInstall { get; init; }
        public string? FinalVersion { get; init; }
        public Task<UpdateRelease> FindReleaseAsync(UpdateComponent component, CancellationToken cancellationToken)
            => FailLookup ? throw new HttpRequestException("sensitive") : Task.FromResult(new UpdateRelease(latest));
        public Task<string> InstallAsync(UpdateComponent component, UpdateRelease release, CancellationToken cancellationToken)
        {
            InstallCount++;
            InstalledComponent = component.Name;
            return FailInstall ? throw new IOException("sensitive") : Task.FromResult(FinalVersion ?? latest);
        }
    }
    private sealed class StubHandler(string content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }
}
