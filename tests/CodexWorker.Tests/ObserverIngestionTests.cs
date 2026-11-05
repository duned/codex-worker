namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using Microsoft.Data.Sqlite;
using TemporaryDirectory = ObserverReportingTests.TemporaryDirectory;

public sealed class ObserverIngestionTests
{
    private const string Id = "obs_0123456789abcdef0123456789abcdef";
    private const string Server = "https://observer.example";
    private const string Credential = "obr_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly ObserverPairingRequest Pairing = new(1, ObserverProtocol.Mode, Id, Server);
    private static readonly DateTimeOffset Origin = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static string Identity(int number) => number.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
    private static ObserverExecution Execution(int number = 1) => new(Identity(number), "Project", "owner/repo", number,
        "Issue", "Implementing", null, null, Origin.AddMinutes(-10));
    private static ObserverExecutionEvent Event(int number, DateTimeOffset at, ObserverExecution? execution = null) =>
        new(1, Id, Identity(number), at, execution ?? Execution());
    private static ObserverStatusSnapshot Snapshot(int number, DateTimeOffset at, params ObserverExecution[] executions) =>
        new(1, Id, Identity(number), at, "running", executions);

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task OnlyPairedObserversCanReportAndOnlySnapshotEstablishesConnectivityAcrossRestart()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "registry.db");
        var clock = new Clock();
        var store = new SqliteRegistryStore(path, timeProvider: clock);
        await store.InitializeAsync();
        var freshness = await store.GetResourceStatusAsync();
        Assert.Equal(ObserverIngestionResult.Unauthorized, await store.IngestObserverSnapshotAsync(Server, Credential, Snapshot(1, clock.Now)));
        Assert.Null(await store.GetObserverInspectionAsync(Id));
        var authorization = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Pairing));
        Assert.Equal(ObserverIngestionResult.Unauthorized, await store.IngestObserverEventAsync(Server, authorization.Authorization, Event(1, clock.Now)));
        Assert.NotNull(await store.RedeemObserverAuthorizationAsync(Pairing, authorization.Authorization, Credential));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential, Event(1, clock.Now)));
        var awaiting = Assert.IsType<ObserverInspection>(await store.GetObserverInspectionAsync(Id));
        Assert.Equal("awaiting-first-report", awaiting.Registration.State);
        Assert.Null(awaiting.Snapshot);
        clock.Now = clock.Now.AddSeconds(1);
        var snapshot = Snapshot(2, clock.Now, Execution());
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(Server, Credential, snapshot));
        var restarted = new SqliteRegistryStore(path, timeProvider: clock);
        await restarted.InitializeAsync();
        var inspection = Assert.IsType<ObserverInspection>(await restarted.GetObserverInspectionAsync(Id));
        Assert.Equal("reporting", inspection.Registration.State);
        Assert.Equal(snapshot.SnapshotId, inspection.Snapshot?.SnapshotId);
        Assert.Equal(clock.Now, inspection.SnapshotReceivedAtUtc);
        Assert.Equal(clock.Now, inspection.LastReceivedAtUtc);
        Assert.Equal("reporting", (await restarted.AuthenticateObserverAsync(Id, Server, Credential))?.State);
        Assert.Equal(2, (await restarted.GetObserverHistoryAsync(Id)).Count);
        Assert.Equal(freshness.Freshness, (await restarted.GetResourceStatusAsync()).Freshness);
        Assert.Empty(await restarted.GetWorkersAsync());
        Assert.Empty(await restarted.GetExecutionsAsync());
        Assert.Empty(await restarted.GetProjectsAsync());
        Assert.False(await restarted.IsWorkerTokenValidAsync(Id, Credential));
        Assert.False((await restarted.RequestAssignmentAsync(new(Id, true, 1, new Dictionary<string, int>()))).HasWork);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ReorderedEventsAndSnapshotsCannotUndoNewerOrTerminalObservations()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = await PairedAsync(temporary.Path, clock);
        var first = Snapshot(1, Origin, Execution());
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(Server, Credential, first));
        clock.Now = Origin.AddSeconds(20);
        var validating = Execution() with { Stage = "Validating", ValidationResult = "running" };
        var newer = Event(2, Origin.AddSeconds(10), validating);
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential, newer));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential, Event(3, Origin.AddSeconds(1))));
        Assert.Equal("Validating", Assert.Single((await store.GetObserverInspectionAsync(Id))?.Executions ?? []).Execution.Stage);
        var completed = validating with { Stage = "Completed", Outcome = "success", ValidationResult = "passed", CompletedAtUtc = Origin.AddSeconds(11) };
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential, Event(4, Origin.AddSeconds(11), completed)));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(Server, Credential, Snapshot(5, Origin.AddSeconds(15), Execution())));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(Server, Credential, Snapshot(6, Origin.AddSeconds(2), Execution()) with { Lifecycle = "stopped" }));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential,
            Event(7, Origin.AddSeconds(16), completed with { Outcome = "failed", Stage = "Failed" })));
        var current = Assert.IsType<ObserverInspection>(await store.GetObserverInspectionAsync(Id));
        Assert.Equal(Identity(5), current.Snapshot?.SnapshotId);
        Assert.Equal("running", current.Snapshot?.Lifecycle);
        Assert.Equal("success", Assert.Single(current.Executions).Execution.Outcome);
        Assert.Equal("success", Assert.Single(Assert.IsType<ObserverStatusSnapshot>(current.Snapshot).Executions).Outcome);
        Assert.Equal(Origin.AddSeconds(11), Assert.Single(current.Executions).ObservedAtUtc);
        Assert.Equal(clock.Now, Assert.Single(current.Executions).ReceivedAtUtc);
        Assert.Equal(7, (await store.GetObserverHistoryAsync(Id)).Count);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DuplicatesDoNotRefreshFreshnessAndConflictingIdentitiesRollBackWholeReport()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = await PairedAsync(temporary.Path, clock);
        var original = Event(1, clock.Now);
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential, original));
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Equal(ObserverIngestionResult.Duplicate, await store.IngestObserverEventAsync(Server, Credential, original));
        Assert.Equal(Origin, (await store.GetObserverInspectionAsync(Id))?.LastReceivedAtUtc);
        Assert.Equal(ObserverIngestionResult.Conflict, await store.IngestObserverEventAsync(Server, Credential,
            original with { Execution = Execution() with { Stage = "Validating" } }));
        Assert.Equal(ObserverIngestionResult.Conflict, await store.IngestObserverSnapshotAsync(Server, Credential,
            Snapshot(2, clock.Now, Execution(2), Execution() with { Repository = "other/repository" })));
        var inspection = Assert.IsType<ObserverInspection>(await store.GetObserverInspectionAsync(Id));
        Assert.Single(inspection.Executions);
        Assert.Null(inspection.Snapshot);
        Assert.Single(await store.GetObserverHistoryAsync(Id));
        Assert.Equal(Origin, inspection.LastReceivedAtUtc);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task RevocationExpiryInvalidReportsAndCancellationDoNotWriteHistory()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = await PairedAsync(temporary.Path, clock);
        Assert.Equal(ObserverIngestionResult.Invalid, await store.IngestObserverSnapshotAsync(Server, Credential, Snapshot(1, clock.Now) with { ContractVersion = 2 }));
        var expiredAt = clock.Now - SqliteRegistryStore.ObserverReportMaximumAge;
        Assert.Equal(ObserverIngestionResult.Expired, await store.IngestObserverEventAsync(Server, Credential,
            Event(1, expiredAt, Execution() with { StartedAtUtc = expiredAt.AddMinutes(-1) })));
        Assert.Equal(ObserverIngestionResult.Invalid, await store.IngestObserverEventAsync(Server, Credential,
            Event(1, clock.Now, Execution() with { StartedAtUtc = clock.Now.AddSeconds(1) })));
        Assert.Equal(ObserverIngestionResult.Expired, await store.IngestObserverEventAsync(Server, Credential, Event(1, clock.Now + SqliteRegistryStore.ObserverReportFutureTolerance + TimeSpan.FromTicks(1))));
        Assert.Equal(ObserverIngestionResult.Unauthorized, await store.IngestObserverEventAsync("https://other.example", Credential, Event(1, clock.Now)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.IngestObserverEventAsync(Server, Credential, Event(1, clock.Now), cancelled.Token));
        Assert.True(await store.RevokeObserverAsync(Id));
        Assert.Equal(ObserverIngestionResult.Unauthorized, await store.IngestObserverSnapshotAsync(Server, Credential, Snapshot(1, clock.Now)));
        Assert.Empty(await store.GetObserverHistoryAsync(Id));
        Assert.Equal("revoked", (await store.GetObserverInspectionAsync(Id))?.Registration.State);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task HistoryAndExecutionRetentionRejectEvictedReplaysAcrossRestart()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = await PairedAsync(temporary.Path, clock);
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(Server, Credential, Snapshot(9000, Origin, Execution(9000), Execution(9002))));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential,
            Event(9000, Origin, Execution(9000) with { Stage = "Completed", Outcome = "success", CompletedAtUtc = Origin })));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential,
            Event(9002, Origin.AddTicks(2), Execution(9002) with { Stage = "Validating" })));
        for (var number = 1; number <= SqliteRegistryStore.ObserverHistoryLimit + 2; number++)
        {
            clock.Now = Origin.AddSeconds(number);
            var execution = Execution(number) with { StartedAtUtc = Origin.AddSeconds(number - 1) };
            Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential, Event(number, clock.Now, execution)));
            if (number == SqliteRegistryStore.ObserverExecutionLimit + 2)
            {
                Assert.Equal(ObserverIngestionResult.Expired, await store.IngestObserverEventAsync(Server, Credential,
                    Event(9003, Origin.AddTicks(1), Execution(9002))));
                var snapshot = Assert.IsType<ObserverStatusSnapshot>((await store.GetObserverInspectionAsync(Id))?.Snapshot);
                Assert.Equal("Validating", Assert.Single(snapshot.Executions, item => item.LocalExecutionId == Identity(9002)).Stage);
            }
        }
        var restarted = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), timeProvider: clock);
        await restarted.InitializeAsync();
        Assert.Equal(SqliteRegistryStore.ObserverHistoryLimit, (await restarted.GetObserverHistoryAsync(Id, SqliteRegistryStore.ObserverHistoryLimit)).Count);
        Assert.Equal(SqliteRegistryStore.ObserverExecutionLimit, (await restarted.GetObserverInspectionAsync(Id))?.Executions.Count);
        var retained = Assert.IsType<ObserverInspection>(await restarted.GetObserverInspectionAsync(Id));
        Assert.DoesNotContain(retained.Executions, item => item.Execution.LocalExecutionId == Identity(9000));
        Assert.Equal("success", Assert.Single(Assert.IsType<ObserverStatusSnapshot>(retained.Snapshot).Executions,
            item => item.LocalExecutionId == Identity(9000)).Outcome);
        Assert.Equal(ObserverIngestionResult.Accepted, await restarted.IngestObserverSnapshotAsync(Server, Credential, Snapshot(9001, clock.Now, Execution(9000))));
        Assert.Equal("success", Assert.Single(Assert.IsType<ObserverStatusSnapshot>((await restarted.GetObserverInspectionAsync(Id))?.Snapshot).Executions).Outcome);
        Assert.Equal(ObserverIngestionResult.Expired, await restarted.IngestObserverEventAsync(Server, Credential, Event(1, Origin.AddSeconds(1), Execution(1) with { StartedAtUtc = Origin })));
        Assert.Equal(ObserverIngestionResult.Expired, await restarted.IngestObserverEventAsync(Server, Credential,
            Event(999, clock.Now, Execution(1) with { StartedAtUtc = Origin })));
        var recent = Event(SqliteRegistryStore.ObserverHistoryLimit + 2, clock.Now,
            Execution(SqliteRegistryStore.ObserverHistoryLimit + 2) with { StartedAtUtc = clock.Now.AddSeconds(-1) });
        Assert.Equal(ObserverIngestionResult.Duplicate, await restarted.IngestObserverEventAsync(Server, Credential, recent));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ObserverIdentitiesIsolateMatchingLocalExecutionAndEventIds()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = await PairedAsync(temporary.Path, clock);
        const string otherId = "obs_1123456789abcdef0123456789abcdef";
        var otherCredential = "obr_" + new string('b', 64);
        var request = Pairing with { ObserverId = otherId };
        var authorization = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(request));
        Assert.NotNull(await store.RedeemObserverAuthorizationAsync(request, authorization.Authorization, otherCredential));
        var report = Event(1, clock.Now);
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, Credential, report));
        var other = report with { ObserverId = otherId, Execution = Execution() with { Repository = "other/repo" } };
        Assert.Equal(ObserverIngestionResult.Unauthorized, await store.IngestObserverEventAsync(Server, Credential, other));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(Server, otherCredential, other));
        Assert.Equal("owner/repo", Assert.Single((await store.GetObserverInspectionAsync(Id))?.Executions ?? []).Execution.Repository);
        Assert.Equal("other/repo", Assert.Single((await store.GetObserverInspectionAsync(otherId))?.Executions ?? []).Execution.Repository);
        Assert.Single(await store.GetObserverHistoryAsync(Id));
        Assert.Single(await store.GetObserverHistoryAsync(otherId));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task Version20UpgradeKeepsPairingAndCredentialsAndStartsWithoutReports()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = await PairedAsync(temporary.Path, clock);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(temporary.Path, "registry.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE observer_reports; DROP TABLE observer_status; DROP TABLE observer_executions; UPDATE schema_metadata SET schema_version=20;";
            await command.ExecuteNonQueryAsync();
        }
        await store.InitializeAsync();
        Assert.Equal("awaiting-first-report", (await store.AuthenticateObserverAsync(Id, Server, Credential))?.State);
        Assert.Empty(await store.GetObserverHistoryAsync(Id));
        Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(Server, Credential, Snapshot(1, clock.Now)));
    }

    private static async Task<SqliteRegistryStore> PairedAsync(string directory, Clock clock)
    {
        var store = new SqliteRegistryStore(Path.Combine(directory, "registry.db"), timeProvider: clock);
        await store.InitializeAsync();
        var authorization = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(Pairing));
        Assert.NotNull(await store.RedeemObserverAuthorizationAsync(Pairing, authorization.Authorization, Credential));
        return store;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Origin;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
