namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using CodexWorker;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TemporaryDirectory = ObserverReportingTests.TemporaryDirectory;

[Collection("ServerTokenEnvironment")]
public sealed class StandaloneReportingTests
{
    private const string Server = "https://observer.example";
    private static readonly DateTimeOffset Now = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static StandaloneReportingCredential Local(bool accepted = true) => new(1, "obs_" + Guid.NewGuid().ToString("N"),
        Server, "obr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), accepted, accepted);

    [Theory]
    [InlineData("lost")]
    [InlineData("unknown")]
    [InlineData("oversize")]
    [InlineData("malformed")]
    [InlineData("unavailable")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task UnacknowledgedSnapshotReplaysAfterRestartWithoutRefreshingServerReceipt(string failure)
    {
        using var fixture = new Fixture();
        var local = Local();
        await fixture.Store.SaveAsync(local, CancellationToken.None);
        var clock = new PairingClock();
        var registry = new SqliteRegistryStore(Path.Combine(fixture.Root, "registry.db"), timeProvider: clock);
        await registry.InitializeAsync();
        var pairing = new ObserverPairingRequest(1, ObserverProtocol.Mode, local.ObserverId, Server);
        var authorization = Assert.IsType<ObserverPairingAuthorization>(await registry.CreateObserverAuthorizationAsync(pairing));
        Assert.NotNull(await registry.RedeemObserverAuthorizationAsync(pairing, authorization.Authorization, local.Credential));
        var bodies = new List<string>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = await Assert.IsAssignableFrom<HttpContent>(request.Content).ReadAsStringAsync(ct);
            bodies.Add(body);
            var report = ObserverProtocol.ReadSnapshot(System.Text.Encoding.UTF8.GetBytes(body));
            var persisted = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(ct));
            Assert.Equal(report.SnapshotId, persisted.PendingSnapshot?.SnapshotId);
            var result = await registry.IngestObserverSnapshotAsync(Server, local.Credential, report, ct);
            Assert.True(result is ObserverIngestionResult.Accepted or ObserverIngestionResult.Duplicate);
            if (bodies.Count == 1) return failure switch
            {
                "lost" => throw new HttpRequestException("Lost acknowledgement."),
                "unknown" => Json(new { state = "unknown" }),
                "oversize" => Json(new { state = "accepted", padding = new string('x', 1024) }),
                "malformed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") },
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            };
            return Json(new { state = "duplicate" });
        }));
        using var client = new StandaloneReportingClient(http);
        await using (var reporter = fixture.Reporter(client)) await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.NotNull((await fixture.Store.LoadAsync(CancellationToken.None))?.PendingSnapshot);
        clock.Now = Now.AddMinutes(1);
        await using (var reporter = fixture.Reporter(client)) await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        var state = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.Null(state.PendingSnapshot);
        Assert.Equal("reporting", state.LastResult);
        Assert.Equal(Now, Assert.Single(await registry.ListObserversAsync()).SnapshotReceivedAtUtc);
        Assert.Equal("running", fixture.Runtime.State);
        Assert.Empty(await registry.GetWorkersAsync());
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ExpiredAndPermanentlyRefusedSnapshotsAllowCurrentStatusOnNextInterval()
    {
        using var fixture = new Fixture();
        var local = Local();
        var expired = await fixture.Runtime.ObserverSnapshotAsync(local.ObserverId, Now.AddDays(-7), CancellationToken.None);
        await fixture.Store.SaveAsync(local with { PendingSnapshot = expired }, CancellationToken.None);
        var snapshots = new List<ObserverStatusSnapshot>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            snapshots.Add(ObserverProtocol.ReadSnapshot(await Assert.IsAssignableFrom<HttpContent>(request.Content).ReadAsByteArrayAsync(ct)));
            return snapshots.Count == 1 ? new HttpResponseMessage(HttpStatusCode.Conflict) : Json(new { state = "accepted" });
        }));
        using var client = new StandaloneReportingClient(http);
        await using var reporter = fixture.Reporter(client);
        await reporter.ReportOnceAsync(CancellationToken.None);
        var refused = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.Null(refused.PendingSnapshot);
        Assert.Equal("rejected", refused.LastResult);
        Assert.Null(refused.LastReportAtUtc);
        fixture.Runtime.State = "stopped";
        await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.Equal(2, snapshots.Count);
        Assert.NotEqual(expired.SnapshotId, snapshots[0].SnapshotId);
        Assert.Equal(Now, snapshots[0].ObservedAtUtc);
        Assert.NotEqual(snapshots[0].SnapshotId, snapshots[1].SnapshotId);
        Assert.Equal("stopped", snapshots[1].Lifecycle);
        Assert.Null((await fixture.Store.LoadAsync(CancellationToken.None))?.PendingSnapshot);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DefaultOffPendingAndManagedOwnershipMakeNoRequests()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network access.")));
        using var client = new StandaloneReportingClient(http);
        await using var reporter = fixture.Reporter(client);
        await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.False(File.Exists(fixture.Store.FilePath));
        Assert.False(File.Exists(fixture.Store.FilePath + ".lock"));
        await fixture.Store.SaveAsync(Local(false), CancellationToken.None);
        await reporter.ReportOnceAsync(CancellationToken.None);
        fixture.Config.Projects.Ownership = "managed";
        await fixture.Store.SaveAsync(Local(), CancellationToken.None);
        await using var managed = fixture.Reporter(client);
        managed.Start();
        await managed.ReportOnceAsync(CancellationToken.None);
        Assert.Equal("pending", (await fixture.Store.LoadAsync(CancellationToken.None))?.LastResult);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ConnectPersistsRecoveryProofBeforeHiddenInputAndStatusAndDisconnectAreLocal()
    {
        using var fixture = new Fixture();
        var before = await File.ReadAllTextAsync(fixture.ConfigurationPath);
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(Server + "/api/v1/observers/pairing/redeem", request.RequestUri?.AbsoluteUri);
            var content = Assert.IsAssignableFrom<HttpContent>(request.Content);
            var payload = Assert.IsType<ObserverPairingRequest>(await content.ReadFromJsonAsync<ObserverPairingRequest>(cancellationToken: ct));
            var persisted = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(ct));
            Assert.Equal(persisted.ObserverId, payload.ObserverId);
            Assert.Equal(persisted.Credential, Assert.Single(request.Headers.GetValues("X-Codex-Observer-Token")));
            Assert.False(persisted.Accepted);
            return Json(new ObserverRegistration(1, payload.ObserverId, ObserverProtocol.Mode, Server, Now, "awaiting-first-report"));
        }));
        using var client = new StandaloneReportingClient(http);
        var output = new StringWriter();
        var inputRead = false;
        var cli = new StandaloneReportingCli(client, output, async ct =>
        {
            inputRead = true;
            Assert.NotNull(await fixture.Store.LoadAsync(ct));
            return "oba_" + new string('a', 64);
        });
        Assert.Equal(0, await cli.ExecuteAsync(fixture.Command("connect", "--server", Server), CancellationToken.None));
        Assert.True(inputRead);
        var connected = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.True(connected.Enabled);
        Assert.True(connected.Accepted);
        Assert.DoesNotContain(connected.Credential, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("oba_", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(connected.Credential, connected.ToString(), StringComparison.Ordinal);
        Assert.Contains("within 10 seconds", output.ToString(), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Store.FilePath));
        Assert.Equal(0, await cli.ExecuteAsync(fixture.Command("status"), CancellationToken.None));
        Assert.Equal(0, await cli.ExecuteAsync(fixture.Command("disconnect"), CancellationToken.None));
        Assert.Equal(0, await cli.ExecuteAsync(fixture.Command("status"), CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.False(File.Exists(fixture.Store.FilePath));
        Assert.False(fixture.Config.Server.Enabled);
        Assert.Equal("standalone", fixture.Config.Projects.Ownership);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.ConfigurationPath));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task UncertainPairingRecoversSameIdentityAndCredentialWithoutRedemptionReplay()
    {
        using var fixture = new Fixture();
        var calls = 0;
        StandaloneReportingCredential? pending = null;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            var persisted = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(ct));
            if (calls == 1)
            {
                pending = persisted;
                throw new HttpRequestException("untrusted message " + persisted.Credential);
            }
            Assert.Equal(pending, persisted);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Server + $"/api/v1/observers/{persisted.ObserverId}/registration", request.RequestUri?.AbsoluteUri);
            Assert.Equal(persisted.Credential, request.Headers.Authorization?.Parameter);
            return Json(new ObserverRegistration(1, persisted.ObserverId, ObserverProtocol.Mode, Server, Now, "awaiting-first-report"));
        }));
        using var client = new StandaloneReportingClient(http);
        var output = new StringWriter();
        var first = new StandaloneReportingCli(client, output, _ => Task.FromResult("oba_" + new string('a', 64)));
        Assert.Equal(ProcessExitCodes.StartupFailure, await first.ExecuteAsync(fixture.Command("connect", "--server", Server), CancellationToken.None));
        Assert.False((await fixture.Store.LoadAsync(CancellationToken.None))?.Enabled);
        var recover = new StandaloneReportingCli(client, output, _ => Task.FromResult(""));
        Assert.Equal(0, await recover.ExecuteAsync(fixture.Command("connect", "--server", Server), CancellationToken.None));
        Assert.Equal(pending?.ObserverId, (await fixture.Store.LoadAsync(CancellationToken.None))?.ObserverId);
        Assert.DoesNotContain(Assert.IsType<StandaloneReportingCredential>(pending).Credential, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task SnapshotUsesInProcessSafeFieldsAndNeverIncludesDiagnosticsOrPaths()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid();
        await fixture.History.CreateAsync(new(id, "Project", "owner/repo", 42, "Issue title", "private/branch", "main",
            Now.AddMinutes(-1), null, "Validating", null, "private prompt", "private validation output", 0, [], null, null, null,
            "private failure", ReportingFailure: "private transcript"));
        var local = Local();
        await fixture.Store.SaveAsync(local, CancellationToken.None);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = await Assert.IsAssignableFrom<HttpContent>(request.Content).ReadAsStringAsync(ct);
            foreach (var excluded in new[] { "private", fixture.Root, "TOKEN", "transcript", "prompt", "validation output", "branch" })
                Assert.DoesNotContain(excluded, body, StringComparison.OrdinalIgnoreCase);
            if (request.RequestUri?.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) == true)
            {
                var report = ObserverProtocol.ReadEvent(System.Text.Encoding.UTF8.GetBytes(body));
                Assert.Equal(id.ToString("N"), report.Execution.LocalExecutionId);
                Assert.Equal("unavailable", report.Execution.ValidationResult);
                return Json(new { state = "accepted" });
            }
            Assert.Equal(Server + $"/api/v1/observers/{local.ObserverId}/snapshots", request.RequestUri?.AbsoluteUri);
            var snapshot = ObserverProtocol.ReadSnapshot(System.Text.Encoding.UTF8.GetBytes(body));
            Assert.Equal(ApplicationVersion.Display, snapshot.Version);
            Assert.Equal(2, snapshot.MaximumCapacity);
            Assert.Equal(1, snapshot.ActiveExecutions);
            Assert.Equal("running", snapshot.Lifecycle);
            var execution = Assert.Single(snapshot.Executions);
            Assert.Equal(id.ToString("N"), execution.LocalExecutionId);
            Assert.Equal(42, execution.IssueNumber);
            Assert.Equal("Validating", execution.Stage);
            Assert.Equal("unavailable", execution.ValidationResult);
            return Json(new { state = "accepted" });
        }));
        using var client = new StandaloneReportingClient(http);
        await using var reporter = fixture.Reporter(client);
        await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.Equal("reporting", (await fixture.Store.LoadAsync(CancellationToken.None))?.LastResult);
        Assert.Equal(Now, (await fixture.Store.LoadAsync(CancellationToken.None))?.LastReportAtUtc);
    }

    [Theory]
    [InlineData("error", "unavailable")]
    [InlineData("revoked", "unauthorized")]
    [InlineData("timeout", "unavailable")]
    [InlineData("exception", "unavailable")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ReportingFailureDoesNotChangeStandaloneScheduling(string failure, string expected)
    {
        using var fixture = new Fixture();
        var local = Local();
        await fixture.Store.SaveAsync(local, CancellationToken.None);
        using var http = new HttpClient(new Handler((_, _) => failure switch
        {
            "timeout" => throw new TaskCanceledException("untrusted " + local.Credential),
            "exception" => throw new HttpRequestException("untrusted " + local.Credential),
            _ => Task.FromResult(new HttpResponseMessage(failure == "revoked" ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("untrusted " + local.Credential) })
        }));
        using var client = new StandaloneReportingClient(http);
        await using var reporter = fixture.Reporter(client);
        await reporter.ReportOnceAsync(CancellationToken.None);
        await reporter.ReportOnceAsync(CancellationToken.None);
        var persisted = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.Equal(expected, persisted.LastResult);
        Assert.True(persisted.Enabled);
        Assert.Equal("running", fixture.Runtime.State);
        Assert.False(fixture.Runtime.Registry.WorkerDraining);
        Assert.True(fixture.Runtime.Registry.TryReserve("Project"));
        fixture.Runtime.Registry.Release("Project");
        var diagnostic = Assert.Single(fixture.Runtime.Events.ReadRecent(), item => item.Type == "observer.reporting");
        Assert.DoesNotContain(local.Credential, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("standalone", fixture.Config.Projects.Ownership);
        Assert.False(fixture.Config.Server.Enabled);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DisconnectWaitsForInFlightDeliveryAndPreventsCredentialResurrection()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(Local(), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            calls++;
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return Json(new { state = "accepted" });
        }));
        using var client = new StandaloneReportingClient(http);
        await using var reporter = fixture.Reporter(client);
        var delivery = reporter.ReportOnceAsync(CancellationToken.None);
        await entered.Task;
        var cli = new StandaloneReportingCli(client, new StringWriter(), _ => throw new InvalidOperationException("No input expected."));
        var disconnect = cli.ExecuteAsync(fixture.Command("disable"), CancellationToken.None);
        Assert.False(disconnect.IsCompleted);
        release.SetResult();
        await delivery;
        Assert.Equal(0, await disconnect);
        await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(File.Exists(fixture.Store.FilePath));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ShutdownCancelsAndJoinsReportingExchange()
    {
        using var fixture = new Fixture();
        await fixture.Store.SaveAsync(Local(), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.SetResult(); }
            return Json(new { state = "accepted" });
        }));
        using var client = new StandaloneReportingClient(http);
        var reporter = fixture.Reporter(client);
        reporter.Start();
        await entered.Task;
        await reporter.DisposeAsync();
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        Assert.True((await fixture.Store.LoadAsync(CancellationToken.None))?.Enabled);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("server")]
    [InlineData("rejected")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task RejectedOrMismatchedAcknowledgementCannotEnableReporting(string reason)
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            var local = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(ct));
            return reason == "rejected" ? new(HttpStatusCode.Unauthorized) : Json(new ObserverRegistration(1,
                reason == "identity" ? "obs_" + Guid.NewGuid().ToString("N") : local.ObserverId, ObserverProtocol.Mode,
                reason == "server" ? "https://other.example" : Server, Now, "awaiting-first-report"));
        }));
        using var client = new StandaloneReportingClient(http);
        var cli = new StandaloneReportingCli(client, new StringWriter(), _ => Task.FromResult("oba_" + new string('a', 64)));
        Assert.Equal(ProcessExitCodes.StartupFailure, await cli.ExecuteAsync(fixture.Command("connect", "--server", Server), CancellationToken.None));
        Assert.False((await fixture.Store.LoadAsync(CancellationToken.None))?.Accepted);
        Assert.False((await fixture.Store.LoadAsync(CancellationToken.None))?.Enabled);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task InvalidCredentialsAndCancelledInputNeverCallServerOrDisableScheduling()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network access.")));
        using var client = new StandaloneReportingClient(http);
        var cli = new StandaloneReportingCli(client, new StringWriter(), _ => Task.FromCanceled<string>(new CancellationToken(true)));
        Assert.Equal(WorkerCliOutput.Cancelled, await cli.ExecuteAsync(fixture.Command("connect", "--server", Server), CancellationToken.None));
        Assert.False((await fixture.Store.LoadAsync(CancellationToken.None))?.Enabled);
        await File.WriteAllTextAsync(fixture.Store.FilePath, JsonSerializer.Serialize(Local() with { Credential = "invalid protected value" }));
        Assert.Equal(ProcessExitCodes.StartupFailure, await cli.ExecuteAsync(fixture.Command("status"), CancellationToken.None));
        await using var reporter = fixture.Reporter(client);
        await reporter.ReportOnceAsync(CancellationToken.None);
        Assert.True(fixture.Runtime.Registry.TryReserve("Project"));
        fixture.Runtime.Registry.Release("Project");
        var diagnostic = Assert.Single(fixture.Runtime.Events.ReadRecent(), item => item.Type == "observer.reporting");
        Assert.DoesNotContain("protected value", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task UnicodeDisplayFieldsCannotExceedSnapshotByteLimit()
    {
        using var fixture = new Fixture();
        for (var number = 0; number < 33; number++)
            await fixture.History.CreateAsync(new(Guid.NewGuid(), new string('界', 200), "owner/repo", number + 1, new string('界', 500), "branch", "main",
                Now.AddMinutes(-1), null, "Implementing", null, null, null, 0, [], null, null, null, null));
        var local = Local();
        var snapshot = await fixture.Runtime.ObserverSnapshotAsync(local.ObserverId, Now, CancellationToken.None);
        Assert.True(snapshot.IsValid());
        Assert.InRange(snapshot.Executions.Count, 1, ObserverProtocol.MaximumExecutions - 1);
        Assert.Null(snapshot.ActiveExecutions);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length <= ObserverProtocol.MaximumPayloadBytes);
        await fixture.Store.SaveAsync(local with { PendingSnapshot = snapshot }, CancellationToken.None);
        var retained = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(CancellationToken.None));
        Assert.Equal(snapshot.SnapshotId, retained.PendingSnapshot?.SnapshotId);
        Assert.Equal(snapshot.Executions.Count, retained.PendingSnapshot?.Executions.Count);
    }

    [Theory]
    [InlineData("connect --server http://observer.example")]
    [InlineData("connect --server https://observer.example/path")]
    [InlineData("connect --server https://user:pass@observer.example")]
    [InlineData("connect --server https://observer.example?token=value")]
    [InlineData("connect --server https://observer.example --token secret")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void InvalidOrSecretArgumentsAreRejectedBeforeIdentityOrNetwork(string arguments) =>
        Assert.Throws<ArgumentException>(() => StandaloneReportingCli.Parse(arguments.Split(' ')));

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public void ReportingYamlIsStrictAndDoesNotEnableManagedFlows()
    {
        using var fixture = new Fixture();
        Assert.Equal(Path.Combine(fixture.Root, "observer.json"), fixture.Config.Reporting.CredentialFile);
        var original = File.ReadAllText(fixture.ConfigurationPath);
        foreach (var invalid in new[] { "reporting:\n  unknown: true\n", "reporting:\n  intervalSeconds: 1\n", "reporting:\n  intervalSeconds: 30\n  intervalSeconds: 40\n", "reporting: null\n", "reporting:\n  credentialFile: ./worker.yml\n", "server:\n  identityFile: ./managed-id\nreporting:\n  credentialFile: ./managed-id.token\n" })
        {
            File.WriteAllText(fixture.ConfigurationPath, original[..original.IndexOf("reporting:", StringComparison.Ordinal)] + invalid);
            Assert.Throws<InvalidDataException>(() => GlobalWorkerConfiguration.Load(fixture.ConfigurationPath));
        }
        var command = WorkerCommandLine.Parse(["reporting", "connect", "--server", Server]);
        var start = WorkerServiceAdministrationContext.CreateStartInfo(command);
        Assert.Contains("--pty", start.ArgumentList);
        Assert.DoesNotContain("--pipe", start.ArgumentList);
        Assert.Equal("reporting", command.Command);
        var writer = new StringWriter();
        WorkerCommandHelp.Write("reporting", writer);
        Assert.Contains("hidden", writer.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    [Theory]
    [InlineData("Completed", "success", "passed", "passed")]
    [InlineData("Failed", "failed", "failed: private output", "failed")]
    [InlineData("Blocked", "blocked", null, null)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task OutageAndLostAcknowledgementReplayAfterRestartWithoutChangingLocalHistory(
        string terminal, string outcome, string? validation, string? expectedValidation)
    {
        using var fixture = new Fixture();
        var local = Local();
        await fixture.Store.SaveAsync(local, CancellationToken.None);
        var registry = new CodexServer.SqliteRegistryStore(Path.Combine(fixture.Root, "server.db"), timeProvider: new Clock());
        await registry.InitializeAsync();
        var pairing = new ObserverPairingRequest(1, ObserverProtocol.Mode, local.ObserverId, Server);
        var authorization = Assert.IsType<ObserverPairingAuthorization>(await registry.CreateObserverAuthorizationAsync(pairing));
        Assert.NotNull(await registry.RedeemObserverAuthorizationAsync(pairing, authorization.Authorization, local.Credential));
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "Project", "owner/repo", 42, "Issue", "private-branch", "main",
            Now.AddMinutes(-2), null, "Implementing", null, "private prompt", null, 0, [], null, null, null, "private failure",
            RetryOfExecutionId: Guid.NewGuid(), AttemptNumber: 2, Resumed: true);
        await fixture.History.CreateAsync(entry);
        await fixture.History.CreateAsync(entry with { ExecutionId = Guid.NewGuid(), ServerExecutionId = "managed-execution" });
        var unavailable = false;
        var loseAcknowledgement = false;
        var events = new List<ObserverExecutionEvent>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (unavailable) throw new HttpRequestException("offline");
            var body = await Assert.IsAssignableFrom<HttpContent>(request.Content).ReadAsByteArrayAsync(ct);
            if (request.RequestUri?.AbsolutePath.EndsWith("/snapshots", StringComparison.Ordinal) == true)
            {
                Assert.Equal(CodexServer.ObserverIngestionResult.Accepted,
                    await registry.IngestObserverSnapshotAsync(Server, local.Credential, ObserverProtocol.ReadSnapshot(body), ct));
                return Json(new { state = "accepted" });
            }
            var report = ObserverProtocol.ReadEvent(body);
            events.Add(report);
            var result = await registry.IngestObserverEventAsync(Server, local.Credential, report, ct);
            Assert.True(result is CodexServer.ObserverIngestionResult.Accepted or CodexServer.ObserverIngestionResult.Duplicate);
            if (loseAcknowledgement) throw new HttpRequestException("lost acknowledgement");
            return Json(new { state = result == CodexServer.ObserverIngestionResult.Duplicate ? "duplicate" : "accepted" });
        }));
        using var client = new StandaloneReportingClient(http);
        await using (var reporter = fixture.Reporter(client))
        {
            await reporter.ReportOnceAsync(CancellationToken.None);
            unavailable = true;
            entry = entry with { State = terminal, CompletedAtUtc = Now.AddMinutes(-1), DurationMilliseconds = 60000, ValidationOutcome = validation };
            await fixture.History.UpdateAsync(entry);
            await reporter.ReportOnceAsync(CancellationToken.None);
        }
        unavailable = false;
        loseAcknowledgement = true;
        await using (var reporter = fixture.Reporter(client)) await reporter.ReportOnceAsync(CancellationToken.None);
        // Reopen the durable database as a new process would, without an execution runner or Git dependency.
        using var reopened = new ExecutionHistoryStore(Path.Combine(fixture.Root, "history.db"));
        var replay = Assert.Single(await reopened.PrepareObserverDeliveryAsync(local.ObserverId, Now, CancellationToken.None));
        Assert.Equal(events[^1], replay);
        loseAcknowledgement = false;
        await using (var reporter = fixture.Reporter(client))
        {
            await reporter.ReportOnceAsync(CancellationToken.None);
            await reporter.ReportOnceAsync(CancellationToken.None);
        }
        Assert.Equal(3, events.Count); // Active, terminal with lost response, exact duplicate replay.
        Assert.Equal(events[1], events[2]);
        Assert.Empty(await reopened.PrepareObserverDeliveryAsync(local.ObserverId, Now, CancellationToken.None));
        var inspection = Assert.IsType<CodexServer.ObserverInspection>(await registry.GetObserverInspectionAsync(local.ObserverId));
        var execution = Assert.Single(inspection.Executions).Execution;
        Assert.Equal(outcome, execution.Outcome);
        Assert.Equal(expectedValidation, execution.ValidationResult);
        Assert.Equal(entry.RetryOfExecutionId?.ToString("N"), execution.RetryOfLocalExecutionId);
        Assert.Equal(2, execution.AttemptNumber);
        Assert.True(execution.Resumed);
        Assert.Equal(entry.State, (await reopened.ReadExecutionAsync(entry.ExecutionId))?.State);
        Assert.Empty(await registry.GetExecutionsAsync());
        Assert.Equal(2, (await reopened.ReadAllAsync()).Count);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DeliveryCursorIsBoundedAndOldAcknowledgementsCannotConsumeNewUpdates()
    {
        using var fixture = new Fixture();
        var local = Local();
        for (var index = 0; index < 270; index++)
            await fixture.History.CreateAsync(new(Guid.NewGuid(), "Project", "owner/repo", index + 1, "Issue", "branch", "main",
                Now.AddMinutes(-300 + index), Now.AddMinutes(-299 + index), "Completed", 60000, null, "passed", 0, [], null, null, null, null));
        var count = 0;
        for (var round = 0; round < 9; round++)
        {
            var batch = await fixture.History.PrepareObserverDeliveryAsync(local.ObserverId, Now, CancellationToken.None);
            if (batch.Count == 0) break;
            Assert.InRange(batch.Count, 1, ObserverProtocol.MaximumExecutions);
            foreach (var report in batch) await fixture.History.AcknowledgeObserverDeliveryAsync(report, CancellationToken.None);
            count += batch.Count;
        }
        Assert.Equal(256, count);
        var active = new ExecutionHistoryEntry(Guid.NewGuid(), "Project", "owner/repo", 300, "Issue", "branch", "main",
            Now.AddMinutes(-1), null, "Implementing", null, null, null, 0, [], null, null, null, null);
        await fixture.History.CreateAsync(active);
        var previous = Assert.Single(await fixture.History.PrepareObserverDeliveryAsync(local.ObserverId, Now, CancellationToken.None));
        await fixture.History.UpdateAsync(active with { State = "Validating" });
        var next = Assert.Single(await fixture.History.PrepareObserverDeliveryAsync(local.ObserverId, Now, CancellationToken.None));
        Assert.True(next.ObservedAtUtc > previous.ObservedAtUtc);
        await fixture.History.AcknowledgeObserverDeliveryAsync(previous, CancellationToken.None);
        Assert.Equal(next, Assert.Single(await fixture.History.PrepareObserverDeliveryAsync(local.ObserverId, Now, CancellationToken.None)));
        var refreshed = Assert.Single(await fixture.History.PrepareObserverDeliveryAsync(local.ObserverId, Now.AddDays(8), CancellationToken.None));
        Assert.NotEqual(next.EventId, refreshed.EventId);
        Assert.Equal(next.Execution, refreshed.Execution);
    }

    [Theory]
    [InlineData("accepted", true)]
    [InlineData("duplicate", true)]
    [InlineData("unknown", false)]
    [InlineData("oversize", false)]
    [InlineData("transient", false)]
    [InlineData("refused", true)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DeliveryRequiresKnownAcknowledgementAndPermanentRefusalDoesNotPoisonBacklog(string response, bool settled)
    {
        using var fixture = new Fixture();
        var local = Local();
        await fixture.Store.SaveAsync(local, CancellationToken.None);
        await fixture.History.CreateAsync(new(Guid.NewGuid(), "Project", "owner/repo", 42, "Issue", "branch", "main",
            Now.AddMinutes(-2), Now.AddMinutes(-1), "Completed", 60000, null, "passed", 0, [], null, null, null, null));
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri?.AbsolutePath.EndsWith("/snapshots", StringComparison.Ordinal) == true ? Json(new { state = "accepted" }) : response switch
            {
                "transient" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "refused" => new HttpResponseMessage(HttpStatusCode.Conflict),
                "oversize" => Json(new { state = "accepted", padding = new string('x', 1024) }),
                _ => Json(new { state = response })
            })));
        using var client = new StandaloneReportingClient(http);
        await using var reporter = fixture.Reporter(client);
        await reporter.ReportOnceAsync(CancellationToken.None);
        var pending = await fixture.History.PrepareObserverDeliveryAsync(local.ObserverId, Now, CancellationToken.None);
        Assert.Equal(settled ? 0 : 1, pending.Count);
        Assert.Equal("Completed", Assert.Single(await fixture.History.ReadAllAsync()).State);
        Assert.Equal("running", fixture.Runtime.State);
        if (response == "refused") Assert.Equal("rejected", (await fixture.Store.LoadAsync(CancellationToken.None))?.LastResult);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DashboardAuthorizationCliAndReporterRecoverWithoutManagedEnrollment(bool expireFirst)
    {
        using var fixture = new Fixture();
        var configurationBefore = await File.ReadAllTextAsync(fixture.ConfigurationPath);
        var clock = new PairingClock();
        var registry = new SqliteRegistryStore(Path.Combine(fixture.Root, "registry.db"), timeProvider: clock);
        await registry.InitializeAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new ServerConfiguration { AdministrationOrigin = Server });
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<AdministrationSessions>();
        builder.Services.AddSingleton<IRegistryStore>(registry);
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "observer-lifecycle-test-management");
        try
        {
            await using var app = builder.Build();
            ServerApplication.MapObserverPairing(app);
            await app.StartAsync();
            var loopback = new Uri(app.Urls.Single());
            using var admin = new HttpClient { BaseAddress = loopback };
            admin.DefaultRequestHeaders.Authorization = new("Bearer", "observer-lifecycle-test-management");
            using var transport = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
            var unavailable = false;
            var loseRedemptionResponse = true;
            var calls = 0;
            // Only the test transport maps the exact HTTPS contract origin to ephemeral loopback HTTP.
            using var http = new HttpClient(new Handler(async (request, ct) =>
            {
                Assert.Equal(Server, request.RequestUri?.GetLeftPart(UriPartial.Authority));
                calls++;
                if (unavailable) throw new HttpRequestException("Simulated Server outage.");
                var path = Assert.IsType<Uri>(request.RequestUri).PathAndQuery;
                request.RequestUri = new Uri(loopback, path);
                var response = await transport.SendAsync(request, ct);
                if (path.EndsWith("/redeem", StringComparison.Ordinal) && response.IsSuccessStatusCode && loseRedemptionResponse)
                {
                    loseRedemptionResponse = false;
                    response.Dispose();
                    throw new HttpRequestException("Simulated lost redemption acknowledgement.");
                }
                return response;
            }));
            using var client = new StandaloneReportingClient(http);
            await using var reporter = new StandaloneReporting(fixture.Config, fixture.Runtime, clock, CancellationToken.None, client);
            await reporter.ReportOnceAsync(CancellationToken.None);
            Assert.Equal(0, calls);
            Assert.False(File.Exists(fixture.Store.FilePath));
            Assert.Empty(await registry.ListObserversAsync());

            ObserverPairingAuthorization? authorization = null;
            var output = new StringWriter();
            var cli = new StandaloneReportingCli(client, output, async ct =>
            {
                var local = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(ct));
                var request = new ObserverPairingRequest(1, ObserverProtocol.Mode, local.ObserverId, Server);
                using var response = await admin.PostAsJsonAsync("/api/v1/observers/pairing/authorize", request, ct);
                response.EnsureSuccessStatusCode();
                authorization = Assert.IsType<ObserverPairingAuthorization>(await response.Content.ReadFromJsonAsync<ObserverPairingAuthorization>(ct));
                if (expireFirst) clock.Now = authorization.ExpiresAtUtc;
                return authorization.Authorization;
            });
            var connect = fixture.Command("connect", "--server", Server);
            Assert.Equal(ProcessExitCodes.StartupFailure, await cli.ExecuteAsync(connect, CancellationToken.None));
            var pending = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(CancellationToken.None));
            Assert.False(pending.Accepted);
            if (expireFirst)
            {
                Assert.Equal("pairing-expired", Assert.Single(await registry.ListObserversAsync()).RegistrationState);
                await reporter.ReportOnceAsync(CancellationToken.None);
                Assert.Equal(1, calls); // Pending credentials cannot send reports.
                expireFirst = false;
                Assert.Equal(ProcessExitCodes.StartupFailure, await cli.ExecuteAsync(connect, CancellationToken.None));
            }
            Assert.Equal("awaiting-first-report", Assert.Single(await registry.ListObserversAsync()).RegistrationState);
            var recover = new StandaloneReportingCli(client, output, _ => Task.FromResult(""));
            Assert.Equal(0, await recover.ExecuteAsync(connect, CancellationToken.None));
            Assert.Equal(0, await recover.ExecuteAsync(connect, CancellationToken.None));
            var paired = Assert.IsType<StandaloneReportingCredential>(await fixture.Store.LoadAsync(CancellationToken.None));
            Assert.Equal(pending.ObserverId, paired.ObserverId);
            Assert.Equal(pending.Credential, paired.Credential);
            using (var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/observers/pairing/redeem"))
            {
                replay.Headers.Authorization = new("Bearer", Assert.IsType<ObserverPairingAuthorization>(authorization).Authorization);
                replay.Headers.Add("X-Codex-Observer-Token", paired.Credential);
                replay.Content = JsonContent.Create(new ObserverPairingRequest(1, ObserverProtocol.Mode, paired.ObserverId, Server));
                using var response = await admin.SendAsync(replay);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            await fixture.History.CreateAsync(new(Guid.NewGuid(), "Project", "owner/repo", 42, "Issue", "branch", "main",
                Now.AddMinutes(-2), Now.AddMinutes(-1), "Completed", 60000, null, "passed", 0, [], null, null, null, null));
            await reporter.ReportOnceAsync(CancellationToken.None);
            var detail = Assert.IsType<ObserverDetail>(await admin.GetFromJsonAsync<ObserverDetail>($"/api/v1/observers/{paired.ObserverId}"));
            Assert.Equal("reporting", detail.Observer.RegistrationState);
            Assert.Equal("fresh", detail.Observer.Freshness);
            Assert.Equal(clock.Now, detail.Observer.SnapshotReceivedAtUtc);
            Assert.Single(await admin.GetFromJsonAsync<ObserverExecutionObservation[]>($"/api/v1/observers/{paired.ObserverId}/history") ?? []);

            unavailable = true;
            await reporter.ReportOnceAsync(CancellationToken.None);
            Assert.Equal("unavailable", (await fixture.Store.LoadAsync(CancellationToken.None))?.LastResult);
            unavailable = false;
            Assert.Equal(0, await recover.ExecuteAsync(connect, CancellationToken.None));
            await reporter.ReportOnceAsync(CancellationToken.None);
            Assert.Equal("reporting", (await fixture.Store.LoadAsync(CancellationToken.None))?.LastResult);
            Assert.Single(await registry.ListObserversAsync());
            using (var revoked = await admin.PostAsync($"/api/v1/observers/{paired.ObserverId}/revoke", null))
                Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            await reporter.ReportOnceAsync(CancellationToken.None);
            Assert.Equal("unauthorized", (await fixture.Store.LoadAsync(CancellationToken.None))?.LastResult);
            Assert.Equal(ProcessExitCodes.StartupFailure, await recover.ExecuteAsync(connect, CancellationToken.None));
            Assert.Equal(0, await recover.ExecuteAsync(fixture.Command("disconnect"), CancellationToken.None));
            var callsBeforeDisable = calls;
            await reporter.ReportOnceAsync(CancellationToken.None);
            Assert.Equal(0, await recover.ExecuteAsync(fixture.Command("status"), CancellationToken.None));
            Assert.Equal(callsBeforeDisable, calls);
            Assert.False(File.Exists(fixture.Store.FilePath));
            Assert.Single((await registry.GetObserverInspectionAsync(paired.ObserverId))?.Executions ?? []);
            Assert.Empty(await registry.GetWorkersAsync());
            Assert.Empty(await registry.GetExecutionsAsync());
            Assert.Equal("running", fixture.Runtime.State);
            Assert.Equal("Completed", Assert.Single(await fixture.History.ReadAllAsync()).State);
            Assert.Equal(configurationBefore, await File.ReadAllTextAsync(fixture.ConfigurationPath));
            Assert.DoesNotContain(paired.Credential, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Assert.IsType<ObserverPairingAuthorization>(authorization).Authorization, output.ToString(), StringComparison.Ordinal);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    private sealed class PairingClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = StandaloneReportingTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();
        internal string Root => directory.Path;
        internal string ConfigurationPath => Path.Combine(Root, "worker.yml");
        internal GlobalWorkerConfiguration Config { get; }
        internal ExecutionHistoryStore History { get; }
        internal WorkerRuntimeReadModel Runtime { get; }
        internal StandaloneReportingStore Store { get; }
        internal Fixture()
        {
            File.WriteAllText(ConfigurationPath, "worker:\n  maxParallelTasks: 2\nprojects:\n  directory: ./projects\nreporting:\n  credentialFile: ./observer.json\n  intervalSeconds: 10\n");
            Config = GlobalWorkerConfiguration.Load(ConfigurationPath);
            Store = new(Config.Reporting.CredentialFile);
            History = new(Path.Combine(Root, "history.db"));
            var project = new WorkerConfiguration { Project = new() { Name = "Project", Repository = "owner/repo", Directory = Root },
                Environment = new() { Variables = new Dictionary<string, string> { ["TOKEN"] = "private environment" } } };
            Runtime = new(Config, [(Path.Combine(Root, "private.yml"), project)], History);
            Runtime.Lifecycle.SetExecutionReadiness(true);
            Runtime.State = "running";
        }
        internal StandaloneReporting Reporter(StandaloneReportingClient client) => new(Config, Runtime, new Clock(), CancellationToken.None, client);
        internal WorkerCommandLine Command(params string[] arguments) => new("reporting", ConfigurationPath, arguments);
        public void Dispose() { History.Dispose(); directory.Dispose(); }
    }
}
