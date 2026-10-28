namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

public sealed class WorkerDeliveryAuthorizationTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task PendingRepairExpiresRetainsPreviousVerifierAndRejectsStaleOrRevokedConfirmations()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new DeliveryClock();
            var store = new SqliteCredentialStore(Path.Combine(directory, "credentials.db"), timeProvider: clock);
            await store.InitializeAsync();
            var id = Guid.NewGuid().ToString("N");
            var original = new string('a', 64);
            var replacement = new string('b', 64);
            var first = Assert.IsType<WorkerDeliveryOperation>(await store.RequestWorkerDeliveryAsync(id, false));
            Assert.Equal("pending", (await store.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
            Assert.Equal(first, await store.RequestWorkerDeliveryAsync(id, false));
            Assert.Equal(first, await store.RequestWorkerDeliveryAsync(id, true));
            Assert.True(await store.ConfirmWorkerDeliveryAsync(id, new(first.Id, new(original))));
            Assert.True(await store.ConfirmWorkerDeliveryAsync(id, new(first.Id, new(original))));
            Assert.False(await store.ConfirmWorkerDeliveryAsync(id, new(first.Id, new(replacement))));
            Assert.Null(await store.RequestWorkerDeliveryAsync(id, false));
            var repair = Assert.IsType<WorkerDeliveryOperation>(await store.RequestWorkerDeliveryAsync(id, true));
            Assert.True(await store.IsWorkerDeliveryTokenValidAsync(id, original));
            clock.Now = clock.Now.AddMinutes(6);
            Assert.Equal("failed", (await store.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
            Assert.False(await store.ConfirmWorkerDeliveryAsync(id, new(repair.Id, new(replacement))));
            var retry = Assert.IsType<WorkerDeliveryOperation>(await store.RequestWorkerDeliveryAsync(id, true));
            Assert.NotEqual(repair.Id, retry.Id);
            Assert.False(await store.ConfirmWorkerDeliveryAsync(id, new(repair.Id, new(replacement))));
            Assert.True(await store.ConfirmWorkerDeliveryAsync(id, new(retry.Id, new(replacement))));
            Assert.False(await store.IsWorkerDeliveryTokenValidAsync(id, original));
            Assert.True(await store.IsWorkerDeliveryTokenValidAsync(id, replacement));
            Assert.Equal("active", (await store.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
            await store.RequestWorkerDeliveryAsync(id, true);
            Assert.True(await store.RevokeWorkerDeliveryTokenAsync(id));
            Assert.Null(await store.RequestWorkerDeliveryAsync(id, false));
            Assert.False(await store.ConfirmWorkerDeliveryAsync(id, new(retry.Id, new(replacement))));
            Assert.Equal("revoked", (await store.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerPersistsBeforeConfirmationAndReusesSecretAfterLostResponse(bool commitBeforeLoss)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identityPath = Path.Combine(directory, "identity");
            var id = await WorkerIdentity.LoadOrCreateAsync(identityPath);
            var store = new SqliteCredentialStore(Path.Combine(directory, "credentials.db"));
            await store.InitializeAsync();
            using var handler = new DeliveryHandler(store, identityPath, id, commitBeforeLoss);
            using var http = new HttpClient(handler);
            var worker = new WorkerRegistrationClient(http);
            const string apiToken = "separate-worker-api-token";
            Assert.False(await worker.SynchronizeDeliveryAuthorizationAsync(identityPath, id, "https://server.example", apiToken, CancellationToken.None));
            Assert.Single(handler.Secrets);
            var persisted = await File.ReadAllTextAsync(identityPath + ".delivery");
            Assert.DoesNotContain(apiToken, persisted, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(identityPath + ".delivery"));
            await worker.SynchronizeDeliveryAuthorizationAsync(identityPath, id, "https://server.example", apiToken, CancellationToken.None);
            Assert.Equal(persisted, await File.ReadAllTextAsync(identityPath + ".delivery"));
            Assert.All(handler.Secrets, secret => Assert.Equal(handler.Secrets[0], secret));
            Assert.Equal("active", (await store.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
            Assert.True(await store.IsWorkerDeliveryTokenValidAsync(id, handler.Secrets[0]));
            Assert.False(await store.IsWorkerDeliveryTokenValidAsync(id, apiToken));
            Assert.DoesNotContain(handler.Secrets[0], JsonSerializer.Serialize(await store.GetWorkerDeliveryAuthorizationStatusAsync(id)), StringComparison.Ordinal);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task FailedLocalPublicationNeverConfirmsAndRevocationCancelsPendingRepair()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identityPath = Path.Combine(directory, "identity");
            var id = await WorkerIdentity.LoadOrCreateAsync(identityPath);
            var store = new SqliteCredentialStore(Path.Combine(directory, "credentials.db"));
            await store.InitializeAsync();
            using var handler = new DeliveryHandler(store, identityPath, id, false);
            using var http = new HttpClient(handler);
            var worker = new WorkerRegistrationClient(http);
            Directory.CreateDirectory(identityPath + ".delivery");
            Assert.False(await worker.SynchronizeDeliveryAuthorizationAsync(identityPath, id, "https://server.example",
                "separate-worker-api-token", CancellationToken.None));
            Assert.Empty(handler.Secrets);
            Assert.Equal("pending", (await store.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
            Assert.True(await store.RevokeWorkerDeliveryTokenAsync(id));
            var repair = Assert.IsType<WorkerDeliveryOperation>(await store.RequestWorkerDeliveryAsync(id, true));
            Assert.True(await store.RevokeWorkerDeliveryTokenAsync(id));
            Assert.False(await store.ConfirmWorkerDeliveryAsync(id, new(repair.Id, new(new string('a', 64)))));
            Assert.Equal("revoked", (await store.GetWorkerDeliveryAuthorizationStatusAsync(id)).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private sealed class DeliveryClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DeliveryHandler(ICredentialStore store, string identityPath, string id, bool commitBeforeLoss) : HttpMessageHandler
    {
        public List<string> Secrets { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("separate-worker-api-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri?.AbsolutePath.EndsWith("/request", StringComparison.Ordinal) == true)
            {
                var operation = await store.RequestWorkerDeliveryAsync(id, false, cancellationToken);
                return operation is null ? new(HttpStatusCode.NoContent) : new(HttpStatusCode.OK) { Content = JsonContent.Create(operation) };
            }
            var confirmation = await Assert.IsAssignableFrom<HttpContent>(request.Content)
                .ReadFromJsonAsync<WorkerDeliveryConfirmation>(cancellationToken: cancellationToken);
            Assert.NotNull(confirmation);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(identityPath + ".delivery", cancellationToken));
            Assert.Equal(confirmation.Secret.Value, persisted.RootElement.GetProperty("Token").GetString());
            Secrets.Add(confirmation.Secret.Value);
            if (Secrets.Count == 1 && !commitBeforeLoss) return new(HttpStatusCode.ServiceUnavailable);
            Assert.True(await store.ConfirmWorkerDeliveryAsync(id, confirmation, cancellationToken));
            return new(Secrets.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
        }
    }
}
