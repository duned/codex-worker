namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

[Collection("ServerTokenEnvironment")]
public sealed class TelegramDeliveryTests
{
    private const string BotToken = "123456:abcdefghijklmnopqrstuvwxyz_123456789";

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("-100123456", true)]
    [InlineData("@test_channel", true)]
    [InlineData("-100123456", false)]
    public async Task ManagedSaveSynchronizeAcknowledgeAndExplicitDelivery(string chat, bool accepted)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "registry.db");
            var registry = new SqliteRegistryStore(database);
            await registry.InitializeAsync();
            var credentials = new SqliteCredentialStore(database,
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await credentials.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");
            await registry.RegisterWorkerAsync(new(2, id, "Worker", "1.0", "test", 1, []));
            var original = Assert.IsType<WorkerRegistrationResponse>(await registry.GetWorkerAsync(id));
            var saved = Assert.IsType<WorkerRegistrationResponse>(await WorkerTelegramSettings.UpdateAsync(id,
                new("Worker", 1, 1, Telegram: new(true, chat, new(BotToken))), original, registry, credentials, CancellationToken.None));
            var settings = Assert.IsType<WorkerTelegramConfiguration>(saved.Telegram);
            var version = ServerApplication.ManagedConfigurationVersion([], telegram: settings);
            Assert.False(ServerApplication.TelegramTestReady(saved, version));
            using var handler = new FakeTelegram(accepted);
            using var client = new HttpClient(handler);
            using var notifier = new TelegramNotifier(false, null, null, client);
            var synchronizer = new ManagedConfigurationSynchronizer(Path.Combine(directory, "snapshot.json"), new());
            await ManagedTelegramConfiguration.SynchronizeAsync(synchronizer, notifier, new(true, "local", "local"),
                _ => Task.FromResult(new ServerManagedConfigurationContract(4, version, [], Telegram: settings)),
                async (credentialId, ct) => new CredentialDeliveryResponse(credentialId, "telegram", "bot-token", 1,
                    await credentials.RetrieveForWorkerAsync(credentialId, id, ct) ?? throw new InvalidDataException()), CancellationToken.None);
            Assert.Empty(handler.Chats);
            Assert.Equal(chat, synchronizer.AppliedTelegram?.ChatId);
            Assert.Equal("synchronized", synchronizer.Status.SynchronizationStatus);
            var acknowledged = saved with { Availability = "online", ConfigurationSynchronization = synchronizer.Status.SynchronizationStatus,
                ConfigurationVersion = synchronizer.Status.AppliedVersion };
            Assert.True(ServerApplication.TelegramTestReady(acknowledged, version));
            Assert.False(ServerApplication.TelegramTestReady(acknowledged with { Availability = "offline" }, version));
            Assert.False(ServerApplication.TelegramTestReady(acknowledged with { Availability = "stale" }, version));
            Assert.False(ServerApplication.TelegramTestReady(acknowledged, "new-revision"));
            var tests = new TelegramTestStore(database, TimeProvider.System);
            await tests.InitializeAsync();
            var queued = Assert.IsType<TelegramTestOperation>(await tests.CreateAsync(id, version, CancellationToken.None));
            Assert.Equal("pending", queued.Status);
            var running = Assert.IsType<TelegramTestOperation>(await tests.ClaimAsync(id, CancellationToken.None));
            Assert.Null(await tests.ClaimAsync(id, CancellationToken.None));
            var diagnostic = await notifier.TestAsync(CancellationToken.None);
            Assert.Equal(accepted ? "accepted" : "telegram-rejected", diagnostic);
            Assert.Equal(chat, Assert.Single(handler.Chats));
            var completed = Assert.IsType<TelegramTestOperation>(await tests.ReportAsync(id, new(running.Id, diagnostic), CancellationToken.None));
            Assert.Equal(accepted ? "succeeded" : "failed", completed.Status);
            Assert.DoesNotContain(BotToken, JsonSerializer.Serialize(completed), StringComparison.Ordinal);
            Assert.Null(await tests.ClaimAsync(id, CancellationToken.None));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ExpiredTestsAreNotReplayedAndReportsAreSanitized()
    {
        var database = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var clock = new TestClock();
            var tests = new TelegramTestStore(database, clock);
            await tests.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");
            await tests.CreateAsync(id, "version", CancellationToken.None);
            var running = Assert.IsType<TelegramTestOperation>(await tests.ClaimAsync(id, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => tests.ReportAsync(id, new(running.Id, BotToken), CancellationToken.None));
            clock.Now = clock.Now.AddMinutes(2);
            Assert.Null(await tests.ClaimAsync(id, CancellationToken.None));
            var expired = Assert.IsType<TelegramTestOperation>(await tests.GetAsync(id, CancellationToken.None));
            Assert.Equal("failed", expired.Status);
            Assert.Equal("delivery-unavailable", expired.Diagnostic);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(database); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedServerWorkerControlPathDeliversOnlyAfterAcknowledgement(bool accepted)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        var priorEncryption = Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY");
        var priorDelivery = Environment.GetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "telegram-test-management");
        Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN", "telegram-test-delivery-authorization-token");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync([
                $"--Server:ListenUrl={url}", $"--Server:DatabasePath={Path.Combine(directory, "server.db")}",
                "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var credentials = app.Services.GetRequiredService<ICredentialStore>();
            var identity = Path.Combine(directory, "identity");
            var id = await WorkerIdentity.LoadOrCreateAsync(identity);
            var token = await WorkerAuthentication.LoadOrCreateTokenAsync(identity);
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            Assert.True(await registry.BootstrapWorkerAsync(bootstrap, new(2, id, "Worker", "1", "linux", 1, []), token));
            // A mismatched Server verifier must be replaced without exposing either value.
            await credentials.SetWorkerDeliveryTokenAsync(id, new("telegram-test-delivery-authorization-token"));
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            var path = $"/api/v1/workers/{id}/telegram/test";
            using var unauthorized = await management.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var unauthorizedRepair = await management.PostAsync($"/api/v1/workers/{id}/credential-access/repair", null);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedRepair.StatusCode);
            management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "telegram-test-management");
            using var managementRequest = await management.PostAsync($"/api/v1/workers/{id}/credential-access/request", null);
            Assert.Equal(HttpStatusCode.Unauthorized, managementRequest.StatusCode);
            using var managementConfirmation = await management.PostAsJsonAsync($"/api/v1/workers/{id}/credential-access/confirm",
                new WorkerDeliveryConfirmation(Guid.NewGuid().ToString("N"), new(new string('a', 64))));
            Assert.Equal(HttpStatusCode.Unauthorized, managementConfirmation.StatusCode);
            using var save = await management.PutAsJsonAsync($"/api/v1/workers/{id}/settings",
                new WorkerConfigurationUpdateRequest("Worker", 1, 1, Telegram: new(true, "-100123456", new(BotToken))));
            save.EnsureSuccessStatusCode();
            Assert.DoesNotContain(BotToken, await save.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var notApplied = await management.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.Conflict, notApplied.StatusCode);
            using var workerHttp = new HttpClient();
            var worker = new WorkerRegistrationClient(workerHttp);
            var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = identity };
            using var handler = new FakeTelegram(accepted);
            using var telegramHttp = new HttpClient(handler);
            using var notifier = new TelegramNotifier(false, null, null, telegramHttp);
            var synchronizer = new ManagedConfigurationSynchronizer(Path.Combine(directory, "snapshot.json"), new());
            Environment.SetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN", null);
            await Assert.ThrowsAsync<InvalidDataException>(() => ManagedTelegramConfiguration.SynchronizeAsync(
                synchronizer, notifier, new(true, "local-token", "local-chat"),
                ct => worker.GetManagedConfigurationAsync(settings, ct),
                (credentialId, ct) => worker.RetrieveCredentialAsync(settings, credentialId, ct), CancellationToken.None));
            Assert.Equal("error", synchronizer.Status.SynchronizationStatus);
            Assert.Empty(handler.Chats);
            using var repair = await management.PostAsync($"/api/v1/workers/{id}/credential-access/repair", null);
            repair.EnsureSuccessStatusCode();
            Assert.Equal("pending", (await repair.Content.ReadFromJsonAsync<WorkerDeliveryAuthorizationStatus>())?.Status);
            await worker.HeartbeatAsync(settings, 1, 1, [], "running", CancellationToken.None);
            Assert.Equal("active", (await credentials.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
            await ManagedTelegramConfiguration.SynchronizeAsync(synchronizer, notifier, new(false, null, null),
                ct => worker.GetManagedConfigurationAsync(settings, ct),
                (credentialId, ct) => worker.RetrieveCredentialAsync(settings, credentialId, ct), CancellationToken.None);
            Assert.Empty(handler.Chats);
            Assert.Equal("active", (await credentials.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
            Assert.True(File.Exists(identity + ".delivery"));
            using var localDelivery = JsonDocument.Parse(await File.ReadAllTextAsync(identity + ".delivery"));
            var protectedSecret = localDelivery.RootElement.GetProperty("Token").GetString() ?? throw new InvalidDataException();
            var dashboardStatus = await management.GetStringAsync($"/api/v1/workers/{id}/credential-access");
            Assert.DoesNotContain(protectedSecret, dashboardStatus, StringComparison.Ordinal);
            Assert.DoesNotContain(BotToken, dashboardStatus, StringComparison.Ordinal);
            await registry.HeartbeatWorkerAsync(new(2, id, "1", "running", 0, 1, [], [],
                synchronizer.Status.SynchronizationStatus, synchronizer.Status.AppliedVersion));
            using var queued = await management.PostAsJsonAsync(path, new { });
            queued.EnsureSuccessStatusCode();
            Assert.True(await worker.ExecuteTelegramTestAsync(settings, synchronizer, notifier, CancellationToken.None));
            var result = await management.GetFromJsonAsync<TelegramTestOperation>(path);
            Assert.Equal(accepted ? "succeeded" : "failed", result?.Status);
            Assert.Equal("-100123456", Assert.Single(handler.Chats));
            Assert.False(await worker.ExecuteTelegramTestAsync(settings, synchronizer, notifier, CancellationToken.None));
            using var secondTest = await management.PostAsJsonAsync(path, new { });
            secondTest.EnsureSuccessStatusCode();
            using var change = await management.PutAsJsonAsync($"/api/v1/workers/{id}/settings",
                new WorkerConfigurationUpdateRequest("Worker", 1, 2, Telegram: new(true, "@next_channel")));
            change.EnsureSuccessStatusCode();
            Assert.True(await worker.ExecuteTelegramTestAsync(settings, synchronizer, notifier, CancellationToken.None));
            var staleResult = await management.GetFromJsonAsync<TelegramTestOperation>(path);
            Assert.Equal("configuration-changed", staleResult?.Diagnostic);
            Assert.Single(handler.Chats);
            using var staleRequest = await management.PostAsJsonAsync(path, new { });
            Assert.Equal(HttpStatusCode.Conflict, staleRequest.StatusCode);
            var credentialId = (await registry.GetWorkerAsync(id))?.Telegram?.CredentialId;
            using var forbiddenCredential = await management.GetAsync($"/api/v1/workers/{id}/credentials/{credentialId}");
            Assert.Equal(HttpStatusCode.Unauthorized, forbiddenCredential.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY", priorEncryption);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN", priorDelivery);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManagedLifecycleUsesWorkerIdentityWithoutNotificationsFromRepeatedSync(bool enabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var handler = new FakeTelegram(true);
            using var client = new HttpClient(handler);
            using var notifier = new TelegramNotifier(false, null, null, client, workerName: "registered-node");
            var synchronizer = new ManagedConfigurationSynchronizer(Path.Combine(directory, "snapshot.json"), new());
            var settings = new WorkerTelegramConfiguration(enabled, enabled ? "123" : null, enabled ? Guid.NewGuid().ToString("N") : null);
            var snapshot = new ServerManagedConfigurationContract(4,
                ManagedConfigurationSynchronizer.CalculateVersion([], telegram: settings), [], Telegram: settings);
            Task SynchronizeAsync() => ManagedTelegramConfiguration.SynchronizeAsync(synchronizer, notifier,
                new(false, BotToken, null), _ => Task.FromResult(snapshot),
                (id, _) => Task.FromResult<CredentialDeliveryResponse?>(new(id, "telegram", "bot-token", 1, BotToken)), CancellationToken.None);

            await SynchronizeAsync();
            await notifier.StartedAsync(0, CancellationToken.None);
            await SynchronizeAsync();
            await SynchronizeAsync();
            await notifier.StoppedAsync(0, CancellationToken.None);

            if (enabled)
            {
                Assert.Equal(new[]
                {
                    $"🟢 registered-node {ApplicationVersion.Display} · INICIADO\n0 proyectos cargados",
                    $"⚫ registered-node {ApplicationVersion.Display} · DETENIDO"
                }, handler.Messages);
            }
            else Assert.Empty(handler.Messages);
            Assert.DoesNotContain(handler.Messages, message => message.Contains(BotToken, StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeTelegram(bool accepted) : HttpMessageHandler
    {
        public List<string?> Chats { get; } = [];
        public List<string> Messages { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.telegram.org", request.RequestUri?.Host);
            Assert.NotNull(request.Content);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Messages.Add(body.RootElement.GetProperty("text").GetString() ?? throw new InvalidDataException("Missing Telegram text."));
            Chats.Add(body.RootElement.GetProperty("chat_id").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(accepted ? "{\"ok\":true}" : "{\"ok\":false,\"description\":\"private upstream detail\"}") };
        }
    }
}
