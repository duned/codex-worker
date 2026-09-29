namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

public sealed class CodexServerTests
{
    [Fact]
    public async Task RegistrationIsAuthenticatedIdempotentAndDurableAcrossServerRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var port = ReservePort();
        var url = $"http://127.0.0.1:{port}";
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "test-registration-token");
        try
        {
            var app = await ServerApplication.BuildAsync(Args(url, database));
            await app.StartAsync();
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                var workerId = Guid.NewGuid().ToString("N");
                var request = new { contractVersion = 1, workerId, displayName = "test worker", workerVersion = "1.2.3",
                    platform = "test", capacity = 2, capabilities = new[] { "git", "codex-cli" } };
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-registration-token");
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                request = new { contractVersion = 1, workerId, displayName = "renamed worker", workerVersion = "1.2.4",
                    platform = "test", capacity = 3, capabilities = new[] { "git", "updated" } };
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                using var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Single(list.RootElement.EnumerateArray());
                var item = list.RootElement[0];
                Assert.Equal("renamed worker", item.GetProperty("displayName").GetString());
                Assert.Equal(3, item.GetProperty("capacity").GetInt32());
                Assert.True(item.TryGetProperty("firstRegisteredAtUtc", out _));
                Assert.True(item.TryGetProperty("lastSeenAtUtc", out _));
                Assert.DoesNotContain("token", item.ToString(), StringComparison.OrdinalIgnoreCase);
                var otherId = Guid.NewGuid().ToString("N");
                var other = new { contractVersion = 1, workerId = otherId, displayName = "second", workerVersion = "1.2.3",
                    platform = "test", capacity = 1, capabilities = new[] { "git" } };
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{otherId}", other)).StatusCode);
                using var twoWorkers = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Equal(2, twoWorkers.RootElement.GetArrayLength());
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/workers/{Guid.NewGuid():N}", request)).StatusCode);
            }
            await app.StopAsync();
            await app.DisposeAsync();
            var restarted = await ServerApplication.BuildAsync(Args(url, database));
            await restarted.StartAsync();
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            using (var persisted = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers")))
            {
                Assert.Equal(2, persisted.RootElement.GetArrayLength());
                Assert.Contains(persisted.RootElement.EnumerateArray(), worker => worker.GetProperty("displayName").GetString() == "renamed worker");
            }
            await restarted.StopAsync();
            await restarted.DisposeAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", prior); }
    }

    [Fact]
    public async Task ServerStartsAndExposesStatusHealthAndVersion()
    {
        using var temporary = new TemporaryDirectory();
        var port = ReservePort();
        var url = $"http://127.0.0.1:{port}";
        await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "server.db")));
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            using var statusResponse = await client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal("ready", status.RootElement.GetProperty("state").GetString());
            Assert.Equal(ServerApplication.DisplayVersion, status.RootElement.GetProperty("version").GetString());
            Assert.True(status.RootElement.TryGetProperty("startedAtUtc", out _));

            using var versionResponse = await client.GetAsync("/api/version");
            using var version = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
            Assert.Equal(ServerApplication.DisplayVersion, version.RootElement.GetProperty("version").GetString());
            Assert.Equal("Codex Server", version.RootElement.GetProperty("product").GetString());

            using var healthResponse = await client.GetAsync("/health");
            using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            Assert.Equal("healthy", health.RootElement.GetProperty("status").GetString());
            Assert.True(health.RootElement.GetProperty("persistenceAvailable").GetBoolean());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task PersistenceSchemaSurvivesRepeatedInitializationAndRestart()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "nested", "server.db");
        var firstStore = new SqliteRegistryStore(databasePath);
        await firstStore.InitializeAsync();
        Assert.True(await firstStore.IsAvailableAsync());
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO workers (worker_id, display_name, registered_at_utc, status_json) VALUES ('worker-1', 'test worker', '2026-01-01T00:00:00Z', NULL);";
            await command.ExecuteNonQueryAsync();
        }
        var secondStore = new SqliteRegistryStore(databasePath);
        await secondStore.InitializeAsync();
        Assert.True(await secondStore.IsAvailableAsync());
        Assert.True(File.Exists(databasePath));
        await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM workers WHERE worker_id = 'worker-1';";
        Assert.Equal(1L, (long)(await verifyCommand.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData("file:///tmp/server")]
    [InlineData("http://user:pass@127.0.0.1:5090")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://0.0.0.0:5090")]
    public async Task InvalidListenConfigurationIsRejected(string listenUrl)
    {
        using var temporary = new TemporaryDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(Args(listenUrl, Path.Combine(temporary.Path, "server.db"))));
    }

    [Fact]
    public async Task EmptyPersistenceLocationIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(["--Server:ListenUrl=http://127.0.0.1:5090", "--Server:DatabasePath="]));
    }

    private static string[] Args(string url, string databasePath) =>
        [$"--Server:ListenUrl={url}", $"--Server:DatabasePath={databasePath}"];

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-server-test-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
