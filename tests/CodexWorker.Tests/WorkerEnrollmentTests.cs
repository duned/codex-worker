namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexProvisioning;
using CodexServer;
using CodexWorker;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerEnrollmentTests
{
    [Fact]
    public async Task GenericEnrollmentCannotReplaceIdentityAndTargetedAuthorizationIsOneUseAndFresh()
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var worker = Registration(Guid.NewGuid().ToString("N"));
        var original = new string('a', 64);
        var replacement = new string('b', 64);
        var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        Assert.True(await store.BootstrapWorkerAsync(bootstrap, worker, original));
        var generic = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        Assert.False(await store.BootstrapWorkerAsync(generic, worker, replacement));
        Assert.True(await store.IsWorkerTokenValidAsync(worker.WorkerId, original));
        // Rejection does not consume another node's generic authorization.
        Assert.True(await store.BootstrapWorkerAsync(generic, Registration(Guid.NewGuid().ToString("N")), replacement));
        var authorization = await store.CreateWorkerAuthorizationAsync(worker.WorkerId, "rotate", TimeSpan.FromMinutes(15));
        Assert.False(await store.BootstrapWorkerAsync(authorization, Registration(Guid.NewGuid().ToString("N")), replacement, operation: "rotate"));
        Assert.False(await store.BootstrapWorkerAsync(authorization, worker, replacement, operation: "recover"));
        Assert.False(await store.BootstrapWorkerAsync(authorization, worker, original, operation: "rotate"));
        Assert.True(await store.BootstrapWorkerAsync(authorization, worker, replacement, operation: "rotate"));
        Assert.False(await store.IsWorkerTokenValidAsync(worker.WorkerId, original));
        Assert.True(await store.IsWorkerTokenValidAsync(worker.WorkerId, replacement));
        Assert.False(await store.BootstrapWorkerAsync(authorization, worker, new string('c', 64), operation: "rotate"));
        var restarted = new SqliteRegistryStore(temporary.Database);
        await restarted.InitializeAsync();
        Assert.True(await restarted.IsWorkerTokenValidAsync(worker.WorkerId, replacement));
        Assert.NotNull(await restarted.GetWorkerAsync(worker.WorkerId));
    }

    [Fact]
    public async Task LocalOperatorCommandIssuesAuthorizationForTheExactWorkerAndOperation()
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var worker = Registration(Guid.NewGuid().ToString("N"));
        Assert.True(await store.BootstrapWorkerAsync(await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)), worker, new string('a', 64)));
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(0, await CodexServer.Program.Main(["worker-token", "authorize", worker.WorkerId, "rotate", temporary.Database]));
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
        var authorization = output.ToString().Trim();
        Assert.True(WorkerEnrollmentProtocol.ValidToken(authorization));
        Assert.DoesNotContain(authorization, error.ToString());
        Assert.False(await store.BootstrapWorkerAsync(authorization, worker, new string('b', 64), operation: "recover"));
        Assert.True(await store.BootstrapWorkerAsync(authorization, worker, new string('b', 64), operation: "rotate"));
    }

    [Fact]
    public async Task ConcurrentEnrollmentCommitsOnlyOneCredential()
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var worker = Registration(Guid.NewGuid().ToString("N"));
        var authorizations = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15))));
        var tokens = Enumerable.Range(0, 4).Select(i => new string((char)('a' + i), 64)).ToArray();
        var results = await Task.WhenAll(authorizations.Select((authorization, i) => store.BootstrapWorkerAsync(authorization, worker, tokens[i])));
        Assert.Single(results, result => result);
        for (var index = 0; index < results.Length; index++)
            Assert.Equal(results[index], await store.IsWorkerTokenValidAsync(worker.WorkerId, tokens[index]));
        Assert.Single(await store.GetWorkersAsync());
    }

    [Theory]
    [InlineData("enroll")]
    [InlineData("rotate")]
    [InlineData("recover")]
    public async Task LostCommitResponseIsReconciledWithRetainedMaterialBeforeUsingNewAuthorization(string operation)
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var settings = temporary.Settings("https://server.example");
        using var handler = new RegistryHandler(store);
        using var http = new HttpClient(handler);
        var client = new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create());
        string? oldToken = null;
        if (operation != "enroll")
        {
            await client.BootstrapAsync(settings, 1, await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)), CancellationToken.None);
            oldToken = WorkerAuthentication.GetToken(settings);
            if (operation == "recover") await store.RevokeWorkerTokenAsync(await WorkerIdentity.LoadAsync(temporary.Identity));
        }
        var identity = await WorkerIdentity.LoadOrCreateAsync(temporary.Identity);
        var authorization = operation == "enroll" ? await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)) :
            await store.CreateWorkerAuthorizationAsync(identity, operation, TimeSpan.FromMinutes(15));
        handler.LoseNextResponse = true;
        await Assert.ThrowsAsync<WorkerStartupException>(() => client.BootstrapAsync(settings, 1, authorization, operation, CancellationToken.None));
        var retained = Assert.IsType<WorkerPendingRegistration>(WorkerPendingRegistration.Load(temporary.Identity));
        Assert.True(await store.IsWorkerTokenValidAsync(identity, retained.Token));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temporary.Identity + ".pending"));
        if (oldToken is not null) Assert.Equal(oldToken, WorkerAuthentication.GetToken(settings));
        var untouched = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        var posts = handler.BootstrapRequests;
        // A new process/client uses the durable pending material, without another bootstrap request.
        await new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, untouched, operation, CancellationToken.None);
        Assert.Equal(posts, handler.BootstrapRequests);
        Assert.Equal(retained.Token, WorkerAuthentication.GetToken(settings));
        Assert.Null(WorkerPendingRegistration.Load(temporary.Identity));
        if (oldToken is not null) Assert.False(await store.IsWorkerTokenValidAsync(identity, oldToken));
        Assert.True(await store.RevokeWorkerBootstrapTokenAsync(untouched));
        if (!OperatingSystem.IsWindows())
            foreach (var suffix in new[] { "", ".token", ".server" })
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temporary.Identity + suffix));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"contractVersion\":999,\"workerId\":\"wrong\"}")]
    [InlineData("{\"contractVersion\":1,\"workerId\":\"wrong\"}")]
    public async Task UnconfirmedAcknowledgementRetainsPendingAndLastWorkingFiles(string acknowledgement)
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var settings = temporary.Settings("https://server.example");
        using var handler = new RegistryHandler(store);
        using var http = new HttpClient(handler);
        var client = new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create());
        await client.BootstrapAsync(settings, 1, await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)), CancellationToken.None);
        var oldToken = WorkerAuthentication.GetToken(settings);
        var oldAssociation = await File.ReadAllTextAsync(temporary.Identity + ".server");
        var identity = await WorkerIdentity.LoadAsync(temporary.Identity);
        handler.NextAcknowledgement = acknowledgement;
        var authorization = await store.CreateWorkerAuthorizationAsync(identity, "rotate", TimeSpan.FromMinutes(15));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.BootstrapAsync(settings, 1,
            authorization, "rotate", CancellationToken.None));
        Assert.Equal(oldToken, WorkerAuthentication.GetToken(settings));
        Assert.Equal(oldAssociation, await File.ReadAllTextAsync(temporary.Identity + ".server"));
        Assert.NotNull(WorkerPendingRegistration.Load(temporary.Identity));
        await client.BootstrapAsync(settings, 1, "unused-authorization", "rotate", CancellationToken.None);
        Assert.NotEqual(oldToken, WorkerAuthentication.GetToken(settings));
    }

    [Fact]
    public async Task UnassociatedCredentialIsNeverReusedAtRequestedEndpoint()
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var settings = temporary.Settings("https://destination.example");
        await WorkerIdentity.LoadOrCreateAsync(temporary.Identity);
        var original = await WorkerAuthentication.LoadOrCreateTokenAsync(temporary.Identity);
        using var handler = new RegistryHandler(store);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<WorkerStartupException>(() => new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create())
            .BootstrapAsync(settings, 1, "unused", CancellationToken.None));
        Assert.Empty(handler.Tokens);
        Assert.Equal(original, WorkerAuthentication.GetToken(settings));
        Assert.Null(WorkerPendingRegistration.Load(temporary.Identity));
    }

    [Fact]
    public async Task FailedReassociationPreservesActiveStateAndNeverDisclosesPreviousCredential()
    {
        using var temporary = new TemporaryState();
        var original = await temporary.RegistryAsync();
        var destination = new SqliteRegistryStore(Path.Combine(temporary.Path, "destination.db"));
        await destination.InitializeAsync();
        var settings = temporary.Settings("https://original.example");
        using var firstHandler = new RegistryHandler(original);
        using var firstHttp = new HttpClient(firstHandler);
        await new WorkerRegistrationClient(firstHttp, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1,
            await original.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)), CancellationToken.None);
        var identity = await WorkerIdentity.LoadAsync(temporary.Identity);
        var oldToken = WorkerAuthentication.GetToken(settings);
        settings.Url = "https://destination.example";
        using var handler = new RegistryHandler(destination);
        using var http = new HttpClient(handler);
        var client = new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create());
        await Assert.ThrowsAsync<WorkerStartupException>(() => client.BootstrapAsync(settings, 1, "unused", CancellationToken.None));
        Assert.Empty(handler.Tokens);
        await Assert.ThrowsAsync<WorkerStartupException>(() => client.BootstrapAsync(settings, 1, "invalid", "associate", CancellationToken.None));
        Assert.Equal("https://original.example", settings.EffectiveUrl);
        Assert.Equal(oldToken, WorkerAuthentication.GetToken(settings));
        Assert.DoesNotContain(oldToken, handler.Tokens);
        var pending = Assert.IsType<WorkerPendingRegistration>(WorkerPendingRegistration.Load(temporary.Identity));
        Assert.NotEqual(oldToken, pending.Token);
        await client.BootstrapAsync(settings, 1, await destination.CreateWorkerAuthorizationAsync(identity, "associate", TimeSpan.FromMinutes(15)), "associate", CancellationToken.None);
        Assert.Equal("https://destination.example", settings.EffectiveUrl);
        Assert.Equal(pending.Token, WorkerAuthentication.GetToken(settings));
        Assert.DoesNotContain(oldToken, handler.Tokens);
        Assert.True(await original.IsWorkerTokenValidAsync(identity, oldToken));
    }

    [Fact]
    public async Task InterruptedPublicationBlocksRuntimeAndCompletesFromVerifiedPendingState()
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var settings = temporary.Settings("https://server.example");
        var identity = await WorkerIdentity.LoadOrCreateAsync(temporary.Identity);
        var token = new string('p', 64);
        Assert.True(await store.BootstrapWorkerAsync(await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)), Registration(identity), token));
        var pending = new WorkerPendingRegistration(identity, settings.Url, "enroll", token, true);
        await WorkerRegistrationFile.ReplaceAsync(temporary.Identity + ".pending", JsonSerializer.Serialize(pending), CancellationToken.None);
        await WorkerRegistrationFile.ReplaceAsync(temporary.Identity + ".token", token, CancellationToken.None);
        Assert.Throws<WorkerStartupException>(() => WorkerAuthentication.GetToken(settings));
        using var handler = new RegistryHandler(store);
        using var http = new HttpClient(handler);
        await new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, "unused", CancellationToken.None);
        Assert.Equal(0, handler.BootstrapRequests);
        Assert.Equal(token, WorkerAuthentication.GetToken(settings));
        Assert.Equal(settings.Url, settings.EffectiveUrl);
    }

    [Fact]
    public async Task ExpiredTargetedAuthorizationDoesNotChangeCredentialOrVisibility()
    {
        using var temporary = new TemporaryState();
        var clock = new EnrollmentClock();
        var store = new SqliteRegistryStore(temporary.Database, timeProvider: clock);
        await store.InitializeAsync();
        var worker = Registration(Guid.NewGuid().ToString("N"));
        var original = new string('a', 64);
        Assert.True(await store.BootstrapWorkerAsync(await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)), worker, original));
        var authorization = await store.CreateWorkerAuthorizationAsync(worker.WorkerId, "recover", TimeSpan.FromMinutes(15));
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.False(await store.BootstrapWorkerAsync(authorization, worker with { DisplayName = "replacement" }, new string('b', 64), operation: "recover"));
        Assert.True(await store.IsWorkerTokenValidAsync(worker.WorkerId, original));
        Assert.Equal(worker.DisplayName, (await store.GetWorkerAsync(worker.WorkerId))?.DisplayName);
        Assert.False(await store.BootstrapWorkerAsync(await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15)),
            Registration(Guid.NewGuid().ToString("N")), "short"));
    }

    [Fact]
    public async Task ConcurrentLocalOperationsCannotReplacePendingMaterialOrReadMixedActiveState()
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var settings = temporary.Settings("https://server.example");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RegistryHandler(store) { BootstrapGate = async cancellationToken =>
        {
            reached.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        } };
        using var http = new HttpClient(handler);
        var client = new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create());
        var authorization = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        var first = client.BootstrapAsync(settings, 1, authorization, CancellationToken.None);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pending = Assert.IsType<WorkerPendingRegistration>(WorkerPendingRegistration.Load(temporary.Identity));
            await Assert.ThrowsAsync<IOException>(() => client.BootstrapAsync(settings, 1, authorization, CancellationToken.None));
            Assert.Throws<WorkerStartupException>(() => WorkerAuthentication.GetConnection(settings));
            Assert.Equal(pending, WorkerPendingRegistration.Load(temporary.Identity));
        }
        finally
        {
            release.SetResult();
            await first;
        }
    }

    [Fact]
    public async Task InvalidPendingEndpointIsRejectedWithoutRepairingOrReplacingFiles()
    {
        using var temporary = new TemporaryState();
        var store = await temporary.RegistryAsync();
        var settings = temporary.Settings("https://server.example");
        var identity = await WorkerIdentity.LoadOrCreateAsync(temporary.Identity);
        var original = await WorkerAuthentication.LoadOrCreateTokenAsync(temporary.Identity);
        await WorkerRegistrationFile.ReplaceAsync(temporary.Identity + ".server", settings.Url, CancellationToken.None);
        var pendingBody = JsonSerializer.Serialize(new { WorkerId = identity, Endpoint = (string?)null,
            Operation = "rotate", Token = new string('p', 64), Verified = false });
        await WorkerRegistrationFile.ReplaceAsync(temporary.Identity + ".pending", pendingBody, CancellationToken.None);
        using var handler = new RegistryHandler(store);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WorkerRegistrationClient(http, TestCapabilityDiscovery.Create())
            .BootstrapAsync(settings, 1, "unused", "rotate", CancellationToken.None));
        Assert.Empty(handler.Tokens);
        Assert.Equal(original, await File.ReadAllTextAsync(temporary.Identity + ".token"));
        Assert.Equal(pendingBody, await File.ReadAllTextAsync(temporary.Identity + ".pending"));
        Assert.Equal(settings.Url, await File.ReadAllTextAsync(temporary.Identity + ".server"));
    }

    [Fact]
    public async Task CancelledPublicationPreservesExistingFileAndRemovesTemporaryMaterial()
    {
        using var temporary = new TemporaryState();
        Directory.CreateDirectory(temporary.Path);
        var path = temporary.Identity + ".token";
        await WorkerRegistrationFile.ReplaceAsync(path, "original", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkerRegistrationFile.ReplaceAsync(path, "replacement", cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    private sealed class EnrollmentClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private static WorkerRegistrationRequest Registration(string identity) => new(2, identity, "worker", "1.0", "test", 1, []);

    private sealed class RegistryHandler(IRegistryStore store) : HttpMessageHandler
    {
        public bool LoseNextResponse { get; set; }
        public string? NextAcknowledgement { get; set; }
        public int BootstrapRequests { get; private set; }
        public Func<CancellationToken, Task>? BootstrapGate { get; init; }
        public List<string> Tokens { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var registration = await Assert.IsAssignableFrom<HttpContent>(request.Content).ReadFromJsonAsync<WorkerRegistrationRequest>(cancellationToken: cancellationToken);
            Assert.NotNull(registration);
            var bearer = request.Headers.Authorization?.Parameter ?? "";
            Tokens.Add(bearer);
            if (request.Method == HttpMethod.Post)
            {
                BootstrapRequests++;
                if (BootstrapGate is not null) await BootstrapGate(cancellationToken);
                var token = request.Headers.GetValues("X-Codex-Worker-Token").Single();
                Tokens.Add(token);
                var operation = request.Headers.GetValues("X-Codex-Worker-Operation").Single();
                if (!await store.BootstrapWorkerAsync(bearer, registration, token, cancellationToken, operation))
                    return new(HttpStatusCode.Unauthorized);
                if (LoseNextResponse)
                {
                    LoseNextResponse = false;
                    throw new HttpRequestException("Simulated lost response after commit.");
                }
                var body = NextAcknowledgement;
                NextAcknowledgement = null;
                return new(HttpStatusCode.OK) { Content = body is null ?
                    JsonContent.Create(new WorkerEnrollmentAcknowledgement(1, registration.WorkerId)) : new StringContent(body) };
            }
            if (!await store.IsWorkerTokenValidAsync(registration.WorkerId, bearer, cancellationToken)) return new(HttpStatusCode.Unauthorized);
            await store.RegisterWorkerAsync(registration, cancellationToken);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(await store.GetWorkerAsync(registration.WorkerId, cancellationToken)) };
        }
    }

    private sealed class TemporaryState : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "enrollment-" + Guid.NewGuid().ToString("N"));
        public string Database => System.IO.Path.Combine(Path, "registry.db");
        public string Identity => System.IO.Path.Combine(Path, "worker-id");
        public WorkerServerSettings Settings(string endpoint) => new() { Enabled = true, Url = endpoint, IdentityFile = Identity };
        public async Task<SqliteRegistryStore> RegistryAsync()
        {
            var store = new SqliteRegistryStore(Database);
            await store.InitializeAsync();
            return store;
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
