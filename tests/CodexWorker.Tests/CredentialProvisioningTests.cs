namespace CodexWorker.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexServer;

public sealed class CredentialProvisioningTests
{
    [Fact]
    public async Task CredentialsPersistEncryptedMetadataAndRestrictRetrievalToAssignedWorker()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "credentials.db");
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var workerId = Guid.NewGuid().ToString("N");
        var otherWorkerId = Guid.NewGuid().ToString("N");
        const string secretValue = "fake-provider-secret-value";
        var store = new SqliteCredentialStore(database, key);
        await store.InitializeAsync();

        var created = await store.CreateAsync(new CreateCredentialRequest("fake-provider", "api-token", new CredentialSecretInput(secretValue)));
        Assert.Equal("Ready", created.Status);
        Assert.Null(created.AssignedWorkerId);
        Assert.Equal(1, created.Version);
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(created), StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, new CredentialSecretInput(secretValue).ToString(), StringComparison.Ordinal);

        var assigned = await store.AssignAsync(created.Id, workerId);
        Assert.Equal(workerId, assigned!.AssignedWorkerId);
        const string workerToken = "fake-worker-delivery-token-with-sufficient-entropy";
        await store.SetWorkerDeliveryTokenAsync(workerId, new CredentialSecretInput(workerToken));
        Assert.True(await store.IsWorkerDeliveryTokenValidAsync(workerId, workerToken));
        Assert.False(await store.IsWorkerDeliveryTokenValidAsync(workerId, "wrong-worker-token"));
        Assert.False(await store.IsWorkerDeliveryTokenValidAsync(otherWorkerId, workerToken));
        Assert.Null(await store.RetrieveForWorkerAsync(created.Id, otherWorkerId));
        Assert.Null(await store.RetrieveForWorkerAsync(created.Id, Guid.NewGuid().ToString("N")));
        Assert.Equal(secretValue, await store.RetrieveForWorkerAsync(created.Id, workerId));

        var databaseText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database));
        Assert.DoesNotContain(secretValue, databaseText, StringComparison.Ordinal);

        var restarted = new SqliteCredentialStore(database, key);
        await restarted.InitializeAsync();
        Assert.True(await restarted.IsWorkerDeliveryTokenValidAsync(workerId, workerToken));
        Assert.Equal(secretValue, await restarted.RetrieveForWorkerAsync(created.Id, workerId));
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(await restarted.ListAsync()), StringComparison.Ordinal);

        var replaced = await restarted.ReplaceSecretAsync(created.Id, new CredentialSecretInput("fake-rotated-secret"));
        Assert.Equal(3, replaced!.Version);
        Assert.Equal("fake-rotated-secret", await restarted.RetrieveForWorkerAsync(created.Id, workerId));
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(await restarted.GetAsync(created.Id)), StringComparison.Ordinal);

        var revoked = await restarted.RevokeAsync(created.Id);
        Assert.Equal("Revoked", revoked!.Status);
        Assert.NotNull(revoked.RevokedAtUtc);
        Assert.Null(revoked.AssignedWorkerId);
        Assert.Null(await restarted.RetrieveForWorkerAsync(created.Id, workerId));
    }

    [Fact]
    public async Task MissingEncryptionKeyFailsWithoutIncludingSecretInFailure()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteCredentialStore(Path.Combine(temporary.Path, "credentials.db"), string.Empty);
        await store.InitializeAsync();
        const string fakeSecret = "fake-secret-that-must-not-leak";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateAsync(new CreateCredentialRequest("fake", "token", new CredentialSecretInput(fakeSecret))));
        Assert.DoesNotContain(fakeSecret, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fakeSecret, error.ToString(), StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"credential-provisioning-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
