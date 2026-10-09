namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using CodexWorker;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

[Collection("ServerTokenEnvironment")]
public sealed class ManagedExecutionMaintenanceTests
{
    [Fact]
    public async Task DurableDispatchSerializesConcurrentCommandsAndNeverReplaysExpiredWork()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "commands.db");
        var clock = new Clock();
        var store = new ExecutionMaintenanceStore(path, clock);
        await store.InitializeAsync();
        var request = Inventory(Guid.NewGuid().ToString("N"));
        var created = await store.CreateAsync(request, "operator");
        Assert.Equal(created, await store.CreateAsync(request, "other-request"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(request with { Offset = 1 }, "operator"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(request with { OperationId = Guid.NewGuid().ToString("N") }, "operator"));
        var claims = await Task.WhenAll(store.ClaimAsync(request.WorkerId), store.ClaimAsync(request.WorkerId));
        Assert.Single(claims.OfType<ExecutionMaintenanceCommand>());
        clock.Now += TimeSpan.FromMinutes(2);
        var reopened = new ExecutionMaintenanceStore(path, clock);
        Assert.Equal("uncertain", (await reopened.GetAsync(request.OperationId))?.Status);
        Assert.Equal("uncertain", (await reopened.ClaimAsync(request.WorkerId))?.Status);
        var report = new ExecutionMaintenanceReport("failed", "interrupted-inspect-before-retry", []);
        var completed = await reopened.ReportAsync(request.OperationId, request.WorkerId, report);
        Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(await reopened.ReportAsync(request.OperationId, request.WorkerId, report)));
        Assert.NotNull(completed?.CompletedAtUtc);
        Assert.Null(await reopened.ClaimAsync(request.WorkerId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReportAsync(request.OperationId, Guid.NewGuid().ToString("N"), report));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReportAsync(request.OperationId, request.WorkerId, report with { Outcome = "succeeded" }));
        Assert.Single(await reopened.ListAsync(request.WorkerId, 1, 0));
        var cancelledRequest = request with { OperationId = Guid.NewGuid().ToString("N") };
        await reopened.CreateAsync(cancelledRequest, "operator");
        Assert.Equal("cancelled", (await reopened.CancelAsync(cancelledRequest.OperationId))?.Status);
        Assert.Null(await reopened.ClaimAsync(request.WorkerId));
    }

    [Fact]
    public async Task ServerAndWorkerChannelAuthorizesInventoryReportingRetryAndRejectsLostOwnership()
    {
        using var temporary = new TemporaryDirectory();
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "maintenance-test-management");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync([
                $"--Server:ListenUrl={url}", $"--Server:DatabasePath={Path.Combine(temporary.Path, "server.db")}",
                "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var identityPath = Path.Combine(temporary.Path, "identity");
            var worker = await WorkerIdentity.LoadOrCreateAsync(identityPath);
            var token = await WorkerAuthentication.LoadOrCreateTokenAsync(identityPath);
            var capabilities = WorkerAuthenticationRequirements.ForRepository("o/r").Select(r =>
                new CodexServer.WorkerCapability(r.Type, r.Name, Scope: r.Scope)).Concat([
                    new CodexServer.WorkerCapability("tool", "git"), new CodexServer.WorkerCapability("agent-provider", "codex"),
                    new CodexServer.WorkerCapability("protocol", ExecutionMaintenanceProtocol.Capability)]).ToArray();
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            Assert.True(await registry.BootstrapWorkerAsync(bootstrap, new(2, worker, "worker", "1", "linux", 1, capabilities), token));
            await registry.HeartbeatWorkerAsync(new(1, worker, "1", "running", 0, 1, capabilities, []));
            await registry.SetWorkerSchedulingPolicyAsync(worker, WorkerSchedulingPolicy.Enabled);
            using var operatorClient = new HttpClient { BaseAddress = new Uri(url) };
            var inventory = Inventory(worker);
            Assert.Equal(HttpStatusCode.Unauthorized, (await operatorClient.PostAsJsonAsync("/api/v1/maintenance/executions", inventory)).StatusCode);
            operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "maintenance-test-management");
            Assert.Equal(HttpStatusCode.Created, (await operatorClient.PostAsJsonAsync("/api/v1/maintenance/executions", inventory)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await operatorClient.GetAsync("/api/v1/maintenance/executions?limit=101")).StatusCode);
            using var history = new ExecutionHistoryStore(Path.Combine(temporary.Path, "worker.db"));
            var orphan = Entry();
            await history.CreateAsync(orphan);
            var runtime = new WorkerRuntimeReadModel(new(), [], history);
            var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = identityPath };
            using var workerClient = new HttpClient();
            var registration = new WorkerRegistrationClient(workerClient);
            Assert.True(await registration.ExecuteMaintenanceCommandAsync(settings, history, runtime, CancellationToken.None));
            var inspection = await operatorClient.GetStringAsync($"/api/v1/maintenance/executions/{inventory.OperationId}");
            Assert.Contains("orphan-worker-record", inspection);
            Assert.Contains("inventory-observed", inspection);

            var project = await registry.CreateProjectAsync(new("p", "o/r", "main", "", []));
            var execution = await registry.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "1")));
            var response = await registry.RequestAssignmentAsync(new(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 }));
            var assignment = Assert.IsType<WorkAssignment>(response.Assignment);
            var local = Entry() with { ServerExecutionId = execution.Id, AssignmentId = assignment.AssignmentId,
                OwnershipGeneration = assignment.Lease?.Generation, ReportingFailure = "pending" };
            await history.CreateAsync(local);
            await registry.ReportExecutionAsync(execution.Id, new(worker, assignment.AssignmentId, local.ExecutionId.ToString(), "Completed",
                CompletedAtUtc: local.CompletedAtUtc, Generation: assignment.Lease?.Generation ?? 0));
            await registry.SetWorkerSchedulingPolicyAsync(worker, WorkerSchedulingPolicy.Draining);
            var retry = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), worker, execution.Id, local.ExecutionId,
                assignment.AssignmentId, assignment.Lease?.Generation, "retry-report", true);
            Assert.Equal(HttpStatusCode.Conflict, (await operatorClient.PostAsJsonAsync("/api/v1/maintenance/executions", retry with { Generation = 99 })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await operatorClient.PostAsJsonAsync("/api/v1/maintenance/executions", retry)).StatusCode);
            Assert.True(await registration.ExecuteMaintenanceCommandAsync(settings, history, runtime, CancellationToken.None));
            Assert.Equal("acknowledged", await history.ReadServerReportDispositionAsync(local.ExecutionId));
            Assert.Null((await history.ReadExecutionAsync(local.ExecutionId))?.ReportingFailure);
            Assert.Equal("Completed", (await registry.GetExecutionAsync(execution.Id))?.State);
            Assert.Contains("completion-report-acknowledged", await operatorClient.GetStringAsync($"/api/v1/maintenance/executions/{retry.OperationId}"));
            Assert.Equal(HttpStatusCode.Created, (await operatorClient.PostAsJsonAsync("/api/v1/maintenance/executions", retry)).StatusCode);

            var cleanup = retry with { OperationId = Guid.NewGuid().ToString("N"), Action = "cleanup" };
            Assert.Equal(HttpStatusCode.Created, (await operatorClient.PostAsJsonAsync("/api/v1/maintenance/executions", cleanup)).StatusCode);
            // Deauthorization after enqueue fences fresh confirmation and Worker polling.
            await registry.RevokeWorkerTokenAsync(worker);
            await Assert.ThrowsAsync<HttpRequestException>(() => registration.ExecuteMaintenanceCommandAsync(settings, history, runtime, CancellationToken.None));
            Assert.NotNull(await history.ReadExecutionAsync(local.ExecutionId));
            Assert.Equal(HttpStatusCode.Conflict, (await operatorClient.PostAsJsonAsync("/api/v1/maintenance/executions", Inventory(Guid.NewGuid().ToString("N")))).StatusCode);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", prior); }
    }

    [Fact]
    public async Task LocalReservationAndFreshAuthorityPreventCleanupAndCompletionReportEffects()
    {
        using var temporary = new TemporaryDirectory();
        using var history = new ExecutionHistoryStore(Path.Combine(temporary.Path, "history.db"));
        var entry = Entry() with { ServerExecutionId = Guid.NewGuid().ToString("N"), AssignmentId = Guid.NewGuid().ToString("N"), OwnershipGeneration = 1 };
        await history.CreateAsync(entry);
        var configuration = new WorkerConfiguration { Project = new() { Name = "p", Repository = "o/r" } };
        var runtime = new WorkerRuntimeReadModel(new(), [("p.yml", configuration)], history);
        var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), entry.ServerExecutionId,
            entry.ExecutionId, entry.AssignmentId, 1, "cleanup", true);
        var command = new ExecutionMaintenanceCommand(request, "running", DateTimeOffset.UtcNow, "operator", DateTimeOffset.UtcNow.AddMinutes(1));
        var reportCalls = 0;
        var agent = new ManagedExecutionMaintenance(history, runtime);
        Task Retry(ExecutionHistoryEntry _, CancellationToken __) { reportCalls++; return Task.CompletedTask; }
        var refused = await agent.ExecuteAsync(command, _ => Task.FromResult(false), Retry, CancellationToken.None);
        Assert.Equal("server-authority-rejected", refused.Reason);
        Assert.Equal(0, reportCalls);
        using (runtime.Registry.TryBeginServerMaintenance())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ExecuteAsync(command, _ => Task.FromResult(true), Retry, CancellationToken.None));
        }
        var retry = command with { Request = request with { Action = "retry-report" } };
        Assert.Equal("server-authority-rejected", (await agent.ExecuteAsync(retry, _ => Task.FromResult(false), Retry, CancellationToken.None)).Reason);
        await Assert.ThrowsAsync<HttpRequestException>(() => agent.ExecuteAsync(retry, _ => Task.FromResult(true),
            (_, _) => throw new HttpRequestException("lost acknowledgement"), CancellationToken.None));
        Assert.Null(await history.ReadServerReportDispositionAsync(entry.ExecutionId));
    }

    [Fact]
    public async Task RegistryAuthorityRejectsOfflineOutdatedAndExpiredLeaseWithoutInventingLocalAuthority()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var registry = new SqliteRegistryStore(Path.Combine(temporary.Path, "server.db"), timeProvider: clock, leaseDurationSeconds: 60);
        await registry.InitializeAsync();
        var worker = Guid.NewGuid().ToString("N");
        var capabilities = WorkerAuthenticationRequirements.ForRepository("o/r").Select(r =>
            new CodexServer.WorkerCapability(r.Type, r.Name, Scope: r.Scope)).Concat([
                new CodexServer.WorkerCapability("tool", "git"), new CodexServer.WorkerCapability("agent-provider", "codex")]).ToArray();
        await registry.RegisterWorkerAsync(new(2, worker, "worker", "1", "linux", 1, capabilities));
        await registry.HeartbeatWorkerAsync(new(2, worker, "1", "running", 0, 1, capabilities, []));
        var inventory = Inventory(worker);
        Assert.Equal("worker-maintenance-protocol-unavailable", await ServerApplication.MaintenanceAuthorityReasonAsync(inventory, registry, CancellationToken.None));
        capabilities = [.. capabilities, new("protocol", ExecutionMaintenanceProtocol.Capability)];
        await registry.HeartbeatWorkerAsync(new(2, worker, "1", "running", 0, 1, capabilities, []));
        Assert.Null(await ServerApplication.MaintenanceAuthorityReasonAsync(inventory, registry, CancellationToken.None));
        var project = await registry.CreateProjectAsync(new("p", "o/r", "main", "", []));
        var execution = await registry.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "1")));
        var response = await registry.RequestAssignmentAsync(new(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 }));
        var assignment = Assert.IsType<WorkAssignment>(response.Assignment);
        var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), worker, execution.Id,
            Guid.NewGuid(), assignment.AssignmentId, assignment.Lease?.Generation, "retry-report");
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Equal("worker-offline", await ServerApplication.MaintenanceAuthorityReasonAsync(request, registry, CancellationToken.None));
        await registry.ExpireLeasesAsync();
        await registry.HeartbeatWorkerAsync(new(2, worker, "1", "running", 0, 1, capabilities, []));
        Assert.Equal("stale-lease-report-reconciliation-required", await ServerApplication.MaintenanceAuthorityReasonAsync(request, registry, CancellationToken.None));
        Assert.Equal("worker-execution-proof-missing", await ServerApplication.MaintenanceAuthorityReasonAsync(request with { Action = "cleanup" }, registry, CancellationToken.None));
        Assert.NotEqual("Completed", (await registry.GetExecutionAsync(execution.Id))?.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectReportsDurableReceiptWithoutReexecuting(bool hasOutcome)
    {
        using var temporary = new TemporaryDirectory();
        var identity = Path.Combine(temporary.Path, "identity");
        var worker = await WorkerIdentity.LoadOrCreateAsync(identity);
        await WorkerAuthentication.LoadOrCreateTokenAsync(identity);
        var request = Inventory(worker);
        var command = new ExecutionMaintenanceCommand(request, "running", DateTimeOffset.UtcNow, "operator", DateTimeOffset.UtcNow.AddMinutes(1));
        var path = Path.Combine(temporary.Path, "history.db");
        using (var initial = new ExecutionHistoryStore(path))
            await initial.SaveMaintenanceReceiptAsync("http://127.0.0.1:5090", new(command,
                hasOutcome ? new("failed", "partial-cleanup-inspect-before-retry", []) : null), false, CancellationToken.None);
        using var reopened = new ExecutionHistoryStore(path);
        var runtime = new WorkerRuntimeReadModel(new(), [], reopened);
        var reports = 0;
        using var client = new HttpClient(new ReportHandler(async (message, ct) =>
        {
            Assert.EndsWith("/report", message.RequestUri?.AbsolutePath);
            var report = await message.Content!.ReadFromJsonAsync<ExecutionMaintenanceReport>(cancellationToken: ct);
            Assert.Equal(hasOutcome ? "partial-cleanup-inspect-before-retry" : "interrupted-inspect-before-retry", report?.Reason);
            reports++;
            return new(HttpStatusCode.OK);
        }));
        var registration = new WorkerRegistrationClient(client);
        Assert.True(await registration.ExecuteMaintenanceCommandAsync(new() { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = identity },
            reopened, runtime, CancellationToken.None));
        Assert.Equal(1, reports);
        Assert.Null(await reopened.ReadPendingMaintenanceAsync("http://127.0.0.1:5090", CancellationToken.None));
    }

    private sealed class ReportHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrPreviouslyAcknowledgedDispatchCannotRepeatMaintenance(bool acknowledged)
    {
        using var temporary = new TemporaryDirectory();
        var identity = Path.Combine(temporary.Path, "identity");
        var worker = await WorkerIdentity.LoadOrCreateAsync(identity);
        await WorkerAuthentication.LoadOrCreateTokenAsync(identity);
        var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), worker, Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), 1, "cleanup", true);
        var command = new ExecutionMaintenanceCommand(request, "running", DateTimeOffset.UtcNow, "operator",
            acknowledged ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.UtcNow.AddMinutes(-1));
        using var history = new ExecutionHistoryStore(Path.Combine(temporary.Path, "history.db"));
        if (acknowledged)
            await history.SaveMaintenanceReceiptAsync("http://127.0.0.1:5090", new(command, new("succeeded", "already-clean", [])), true, CancellationToken.None);
        var runtime = new WorkerRuntimeReadModel(new(), [], history);
        ExecutionMaintenanceReport? report = null;
        using var client = new HttpClient(new ReportHandler(async (message, ct) =>
        {
            if (message.RequestUri?.AbsolutePath.EndsWith("/request", StringComparison.Ordinal) == true)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(command) };
            Assert.EndsWith("/report", message.RequestUri?.AbsolutePath);
            report = await message.Content!.ReadFromJsonAsync<ExecutionMaintenanceReport>(cancellationToken: ct);
            return new(HttpStatusCode.OK);
        }));
        Assert.True(await new WorkerRegistrationClient(client).ExecuteMaintenanceCommandAsync(new()
            { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = identity }, history, runtime, CancellationToken.None));
        Assert.Equal(acknowledged ? "already-clean" : "timeout-inspect-before-retry", report?.Reason);
        Assert.Empty((await history.ReadAllAsync()));
    }

    private static ExecutionMaintenanceRequest Inventory(string worker) => new(Guid.NewGuid().ToString("N"), worker, null, null, null, null, "inventory");
    private static ExecutionHistoryEntry Entry() => new(Guid.NewGuid(), "p", "o/r", 1, "issue", "feature/1", "main",
        DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow.AddDays(-39), "Completed", null, null, null, 0, [], null, null, null, null, "operator-cleaned");
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"managed-maintenance-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
