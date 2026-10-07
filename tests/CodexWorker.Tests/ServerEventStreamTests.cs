namespace CodexWorker.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[Collection("ServerTokenEnvironment")]
public sealed class ServerEventStreamTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task PopulatedWorkerEventsMatchHttpContractAcrossUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"server-stream-contract-{Guid.NewGuid():N}");
        var previousToken = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "test-management-token");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync(
                [$"--Server:ListenUrl=http://127.0.0.1:{port}", $"--Server:DatabasePath={Path.Combine(directory, "registry.db")}",
                 "--Logging:LogLevel:Default=Warning"]);
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var workerId = Guid.NewGuid().ToString("N");
            WorkerCapability[] capabilities = [new("tool", "git", "2.0", "node")];
            await store.RegisterWorkerAsync(new(2, workerId, "populated worker", "1.0", "test", 4, capabilities));
            await store.HeartbeatWorkerAsync(new(2, workerId, "1.0", "running", 1, 4, capabilities, ["project-a"]));
            await app.StartAsync();
            using var handler = new SocketsHttpHandler { MaxResponseDrainSize = 0 };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-management-token");
            for (var update = 0; update < 2; update++)
            {
                using var response = await client.GetAsync("/api/v1/events/stream", HttpCompletionOption.ResponseHeadersRead)
                    .WaitAsync(Deadline);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                Assert.Equal("event: workers", await reader.ReadLineAsync().WaitAsync(Deadline));
                var data = await reader.ReadLineAsync().WaitAsync(Deadline);
                Assert.NotNull(data);
                Assert.StartsWith("data: ", data);
                Assert.Equal(string.Empty, await reader.ReadLineAsync().WaitAsync(Deadline));
                using var streamed = JsonDocument.Parse(data[6..]);
                using var http = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.True(JsonElement.DeepEquals(http.RootElement, streamed.RootElement));
                var worker = streamed.RootElement[0];
                Assert.Equal(workerId, worker.GetProperty("workerId").GetString());
                Assert.Equal("Enabled", worker.GetProperty("schedulingPolicy").GetString());
                Assert.Equal("online", worker.GetProperty("availability").GetString());
                Assert.Equal(4, worker.GetProperty("capacity").GetInt32());
                Assert.Equal(4, worker.GetProperty("maximumCapacity").GetInt32());
                Assert.Equal(1, worker.GetProperty("activeExecutions").GetInt32());
                Assert.Equal(3, worker.GetProperty("availableCapacity").GetInt32());
                Assert.Equal("git", worker.GetProperty("capabilities")[0].GetProperty("name").GetString());
                Assert.Equal("node", worker.GetProperty("capabilities")[0].GetProperty("scope").GetString());
                if (update == 0)
                    await store.HeartbeatWorkerAsync(new(2, workerId, "1.1", "running", 1, 4, capabilities, ["project-a"]));
            }

            await app.StopAsync().WaitAsync(Deadline);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previousToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LiveStreamsCompleteOnClientDisconnectAndHostStop()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"server-stream-test-{Guid.NewGuid():N}");
        var previousToken = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "test-management-token");
        var responses = new List<HttpResponseMessage>();
        var readers = new List<StreamReader>();
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync(
                [$"--Server:ListenUrl=http://127.0.0.1:{port}", $"--Server:DatabasePath={Path.Combine(directory, "registry.db")}",
                 "--Logging:LogLevel:Default=Warning"]);
            var completed = new ConcurrentDictionary<string, TaskCompletionSource>();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path != "/api/v1/events/stream")
                {
                    await next(context);
                    return;
                }
                var completion = completed.GetOrAdd(context.Request.Query["id"].ToString(),
                    _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                try { await next(context); }
                finally { completion.TrySetResult(); }
            });
            await app.StartAsync();
            // Closing a response must disconnect immediately, without HttpClient's
            // bounded attempt to drain an infinite response for connection reuse.
            using var handler = new SocketsHttpHandler { MaxResponseDrainSize = 0 };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            using (var unauthorized = await client.GetAsync("/api/v1/events/stream"))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-management-token");

            async Task<StreamReader> OpenAsync(string id)
            {
                var response = await client.GetAsync($"/api/v1/events/stream?id={id}", HttpCompletionOption.ResponseHeadersRead)
                    .WaitAsync(Deadline);
                responses.Add(response);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
                var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                readers.Add(reader);
                Assert.Equal("event: workers", await reader.ReadLineAsync().WaitAsync(Deadline));
                Assert.Equal("data: []", await reader.ReadLineAsync().WaitAsync(Deadline));
                Assert.Equal(string.Empty, await reader.ReadLineAsync().WaitAsync(Deadline));
                Assert.False(completed[id].Task.IsCompleted);
                return reader;
            }

            // Reading the complete first event coordinates with the response flush.
            var disconnected = await OpenAsync("disconnect");
            disconnected.Dispose();
            responses[^1].Dispose();
            await completed["disconnect"].Task.WaitAsync(Deadline);
            Assert.False(app.Lifetime.ApplicationStopping.IsCancellationRequested);
            using (var ready = await client.GetAsync("/readyz"))
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            // Open all seven requests before stopping, and keep their clients connected.
            // Each request is independently live while the preceding ones remain open.
            var active = new List<StreamReader>();
            for (var index = 0; index < 7; index++) active.Add(await OpenAsync(index.ToString()));
            var drained = active.Select(reader => reader.ReadToEndAsync()).ToArray();
            await app.StopAsync().WaitAsync(Deadline);
            await Task.WhenAll(drained).WaitAsync(Deadline);
            await Task.WhenAll(Enumerable.Range(0, 7).Select(index => completed[index.ToString()].Task)).WaitAsync(Deadline);
            Assert.All(app.Services.GetServices<IHostedService>().OfType<BackgroundService>(),
                service => Assert.True(service.ExecuteTask?.IsCompleted));
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
            foreach (var response in responses) response.Dispose();
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previousToken);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("read", false)]
    [InlineData("write", false)]
    [InlineData("flush", false)]
    [InlineData("read", true)]
    [InlineData("write", true)]
    [InlineData("flush", true)]
    public async Task CancellationReachesBlockedStreamOperations(string operation, bool clientDisconnect)
    {
        using var stopping = new CancellationTokenSource();
        using var disconnected = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = DispatchProxy.Create<IRegistryStore, StreamRegistryProxy>();
        var proxy = (StreamRegistryProxy)store;
        proxy.Read = async token =>
        {
            if (operation == "read") await BlockAsync(token, entered);
            return Array.Empty<WorkerRegistrationResponse>();
        };
        await using var body = new BlockingResponseStream(operation, entered);
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestAborted = disconnected.Token, RequestServices = services };
        context.Response.Body = body;
        var streaming = ServerApplication.StreamWorkerUpdatesAsync(context, store, stopping.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(streaming.IsCompleted);
            if (clientDisconnect) disconnected.Cancel();
            else stopping.Cancel();
            // Expected cancellation completes normally, including inside an awaited operation.
            await streaming.WaitAsync(Deadline);
            Assert.True(streaming.IsCompletedSuccessfully);
        }
        finally
        {
            disconnected.Cancel();
            stopping.Cancel();
        }
    }

    private static async Task BlockAsync(CancellationToken token, TaskCompletionSource entered)
    {
        Assert.True(token.CanBeCanceled);
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    public class StreamRegistryProxy : DispatchProxy
    {
        public Func<CancellationToken, Task<IReadOnlyList<WorkerRegistrationResponse>>>? Read { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRegistryStore.GetWorkersAsync) &&
                args is [CancellationToken token] && Read is { } read)
                return read(token);
            throw new InvalidOperationException("Unexpected registry operation in stream test.");
        }
    }

    private sealed class BlockingResponseStream(string operation, TaskCompletionSource entered) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (operation == "write") await BlockAsync(cancellationToken, entered);
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (operation == "write") await BlockAsync(cancellationToken, entered);
            await base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (operation == "flush") await BlockAsync(cancellationToken, entered);
            await base.FlushAsync(cancellationToken);
        }
    }
}
