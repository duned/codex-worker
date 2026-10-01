namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using CodexWorker;

public sealed class ProvisioningCommandTests
{
    [Theory]
    [InlineData("git", ProvisioningCommandAction.Install, true)]
    [InlineData("git", ProvisioningCommandAction.Update, true)]
    [InlineData("git", ProvisioningCommandAction.Uninstall, true)]
    [InlineData("codex-cli", ProvisioningCommandAction.Install, true)]
    [InlineData("git", ProvisioningCommandAction.Logout, false)]
    [InlineData("github-cli", ProvisioningCommandAction.Logout, true)]
    public void OnlyRegisteredCapabilityActionsAreSupported(string capability, ProvisioningCommandAction action, bool supported)
    {
        Assert.Equal(supported, ProvisioningCommandProtocol.Supported(new("server", capability, action)));
        Assert.False(ProvisioningCommandProtocol.Valid(new("server", capability, (ProvisioningCommandAction)123)));
        Assert.False(ProvisioningCommandProtocol.Valid(new("server", "git; echo secret", action)));
    }

    [Fact]
    public void AdvertisedActionsHaveRegisteredCommandExecutors()
    {
        foreach (var definition in CapabilityCatalog.Definitions)
        {
            var capability = CapabilityCatalog.Describe(definition, CapabilityCatalog.Unknown(definition), connected: true);
            foreach (var action in capability.AvailableActions)
            {
                var command = action == "refresh" ? ProvisioningCommandAction.Detect
                    : Enum.Parse<ProvisioningCommandAction>(action, ignoreCase: true);
                Assert.True(ProvisioningCommandProtocol.Supported(new("server", definition.Id, command)));
            }
            if (definition.RequiresAuthentication)
            {
                Assert.Contains("checkauthentication", capability.AvailableActions);
                Assert.Contains("logout", capability.AvailableActions);
            }
            if (definition.RequiresConfiguration)
                Assert.Contains("checkconfiguration", capability.AvailableActions);
            Assert.Empty(CapabilityCatalog.Describe(definition, capability.State, connected: false).AvailableActions);
        }
    }

