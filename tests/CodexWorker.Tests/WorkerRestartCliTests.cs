namespace CodexWorker.Tests;

public sealed class WorkerRestartCliTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void RestartParsesConfigurationAndJsonAndHasSideEffectFreeHelp()
    {
        var command = WorkerCommandLine.Parse(["restart", "--json", "--config", "/tmp/worker.yml"]);
        Assert.Equal("restart", command.Command);
        Assert.Equal("/tmp/worker.yml", command.ConfigurationPath);
        Assert.Equal("--json", Assert.Single(command.Arguments));
        using var writer = new StringWriter();
        WorkerCommandHelp.Write("restart", writer);
        Assert.Contains("sudo codex-worker restart", writer.ToString());
        Assert.Contains("without interrupting", writer.ToString());
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void RestartOnlyBeginsAfterDrainCompletes()
    {
        var lifecycle = new WorkerLifecycle();
        lifecycle.SetReady();
        lifecycle.SetActiveExecutions(1);
        lifecycle.RequestDrain();
        Assert.Throws<InvalidOperationException>(lifecycle.BeginServiceRestart);
        lifecycle.SetActiveExecutions(0);
        lifecycle.BeginServiceRestart();
        Assert.Equal("restarting", lifecycle.Snapshot.State);
        Assert.True(lifecycle.Snapshot.DrainRequested);
    }

    private sealed class StatusHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request, cancellationToken));
    }

    private static HttpResponseMessage Status(string instance, bool drained = false, int active = 0) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new WorkerStatus("1.0.0", "running", 0, 1,
                active, 1 - active, 1, 1, drained ? "drained" : "ready", drained, null, "ready", InstanceId: instance))
        };

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task LocalRestartDrainsBeforeServiceDispatchAndVerifiesReplacement()
    {
        var drained = false;
        var restarted = false;
        using var handler = new StatusHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                if (request.RequestUri?.AbsolutePath == "/api/worker/drain") drained = true;
                else
                {
                    Assert.Equal("/api/worker/restart/prepare", request.RequestUri?.AbsolutePath);
                    Assert.True(drained);
                }
                return new(System.Net.HttpStatusCode.OK);
            }
            return Status(restarted ? "replacement" : "original", drained && !restarted);
        });
        using var client = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        await WorkerRestartCli.RestartAsync(client, "original", _ =>
        {
            Assert.True(drained);
            restarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.True(restarted);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task UnrelatedApiCannotTriggerInstalledServiceRestart()
    {
        using var handler = new StatusHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Status("other-service");
        });
        using var client = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var restarted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkerRestartCli.RestartAsync(client, "installed-service", _ =>
        {
            restarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.False(restarted);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CancelledDrainNeverInvokesServiceManager()
    {
        using var cancellation = new CancellationTokenSource();
        var statusReads = 0;
        using var handler = new StatusHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) return new(System.Net.HttpStatusCode.OK);
            if (++statusReads > 1) cancellation.Cancel();
            return Status("original", drained: true, active: 1);
        });
        using var client = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var restarted = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerRestartCli.RestartAsync(client, "original", _ =>
        {
            restarted = true;
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.False(restarted);
    }

}
