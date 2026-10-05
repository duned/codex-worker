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

    [Theory]
    [InlineData("gh")]
    [InlineData("codex")]
    public async Task StandaloneWithConfiguredProjectsRemainsAliveWhenRequiredAuthenticationIsMissing(string executable)
    {
        using var temporary = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var instructions = Path.Combine(temporary.Path, "AGENTS.md");
        await File.WriteAllTextAsync(instructions, "test instructions");
        var discovery = new NodeCapabilityDiscovery((tool, arguments, _) => Task.FromResult(
            (tool == (executable == "codex" ? CodexServiceEnvironment.Executable : executable) &&
                arguments[0] is "auth" or "login" ? 1 : 0, "1.0.0")));
        var capabilities = new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")));
        var provider = new TestProvider { Available = true };
        using var output = new StopOnStartedWriter(stop);
        var configuration = new GlobalWorkerConfiguration
        {
            Projects = new() { Ownership = "standalone", Directory = temporary.Path },
            Api = new() { Enabled = false }
        };
        var project = new WorkerConfiguration
        {
            Project = new() { Name = "Project", Repository = "owner/repo", Directory = temporary.Path },
            Codex = new() { InstructionsFile = instructions }
        };
        var host = new WorkerHost(configuration, [("project.yml", project)], new WorkerConsole(output, interactive: false),
            registrationClient: new WorkerRegistrationClient(provisioningDiscovery: discovery, capabilityDiscovery: capabilities),
            agentAuthentication: provider);

        // This checkout cannot complete its safety checks yet. Missing node authentication
        // must leave the control loop available for provisioning rather than fail startup.
        await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(stop.IsCancellationRequested);
        Assert.Contains("Worker started.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unavailable", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(executable == "codex" ? 0 : 1, provider.Calls);
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
    public async Task ColdStartWithServerProjectAndNoLocalYamlStaysOnlineWithProjectUnavailable()
    {
        using var temporary = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        using var output = new StopOnStartedWriter(stop);
        var project = new ServerProjectContract("central-id", "Central", "owner/repo", "main", "", [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var snapshot = new ServerManagedConfigurationContract(1, ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]);
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        using var handler = new Handler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath.EndsWith("/configuration", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) }
                : new HttpResponseMessage(request.RequestUri?.AbsolutePath.EndsWith("/request", StringComparison.Ordinal) == true
                    ? HttpStatusCode.NoContent : HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0")));
        var capabilities = new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")));
        var global = new GlobalWorkerConfiguration
        {
            Server = settings, Projects = new() { Ownership = "managed", Directory = Path.Combine(temporary.Path, "absent-projects") },
            ManagedProjects = new() { CheckoutDirectory = Path.Combine(temporary.Path, "checkouts") },
            Api = new() { Enabled = false }
        };
        var host = new WorkerHost(global, ProjectConfigurationDiscovery.LoadForWorker(global),
            new WorkerConsole(output, interactive: false),
            registrationClient: new WorkerRegistrationClient(client, discovery, capabilities),
            agentAuthentication: new TestProvider { Available = true });

        await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains("Worker started.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Central", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unavailable", output.ToString(), StringComparison.Ordinal);
        var cached = new ManagedConfigurationSynchronizer(settings.IdentityFile + ".configuration.json", global.ManagedProjects);
        Assert.Equal("Central", Assert.Single(cached.LoadLastValid()).Configuration.Project.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmaterializedCatalogSurvivesRestartOutageAndReconnect(bool hasProject)
    {
        using var temporary = new TemporaryDirectory();
        var project = new ServerProjectContract("central-id", "Central", "owner/repo", "main", "", [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        ServerProjectContract[] projects = hasProject ? [project] : [];
        var snapshot = new ServerManagedConfigurationContract(1, ManagedConfigurationSynchronizer.CalculateVersion(projects), projects);
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var global = new GlobalWorkerConfiguration
        {
            Server = settings, Projects = new() { Ownership = "managed", Directory = Path.Combine(temporary.Path, "absent-projects") },
            ManagedProjects = new() { CheckoutDirectory = Path.Combine(temporary.Path, "checkouts") },
            Api = new() { ListenUrl = $"http://127.0.0.1:{port}" }
        };
        using var management = new HttpClient { BaseAddress = new Uri(global.Api.ListenUrl) };

        // Each run retains the identity and catalog but never creates a checkout or YAML.
        foreach (var offline in new[] { false, true, false })
        {
            using var stop = new CancellationTokenSource();
            using var output = new StartedWriter();
            var heartbeats = 0;
            var provisioningRequests = 0;
            using var handler = new Handler(async (request, token) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                Assert.False(path.EndsWith("/assignments/request", StringComparison.Ordinal));
                if (offline) throw new HttpRequestException("Server offline");
                if (path.EndsWith("/configuration", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) };
                if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
                {
                    var heartbeat = await (request.Content ?? throw new InvalidDataException("Missing heartbeat"))
                        .ReadFromJsonAsync<WorkerHeartbeatContract>(token);
                    Assert.NotNull(heartbeat);
                    Assert.Equal(0, heartbeat.ActiveExecutions);
                    Assert.NotEmpty(heartbeat.Capabilities);
                    Interlocked.Increment(ref heartbeats);
                }
                if (path.EndsWith("/provisioning/request", StringComparison.Ordinal))
                    Interlocked.Increment(ref provisioningRequests);
                return new(path.EndsWith("/request", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0")));
            var capabilities = new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")));
            var host = new WorkerHost(global, ProjectConfigurationDiscovery.LoadForWorker(global),
                new WorkerConsole(output, interactive: false),
                registrationClient: new WorkerRegistrationClient(client, discovery, capabilities),
                agentAuthentication: new TestProvider { Available = true });
            var run = host.RunAsync(stop.Token);
            try
            {
                await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(run.IsCompleted);
                var status = await management.GetFromJsonAsync<WorkerStatus>("/api/status");
                Assert.NotNull(status);
                Assert.Equal(hasProject || offline ? "not-ready" : "running", status.State);
                Assert.Equal(hasProject ? 1 : 0, status.ConfiguredProjectCount);
                Assert.Equal(0, status.EnabledProjectCount);
                var runtimeProjects = await management.GetFromJsonAsync<ProjectRuntimeInfo[]>("/api/projects");
                Assert.NotNull(runtimeProjects);
                if (hasProject)
                {
                    var runtime = Assert.Single(runtimeProjects);
                    Assert.Equal("Unavailable", runtime.State);
                    Assert.Contains("not materialized", runtime.UnavailableReason ?? "", StringComparison.Ordinal);
                }
                else Assert.Empty(runtimeProjects);
                Assert.False(Directory.Exists(global.ManagedProjects.CheckoutDirectory));
                if (!offline)
                {
                    Assert.True(Volatile.Read(ref heartbeats) > 0);
                    Assert.True(Volatile.Read(ref provisioningRequests) > 0);
                }
            }
            finally
            {
                stop.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    [Fact]
    public async Task CachedUnmaterializedProjectReconnectsWithoutRestartOrAssignment()
    {
        using var temporary = new TemporaryDirectory();
        using var stop = new CancellationTokenSource();
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        var global = new GlobalWorkerConfiguration
        {
            Server = settings, Projects = new() { Ownership = "managed" },
            ManagedProjects = new() { CheckoutDirectory = Path.Combine(temporary.Path, "checkouts") },
            Worker = new() { PollingSeconds = 1 }, Api = new() { Enabled = false }
        };
        var project = new ServerProjectContract("central-id", "Central", "owner/repo", "main", "", [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var snapshot = new ServerManagedConfigurationContract(1, ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]);
        new ManagedConfigurationSynchronizer(settings.IdentityFile + ".configuration.json", global.ManagedProjects).Apply(snapshot);
        var requests = 0;
        var cachedHeartbeat = false;
        var reconnected = false;
        using var handler = new Handler(async (request, token) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            Assert.False(path.EndsWith("/assignments/request", StringComparison.Ordinal));
            if (path.EndsWith("/configuration", StringComparison.Ordinal))
            {
                if (++requests <= 2) throw new HttpRequestException("Server offline");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) };
            }
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var heartbeat = await (request.Content ?? throw new InvalidDataException("Missing heartbeat"))
                    .ReadFromJsonAsync<WorkerHeartbeatContract>(token);
                Assert.NotNull(heartbeat);
                cachedHeartbeat |= heartbeat.ConfigurationSynchronization == "unavailable";
                if (requests >= 3 && heartbeat.ConfigurationSynchronization == "synchronized")
                {
                    Assert.Equal("not-ready", heartbeat.LifecycleState);
                    reconnected = true;
                    stop.Cancel();
                }
            }
            return new(path.EndsWith("/request", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var host = new WorkerHost(global, [], new WorkerConsole(new StringWriter(), interactive: false),
            timeProvider: new AdvancingClock(),
            registrationClient: new WorkerRegistrationClient(client,
                new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0"))),
                new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")))),
            agentAuthentication: new TestProvider { Available = true });
        try
        {
            await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(cachedHeartbeat);
            Assert.True(reconnected);
            Assert.False(Directory.Exists(global.ManagedProjects.CheckoutDirectory));
        }
        finally { stop.Cancel(); }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerMinute), TimeSpan.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidServerContractOrCorruptCacheStillFailsStartup(bool corruptCache)
    {
        using var temporary = new TemporaryDirectory();
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        var runtime = new ManagedProjectRuntimeSettings { CheckoutDirectory = Path.Combine(temporary.Path, "checkouts") };
        var cache = settings.IdentityFile + ".configuration.json";
        var valid = new ServerManagedConfigurationContract(1, ManagedConfigurationSynchronizer.CalculateVersion([]), []);
        new ManagedConfigurationSynchronizer(cache, runtime).Apply(valid);
        if (corruptCache) await File.WriteAllTextAsync(cache, "{}");
        using var handler = new Handler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath.EndsWith("/configuration", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(valid with { ContractVersion = 99 }) }
                : new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var host = new WorkerHost(new GlobalWorkerConfiguration
        {
            Server = settings, Projects = new() { Ownership = "managed" }, ManagedProjects = runtime,
            Api = new() { Enabled = false }
        }, [], new WorkerConsole(new StringWriter(), interactive: false),
            registrationClient: new WorkerRegistrationClient(client,
                new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0"))),
                new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")))));

        await Assert.ThrowsAsync<WorkerStartupException>(() => host.RunAsync(CancellationToken.None));
    }

    private sealed class StartedWriter : StringWriter
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Write(string? value)
        {
            base.Write(value);
            if (value?.Contains("Worker started.", StringComparison.Ordinal) == true) Started.TrySetResult(true);
        }
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
