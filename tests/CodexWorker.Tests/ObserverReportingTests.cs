namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

public sealed class ObserverReportingTests
{
    internal sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"observer-tests-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private const string Identity = "obs_0123456789abcdef0123456789abcdef";
    private const string Credential = "obr_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Server = "https://observer.example";
    private static ObserverPairingRequest Request => new(1, ObserverProtocol.Mode, Identity, Server);
    private static readonly DateTimeOffset Now = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void ProtocolRejectsUnknownDuplicateOversizedAndForbiddenFields()
    {
        var execution = new ObserverExecution("0123456789abcdef0123456789abcdef", "Project", "owner/repository", 42,
            "Issue title", "Validating", null, "running", Now);
        var snapshot = new ObserverStatusSnapshot(1, Identity, "1123456789abcdef0123456789abcdef", Now, "running", [execution]);
        var json = JsonSerializer.Serialize(snapshot);
        // Reports without the new optional metadata must keep their canonical payload hash
        // across upgrades, so replaying an accepted legacy report remains idempotent.
        var legacyJson = JsonSerializer.Serialize(new
        {
            snapshot.ContractVersion, snapshot.ObserverId, snapshot.SnapshotId, snapshot.ObservedAtUtc,
            snapshot.Lifecycle, snapshot.Executions
        });
        Assert.Equal(legacyJson, json);
        Assert.True(ObserverProtocol.ReadSnapshot(Encoding.UTF8.GetBytes(json)).IsValid());
        var report = new ObserverExecutionEvent(1, Identity, "2123456789abcdef0123456789abcdef", Now, execution);
        Assert.True(ObserverProtocol.ReadEvent(JsonSerializer.SerializeToUtf8Bytes(report)).IsValid());
        Assert.Throws<JsonException>(() => ObserverProtocol.ReadSnapshot(new byte[ObserverProtocol.MaximumPayloadBytes + 1]));
        foreach (var field in new[] { "Secret", "Environment", "LocalPath", "Prompt", "Transcript", "RawOutput", "Diagnostics" })
            Assert.Throws<JsonException>(() => ObserverProtocol.ReadSnapshot(Encoding.UTF8.GetBytes(json.Insert(1, $"\"{field}\":\"excluded\","))));
        Assert.Throws<JsonException>(() => ObserverProtocol.ReadSnapshot(Encoding.UTF8.GetBytes(json.Insert(1, "\"ContractVersion\":1,"))));
        Assert.False((snapshot with { ContractVersion = 2 }).IsValid());
        Assert.False((snapshot with { ObserverId = Identity[4..] }).IsValid());
        Assert.False((snapshot with { Executions = [execution, execution] }).IsValid());
        Assert.False((snapshot with { Executions = Enumerable.Repeat(execution, 33).ToArray() }).IsValid());
        Assert.False((execution with { Repository = "/home/private/repository" }).IsValid());
        Assert.False((execution with { IssueTitle = new string('x', 501) }).IsValid());
        Assert.False((execution with { Stage = "arbitrary output" }).IsValid());
        Assert.False((execution with { Outcome = "arbitrary output" }).IsValid());
        Assert.False((execution with { ValidationResult = "arbitrary output" }).IsValid());
        Assert.False((execution with { DurationMilliseconds = -1 }).IsValid());
        Assert.False((execution with { CompletedAtUtc = Now.AddTicks(-1) }).IsValid());
        Assert.False((snapshot with { ObservedAtUtc = default }).IsValid());
        Assert.True((snapshot with { Name = "Node", Version = "1.2.3", Readiness = "ready", MaximumCapacity = 4, ActiveExecutions = 2 }).IsValid());
        Assert.False((snapshot with { Name = new string('x', 201) }).IsValid());
        Assert.False((snapshot with { Version = "invalid\nversion" }).IsValid());
        Assert.False((snapshot with { Readiness = "arbitrary diagnostics" }).IsValid());
        Assert.False((snapshot with { MaximumCapacity = 0 }).IsValid());
        Assert.False((snapshot with { ActiveExecutions = 9 }).IsValid());
        Assert.False((snapshot with { MaximumCapacity = 1, ActiveExecutions = 2 }).IsValid());
        Assert.False((Request with { Server = Server + "/" }).IsValid());
        Assert.False((Request with { Mode = "managed" }).IsValid());
        Assert.False(ObserverProtocol.ValidIdentity(Identity.ToUpperInvariant()));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task PairingIsSingleUseAndRecoverySurvivesRestartWithoutManagedEnrollment()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var clock = new Clock();
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Request));
        Assert.True(pairing.IsValid());
        Assert.Equal("pending", pairing.State);
        Assert.DoesNotContain(pairing.Authorization, pairing.ToString(), StringComparison.Ordinal);
        Assert.Null(await store.AuthenticateObserverAsync(Identity, Server, Credential));
        Assert.Null(await store.CreateObserverAuthorizationAsync(Request));
        Assert.Null(await store.RedeemObserverAuthorizationAsync(Request with { Server = "https://other.example" }, pairing.Authorization, Credential));
        Assert.Null(await store.RedeemObserverAuthorizationAsync(Request with { ObserverId = "obs_1123456789abcdef0123456789abcdef" }, pairing.Authorization, Credential));
        Assert.Null(await store.RedeemObserverAuthorizationAsync(Request, Credential, Credential));
        var accepted = Assert.IsType<ObserverRegistration>(await store.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        Assert.Equal("accepted", accepted.PairingState);
        Assert.Equal("awaiting-first-report", accepted.State);
        Assert.Null(await store.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        Assert.Null(await store.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, "obr_" + new string('a', 64)));
        Assert.Null(await store.CreateObserverAuthorizationAsync(Request));
        var restarted = new SqliteRegistryStore(database, timeProvider: clock);
        await restarted.InitializeAsync();
        Assert.Equal(accepted, await restarted.AuthenticateObserverAsync(Identity, Server, Credential));
        Assert.Null(await restarted.AuthenticateObserverAsync(Identity, "https://other.example", Credential));
        Assert.Null(await restarted.AuthenticateObserverAsync(Identity, Server, "obr_" + new string('a', 64)));
        Assert.Empty(await restarted.GetWorkersAsync());
        Assert.Null(await restarted.GetWorkerAsync(Identity));
        Assert.False(await restarted.IsWorkerTokenValidAsync(Identity, Credential));
        Assert.False(new WorkerRegistrationRequest(2, Identity, "Worker", "1", "linux", 1, []).IsValid());
        var credentials = new SqliteCredentialStore(database, Convert.ToBase64String(new byte[32]));
        await credentials.InitializeAsync();
        Assert.False(await credentials.IsWorkerDeliveryTokenValidAsync(Identity, Credential));
        var managedId = "3123456789abcdef0123456789abcdef";
        var managedToken = new string('m', 48);
        var managedAuthorization = await restarted.CreateWorkerAuthorizationAsync(managedId, "enroll", TimeSpan.FromMinutes(5));
        Assert.True(await restarted.BootstrapWorkerAsync(managedAuthorization,
            new(2, managedId, "Managed", "1", "linux", 1, []), managedToken));
        Assert.True(await restarted.IsWorkerTokenValidAsync(managedId, managedToken));
        Assert.False(await restarted.IsWorkerTokenValidAsync(managedId, Credential));
        Assert.Null(await restarted.AuthenticateObserverAsync(Identity, Server, managedToken));
        Assert.Equal(managedId, Assert.Single(await restarted.GetWorkersAsync()).WorkerId);
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT authorization_hash FROM observer_pairings;";
            Assert.NotEqual(pairing.Authorization, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT credential_hash FROM observers;";
            Assert.NotEqual(Credential, await command.ExecuteScalarAsync());
        }
        Assert.True(await restarted.RevokeObserverAsync(Identity));
        Assert.Null(await restarted.AuthenticateObserverAsync(Identity, Server, Credential));
        Assert.Null(await restarted.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        Assert.Null(await restarted.CreateObserverAuthorizationAsync(Request));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task PairingExpiryUsesExactDeadline(long ticks, bool accepted)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), timeProvider: clock);
        await store.InitializeAsync();
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Request));
        clock.Now = pairing.ExpiresAtUtc.AddTicks(ticks);
        Assert.Equal(accepted, await store.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential) is not null);
        if (!accepted)
        {
            Assert.Null(await store.AuthenticateObserverAsync(Identity, Server, Credential));
            var replacement = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Request));
            Assert.Null(await store.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
            Assert.NotNull(await store.RedeemObserverAuthorizationAsync(Request, replacement.Authorization, Credential));
        }
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task RevokingPendingPairingPreventsRedemptionAndReplacement()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), timeProvider: clock);
        await store.InitializeAsync();
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Request));
        Assert.True(await store.RevokeObserverAsync(Identity));
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Null(await store.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        Assert.Null(await store.CreateObserverAuthorizationAsync(Request));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task Version19UpgradePreservesManagedWorkersAndAddsObserverRegistry()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var managedId = "3123456789abcdef0123456789abcdef";
        await store.RegisterWorkerAsync(new(2, managedId, "Managed", "1", "linux", 1, []));
        await using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE observers; DROP TABLE observer_pairings; UPDATE schema_metadata SET schema_version=19;";
            await command.ExecuteNonQueryAsync();
        }
        await store.InitializeAsync();
        Assert.True(await store.IsAvailableAsync());
        Assert.Equal(managedId, Assert.Single(await store.GetWorkersAsync()).WorkerId);
        Assert.NotNull(await store.CreateObserverAuthorizationAsync(Request));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task CompetingRedemptionsActivateExactlyOneCredential()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var clock = new Clock();
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Request));
        var otherCredential = "obr_" + new string('b', 64);
        var otherStore = new SqliteRegistryStore(database, timeProvider: clock);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ObserverRegistration?> RedeemAsync(SqliteRegistryStore registry, string credential)
        {
            await start.Task;
            return await registry.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, credential);
        }
        var first = RedeemAsync(store, Credential);
        var second = RedeemAsync(otherStore, otherCredential);
        start.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result is not null);
        Assert.Equal(results[0] is not null, await store.AuthenticateObserverAsync(Identity, Server, Credential) is not null);
        Assert.Equal(results[1] is not null, await store.AuthenticateObserverAsync(Identity, Server, otherCredential) is not null);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task BackupRevokesObserverAndPendingPairingWithoutResurrectingAuthorization()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var clock = new Clock();
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        await new SqliteCredentialStore(database, Convert.ToBase64String(new byte[32])).InitializeAsync();
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Request));
        Assert.NotNull(await store.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        var pendingRequest = Request with { ObserverId = "obs_1123456789abcdef0123456789abcdef" };
        var pending = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(pendingRequest));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(Server, Credential,
            new(1, Identity, "0123456789abcdef0123456789abcdef", clock.Now, "running", [])));
        var archive = Path.Combine(temporary.Path, "backup.zip");
        await new ServerBackup(database).ExportAsync(archive);
        var restoredDatabase = Path.Combine(temporary.Path, "restored.db");
        await new ServerBackup(restoredDatabase).RestoreOfflineAsync(archive);
        var restored = new SqliteRegistryStore(restoredDatabase, timeProvider: clock);
        await restored.InitializeAsync();
        Assert.Null(await restored.AuthenticateObserverAsync(Identity, Server, Credential));
        Assert.Null(await restored.RedeemObserverAuthorizationAsync(pendingRequest, pending.Authorization, Credential));
        Assert.Null(await restored.CreateObserverAuthorizationAsync(pendingRequest));
        Assert.NotNull((await restored.GetObserverInspectionAsync(Identity))?.Snapshot);
        Assert.Single(await restored.GetObserverHistoryAsync(Identity));
        Assert.NotNull(await store.AuthenticateObserverAsync(Identity, Server, Credential));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = ObserverReportingTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
