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

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task BranchMaintenanceRequiresSeparateGrantFreshCapabilityAndExactBinding()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var database = Path.Combine(temporary.Path, "registry.db");
        var registry = new SqliteRegistryStore(database, timeProvider: clock);
        await registry.InitializeAsync();
        var commands = new ExecutionMaintenanceStore(database, clock);
        await commands.InitializeAsync();
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await registry.CreateObserverAuthorizationAsync(Request));
        Assert.NotNull(await registry.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        var snapshot = new ObserverStatusSnapshot(1, Identity, Guid.NewGuid().ToString("N"), Now, "drained", [], ActiveExecutions: 0);
        var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), Identity, null, null, null, null,
            Action: "archived-branches", Apply: true, Project: "sample", Repository: "owner/repo", OlderThanDays: 30,
            Branches: [new("done/feature/task-1", new string('a', 40))]);
        Assert.True(ExecutionMaintenanceProtocol.Valid(request));
        Assert.False(ExecutionMaintenanceProtocol.Valid(request with { Action = "inventory", Apply = false, Branches = null }));
        Assert.Equal(ObserverIngestionResult.Accepted, await registry.IngestObserverSnapshotAsync(Server, Credential, snapshot));
        Task<string?> Reason(ExecutionMaintenanceRequest scope) => ServerApplication.MaintenanceAuthorityReasonAsync(
            scope, registry, CancellationToken.None, clock, commands, 90);
        Assert.Equal("observer-maintenance-not-authorized", await Reason(request));
        await commands.SetObserverBranchGrantAsync(Identity, new("sample", "owner/repo"));
        Assert.Equal("worker-branch-protocol-unavailable", await Reason(request));
        snapshot = snapshot with { SnapshotId = Guid.NewGuid().ToString("N"), ObservedAtUtc = Now.AddSeconds(1),
            BranchMaintenance = [new("sample", "owner/repo")] };
        clock.Now = snapshot.ObservedAtUtc;
        Assert.Equal(ObserverIngestionResult.Accepted, await registry.IngestObserverSnapshotAsync(Server, Credential, snapshot));
        Assert.Null(await Reason(request));
        Assert.Equal("observer-maintenance-not-authorized", await Reason(request with { Repository = "owner/other" }));
        Assert.Equal("observer-maintenance-not-authorized", await Reason(request with { Project = "other" }));
        clock.Now = clock.Now.AddSeconds(91);
        Assert.Equal("worker-offline", await Reason(request));
        clock.Now = Now.AddSeconds(2);
        snapshot = snapshot with { SnapshotId = Guid.NewGuid().ToString("N"), ObservedAtUtc = clock.Now, Lifecycle = "running" };
        Assert.Equal(ObserverIngestionResult.Accepted, await registry.IngestObserverSnapshotAsync(Server, Credential, snapshot));
        Assert.Equal("worker-drain-required", await Reason(request));
        Assert.Null(await Reason(request with { Apply = false }));
        await commands.SetObserverBranchGrantAsync(Identity, null);
        Assert.Equal("observer-maintenance-not-authorized", await Reason(request));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task SelectedBranchScopeSurvivesSerializationAndIdempotentRequests()
    {
        using var temporary = new TemporaryDirectory();
        var commands = new ExecutionMaintenanceStore(Path.Combine(temporary.Path, "commands.db"), new Clock());
        await commands.InitializeAsync();
        var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), Identity, null, null, null, null,
            Action: "archived-branches", Apply: true, Project: "sample", Repository: "owner/repo", OlderThanDays: 30,
            Branches: [new("done/feature/task-1", new string('a', 40))]);
        var copy = JsonSerializer.Deserialize<ExecutionMaintenanceRequest>(JsonSerializer.Serialize(request));
        Assert.NotNull(copy);
        Assert.True(ExecutionMaintenanceProtocol.SameRequest(request, copy));
        Assert.False(ExecutionMaintenanceProtocol.SameRequest(request, copy with { Branches = [new("done/feature/task-1", new string('b', 40))] }));
        await commands.CreateAsync(request, "test");
        Assert.Equal(request.OperationId, (await commands.CreateAsync(copy, "test")).Request.OperationId);
        Assert.NotNull(await commands.CancelAsync(request.OperationId));
        Assert.Null(await commands.ClaimAsync(Identity));
        Assert.False(new ObserverBranchMaintenanceBinding("sample", "/private/path").IsValid());
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task LostStandaloneBranchReportReplaysOnlyDurablePartialResults()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "history.db");
        var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), Identity, null, null, null, null,
            Action: "archived-branches", Apply: true, Project: "sample", Repository: "owner/repo", OlderThanDays: 30,
            Branches: [new("done/feature/task-1", new string('a', 40))]);
        var command = new ExecutionMaintenanceCommand(request, "running", DateTimeOffset.UtcNow, "operator", DateTimeOffset.UtcNow.AddMinutes(1));
        var executions = 0;
        var reports = 0;
        Task<ExecutionMaintenanceReport> Execute(ExecutionMaintenanceCommand _, Func<CancellationToken, Task<bool>> authorize, CancellationToken ct)
        {
            executions++;
            return ExecuteAuthorized();
            async Task<ExecutionMaintenanceReport> ExecuteAuthorized()
            {
                Assert.True(await authorize(ct));
                return new("succeeded", "archived-branches-observed", [], Branches:
                    [new("done/feature/task-1", new string('a', 40), null, "review", "Remote removed; inspect local ref.", false, true)]);
            }
        }
        async Task<System.Net.Http.HttpResponseMessage> Send(System.Net.Http.HttpRequestMessage message, CancellationToken ct)
        {
            if (message.RequestUri?.AbsolutePath.EndsWith("/report", StringComparison.Ordinal) == true)
            {
                reports++;
                var report = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<ExecutionMaintenanceReport>(
                    Assert.IsAssignableFrom<System.Net.Http.HttpContent>(message.Content), cancellationToken: ct);
                Assert.True(Assert.Single(report?.Branches ?? []).RemoteDeleted);
                if (reports == 1) throw new HttpRequestException("Response lost.");
                return new(System.Net.HttpStatusCode.OK);
            }
            return new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(command) };
        }
        Task<bool> Exchange(ExecutionHistoryStore history) => WorkerRegistrationClient.ExecuteMaintenanceExchangeAsync(
            Identity, Server + "/observers/" + Identity, history, new(new(), [], history),
            suffix => new(HttpMethod.Post, Server + "/" + suffix), Send, Execute, CancellationToken.None);
        using (var history = new ExecutionHistoryStore(database))
            await Assert.ThrowsAsync<HttpRequestException>(() => Exchange(history));
        using var reopened = new ExecutionHistoryStore(database);
        Assert.True(await Exchange(reopened));
        Assert.Equal(1, executions);
        Assert.Equal(2, reports);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task BranchTargetProjectionUsesAuthoritativeScopeCapabilityAndFreshness()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var database = Path.Combine(temporary.Path, "registry.db");
        var registry = new SqliteRegistryStore(database, timeProvider: clock);
        await registry.InitializeAsync();
        var commands = new ExecutionMaintenanceStore(database, clock);
        await commands.InitializeAsync();
        var project = await registry.CreateProjectAsync(new("codex-worker", "owner/repo", "main", "", []));
        project = Assert.IsType<CentralProject>(await registry.UpdateProjectLifecycleAsync(project.Id, false, project.Revision));
        var managed = Guid.NewGuid().ToString("N");
        CodexServer.WorkerCapability[] capabilities = [new("protocol", ExecutionMaintenanceProtocol.Capability), new("protocol", ExecutionMaintenanceProtocol.BranchCapability)];
        await registry.RegisterWorkerAsync(new(2, managed, "Managed", "1", "linux", 1, capabilities));
        await registry.HeartbeatWorkerAsync(new(2, managed, "1", "running", 0, 1, capabilities, []));
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await registry.CreateObserverAuthorizationAsync(Request));
        Assert.NotNull(await registry.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        var snapshot = new ObserverStatusSnapshot(1, Identity, Guid.NewGuid().ToString("N"), Now, "drained", [],
            Name: "Standalone", ActiveExecutions: 0, BranchMaintenance: [new("codex-worker", "owner/repo")]);
        Assert.Equal(ObserverIngestionResult.Accepted, await registry.IngestObserverSnapshotAsync(Server, Credential, snapshot));
        Task<BranchMaintenanceTargets> Read() => ServerApplication.ReadBranchMaintenanceTargetsAsync(project, registry, commands, clock, 90, CancellationToken.None);
        var initial = await Read();
        Assert.True(Assert.Single(initial.Targets, t => t.Mode == "managed").Eligible);
        Assert.Equal("observer-maintenance-not-authorized", Assert.Single(initial.Targets, t => t.Mode == ObserverProtocol.Mode).Reason);
        await commands.SetObserverBranchGrantAsync(Identity, new("codex-worker", "owner/repo"));
        var capable = Assert.Single((await Read()).Targets, t => t.Mode == ObserverProtocol.Mode);
        Assert.True(capable.Eligible);
        Assert.True(capable.CanApply);
        Assert.Equal("Standalone", capable.DisplayName);
        var scope = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), Identity, null, null, null, null,
            Action: "archived-branches", Project: project.Name, Repository: project.Repository, OlderThanDays: 30,
            ProjectId: project.Id, ProjectRevision: project.Revision);
        Assert.True(ExecutionMaintenanceProtocol.Valid(scope));
        Assert.False(ExecutionMaintenanceProtocol.Valid(scope with { ProjectRevision = null }));
        Assert.Null(await ServerApplication.MaintenanceAuthorityReasonAsync(scope, registry, CancellationToken.None, clock, commands));
        Assert.Null(await ServerApplication.MaintenanceAuthorityReasonAsync(scope with { Apply = true }, registry, CancellationToken.None, clock, commands));
        Assert.Null(await ServerApplication.MaintenanceAuthorityReasonAsync(scope with { WorkerId = managed }, registry, CancellationToken.None, clock, commands));
        Assert.Equal("project-binding-changed", await ServerApplication.MaintenanceAuthorityReasonAsync(
            scope with { Repository = "owner/other" }, registry, CancellationToken.None, clock, commands));
        clock.Now += TimeSpan.FromTicks(1);
        Assert.Equal(ObserverIngestionResult.Accepted, await registry.IngestObserverSnapshotAsync(Server, Credential,
            snapshot with { SnapshotId = Guid.NewGuid().ToString("N"), ObservedAtUtc = clock.Now, BranchMaintenance = [] }));
        Assert.Equal("worker-branch-protocol-unavailable", Assert.Single((await Read()).Targets, t => t.WorkerId == Identity).Reason);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.Equal(ObserverIngestionResult.Accepted, await registry.IngestObserverSnapshotAsync(Server, Credential,
            snapshot with { SnapshotId = Guid.NewGuid().ToString("N"), ObservedAtUtc = clock.Now }));
        Assert.False((await registry.GetProjectAsync(project.Id))?.Enabled);
        clock.Now += TimeSpan.FromSeconds(91);
        Assert.Equal("worker-offline", Assert.Single((await Read()).Targets, t => t.WorkerId == Identity).Reason);
        clock.Now = Now;
        await registry.UpdateProjectAsync(project.Id, new("sample", "owner/changed", "main", "", []), project.Revision);
        Assert.Equal("project-binding-changed", await ServerApplication.MaintenanceAuthorityReasonAsync(scope, registry, CancellationToken.None, clock, commands));
        Assert.False(Assert.Single((await Read()).Targets, t => t.WorkerId == Identity).Eligible);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task SelectedStandaloneBranchTargetCannotBeReroutedAndRevocationFencesQueuedWork()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var database = Path.Combine(temporary.Path, "registry.db");
        var registry = new SqliteRegistryStore(database, timeProvider: clock);
        await registry.InitializeAsync();
        var commands = new ExecutionMaintenanceStore(database, clock);
        await commands.InitializeAsync();
        var project = await registry.CreateProjectAsync(new("sample", "owner/repo", "main", "", []));
        var pairing = Assert.IsType<ObserverPairingAuthorization>(await registry.CreateObserverAuthorizationAsync(Request));
        Assert.NotNull(await registry.RedeemObserverAuthorizationAsync(Request, pairing.Authorization, Credential));
        var snapshot = new ObserverStatusSnapshot(1, Identity, Guid.NewGuid().ToString("N"), Now, "drained", [],
            ActiveExecutions: 0, BranchMaintenance: [new(project.Name, project.Repository)]);
        Assert.Equal(ObserverIngestionResult.Accepted, await registry.IngestObserverSnapshotAsync(Server, Credential, snapshot));
        await commands.SetObserverBranchGrantAsync(Identity, new(project.Name, project.Repository));
        var targets = await ServerApplication.ReadBranchMaintenanceTargetsAsync(project, registry, commands, clock, 90, CancellationToken.None);
        var target = Assert.Single(targets.Targets);
        Assert.True(target.Eligible);
        Assert.True(target.CanApply);
        var request = new ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), target.WorkerId, null, null, null, null,
            Action: "archived-branches", Apply: true, Project: targets.Project, Repository: targets.Repository,
            OlderThanDays: 30, ProjectId: targets.ProjectId, ProjectRevision: targets.Revision,
            Branches: [new("done/feature/task-1", new string('a', 40))]);
        Assert.Null(await ServerApplication.MaintenanceAuthorityReasonAsync(request, registry, CancellationToken.None, clock, commands));
        await commands.CreateAsync(request, "operator");
        var otherWorker = "obs_" + new string('b', 32);
        Assert.Null(await commands.ClaimAsync(otherWorker));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.CreateAsync(request with { WorkerId = otherWorker }, "operator"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.CreateAsync(request with { Repository = "owner/other" }, "operator"));
        var claimed = Assert.IsType<ExecutionMaintenanceCommand>(await commands.ClaimAsync(target.WorkerId));
        Assert.True(ExecutionMaintenanceProtocol.SameRequest(request, claimed.Request));
        // A separate grant can be revoked after selection/dispatch. Fresh authorization inside the repository gate must refuse it.
        await commands.SetObserverBranchGrantAsync(Identity, null);
        Assert.Equal("observer-maintenance-not-authorized", await ServerApplication.MaintenanceAuthorityReasonAsync(
            claimed.Request, registry, CancellationToken.None, clock, commands));
        var refusal = new ExecutionMaintenanceReport("refused", "server-authority-rejected", []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.ReportAsync(request.OperationId, otherWorker, refusal));
        var terminal = Assert.IsType<ExecutionMaintenanceCommand>(await commands.ReportAsync(request.OperationId, Identity, refusal));
        Assert.Equal("refused", terminal.Status);
        Assert.Equal("server-authority-rejected", terminal.Report?.Reason);
        Assert.Null(await commands.ClaimAsync(Identity));
        Assert.False(Assert.Single((await ServerApplication.ReadBranchMaintenanceTargetsAsync(
            project, registry, commands, clock, 90, CancellationToken.None)).Targets).Eligible);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = ObserverReportingTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