    [Fact]
    public void WireProtocolRejectsCommandsAndSecretFields()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProvisioningCommandRequest>("""
            {"NodeId":"server","CapabilityId":"git","Action":"Install","Command":"echo secret"}
            """));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProvisioningCommandReport>("""
            {"Status":"Failed","Diagnostic":"ProcessFailed","Output":"secret"}
            """));
        Assert.False(ProvisioningCommandProtocol.ValidReport(new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Denied)));
    }

    [Fact]
    public async Task ExecutorUsesFixedArgumentsAndRequiresElevationAndPermission()
    {
        var calls = new List<(string Executable, IReadOnlyList<string> Arguments)>();
        var executor = new NodeProvisioningCommandExecutor(Discovery(), (executable, arguments, _) =>
        {
            calls.Add((executable, arguments));
            return Task.FromResult(0);
        }, () => true, () => false);
        var command = Running(new("server", "git", ProvisioningCommandAction.Update));
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(command, true)).Diagnostic);
        Assert.Empty(calls);
        command = command with { Request = command.Request with { AllowElevation = true } };
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(command, false)).Diagnostic);
        Assert.Empty(calls);
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await executor.ExecuteAsync(command, true)).Status);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal("/usr/bin/sudo", call.Executable));
        Assert.Equal(new[] { "-n", "/usr/bin/apt-get", "update" }, calls[0].Arguments);
        Assert.Equal(new[] { "-n", "/usr/bin/apt-get", "install", "-y", "--no-install-recommends", "git", "openssh-client" }, calls[1].Arguments);
    }

    [Fact]
    public async Task ExecutorDiscardsExceptionSecretsAndHonorsCancellationAndExpiredDeadline()
    {
        var executor = new NodeProvisioningCommandExecutor(Discovery(), (_, _, _) => Task.FromException<int>(new IOException("private-token")));
        var command = Running(new("server", "codex-cli", ProvisioningCommandAction.Logout));
        var result = await executor.ExecuteAsync(command, true);
        Assert.Equal(ProvisioningDiagnostic.ProcessFailed, result.Diagnostic);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(ProvisioningDiagnostic.TimedOut, (await executor.ExecuteAsync(command with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, true)).Diagnostic);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(ProvisioningDiagnostic.Cancelled, (await executor.ExecuteAsync(command, true, cancelled.Token)).Diagnostic);
    }

    [Fact]
    public async Task DurableDispatchDoesNotReplayAndConflictsRemainLockedUntilAcknowledgement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "provisioning-commands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "registry.db");
            var store = new ProvisioningCommandStore(path);
            await store.InitializeAsync();
            var pending = await store.CreateAsync(new("server", "git", ProvisioningCommandAction.Detect));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(pending.Request));
            var other = await store.CreateAsync(new("server", "codex-cli", ProvisioningCommandAction.Detect));
            var claims = await Task.WhenAll(store.ClaimAsync("server"), store.ClaimAsync("server"));
            Assert.Equal(2, claims.Select(item => item!.Id).Distinct().Count());
            var restarted = new ProvisioningCommandStore(path);
            await restarted.InitializeAsync();
            Assert.Null(await restarted.ClaimAsync("server"));
            Assert.Equal(2, (await restarted.ListAsync()).Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CancelAsync(pending.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ReportAsync(pending.Id, new string('a', 32), new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ReconcileAsync(pending.Id));
            var report = new ProvisioningCommandReport(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed);
            var completed = await restarted.ReportAsync(pending.Id, "server", report);
            Assert.NotNull(completed!.StartedAtUtc);
            Assert.NotNull(completed.CompletedAtUtc);
            Assert.Equal(completed, await restarted.ReportAsync(pending.Id, "server", report));
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ReportAsync(pending.Id, "server", new(ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing)));
            await restarted.CreateAsync(pending.Request);
            Assert.Equal(other.Id, Assert.Single(await restarted.ListAsync(), item => item.Request.CapabilityId == "codex-cli").Id);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PendingCancellationSurvivesRestartAndReleasesCapability()
    {
        var path = Path.Combine(Path.GetTempPath(), "provisioning-cancel-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new ProvisioningCommandStore(path);
            await store.InitializeAsync();
            var operation = await store.CreateAsync(new("server", "git", ProvisioningCommandAction.Detect));
            Assert.Equal(ProvisioningCommandStatus.Cancelled, (await store.CancelAsync(operation.Id))!.Status);
            Assert.Null(await store.ClaimAsync("server"));
            await store.CreateAsync(operation.Request);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task ExpiredUnacknowledgedOperationRequiresExplicitReconciliationBeforeReplacement()
    {
        var path = Path.Combine(Path.GetTempPath(), "provisioning-reconcile-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var clock = new CommandClock();
            var store = new ProvisioningCommandStore(path, clock);
            await store.InitializeAsync();
            var request = new ProvisioningCommandRequest("server", "git", ProvisioningCommandAction.Install, 5, true);
            var operation = await store.CreateAsync(request);
            await store.ClaimAsync("server");
            clock.Now = clock.Now.AddSeconds(6);
            Assert.Null(await store.ClaimAsync("server"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(request));
            var reconciled = await store.ReconcileAsync(operation.Id);
            Assert.Equal(ProvisioningDiagnostic.Interrupted, reconciled!.Diagnostic);
            Assert.Equal(ProvisioningCommandStatus.Failed, reconciled.Status);
            await store.CreateAsync(request);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task DeadlineCancelsRunningHandler()
    {
        var started = false;
        var executor = new NodeProvisioningCommandExecutor(Discovery(), async (_, _, token) =>
        {
            started = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        var operation = Running(new("server", "codex-cli", ProvisioningCommandAction.Logout)) with
        { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(1) };
        Assert.Equal(ProvisioningDiagnostic.TimedOut, (await executor.ExecuteAsync(operation, true)).Diagnostic);
        Assert.True(started);
    }

    private sealed class CommandClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ManagedWorkerExecutesKnownCommandAndReportsPolicyDenialWithoutExecutingMutation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "command-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identityPath = Path.Combine(directory, "worker-id");
            var workerId = await WorkerIdentity.LoadOrCreateAsync(identityPath);
            var token = await WorkerAuthentication.LoadOrCreateTokenAsync(identityPath);
            var operation = Running(new(workerId, "git", ProvisioningCommandAction.Detect));
            var handler = new CommandHandler(operation, token);
            using var client = new HttpClient(handler);
            var probes = 0;
            var discovery = new NodeCapabilityDiscovery((_, _, _) =>
            {
                probes++;
                return Task.FromResult((0, "2.0"));
            });
            var registration = new WorkerRegistrationClient(client, discovery);
            var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = identityPath };
            Assert.True(await registration.ExecuteProvisioningCommandAsync(settings, new(), CancellationToken.None));
            Assert.Equal(ProvisioningCommandStatus.Succeeded, handler.Report!.Status);
            Assert.True(probes > 0);
            probes = 0;
            handler.Operation = Running(new(workerId, "git", ProvisioningCommandAction.Uninstall, AllowElevation: true));
            Assert.True(await registration.ExecuteProvisioningCommandAsync(settings, new(), CancellationToken.None));
            Assert.Equal(ProvisioningDiagnostic.Denied, handler.Report!.Diagnostic);
            Assert.Equal(0, probes);
            foreach (var action in new[] { ProvisioningCommandAction.GenerateSshKey, ProvisioningCommandAction.PrepareAuthentication })
            {
                handler.Operation = Running(new(workerId, action == ProvisioningCommandAction.GenerateSshKey ? "git" : "github-cli", action));
                Assert.True(await registration.ExecuteProvisioningCommandAsync(settings,
                    new ProvisioningPolicy { Enabled = true, AllowNonPrivileged = true }, CancellationToken.None));
                Assert.Equal(ProvisioningDiagnostic.Denied, handler.Report!.Diagnostic);
                Assert.Equal(0, probes);
            }
            handler.Operation = handler.Operation with { Request = handler.Operation.Request with { NodeId = new string('b', 32) } };
            await Assert.ThrowsAsync<InvalidDataException>(() => registration.ExecuteProvisioningCommandAsync(settings, new(), CancellationToken.None));
            Assert.Equal(0, probes);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CommandHandler(ProvisioningCommand operation, string token) : HttpMessageHandler
    {
        public ProvisioningCommand Operation { get; set; } = operation;
        public ProvisioningCommandReport? Report { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(token, request.Headers.Authorization!.Parameter);
            if (request.RequestUri!.AbsolutePath.EndsWith("/request", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Operation) };
            Assert.EndsWith($"/{Operation.Id}/report", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            Report = await request.Content!.ReadFromJsonAsync<ProvisioningCommandReport>(cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }

    private static NodeCapabilityDiscovery Discovery() => new((_, _, _) => Task.FromResult((0, "2.0")));
    private static ProvisioningCommand Running(ProvisioningCommandRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid().ToString("N"), request, now, ProvisioningCommandStatus.Running,
            ProvisioningDiagnostic.Executing, now, now.AddSeconds(request.TimeoutSeconds));
    }
}
