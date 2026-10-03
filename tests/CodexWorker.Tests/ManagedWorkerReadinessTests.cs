using System.Net;
using System.Net.Http.Json;
using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ManagedWorkerReadinessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneWithoutProjectsRemainsAliveWhenToolsOrPreflightAreUnavailable(bool installed)
    {
        using var temporary = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var provider = new TestProvider();
        var discovery = new NodeCapabilityDiscovery((_, _, _) => installed
            ? Task.FromResult((0, "1.0.0"))
            : Task.FromException<(int, string)>(new FileNotFoundException()));
        var capabilities = new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(127, "", "")));
        using var output = new StopOnStartedWriter(stop);
        var configuration = new GlobalWorkerConfiguration
        {
            Projects = new() { Ownership = "standalone", Directory = temporary.Path },
            Api = new() { Enabled = false }
        };
        var projects = ProjectConfigurationDiscovery.LoadForWorker(configuration);
        Assert.Empty(projects);
        var host = new WorkerHost(configuration, projects, new WorkerConsole(output, interactive: false),
            registrationClient: new WorkerRegistrationClient(provisioningDiscovery: discovery, capabilityDiscovery: capabilities),
            agentAuthentication: provider);

        await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(stop.IsCancellationRequested);
        Assert.Contains("Worker started.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(installed ? 1 : 0, provider.Calls);
    }

    private sealed class StopOnStartedWriter(CancellationTokenSource stop) : StringWriter
    {
        public override void Write(string? value)
        {
            base.Write(value);
            if (value?.Contains("Worker started.", StringComparison.Ordinal) == true) stop.Cancel();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnavailableAgentDoesNotStopControlLoopAndProvisioningRestoresReadiness(bool installed, bool authenticated)
    {
        using var temporary = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var expectedPreflights = installed && authenticated ? 2 : 1;
        var provider = new TestProvider { Available = false };
        var discovery = new NodeCapabilityDiscovery((executable, arguments, _) =>
        {
            if (!installed) return Task.FromException<(int, string)>(new FileNotFoundException());
            return Task.FromResult((executable == CodexServiceEnvironment.Executable &&
                arguments.SequenceEqual(new[] { "login", "status" }) && !authenticated ? 1 : 0, "1.0.0"));
        });
        var capabilities = new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(
            new ProcessResult(installed ? 0 : 127, installed ? "1.0.0" : "", "")));
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        var registered = 0;
        var commands = 0;
        var notReady = false;
        var ready = false;
        using var handler = new Handler(async (request, token) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Put) registered++;
            if (path.EndsWith("/configuration", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServerManagedConfigurationContract(1, ManagedConfigurationSynchronizer.CalculateVersion([]), [])) };
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var heartbeat = await (request.Content ?? throw new InvalidDataException("Missing heartbeat")).ReadFromJsonAsync<WorkerHeartbeatContract>(token);
                if (heartbeat?.LifecycleState == "not-ready")
                {
                    notReady = true;
                    Assert.DoesNotContain(heartbeat.Capabilities, item => item.Type == "agent-provider");
                }
                if (heartbeat?.LifecycleState == "running")
                {
                    ready = true;
                    Assert.Contains(WorkerAgentCapabilities.AuthenticatedProvider("codex"), heartbeat.Capabilities);
                    stop.Cancel();
                }
            }
            if (path.EndsWith("/commands/request", StringComparison.Ordinal) && commands++ == 0)
            {
                Assert.True(notReady);
                installed = authenticated = provider.Available = true;
                var now = DateTimeOffset.UtcNow;
                var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile, token);
                var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"),
                    new(identity, "codex-cli", ProvisioningCommandAction.Detect), now,
                    ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddSeconds(120));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(command) };
            }
            Assert.False(path.EndsWith("/assignments/request", StringComparison.Ordinal));
            return new(path.EndsWith("/provisioning/request", StringComparison.Ordinal) ||
                path.EndsWith("/commands/request", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var configuration = new GlobalWorkerConfiguration
        {
            Server = settings,
            Projects = new() { Ownership = "managed", Directory = temporary.Path },
            Api = new() { Enabled = false }
        };
        var host = new WorkerHost(configuration, [], new WorkerConsole(new StringWriter(), interactive: false),
            registrationClient: new WorkerRegistrationClient(client, discovery, capabilities), agentAuthentication: provider);

        await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, registered);
        Assert.True(notReady);
        Assert.True(ready);
        Assert.Equal(expectedPreflights, provider.Calls);
    }

    [Fact]
    public async Task FailedPreflightIsRetriedOnlyAfterExplicitRefreshAndCancellationPropagates()
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0")));
        var provider = new TestProvider();
        var readiness = new ManagedCodexReadiness(provider);
        Assert.False(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.Equal("codex-cli:execution-preflight-failed", readiness.DiagnosticCode);
        await discovery.GetAsync(refresh: true);
        Assert.False(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        provider.Available = true;
        Assert.True(await readiness.EvaluateAsync(discovery, true, CancellationToken.None));
        Assert.Null(readiness.DiagnosticCode);
        Assert.Equal(2, provider.Calls);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.EvaluateAsync(discovery, true, stop.Token));
    }

    [Fact]
    public async Task RequiredDependencyLossLeavesAgentReadyWithoutRepeatingPreflight()
    {
        var githubAuthenticated = true;
        var discovery = new NodeCapabilityDiscovery((executable, arguments, _) => Task.FromResult(
            (executable == "gh" && arguments[0] == "auth" && !githubAuthenticated ? 1 : 0, "1.0.0")));
        var provider = new TestProvider { Available = true };
        var readiness = new ManagedCodexReadiness(provider);
        Assert.True(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        githubAuthenticated = false;
        await discovery.GetAsync(refresh: true);
        Assert.True(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.False(CapabilityCatalog.ExecutionReadiness(await discovery.GetAsync()).Available);
        Assert.Equal(1, provider.Calls);
        githubAuthenticated = true;
        await discovery.GetAsync(refresh: true);
        Assert.True(await readiness.EvaluateAsync(discovery, false, CancellationToken.None));
        Assert.True(CapabilityCatalog.ExecutionReadiness(await discovery.GetAsync()).Available);
        Assert.Equal(1, provider.Calls);
    }

    private sealed class TestProvider : IAgentAuthenticationProvider
    {
        public string Provider => "codex";
        public bool Available { get; set; }
        public int Calls { get; private set; }
        public Task ValidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Available ? Task.CompletedTask : Task.FromException(new WorkerInfrastructureException("preflight unavailable"));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "managed-readiness-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
