namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Net;
using System.Text.Json;

public sealed class WorkerTelegramSettingsTests
{
    private const string Token = "123456:abcdefghijklmnopqrstuvwxyz_123456789";

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task PersistsPerWorkerEncryptedSettingsWithRevisionChecksAndExplicitClear()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var credentials = new SqliteCredentialStore(database,
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        await credentials.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        var otherId = Guid.NewGuid().ToString("N");
        foreach (var id in new[] { workerId, otherId })
            await registry.RegisterWorkerAsync(new(2, id, "Worker", "1.0", "test", 1, []));
        var original = Assert.IsType<WorkerRegistrationResponse>(await registry.GetWorkerAsync(workerId));
        Assert.Null(original.Telegram);
        var request = new WorkerConfigurationUpdateRequest("Worker", 1, 1,
            Telegram: new(true, "-123", new(Token)));
        var saved = Assert.IsType<WorkerRegistrationResponse>(await WorkerTelegramSettings.UpdateAsync(
            workerId, request, original, registry, credentials, CancellationToken.None));
        var settings = Assert.IsType<WorkerTelegramConfiguration>(saved.Telegram);
        Assert.True(settings.Enabled);
        Assert.True(settings.TokenConfigured);
        var credentialId = Assert.IsType<string>(settings.CredentialId);
        Assert.Equal(Token, await credentials.RetrieveForWorkerAsync(credentialId, workerId));
        Assert.Null(await credentials.RetrieveForWorkerAsync(credentialId, otherId));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(saved), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database)), StringComparison.Ordinal);
        Assert.Null((await registry.GetWorkerAsync(otherId))?.Telegram);
        Assert.Equal(settings, (await new SqliteRegistryStore(database).GetWorkerAsync(workerId))?.Telegram);

        Assert.Null(await WorkerTelegramSettings.UpdateAsync(workerId, request, original, registry, credentials, CancellationToken.None));
        Assert.Contains(await credentials.ListAsync(), item => item.Id != credentialId && item.Status == "Revoked");
        Assert.Equal(settings, (await registry.GetWorkerAsync(workerId))?.Telegram);
        var nameOnly = Assert.IsType<WorkerRegistrationResponse>(await WorkerTelegramSettings.UpdateAsync(workerId,
            new("Renamed", 1, saved.ConfigurationRevision), saved, registry, credentials, CancellationToken.None));
        Assert.Equal(settings, nameOnly.Telegram);
        var cleared = Assert.IsType<WorkerRegistrationResponse>(await WorkerTelegramSettings.UpdateAsync(workerId,
            new("Renamed", 1, nameOnly.ConfigurationRevision, Telegram: new(false, null, ClearToken: true)),
            nameOnly, registry, credentials, CancellationToken.None));
        Assert.False(Assert.IsType<WorkerTelegramConfiguration>(cleared.Telegram).TokenConfigured);
        Assert.Null(await credentials.RetrieveForWorkerAsync(credentialId, workerId));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true, null, null)]
    [InlineData(true, "123", null)]
    [InlineData(true, "123", "invalid")]
    [InlineData(false, "not a chat", null)]
    public async Task RejectsMissingAndInvalidValuesWithoutChangingRevision(bool enabled, string? chat, string? token)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "registry.db");
        var registry = new SqliteRegistryStore(path);
        await registry.InitializeAsync();
        var credentials = new SqliteCredentialStore(path);
        await credentials.InitializeAsync();
        var id = Guid.NewGuid().ToString("N");
        await registry.RegisterWorkerAsync(new(2, id, "Worker", "1.0", "test", 1, []));
        var current = Assert.IsType<WorkerRegistrationResponse>(await registry.GetWorkerAsync(id));
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerTelegramSettings.UpdateAsync(id,
            new("Worker", 1, 1, Telegram: new(enabled, chat, token is null ? null : new(token))),
            current, registry, credentials, CancellationToken.None));
        Assert.Equal(1, (await registry.GetWorkerAsync(id))?.ConfigurationRevision);
        Assert.Empty(await credentials.ListAsync());
        var disabled = await WorkerTelegramSettings.UpdateAsync(id,
            new("Worker", 1, 1, Telegram: new(false, null)), current, registry, credentials, CancellationToken.None);
        Assert.False(disabled?.Telegram?.Enabled);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task EnvironmentIsPreservedUntilOverrideAndDisableNeverRetrievesOrFallsBack()
    {
        var calls = 0;
        var credentialId = Guid.NewGuid().ToString("N");
        Task<CredentialDeliveryResponse?> Retrieve(string id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return Task.FromResult<CredentialDeliveryResponse?>(new(id, "telegram", "bot-token", 1, Token));
        }
        var legacy = await ManagedTelegramConfiguration.ResolveAsync(null, true, "environment-token", "environment-chat", Retrieve, CancellationToken.None);
        Assert.Equal("environment-token", legacy.Token);
        Assert.Equal("environment-chat", legacy.ChatId);
        Assert.Equal(0, calls);
        var managed = await ManagedTelegramConfiguration.ResolveAsync(new(true, "-123", credentialId), false,
            "environment-token", "environment-chat", Retrieve, CancellationToken.None);
        Assert.Equal(Token, managed.Token);
        Assert.Equal("-123", managed.ChatId);
        Assert.DoesNotContain(Token, managed.ToString(), StringComparison.Ordinal);
        var disabled = await ManagedTelegramConfiguration.ResolveAsync(new(false, null, null), true,
            "environment-token", "environment-chat", Retrieve, CancellationToken.None);
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.Token);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => ManagedTelegramConfiguration.ResolveAsync(new(true, "123", credentialId),
            true, "environment-token", "environment-chat", (_, _) => Task.FromResult<CredentialDeliveryResponse?>(null), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ManagedTelegramConfiguration.ResolveAsync(new(true, "123", credentialId),
            false, null, null, (id, _) => Task.FromResult<CredentialDeliveryResponse?>(new(id, "telegram", "bot-token", 1, "invalid-secret")), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ManagedTelegramConfiguration.ResolveAsync(new(true, "123", credentialId),
            false, null, null, Retrieve, cancellation.Token));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task NotifierAppliesDisableAndReplacementWithoutChangingDeliveryFormatting()
    {
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var notifier = new TelegramNotifier(true, Token, "123", client);
        await notifier.NoWorkAsync(CancellationToken.None);
        notifier.Configure(false, null, null);
        await notifier.NoWorkAsync(CancellationToken.None);
        Assert.Single(handler.Deliveries);
        notifier.Configure(true, "replacement", "456");
        await notifier.NoWorkAsync(CancellationToken.None);
        Assert.Equal(2, handler.Deliveries.Count);
        Assert.Contains("botreplacement", handler.Deliveries[1].Url, StringComparison.Ordinal);
        Assert.Contains("456", handler.Deliveries[1].Body, StringComparison.Ordinal);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task FailedRuntimeApplicationRetainsNotifierAndCacheAndDisableRecoversWithoutSecretDelivery()
    {
        using var temporary = new TemporaryDirectory();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var notifier = new TelegramNotifier(true, Token, "123", client);
        var path = Path.Combine(temporary.Path, "snapshot.json");
        var synchronizer = new ManagedConfigurationSynchronizer(path, new());
        var legacy = new ServerManagedConfigurationContract(2, ManagedConfigurationSynchronizer.CalculateVersion([]), []);
        synchronizer.Apply(legacy);
        var originalCache = await File.ReadAllTextAsync(path);
        var enabled = new WorkerTelegramConfiguration(true, "456", Guid.NewGuid().ToString("N"));
        var snapshot = new ServerManagedConfigurationContract(4,
            ManagedConfigurationSynchronizer.CalculateVersion([], telegram: enabled), [], Telegram: enabled);
        await Assert.ThrowsAsync<InvalidDataException>(() => ManagedTelegramConfiguration.SynchronizeAsync(
            synchronizer, notifier, new(false, null, null), _ => Task.FromResult(snapshot),
            (_, _) => Task.FromResult<CredentialDeliveryResponse?>(null), CancellationToken.None));
        Assert.Equal(originalCache, await File.ReadAllTextAsync(path));
        Assert.Equal(legacy.Version, synchronizer.Status.AppliedVersion);
        Assert.Equal("error", synchronizer.Status.SynchronizationStatus);
        Assert.Contains("credential delivery authorization", synchronizer.Status.Error ?? "", StringComparison.Ordinal);
        await notifier.NoWorkAsync(CancellationToken.None);
        Assert.Single(handler.Deliveries);
        var disabled = enabled with { Enabled = false, CredentialId = null, ChatId = null };
        snapshot = snapshot with { Telegram = disabled, Version = ManagedConfigurationSynchronizer.CalculateVersion([], telegram: disabled) };
        var deliveries = 0;
        await ManagedTelegramConfiguration.SynchronizeAsync(synchronizer, notifier, new(true, Token, "123"),
            _ => Task.FromResult(snapshot), (_, _) => { deliveries++; return Task.FromResult<CredentialDeliveryResponse?>(null); }, CancellationToken.None);
        Assert.Equal(0, deliveries);
        Assert.Equal("synchronized", synchronizer.Status.SynchronizationStatus);
        Assert.Equal(snapshot.Version, synchronizer.Status.AppliedVersion);
        await notifier.NoWorkAsync(CancellationToken.None);
        Assert.Single(handler.Deliveries);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void SnapshotHashIncludesTelegramAndCacheContainsOnlyReference()
    {
        using var temporary = new TemporaryDirectory();
        var configuration = new WorkerTelegramConfiguration(true, "123", Guid.NewGuid().ToString("N"));
        var version = ManagedConfigurationSynchronizer.CalculateVersion([], telegram: configuration);
        Assert.Equal(ServerApplication.ManagedConfigurationVersion([], telegram: configuration), version);
        var path = Path.Combine(temporary.Path, "snapshot.json");
        var synchronizer = new ManagedConfigurationSynchronizer(path, new());
        var snapshot = new ServerManagedConfigurationContract(4, version, [], Telegram: configuration);
        synchronizer.Apply(snapshot);
        Assert.Contains(configuration.CredentialId ?? "missing", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(snapshot with { Telegram = configuration with { Enabled = false } }));
        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(snapshot with { ContractVersion = 3 }));
        Assert.Equal(version, synchronizer.Status.AppliedVersion);
        Assert.Equal(configuration, synchronizer.AppliedTelegram);
        var cached = new ManagedConfigurationSynchronizer(path, new());
        cached.LoadLastValid();
        Assert.Equal(configuration, cached.AppliedTelegram);
        Assert.Equal("cached", cached.Status.SynchronizationStatus);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"telegram-settings-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string Body)> Deliveries { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Deliveries.Add((request.RequestUri?.ToString() ?? "", request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK);
        }
    }
}
