namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using CodexWorker;
using System.Text.Json;

public sealed class NodeCapabilityTests
{
    [Theory]
    [InlineData("codex-cli")]
    [InlineData("github-cli")]
    public async Task ReadinessTracksInstallationAuthenticationAndAuthenticationLoss(string id)
    {
        var installed = false;
        var authenticated = false;
        var discovery = new NodeCapabilityDiscovery((_, arguments, _) =>
        {
            if (!installed) return Task.FromException<(int, string)>(new FileNotFoundException());
            return Task.FromResult((arguments[0] is "auth" or "login" && !authenticated ? 1 : 0, "tool 1.2.3"));
        });
        var definition = CapabilityCatalog.Definitions.Single(item => item.Id == id);
        async Task<NodeCapability> Observe(bool refresh = false) => CapabilityCatalog.Describe(definition,
            (await discovery.GetAsync(refresh)).Single(item => item.Id == id), connected: true);

        var missing = await Observe();
        Assert.False(missing.Readiness.Available);
        Assert.Contains("tool-missing", missing.Readiness.BlockingReasons);
        Assert.Equal(CapabilityHealth.Healthy, missing.State.Health);
        installed = true;
        var loginRequired = await Observe(refresh: true);
        Assert.Equal(InstallationState.Installed, loginRequired.State.Installation);
        Assert.Equal(["authentication-required"], loginRequired.Readiness.BlockingReasons);
        authenticated = true;
        Assert.False((await Observe()).Readiness.Available); // Cached observations grant no new readiness.
        Assert.True((await Observe(refresh: true)).Readiness.Available);
        authenticated = false;
        var lost = await Observe(refresh: true);
        Assert.Equal(InstallationState.Installed, lost.State.Installation);
        Assert.False(lost.Readiness.Available);
        Assert.Equal(["authentication-required"], lost.Readiness.BlockingReasons);
    }

    [Fact]
    public void ReadinessUsesDefinitionDependenciesAndDoesNotRequireCredentialsForEveryProvider()
    {
        var sdk = CapabilityCatalog.Definitions.Single(item => item.Id == "dotnet-sdk");
        var installed = CapabilityCatalog.Unknown(sdk) with
        { Installation = InstallationState.Installed, Health = CapabilityHealth.Healthy, DiagnosticCode = null };
        Assert.True(CapabilityCatalog.Evaluate(sdk, installed).Available);
        Assert.True(CapabilityCatalog.ProvidesTool(ProvidedToolKind.DotNetSdk, [installed]));
        Assert.True(CapabilityCatalog.ProvidesTool(ProvidedToolKind.DotNetRuntime, [installed]));
        Assert.False(CapabilityCatalog.ProvidesTool(ProvidedToolKind.DockerEngine, [installed]));
        var future = new CapabilityDefinition("future-tool", "Future tool", "future", [AuthenticationDependencyKind.GitHubCliLogin], false, []);
        var missingAuth = installed with { Id = future.Id };
        Assert.Equal(["authentication-required"], CapabilityCatalog.Evaluate(future, missingAuth).BlockingReasons);
        Assert.True(CapabilityCatalog.Evaluate(future, missingAuth with { Authentication = RequirementState.Satisfied }).Available);
        Assert.False(CapabilityCatalog.ExecutionReadiness([installed]).Available);
    }

    [Fact]
    public void OptionalToolEligibilityRequiresItsConfigurationButDoesNotBlockOtherTasks()
    {
        var inventory = CapabilityCatalog.Definitions.Select(definition => CapabilityCatalog.Unknown(definition) with
        {
            Installation = InstallationState.Installed, Health = CapabilityHealth.Healthy,
            Authentication = definition.RequiresAuthentication ? RequirementState.Satisfied : null,
            Configuration = definition.RequiresConfiguration ? RequirementState.Satisfied : null,
            DiagnosticCode = null
        }).Select(state => state.Id == "docker" ? state with { Configuration = RequirementState.Required } : state).ToArray();
        Assert.True(CapabilityCatalog.ExecutionReadiness(inventory).Available);
        var result = WorkerEligibility.Evaluate([new("tool", "docker")], [new("tool", "docker")], inventory);
        Assert.False(result.IsEligible);
        Assert.Contains("configuration-required", Assert.Single(result.MissingRequirements), StringComparison.Ordinal);
        Assert.True(WorkerEligibility.Evaluate([], [], inventory).IsEligible);
        var ready = inventory.Select(state => state.Id == "docker" ? state with { Configuration = RequirementState.Satisfied } : state).ToArray();
        Assert.True(WorkerEligibility.Evaluate([new("tool", "docker")], [new("tool", "docker")], ready).IsEligible);
    }

