namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

[Collection("ServerTokenEnvironment")]

public sealed class WorkerJournalTests
{
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"worker-journal-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(200, "")]
    [InlineData(200, "invalid-json")]
    [InlineData(503, "")]
    [InlineData(204, "")]
    public async Task JournalPollingFailureDoesNotInvalidateHeartbeat(int status, string body)
    {
        using var temporary = new TemporaryDirectory();
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(temporary.Path, "worker-id") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile, CancellationToken.None);
        using var handler = new JournalHandler((HttpStatusCode)status, body);
        using var client = new HttpClient(handler);
        await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).HeartbeatAsync(
            settings, 1, 0, [], "idle", CancellationToken.None, []);
        Assert.True(handler.JournalRequested);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task JournalPollingPreservesShutdownCancellation()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(temporary.Path, "worker-id") };
        await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
        await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        using var handler = new JournalHandler(HttpStatusCode.OK, "", cancellation);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).HeartbeatAsync(
                settings, 1, 0, [], "idle", cancellation.Token, []));
        Assert.True(handler.JournalRequested);
    }

    private sealed class JournalHandler(HttpStatusCode status, string body, CancellationTokenSource? cancellation = null) : HttpMessageHandler
    {
        public bool JournalRequested { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal) == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            JournalRequested = true;
            if (cancellation is not null)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task EndpointRequiresManagementAuthorizationAndBoundsRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"worker-journal-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "journal-test-management");
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync(
                [$"--Server:ListenUrl=http://127.0.0.1:{port}", $"--Server:DatabasePath={Path.Combine(directory, "registry.db")}", "--Logging:LogLevel:Default=Warning"]);
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var worker = Guid.NewGuid().ToString("N");
            await store.RegisterWorkerAsync(new(2, worker, "journal worker", "1.0", "test", 1, []));
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            using var unauthorized = await client.GetAsync($"/api/v1/workers/{worker}/journal");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "journal-test-management");
            using var invalid = await client.GetAsync($"/api/v1/workers/{worker}/journal?lines=201");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var workerOnly = await client.GetAsync($"/api/v1/workers/{worker}/journal/request");
            Assert.Equal(HttpStatusCode.Unauthorized, workerOnly.StatusCode);
            await app.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void RequestsAreScopedBoundedAndRedacted()
    {
        var requests = new WorkerJournalRequests();
        Assert.Equal("pending", requests.Read("worker-a", 100).State);
        Assert.Null(requests.Pending("worker-b"));
        var request = Assert.IsType<WorkerJournalRequest>(requests.Pending("worker-a"));
        Assert.Equal(100, request.Lines);
        Assert.Equal("pending", requests.Read("worker-a", 100, refresh: true).State);
        Assert.Equal(request.Id, requests.Pending("worker-a")?.Id);
        Assert.False(requests.Complete("worker-b", request.Id, new("ready", [])));
        Assert.False(requests.Complete("worker-a", Guid.NewGuid(), new("ready", [])));
        Assert.False(requests.Complete("worker-a", request.Id, new("ready", Enumerable.Repeat("line", 101).ToArray())));
        Assert.True(requests.Complete("worker-a", request.Id, new("ready", ["2026-01-01 first", "2026-01-02 token=sensitive"])));
        var result = requests.Read("worker-a", 100);
        Assert.Equal("ready", result.State);
        Assert.Equal("2026-01-01 first", result.Lines[0]);
        Assert.DoesNotContain("sensitive", result.Lines[1]);
        Assert.Null(requests.Pending("worker-a"));

        Assert.Equal("pending", requests.Read("worker-a", 100, refresh: true).State);
        var refreshed = Assert.IsType<WorkerJournalRequest>(requests.Pending("worker-a"));
        Assert.NotEqual(request.Id, refreshed.Id);
        Assert.True(requests.Complete("worker-a", refreshed.Id, new("ready", ["2026-01-03 latest"])));
        Assert.Equal("2026-01-03 latest", Assert.Single(requests.Read("worker-a", 100).Lines));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ResultsRejectUnknownStatesAndOversizedPayloads()
    {
        Assert.False(WorkerJournalProtocol.Valid(new("unknown", [])));
        Assert.False(WorkerJournalProtocol.Valid(new("ready", [new string('x', 1001)])));
        Assert.False(WorkerJournalProtocol.Valid(new("ready", Enumerable.Repeat(new string('x', 1000), 101).ToArray())));
        Assert.True(WorkerJournalProtocol.Valid(new("unsupported", [])));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task ReaderRejectsUnboundedRequests(int lines)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WorkerJournalReader.ReadAsync(lines, CancellationToken.None));
    }
}
