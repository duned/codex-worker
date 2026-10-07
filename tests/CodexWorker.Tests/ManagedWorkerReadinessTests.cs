using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
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
            agentAuthentication: provider, executionHistoryPath: Path.Combine(temporary.Path, "history.db"));

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
            agentAuthentication: provider, executionHistoryPath: Path.Combine(temporary.Path, "history.db"));

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
            registrationClient: new WorkerRegistrationClient(client, discovery, capabilities), agentAuthentication: provider,
            executionHistoryPath: Path.Combine(temporary.Path, "history.db"));

        await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, registered);
        Assert.True(notReady);
        Assert.True(ready);
        Assert.Equal(expectedPreflights, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdStartWithServerProjectAndNoLocalYamlReportsEligibilityWithoutMaterialization(bool missingCapability)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temporary = new TemporaryDirectory();
        await temporary.EnableProjectProbesAsync();
        using var stop = new CancellationTokenSource();
        using var output = new StopOnStartedWriter(stop);
        var project = new ServerProjectContract("central-id", "Central", "owner/repo", "main", "", missingCapability ? [new("runtime", "unavailable-runtime")] : [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var snapshot = new ServerManagedConfigurationContract(1, ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]);
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        var ready = false;
        ManagedProjectDiagnostic? observation = null;
        using var handler = new Handler(async (request, token) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/configuration", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) };
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var heartbeat = await (request.Content ?? throw new InvalidDataException("Missing heartbeat"))
                    .ReadFromJsonAsync<WorkerHeartbeatContract>(token);
                ready |= heartbeat?.LifecycleState == "running";
                if (heartbeat?.ManagedDiagnostics?.Projects.FirstOrDefault() is { State: "blocked" or "not-materialized" } current)
                    observation = current;
            }
            return new(path.EndsWith("/request", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        });
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
            agentAuthentication: new TestProvider { Available = true },
            executionHistoryPath: Path.Combine(temporary.Path, "history.db"));

        await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains("Worker started.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(!missingCapability, ready);
        Assert.Equal(missingCapability ? "blocked" : "not-materialized", observation?.State);
        Assert.Equal(missingCapability ? "project-capabilities-missing" : null, observation?.DiagnosticCode);
        Assert.DoesNotContain("not materialized", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(global.ManagedProjects.CheckoutDirectory));
        var cached = new ManagedConfigurationSynchronizer(settings.IdentityFile + ".configuration.json", global.ManagedProjects);
        Assert.Equal("Central", Assert.Single(cached.LoadLastValid()).Configuration.Project.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmaterializedCatalogSurvivesRestartOutageAndReconnect(bool hasProject)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temporary = new TemporaryDirectory();
        await temporary.EnableProjectProbesAsync();
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
                if (path.EndsWith("/assignments/request", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkerAssignmentResponseContract(false, null)) };
                return new(path.EndsWith("/request", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0")));
            var capabilities = new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")));
            var host = new WorkerHost(global, ProjectConfigurationDiscovery.LoadForWorker(global),
                new WorkerConsole(output, interactive: false),
                registrationClient: new WorkerRegistrationClient(client, discovery, capabilities),
                agentAuthentication: new TestProvider { Available = true },
                executionHistoryPath: Path.Combine(temporary.Path, "history.db"));
            var run = host.RunAsync(stop.Token);
            try
            {
                await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.False(run.IsCompleted);
                var status = await management.GetFromJsonAsync<WorkerStatus>("/api/status");
                Assert.NotNull(status);
                Assert.Equal(offline ? "not-ready" : "running", status.State);
                Assert.Equal(hasProject ? 1 : 0, status.ConfiguredProjectCount);
                Assert.Equal(hasProject ? 1 : 0, status.EnabledProjectCount);
                var runtimeProjects = await management.GetFromJsonAsync<ProjectRuntimeInfo[]>("/api/projects");
                Assert.NotNull(runtimeProjects);
                if (hasProject)
                {
                    var runtime = Assert.Single(runtimeProjects);
                    Assert.Equal("Enabled", runtime.State);
                    Assert.Null(runtime.UnavailableReason);
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
        if (!OperatingSystem.IsLinux()) return;
        using var temporary = new TemporaryDirectory();
        await temporary.EnableProjectProbesAsync();
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
                    Assert.Equal("running", heartbeat.LifecycleState);
                    reconnected = true;
                    stop.Cancel();
                }
            }
            if (path.EndsWith("/assignments/request", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkerAssignmentResponseContract(false, null)) };
            return new(path.EndsWith("/request", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var host = new WorkerHost(global, [], new WorkerConsole(new StringWriter(), interactive: false),
            timeProvider: new AdvancingClock(),
            registrationClient: new WorkerRegistrationClient(client,
                new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0"))),
                new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")))),
            agentAuthentication: new TestProvider { Available = true },
            executionHistoryPath: Path.Combine(temporary.Path, "history.db"));
        try
        {
            await host.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(cachedHeartbeat);
            Assert.True(reconnected);
            Assert.False(Directory.Exists(global.ManagedProjects.CheckoutDirectory));
        }
        finally { stop.Cancel(); }
    }

    [Theory]
    [InlineData("materialization")]
    [InlineData("snapshot-stale")]
    [InlineData("execution-stale")]
    [InlineData("execution-disabled")]
    [InlineData("execution-invalid-version")]
    [InlineData("execution-duplicate-project")]
    public async Task AssignmentWithNoCheckoutHasBoundedPreparationOutcome(string scenario)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temporary = new TemporaryDirectory();
        await temporary.EnableProjectProbesAsync();
        using var stop = new CancellationTokenSource();
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };
        var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
        var project = new ServerProjectContract("opaque-central-id", "Central", "owner/repo", "main", "", [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var global = new GlobalWorkerConfiguration
        {
            Server = settings, Projects = new() { Ownership = "managed" },
            ManagedProjects = new() { CheckoutDirectory = Path.Combine(temporary.Path, "checkouts") },
            Api = new() { Enabled = false }
        };
        var assigned = false;
        using var output = new StringWriter();
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var terminalLogged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedObservation = new TaskCompletionSource<WorkerHeartbeatContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<WorkerExecutionReportContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, token) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                var heartbeat = await (request.Content ?? throw new InvalidDataException("Missing heartbeat"))
                    .ReadFromJsonAsync<WorkerHeartbeatContract>(token);
                if (heartbeat?.ManagedDiagnostics?.Projects.Any(item => item.State == "failed") == true)
                    failedObservation.TrySetResult(heartbeat);
            }
            if (path.EndsWith("/configuration", StringComparison.Ordinal))
            {
                var current = assigned && scenario == "execution-stale" ? project with { Revision = 2 } :
                    assigned && scenario == "execution-disabled" ? project with { Enabled = false } : project;
                ServerProjectContract[] projects = assigned && scenario == "execution-duplicate-project" ? [current, current] : [current];
                var version = assigned && scenario == "execution-invalid-version" ? "invalid" :
                    ManagedConfigurationSynchronizer.CalculateVersion(projects);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServerManagedConfigurationContract(1,
                    version, projects)) };
            }
            if (path.EndsWith("/assignments/request", StringComparison.Ordinal))
            {
                Assert.False(Directory.Exists(global.ManagedProjects.CheckoutDirectory));
                var capacity = await (request.Content ?? throw new InvalidDataException("Missing request"))
                    .ReadFromJsonAsync<WorkerAssignmentRequestContract>(token);
                Assert.NotNull(capacity);
                Assert.Equal(1, capacity.ProjectCapacities[project.Id]);
                Assert.False(assigned);
                assigned = true;
                var now = DateTimeOffset.UtcNow;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkerAssignmentResponseContract(true,
                    new("assignment", "execution", scenario == "snapshot-stale" ? project with { Revision = 2 } : project,
                        new("github-issue", "17"), workerId, new Dictionary<string, string>(),
                        new("execution", workerId, 1, now, now.AddMinutes(15), "Active", 60)))) };
            }
            if (path.EndsWith("/report", StringComparison.Ordinal))
            {
                var report = await (request.Content ?? throw new InvalidDataException("Missing report"))
                    .ReadFromJsonAsync<WorkerExecutionReportContract>(token);
                reported.TrySetResult(report ?? throw new InvalidDataException("Missing report"));
            }
            return new(path.EndsWith("/request", StringComparison.Ordinal) ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var host = new WorkerHost(global, [], new WorkerConsole(output, interactive: false),
            registrationClient: new WorkerRegistrationClient(client,
                new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0"))),
                new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(0, "1.0.0", "")))),
            agentAuthentication: new TestProvider { Available = true },
            executionHistoryPath: Path.Combine(temporary.Path, "history.db"), operationalLog: message =>
            {
                messages.Enqueue(message);
                if (message.Contains("Server report completed", StringComparison.Ordinal)) terminalLogged.TrySetResult();
            });
        var run = host.RunAsync(stop.Token);
        try
        {
            var report = await reported.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (scenario == "materialization")
            {
                var heartbeat = await failedObservation.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var observation = Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(heartbeat.ManagedDiagnostics).Projects);
                Assert.Equal(project.Id, observation.ProjectId);
                Assert.Equal(project.Revision, observation.Revision);
                Assert.Equal("project-preparation-failed", observation.DiagnosticCode);
                Assert.DoesNotContain("clone unavailable", JsonSerializer.Serialize(heartbeat), StringComparison.Ordinal);
            }
            await terminalLogged.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var managed = messages.Where(message => message.StartsWith("Managed ·", StringComparison.Ordinal)).ToArray();
            Assert.Contains(managed, message => message.Contains("assignment received", StringComparison.Ordinal));
            Assert.Contains(managed, message => message.Contains("assignment rejected", StringComparison.Ordinal));
            Assert.Contains(managed, message => message.Contains("terminal outcome Blocked · Server report completed", StringComparison.Ordinal));
            var receipt = Assert.Single(managed, message => message.Contains("assignment received", StringComparison.Ordinal));
            Assert.Contains("project opaque-central-id/Central", receipt, StringComparison.Ordinal);
            Assert.Contains("work github-issue #17", receipt, StringComparison.Ordinal);
            Assert.Contains("assignment assignment · Server execution execution · lease generation 1", receipt, StringComparison.Ordinal);
            Assert.All(managed.Where(message => message != receipt), message =>
            {
                Assert.StartsWith("Managed · assignment [", message, StringComparison.Ordinal);
                Assert.DoesNotContain("Server execution", message, StringComparison.Ordinal);
            });
            Assert.Equal(managed.Length, managed.Distinct(StringComparer.Ordinal).Count());
            lock (output)
                Assert.DoesNotContain("Managed ·", output.ToString(), StringComparison.Ordinal);
            if (scenario == "materialization")
            {
                Assert.DoesNotContain(managed, message => message.Contains("project ready", StringComparison.Ordinal));
                Assert.Contains(managed, message => message.Contains("first materialization", StringComparison.Ordinal));
                Assert.Contains(managed, message => message.Contains("infrastructure failure before execution", StringComparison.Ordinal));
                Assert.DoesNotContain(managed, message => message.Contains("clone unavailable", StringComparison.Ordinal));
                Assert.DoesNotContain(managed, message => message.Contains("starting Worker execution", StringComparison.Ordinal));
            }
            Assert.False(run.IsCompleted);
            Assert.Equal("Failed", report.State);
            Assert.Equal(1, report.Generation);
            Assert.False(report.Recoverable);
            Assert.Contains(scenario is "materialization" or "execution-invalid-version" or "execution-duplicate-project"
                ? "local project preparation failed" : "revision",
                report.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(global.ManagedProjects.CheckoutDirectory,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(project.Id))).ToLowerInvariant())));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
        }
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
        Assert.StartsWith("codex-cli:execution-preflight-failed", readiness.DiagnosticCode, StringComparison.Ordinal);
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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken);
            if (request.Method == HttpMethod.Put && response.IsSuccessStatusCode)
            {
                var registration = await (request.Content ?? throw new InvalidDataException("Missing registration"))
                    .ReadFromJsonAsync<WorkerRegistrationContract>(cancellationToken);
                Assert.NotNull(registration);
                response.Content = JsonContent.Create(new { contractVersion = registration.ContractVersion, workerId = registration.WorkerId });
            }
            return response;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "managed-readiness-" + Guid.NewGuid().ToString("N"));
        private string? _previousPath;
        private readonly string? _previousHome = Environment.GetEnvironmentVariable("HOME");
        private readonly string? _previousGhConfig = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
        private readonly string? _previousXdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public async Task EnableProjectProbesAsync()
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            Environment.SetEnvironmentVariable("HOME", Path);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", System.IO.Path.Combine(Path, "operator"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", System.IO.Path.Combine(Path, "xdg"));
            var prepared = await new NodeGitHubSetup().ExecuteAsync(new("server", "github-cli",
                ProvisioningCommandAction.PrepareAuthentication), CancellationToken.None);
            Assert.Equal(ProvisioningCommandStatus.Succeeded, prepared.Status);
            var bin = System.IO.Path.Combine(Path, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(System.IO.Path.Combine(bin, "git"), "#!/bin/sh\nif [ \"$1\" = ls-remote ]; then exit 0; fi\necho 'clone unavailable' >&2\nexit 1\n");
            File.WriteAllText(System.IO.Path.Combine(bin, "gh"), "#!/bin/sh\nif [ \"$1\" = api ]; then echo '{\"push\":true}'; else echo '[]'; fi\n");
            foreach (var executable in new[] { "git", "gh" })
                File.SetUnixFileMode(System.IO.Path.Combine(bin, executable), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _previousPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", bin + ":" + _previousPath);
        }
        public void Dispose()
        {
            if (_previousPath is not null) Environment.SetEnvironmentVariable("PATH", _previousPath);
            Environment.SetEnvironmentVariable("HOME", _previousHome);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", _previousGhConfig);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousXdgConfig);
            Directory.Delete(Path, recursive: true);
        }
    }
}