    [Fact]
    public async Task AuthenticationProbeFailurePreservesObservedInstallation()
    {
        var discovery = new NodeCapabilityDiscovery((_, arguments, _) => arguments[0] == "login"
            ? Task.FromException<(int, string)>(new FileNotFoundException("private-output"))
            : Task.FromResult((0, "1.2.3")));
        var state = (await discovery.GetAsync()).Single(item => item.Id == "codex-cli");
        Assert.Equal(InstallationState.Installed, state.Installation);
        Assert.Equal(CapabilityHealth.Error, state.Health);
        Assert.Equal("probe-failed", state.DiagnosticCode);
        Assert.False(CapabilityCatalog.Ready([state]));
    }

    [Fact]
    public async Task DetectionSeparatesInstallationAndAuthenticationAndDiscardsSecretOutput()
    {
        var discovery = new NodeCapabilityDiscovery((executable, arguments, _) =>
        {
            if (executable == "git") return Task.FromException<(int, string)>(new FileNotFoundException());
            if (arguments[0] == "--version") return Task.FromResult((0, "tool 1.2.3 private-token"));
            return Task.FromResult((executable == "gh" ? 1 : 0, "private-token"));
        });
        var states = await discovery.GetAsync();
        var git = Assert.Single(states, state => state.Id == "git");
        Assert.Equal(InstallationState.Missing, git.Installation);
        Assert.Null(git.Authentication);
        var github = Assert.Single(states, state => state.Id == "github-cli");
        Assert.Equal(InstallationState.Installed, github.Installation);
        Assert.Equal("1.2.3", github.DetectedVersion);
        Assert.Equal(RequirementState.Required, github.Authentication);
        Assert.Equal(UpdateState.Unknown, github.Update);
        Assert.Equal(RequirementState.Satisfied, Assert.Single(states, state => state.Id == "codex-cli").Authentication);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(states), StringComparison.Ordinal);
        Assert.True(CapabilityCatalog.ValidInventory(states));
        Assert.False(CapabilityCatalog.Ready(states));
    }

    [Fact]
    public async Task ExplicitRefreshRedetectsMachineFactsWhileOrdinaryReadsAreCached()
    {
        var installed = false;
        var discovery = new NodeCapabilityDiscovery((_, arguments, _) => installed
            ? Task.FromResult((0, arguments[0] switch
            {
                "--list-sdks" => "10.0.100 [/sdk]",
                "--list-runtimes" => "Microsoft.NETCore.App 10.0.1 [/runtime]\nMicrosoft.AspNetCore.App 10.0.1 [/runtime]",
                _ => "2.0"
            })) : Task.FromException<(int, string)>(new FileNotFoundException()));
        var before = await discovery.GetAsync();
        installed = true;
        Assert.Same(before, await discovery.GetAsync());
        var after = await discovery.GetAsync(refresh: true);
        Assert.All(after, state => Assert.Equal(InstallationState.Installed, state.Installation));
        Assert.True(CapabilityCatalog.Ready(after));
    }

    [Fact]
    public async Task ConcurrentInspectionShowsRefreshInProgress()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new NodeCapabilityDiscovery(async (_, _, token) =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(token);
            return (0, "2.0");
        });
        var refresh = discovery.GetAsync(refresh: true);
        await started.Task;
        var snapshot = await discovery.GetAsync();
        Assert.All(snapshot, state => Assert.Equal(CapabilityOperationState.Running, state.Operation.State));
        finish.SetResult();
        Assert.All(await refresh, state => Assert.Equal(CapabilityOperationState.Idle, state.Operation.State));
    }

    [Fact]
    public async Task ProbeFailuresAreSafeAndCallerCancellationPropagates()
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromException<(int, string)>(new TimeoutException("secret")));
        var states = await discovery.GetAsync();
        Assert.All(states, state => Assert.Equal(CapabilityHealth.Error, state.Health));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(states), StringComparison.Ordinal);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.GetAsync(cancellationToken: cancelled.Token));
    }

    [Fact]
    public void ManagedWireContractRoundTripsInventoryWithoutChangingLegacySchedulerCapabilities()
    {
        var inventory = CapabilityCatalog.Definitions.Select(CapabilityCatalog.Unknown).ToArray();
        var contract = new WorkerHeartbeatContract(2, new string('a', 32), "1.0", "running", 0, 1,
            [new("tool", "git", "2.0")], [], CapabilityInventory: inventory);
        var wireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<WorkerHeartbeatRequest>(JsonSerializer.Serialize(contract, wireOptions), wireOptions)!;
        Assert.Equal(inventory, request.CapabilityInventory);
        Assert.Equal("git", Assert.Single(request.Capabilities).Name);
        Assert.Null(JsonSerializer.Deserialize<WorkerHeartbeatRequest>("""
            {"ContractVersion":2,"WorkerId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","WorkerVersion":"1.0",
            "LifecycleState":"running","ActiveExecutions":0,"MaximumCapacity":1,"Capabilities":[],"ActiveProjects":[]}
            """)!.CapabilityInventory);
    }

    [Fact]
    public void ConnectedWorkerCanLackReadinessAndOperationsSuppressActions()
    {
        var worker = Worker();
        var node = NodeProvisioning.Describe(worker, []);
        Assert.Equal("connected", node.Connectivity);
        Assert.Equal("not-ready", node.ExecutionReadiness);
        Assert.All(node.Capabilities, item => Assert.Contains("refresh", item.AvailableActions));
        var plan = new ProvisioningPlan("plan", worker.WorkerId, DateTimeOffset.UtcNow, "Running",
            [new("action", "tool", "git", Operation: "install")], "action");
        var busy = NodeProvisioning.Describe(worker, [plan]);
        var git = Assert.Single(busy.Capabilities, item => item.Definition.Id == "git");
        Assert.Equal(CapabilityOperationState.Running, git.State.Operation.State);
        Assert.Empty(git.AvailableActions);
        Assert.Equal("busy", busy.ProvisioningReadiness);
        var failed = NodeProvisioning.Describe(worker, [plan with { State = "Failed", Failure = "private-token" }]);
        Assert.Equal("operation-failed", Assert.Single(failed.Capabilities, item => item.Definition.Id == "git").State.Operation.DiagnosticCode);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(failed), StringComparison.Ordinal);
        var disconnected = NodeProvisioning.Describe(worker with { Availability = "stale" }, []);
        Assert.True(disconnected.ObservationsStale);
        Assert.All(disconnected.Capabilities, item => Assert.Empty(item.AvailableActions));
        var stopped = NodeProvisioning.Describe(worker with { Availability = "offline", LifecycleState = "stopped" }, []);
        Assert.Equal("disconnected", stopped.Connectivity);
        Assert.Equal("not-ready", stopped.ExecutionReadiness);
        Assert.All(stopped.Capabilities, item => Assert.Empty(item.AvailableActions));
    }

    [Fact]
    public void OptionalWorkloadProvidersDoNotMakeAnOtherwiseReadyWorkerUnavailable()
    {
        var inventory = CapabilityCatalog.Definitions.Where(item => item.RequiredForExecution)
            .Select(definition => CapabilityCatalog.Unknown(definition) with
            {
                Installation = InstallationState.Installed,
                Health = CapabilityHealth.Healthy,
                Authentication = definition.RequiresAuthentication ? RequirementState.Satisfied : null,
                Configuration = definition.RequiresConfiguration ? RequirementState.Satisfied : null,
                DetectedAtUtc = DateTimeOffset.UtcNow,
                DiagnosticCode = null
            }).ToArray();
        var node = NodeProvisioning.Describe(Worker() with { CapabilityInventory = inventory }, []);
        Assert.Equal("ready", node.ExecutionReadiness);
        Assert.True(node.ObservationsStale); // Optional tools remain unknown for older reports.
        Assert.Equal(InstallationState.Unknown, Assert.Single(node.Capabilities, item => item.Definition.Id == "docker").State.Installation);
    }

    [Fact]
    public void InventoryRejectsDuplicateIdentifiersAndFreeFormDiagnostics()
    {
        var state = CapabilityCatalog.Unknown(CapabilityCatalog.Definitions[0]);
        Assert.False(CapabilityCatalog.ValidInventory([state, state]));
        Assert.False(CapabilityCatalog.ValidInventory([state with { DiagnosticCode = "private-token" }]));
        Assert.False(CapabilityCatalog.ValidInventory([state with { DetectedVersion = "private-token" }]));
        Assert.False(CapabilityCatalog.ValidInventory([state with { Operation = new(CapabilityOperationState.Failed, DiagnosticCode: "private-token") }]));
    }

    [Fact]
    public async Task RegistryKeepsLatestInventoryAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "node-capabilities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "registry.db");
            var store = new SqliteRegistryStore(database);
            await store.InitializeAsync();
            var workerId = new string('a', 32);
            await store.RegisterWorkerAsync(new(2, workerId, "Worker", "1.0", "linux", 1, []));
            var inventory = CapabilityCatalog.Definitions.Select(CapabilityCatalog.Unknown).ToArray();
            await store.HeartbeatWorkerAsync(new(2, workerId, "1.0", "running", 0, 1, [], [], CapabilityInventory: inventory));
            var restarted = new SqliteRegistryStore(database);
            await restarted.InitializeAsync();
            var worker = await restarted.GetWorkerAsync(workerId);
            Assert.NotNull(worker);
            Assert.Equal(inventory, worker.CapabilityInventory);
            var node = NodeProvisioning.Describe(worker, []);
            Assert.Equal(CapabilityCatalog.Definitions.Count, node.Capabilities.Count);
            Assert.Equal("not-ready", node.ExecutionReadiness);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static WorkerRegistrationResponse Worker() => new(2, new string('a', 32), "Worker", "1.0", "linux", 1,
        [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "online", 0, 1, 1, "running", [],
        CapabilityInventory: CapabilityCatalog.Definitions.Select(CapabilityCatalog.Unknown).ToArray());
}
