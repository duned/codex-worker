using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CodexWorker;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CodexWorker.Tests;

public sealed class ManagementApiTests
{
    [Fact]
    public async Task ApiServesSafeRuntimeSnapshotsAndBoundedEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var history = new AsyncDisposer(new ExecutionHistoryStore(Path.Combine(directory, "history.db")));
        var configuration = new GlobalWorkerConfiguration
        {
            Worker = new GlobalWorkerSettings { MaxParallelTasks = 3 },
            Api = new ManagementApiSettings { EventHistoryLimit = 2 }
        };
        var project = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo" },
            Worker = new WorkerSettings { MaxParallelTasks = 2 },
            Environment = new ProjectEnvironmentSettings { Variables = new Dictionary<string, string> { ["TOKEN"] = "secret-value" } }
        };
        var model = new WorkerRuntimeReadModel(configuration, [("project.yml", project)], history.Store);
        model.State = "running";
        var executionId = Guid.NewGuid();
        await history.Store.CreateAsync(new ExecutionHistoryEntry(executionId, "sample", "owner/repo", 4, "Example issue",
            "feature/4", "main", DateTimeOffset.UtcNow, null, "Implementing", null, null, null, 1,
            [new ValidationRepairRecord("dotnet test", 1, 2, "private repair output", false)], null, null, null, null));
        model.Events.Publish("one", "one");
        model.Events.Publish("two", "two");
        model.Events.Publish("three", "three");

        var port = GetFreePort();
        var app = await ManagementApi.StartAsync(model, new ManagementApiSettings { ListenUrl = $"http://127.0.0.1:{port}", EventHistoryLimit = 2 }, CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var status = JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement;
            Assert.Equal(ApplicationVersion.Display, status.GetProperty("version").GetString());
            Assert.Equal(3, status.GetProperty("maxParallelTasks").GetInt32());
            Assert.Equal(1, status.GetProperty("activeExecutionCount").GetInt32());
            Assert.Equal(2, status.GetProperty("availableExecutionCapacity").GetInt32());
            Assert.Equal("running", status.GetProperty("state").GetString());

            var projects = JsonDocument.Parse(await client.GetStringAsync("/api/projects")).RootElement;
            Assert.Equal("owner/repo", projects[0].GetProperty("repository").GetString());
            Assert.Equal(1, projects[0].GetProperty("activeExecutionCount").GetInt32());
            Assert.Equal(2, projects[0].GetProperty("maxParallelTasks").GetInt32());
            var executions = JsonDocument.Parse(await client.GetStringAsync("/api/executions")).RootElement;
            Assert.Equal(executionId.ToString(), executions[0].GetProperty("executionId").GetString());
            Assert.Equal("Implementing", executions[0].GetProperty("state").GetString());
            Assert.DoesNotContain("private repair output", executions.GetRawText());
            Assert.DoesNotContain("secret-value", status.GetRawText() + projects.GetRawText() + executions.GetRawText());

            var capabilities = await client.GetStringAsync("/api/capabilities");
            Assert.Contains("codex-cli", capabilities);
            var events = JsonDocument.Parse(await client.GetStringAsync("/api/events")).RootElement;
            Assert.Equal(2, events.GetArrayLength());
            Assert.Equal("two", events[0].GetProperty("message").GetString());

            using var streamCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var streamResponse = await client.GetAsync("/api/events/stream", HttpCompletionOption.ResponseHeadersRead, streamCancellation.Token);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            await using var eventStream = await streamResponse.Content.ReadAsStreamAsync(streamCancellation.Token);
            await Task.Delay(50, streamCancellation.Token);
            model.Events.Publish("live", "new event");
            using var reader = new StreamReader(eventStream);
            var frame = new List<string>();
            while (true)
            {
                var line = await reader.ReadLineAsync(streamCancellation.Token);
                if (line is null || line.Length == 0) break;
                frame.Add(line);
            }
            Assert.Contains(frame, line => line == "event: live");
            Assert.Contains(frame, line => line.Contains("new event", StringComparison.Ordinal));

            var concurrentReads = Enumerable.Range(0, 12).Select(_ => Task.WhenAll(
                client.GetStringAsync("/api/status"), client.GetStringAsync("/api/projects"),
                client.GetStringAsync("/api/executions")));
            await Task.WhenAll(concurrentReads);

            var disable = new StringContent("{\"action\":\"disable\"}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/projects/sample/lifecycle", disable)).StatusCode);
            var disabled = JsonDocument.Parse(await client.GetStringAsync("/api/projects")).RootElement[0];
            Assert.False(disabled.GetProperty("enabled").GetBoolean());
            Assert.Equal("Disabled", disabled.GetProperty("state").GetString());
            var drain = new StringContent("{\"action\":\"drain\"}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/projects/sample/lifecycle", drain)).StatusCode);
            var draining = JsonDocument.Parse(await client.GetStringAsync("/api/projects")).RootElement[0];
            Assert.Equal("Draining", draining.GetProperty("state").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/worker/drain", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            var workerDrain = JsonDocument.Parse(await client.GetStringAsync("/api/worker/drain")).RootElement;
            Assert.True(workerDrain.GetProperty("draining").GetBoolean());
        }
        finally
        {
            if (app is not null) await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ApiDefaultsToLoopbackAndRejectsPublicBindings()
    {
        Assert.Equal("http://127.0.0.1:5080", new ManagementApiSettings().ListenUrl);
        var configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".yml");
        try
        {
            File.WriteAllText(configPath, "worker: {}\nprojects:\n  directory: ./projects\napi:\n  listenUrl: http://0.0.0.0:5080\n");
            var error = Assert.Throws<InvalidDataException>(() => GlobalWorkerConfiguration.Load(configPath));
            Assert.Contains("loopback", error.Message);
        }
        finally { File.Delete(configPath); }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class AsyncDisposer(ExecutionHistoryStore store) : IAsyncDisposable
    {
        public ExecutionHistoryStore Store { get; } = store;
        public ValueTask DisposeAsync() { Store.Dispose(); return ValueTask.CompletedTask; }
    }
}
