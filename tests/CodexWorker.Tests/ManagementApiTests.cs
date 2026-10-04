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
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = "/safe/repo" },
            Worker = new WorkerSettings { MaxParallelTasks = 2 },
            Environment = new ProjectEnvironmentSettings { Variables = new Dictionary<string, string> { ["TOKEN"] = "secret-value" } }
        };
        var model = new WorkerRuntimeReadModel(configuration, [("/safe/project.yml", project)], history.Store);
        model.State = "running";
        var executionId = Guid.NewGuid();
        await history.Store.CreateAsync(new ExecutionHistoryEntry(executionId, "sample", "owner/repo", 4, "Example issue",
            "feature/4", "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1200, null, "failed", 1,
            [new ValidationRepairRecord("dotnet test", 1, 2, "private repair output", false)], null, null, null, "failed",
            "recoverable", "base-sha", "2 changed path(s); 0 staged path(s). Workspace retained for recovery.", Guid.NewGuid(), 2, true,
            DateTimeOffset.UtcNow.AddDays(7), EffectiveModel: "task-model", EffectiveEffort: "low"));
        model.Events.Publish("one", "one");
        model.Events.Publish("two", "two");
        model.Events.Publish("three", "three");

        var port = GetFreePort();
        var app = await ManagementApi.StartAsync(model, new ManagementApiSettings { ListenUrl = $"http://127.0.0.1:{port}", EventHistoryLimit = 2 }, CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var dashboard = await client.GetStringAsync("/");
            Assert.Contains("Codex Worker", dashboard);
            Assert.Contains("/api/status", dashboard);
            Assert.Contains("/api/events/stream", dashboard);
            Assert.DoesNotContain("secret-value", dashboard);
            var status = JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement;
            Assert.Equal(ApplicationVersion.Display, status.GetProperty("version").GetString());
            Assert.Equal(3, status.GetProperty("maxParallelTasks").GetInt32());
            Assert.Equal(0, status.GetProperty("activeExecutionCount").GetInt32());
            Assert.Equal(3, status.GetProperty("availableExecutionCapacity").GetInt32());
            Assert.Equal("running", status.GetProperty("state").GetString());
            Assert.Equal("starting", status.GetProperty("lifecycleState").GetString());

            var projects = JsonDocument.Parse(await client.GetStringAsync("/api/projects")).RootElement;
            Assert.Equal("/safe/project.yml", projects[0].GetProperty("configurationPath").GetString());
            Assert.Equal("/safe/repo", projects[0].GetProperty("projectDirectory").GetString());
            Assert.Equal("owner/repo", projects[0].GetProperty("repository").GetString());
            Assert.Equal(0, projects[0].GetProperty("activeExecutionCount").GetInt32());
            Assert.Equal(2, projects[0].GetProperty("maxParallelTasks").GetInt32());
            var executions = JsonDocument.Parse(await client.GetStringAsync("/api/executions")).RootElement;
            Assert.Equal(executionId.ToString(), executions[0].GetProperty("executionId").GetString());
            Assert.Equal("Failed", executions[0].GetProperty("state").GetString());
            Assert.Equal("task-model", executions[0].GetProperty("effectiveModel").GetString());
            Assert.Equal("low", executions[0].GetProperty("effectiveEffort").GetString());
            Assert.Equal("recoverable", executions[0].GetProperty("recoveryState").GetString());
            Assert.Equal("base-sha", executions[0].GetProperty("recoveryBaseCommit").GetString());
            Assert.Contains("Workspace retained", executions[0].GetProperty("recoveryStatus").GetString());
            Assert.True(executions[0].GetProperty("recoveryExpiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);
            Assert.Equal(2, executions[0].GetProperty("attemptNumber").GetInt32());
            Assert.True(executions[0].GetProperty("resumed").GetBoolean());
            Assert.NotEqual(Guid.Empty.ToString(), executions[0].GetProperty("retryOfExecutionId").GetString());
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
            Assert.Equal("drain-requested", workerDrain.GetProperty("state").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/worker/drain/cancel", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            workerDrain = JsonDocument.Parse(await client.GetStringAsync("/api/worker/drain")).RootElement;
            Assert.False(workerDrain.GetProperty("draining").GetBoolean());
            Assert.Equal("ready", workerDrain.GetProperty("state").GetString());
        }
        finally
        {
            if (app is not null) await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExactAndIssueLookupsIncludeOldAttemptsOutsideBoundedRecentList()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var history = new ExecutionHistoryStore(Path.Combine(directory, "history.db"));
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 42, "Old issue",
            "feature/42", "main", start, start.AddMinutes(1), "Failed", 60000, "private task output", "failed", 1,
            [new ValidationRepairRecord("test", 1, 1, "private repair output", false)], "commit", "main", "completed/42",
            "private process output", "recoverable", "base", "Workspace retained", RecoveryExpiresAtUtc: start.AddDays(7),
            ReportingFailure: "token=private-token reporting failed\nprivate process output", OriginalIssueBody: "private issue body");
        var second = first with
        {
            ExecutionId = Guid.NewGuid(), StartedAtUtc = start.AddMinutes(2), CompletedAtUtc = null,
            State = "Validating", DurationMilliseconds = null, AttemptNumber = 2, RetryOfExecutionId = first.ExecutionId,
            Resumed = true, RecoveryState = null, RecoveryExpiresAtUtc = null
        };
        await history.CreateAsync(first);
        await history.CreateAsync(second);
        var otherRepository = first with { ExecutionId = Guid.NewGuid(), Project = "other", Repository = "owner/other" };
        await history.CreateAsync(otherRepository);
        for (var index = 0; index < 501; index++)
            await history.CreateAsync(first with { ExecutionId = Guid.NewGuid(), IssueNumber = 1000 + index, StartedAtUtc = start.AddDays(1).AddMinutes(index) });
        var model = new WorkerRuntimeReadModel(new GlobalWorkerConfiguration(), [], history);
        var port = GetFreePort();
        var app = await ManagementApi.StartAsync(model, new ManagementApiSettings { ListenUrl = $"http://127.0.0.1:{port}" }, CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var recent = await model.ExecutionsAsync(int.MaxValue, CancellationToken.None);
            Assert.Equal(500, recent.Count);
            Assert.DoesNotContain(recent, entry => entry.ExecutionId == first.ExecutionId || entry.ExecutionId == second.ExecutionId);
            Assert.Single(await model.ExecutionsAsync(0, CancellationToken.None));
            var defaultList = JsonDocument.Parse(await client.GetStringAsync("/api/executions")).RootElement;
            Assert.Equal(100, defaultList.GetArrayLength());
            var exactJson = await client.GetStringAsync($"/api/executions/{first.ExecutionId}");
            var exact = JsonDocument.Parse(exactJson).RootElement;
            Assert.Equal(first.ExecutionId.ToString(), exact.GetProperty("executionId").GetString());
            Assert.Equal("failed", exact.GetProperty("result").GetString());
            Assert.Equal("feature/42", exact.GetProperty("featureBranch").GetString());
            Assert.Equal("completed/42", exact.GetProperty("completedBranch").GetString());
            Assert.Equal("commit", exact.GetProperty("commitSha").GetString());
            Assert.Equal("main", exact.GetProperty("integrationBranch").GetString());
            Assert.Equal("recoverable", exact.GetProperty("recoveryState").GetString());
            Assert.Equal(start.AddDays(7), exact.GetProperty("recoveryExpiresAtUtc").GetDateTimeOffset());
            Assert.Null(exact.GetProperty("failureReason").GetString());
            Assert.Contains("[redacted]", exact.GetProperty("reportingFailure").GetString());
            Assert.DoesNotContain("private", exactJson);
            var attempts = JsonDocument.Parse(await client.GetStringAsync("/api/executions/issue/42")).RootElement;
            Assert.Equal(3, attempts.GetArrayLength());
            Assert.Equal("owner/other", attempts[0].GetProperty("repository").GetString());
            var matchingAttempts = attempts.EnumerateArray().Where(entry =>
                entry.GetProperty("repository").GetString() == "owner/repo").ToArray();
            Assert.Equal(first.ExecutionId.ToString(), matchingAttempts[0].GetProperty("executionId").GetString());
            Assert.Equal(first.ExecutionId.ToString(), matchingAttempts[1].GetProperty("retryOfExecutionId").GetString());
            Assert.Equal(2, matchingAttempts[1].GetProperty("attemptNumber").GetInt32());
            Assert.True(matchingAttempts[1].GetProperty("resumed").GetBoolean());
            Assert.Equal("Validating", matchingAttempts[1].GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, matchingAttempts[1].GetProperty("result").ValueKind);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/executions/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/executions/issue/999")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/executions/issue/0")).StatusCode);
        }
        finally
        {
            if (app is not null) await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("InfrastructureFailure", "infrastructure-failure")]
    [InlineData("Cancelled", "cancelled")]
    [InlineData("IntegrationConflict", "integration-conflict")]
    public async Task OperationalDiagnosticsAreBoundedAndRedacted(string state, string result)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var history = new ExecutionHistoryStore(Path.Combine(directory, "history.db"));
            var project = new WorkerConfiguration
            {
                Project = new ProjectSettings { Name = "sample", Repository = "owner/repo" },
                Environment = new ProjectEnvironmentSettings { Variables = new Dictionary<string, string> { ["PRIVATE"] = "private-value" } }
            };
            var id = Guid.NewGuid();
            var diagnostic = "private-value token=private-token " + new string('x', 2000) + "\nraw process output";
            await history.CreateAsync(new ExecutionHistoryEntry(id, "sample", "owner/repo", 1, "Issue",
                "feature/1", "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, state, 1000, null, null, 0, [],
                null, null, null, diagnostic, ReportingFailure: diagnostic));
            var model = new WorkerRuntimeReadModel(new GlobalWorkerConfiguration(), [("project.yml", project)], history);
            var entry = await model.ExecutionAsync(id, CancellationToken.None);
            Assert.NotNull(entry);
            Assert.Equal(result, entry.Result);
            Assert.NotNull(entry.ReportingFailure);
            Assert.True(entry.ReportingFailure.Length <= 1001);
            Assert.DoesNotContain("private-value", entry.ReportingFailure);
            Assert.DoesNotContain("private-token", entry.ReportingFailure);
            Assert.DoesNotContain("raw process output", entry.ReportingFailure);
            if (state == "IntegrationConflict") Assert.Null(entry.FailureReason);
            else Assert.Equal(entry.ReportingFailure, entry.FailureReason);
        }
        finally { Directory.Delete(directory, recursive: true); }
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
