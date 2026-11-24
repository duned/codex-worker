namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodexProvisioning;
using CodexServer;
using CodexWorker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServerWorkerCapability = CodexServer.WorkerCapability;
using WorkerCapability = CodexServer.WorkerCapability;

public sealed partial class CodexServerTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Fact]
    public async Task CleanWorkerBootstrapRegistersVisibleWorkerAndEstablishesHeartbeatAuthentication()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "bootstrap.db");
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "bootstrap-management-secret");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, database));
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
            var identityPath = Path.Combine(temporary.Path, "worker-id");
            var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = identityPath };
            using var client = new HttpClient();

            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, bootstrap, CancellationToken.None);
            // Replaying after a lost success response recovers through the durable Worker credential.
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, bootstrap, CancellationToken.None);

            var identity = await WorkerIdentity.LoadOrCreateAsync(identityPath);
            Assert.Single(await store.GetWorkersAsync());
            Assert.True(await store.IsWorkerTokenValidAsync(identity, WorkerAuthentication.GetToken(settings)));
            var delivery = app.Services.GetRequiredService<ICredentialStore>();
            Assert.Equal("active", (await delivery.GetWorkerDeliveryAuthorizationStatusAsync(identity)).Status);
            Assert.True(File.Exists(identityPath + ".delivery"));
            Assert.False(await delivery.IsWorkerDeliveryTokenValidAsync(identity, WorkerAuthentication.GetToken(settings)));
            var priorDelivery = await File.ReadAllTextAsync(identityPath + ".delivery");
            var association = await store.CreateWorkerAuthorizationAsync(identity, "associate", TimeSpan.FromMinutes(15));
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, association, "associate", CancellationToken.None);
            Assert.Equal("active", (await delivery.GetWorkerDeliveryAuthorizationStatusAsync(identity)).Status);
            Assert.NotEqual(priorDelivery, await File.ReadAllTextAsync(identityPath + ".delivery"));
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).HeartbeatAsync(settings, 1, 0, [], "running", CancellationToken.None);
            Assert.False(await EnrollWorkerAsync(store, bootstrap, identity, WorkerAuthentication.GetToken(settings)));
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            management.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "bootstrap-management-secret");
            using var workers = JsonDocument.Parse(await management.GetStringAsync("/api/v1/workers"));
            Assert.Single(workers.RootElement.EnumerateArray());
            Assert.Equal(identity, workers.RootElement[0].GetProperty("workerId").GetString());
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Fact]
    public async Task ManagedNotReadyHeartbeatIsAcceptedAndWorkerRemainsOnlineButCannotReceiveWork()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "not-ready-management-token");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "not-ready.db")));
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var project = await store.CreateProjectAsync(new CentralProjectDefinition("Agent project", "team/project", "main", "",
                [new("agent-provider", "codex")]));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "130")));
            var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
            var identityPath = Path.Combine(temporary.Path, "worker-id");
            var settings = new WorkerServerSettings
            {
                Enabled = true,
                Url = url,
                IdentityFile = identityPath
            };
            using var workerClient = new HttpClient { BaseAddress = new Uri(url) };
            var registration = new WorkerRegistrationClient(workerClient, TestCapabilityDiscovery.Create());
            await registration.BootstrapAsync(settings, 1, bootstrap, CancellationToken.None);
            var workerId = await WorkerIdentity.LoadOrCreateAsync(identityPath);

            await registration.HeartbeatAsync(settings, 1, 0, [], WorkerLifecycleStates.NotReady, CancellationToken.None, []);
            await registration.HeartbeatAsync(settings, 1, 0, [], WorkerLifecycleStates.NotReady, CancellationToken.None, []);

            var worker = Assert.IsType<WorkerRegistrationResponse>(await store.GetWorkerAsync(workerId));
            Assert.Equal("online", worker.Availability);
            Assert.Equal(WorkerLifecycleStates.NotReady, worker.LifecycleState);
            using var managementClient = new HttpClient { BaseAddress = new Uri(url) };
            managementClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", "not-ready-management-token");
            var diagnostics = await managementClient.GetFromJsonAsync<WorkerDiagnostics>($"/api/v1/workers/{workerId}/diagnostics");
            Assert.NotNull(diagnostics);
            Assert.Equal("online", diagnostics.Availability);
            Assert.False(Assert.Single(diagnostics.Projects).IsEligible);
            Assert.Contains(diagnostics.Reasons, reason => reason.Contains("AI agent unavailable", StringComparison.Ordinal));

            workerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", WorkerAuthentication.GetToken(settings));
            var assignmentResponse = await workerClient.PostAsJsonAsync($"/api/v1/workers/{workerId}/assignments/request",
                new WorkerAssignmentRequest(workerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 }));
            Assert.Equal(HttpStatusCode.OK, assignmentResponse.StatusCode);
            var assignment = await assignmentResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>();
            Assert.NotNull(assignment);
            Assert.False(assignment.HasWork);
            Assert.Equal("Queued", Assert.Single(await store.GetExecutionsAsync()).State);

            var invalidHeartbeat = new
            {
                contractVersion = 2,
                workerId,
                workerVersion = "1.0",
                lifecycleState = "unknown-state",
                activeExecutions = 0,
                maximumCapacity = 1,
                capabilities = Array.Empty<object>(),
                activeProjects = Array.Empty<string>()
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await workerClient.PostAsJsonAsync(
                $"/api/v1/workers/{workerId}/heartbeat", invalidHeartbeat)).StatusCode);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(null)]
    [InlineData("custom/registry.db")]
    public async Task OperatorCommandUsesServiceDatabaseAndFreshWorkerCliCanRetry(string? databaseOverride)
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorDirectory = Environment.GetEnvironmentVariable("Server__DataDirectory");
        var priorDatabase = Environment.GetEnvironmentVariable("Server__DatabasePath");
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var originalInput = Console.In;
        using var tokenOutput = new StringWriter();
        using var diagnostics = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", temporary.Path);
            Environment.SetEnvironmentVariable("Server__DatabasePath", databaseOverride);
            await using var app = await ServerApplication.BuildAsync(
                [$"--Server:ListenUrl={url}", "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            // The installed helper supplies the same environment to this entry point
            // as systemd supplies to the service. No database argument is needed.
            Console.SetOut(tokenOutput);
            Console.SetError(diagnostics);
            Assert.Equal(0, await CodexServer.Program.Main(["worker-token", "create"]));
            var token = tokenOutput.ToString().Trim();
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
            Assert.DoesNotContain(token, diagnostics.ToString(), StringComparison.Ordinal);
            var database = new ServerConfiguration { DataDirectory = temporary.Path, DatabasePath = databaseOverride }.ResolveDatabasePath();
            Assert.True(File.Exists(database));
            Assert.Contains(database, diagnostics.ToString());

            Console.SetOut(originalOutput);
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var identityPath = Path.Combine(temporary.Path, "worker-id");
            async Task<int> Register(string value, string path)
            {
                using var input = new StringReader(value + "\r\n");
                return await CodexWorker.Program.RunAsync(["register", "--server", url, "--token-stdin",
                    "--capacity", "2", "--identity-file", path], CancellationToken.None, input);
            }

            // Reproduce the old documented command's separate database: its fresh
            // token is rejected, but staging does not prevent a valid clean retry.
            tokenOutput.GetStringBuilder().Clear();
            Console.SetOut(tokenOutput);
            await CodexServer.Program.Main(["worker-token", "create", Path.Combine(temporary.Path, "wrong.db")]);
            var wrongDatabaseToken = tokenOutput.ToString().Trim();
            Console.SetOut(originalOutput);
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register(wrongDatabaseToken, identityPath));
            Assert.Empty(await store.GetWorkersAsync());
            Assert.Equal(ProcessExitCodes.Success, await Register("  " + token + "  ", identityPath));
            Assert.Single(await store.GetWorkersAsync());
            Assert.Equal(ProcessExitCodes.Success, await Register(token, identityPath)); // lost-response recovery
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register(token, Path.Combine(temporary.Path, "reuse-id")));
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register("invalid", Path.Combine(temporary.Path, "invalid-id")));

            tokenOutput.GetStringBuilder().Clear();
            Console.SetOut(tokenOutput);
            await CodexServer.Program.Main(["worker-token", "create"]);
            var expired = tokenOutput.ToString().Trim();
            Console.SetOut(originalOutput);
            // Advance persisted expiry without waiting 15 minutes. Generation and
            // HTTP validation still use the production paths and actual SQLite DB.
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE worker_bootstrap_tokens SET expires_at_utc='2000-01-01T00:00:00.0000000+00:00';";
                await command.ExecuteNonQueryAsync();
            }
            Assert.Equal(ProcessExitCodes.StartupFailure, await Register(expired, Path.Combine(temporary.Path, "expired-id")));
            Assert.Single(await store.GetWorkersAsync());
            Assert.DoesNotContain(token, diagnostics.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(expired, diagnostics.ToString(), StringComparison.Ordinal);
            await app.StopAsync();
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Console.SetIn(originalInput);
            Environment.SetEnvironmentVariable("Server__DataDirectory", priorDirectory);
            Environment.SetEnvironmentVariable("Server__DatabasePath", priorDatabase);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RegistrationValidationReturnsSafeActionableContractAndCorrelation()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "validation.db")));
        await app.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/api/v1/workers/register");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "private-bootstrap-token");
        request.Headers.Add("X-Codex-Worker-Token", "private-worker-token");
        request.Content = JsonContent.Create(new WorkerRegistrationRequest(2, Guid.NewGuid().ToString("N"),
            "private-display-name", "1.0", "test", 9, []));
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("invalid_worker_registration", json.RootElement.GetProperty("code").GetString());
        Assert.Contains("capacity (1..8)", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(response.Headers.GetValues("X-Codex-Request-Id").Single(), json.RootElement.GetProperty("requestId").GetString());
        Assert.DoesNotContain("private-", body, StringComparison.Ordinal);
        Assert.Empty(await app.Services.GetRequiredService<IRegistryStore>().GetWorkersAsync());

        var settings = new CodexWorker.WorkerServerSettings
        {
            Enabled = true, Url = url, IdentityFile = Path.Combine(temporary.Path, "worker-id")
        };
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CodexWorker.WorkerRegistrationClient(client).BootstrapAsync(settings, 9, "private-bootstrap-token", CancellationToken.None));
        Assert.Contains("capacity", failure.Message);
        Assert.DoesNotContain("private-bootstrap-token", failure.Message, StringComparison.Ordinal);

        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var originalName = Environment.GetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME");
        using var capture = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME", new string('x', 201));
            Console.SetOut(capture);
            Console.SetError(capture);
            using var registrationInput = new StringReader("private-bootstrap-token");
            var exitCode = await CodexWorker.Program.RunAsync(["register", "--server", url, "--token-stdin",
                "--identity-file", Path.Combine(temporary.Path, "cli-worker-id")], CancellationToken.None, registrationInput);
            Assert.Equal(CodexWorker.ProcessExitCodes.StartupFailure, exitCode);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME", originalName);
        }
        Assert.Contains("HTTP 400", capture.ToString());
        Assert.Contains("displayName (1..200)", capture.ToString());
        Assert.Contains("Request ID:", capture.ToString());
        Assert.DoesNotContain("private-bootstrap-token", capture.ToString(), StringComparison.Ordinal);
        await app.StopAsync();
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task InvalidBootstrapCredentialIsActionableAndDoesNotCreateWorker()
    {
        using var temporary = new TemporaryDirectory();
        var url = $"http://127.0.0.1:{ReservePort()}";
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        var priorRegistration = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "bootstrap-management-secret");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", null);
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "bootstrap.db")));
            await app.StartAsync();
            using var client = new HttpClient();
            var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = Path.Combine(temporary.Path, "worker-id") };
            var failure = await Assert.ThrowsAsync<WorkerStartupException>(() =>
                new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, "invalid-bootstrap-token", CancellationToken.None));
            Assert.Contains("invalid, expired, used", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("invalid-bootstrap-token", failure.Message, StringComparison.Ordinal);
            Assert.Empty(await app.Services.GetRequiredService<IRegistryStore>().GetWorkersAsync());
            var freshBootstrap = await app.Services.GetRequiredService<IRegistryStore>()
                .CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, freshBootstrap, CancellationToken.None);
            Assert.Single(await app.Services.GetRequiredService<IRegistryStore>().GetWorkersAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", priorRegistration);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RegistrationIsAuthenticatedIdempotentAndDurableAcrossServerRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "registry.db");
        var port = ReservePort();
        var url = $"http://127.0.0.1:{port}";
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "test-registration-token");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "test-management-token");
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, database));
            await app.StartAsync();
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                var workerId = Guid.NewGuid().ToString("N");
                var registry = app.Services.GetRequiredService<IRegistryStore>();
                var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
                Assert.True(await registry.BootstrapWorkerAsync(bootstrap, new WorkerRegistrationRequest(1, workerId, "test worker", "1.2.3", "test", 2, []), "individual-worker-token-with-sufficient-entropy"));
                var request = new { contractVersion = 1, workerId, displayName = "test worker", workerVersion = "1.2.3",
                    platform = "test", capacity = 2, capabilities = new[] { "git", "codex-cli" } };
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-worker-token-with-sufficient-entropy");
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                request = new { contractVersion = 1, workerId, displayName = "renamed worker", workerVersion = "1.2.4",
                    platform = "test", capacity = 3, capabilities = new[] { "git", "updated" } };
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", request)).StatusCode);
                var heartbeat = new { contractVersion = 1, workerId, workerVersion = "1.2.4", lifecycleState = "running",
                    activeExecutions = 1, maximumCapacity = 3, capabilities = new[] { "git", "updated" }, activeProjects = new[] { "project-a" } };
                client.DefaultRequestHeaders.Authorization = null;
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/workers/{workerId}/heartbeat", heartbeat)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-worker-token-with-sufficient-entropy");
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/workers/{workerId}/heartbeat", heartbeat)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/workers/{workerId}/heartbeat", heartbeat)).StatusCode);
                client.DefaultRequestHeaders.Authorization = null;
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/workers")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/workers/{workerId}")).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
                using var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Single(list.RootElement.EnumerateArray());
                var item = list.RootElement[0];
                Assert.Equal("renamed worker", item.GetProperty("displayName").GetString());
                Assert.Equal(3, item.GetProperty("capacity").GetInt32());
                Assert.Equal("online", item.GetProperty("availability").GetString());
                Assert.Equal(1, item.GetProperty("activeExecutions").GetInt32());
                Assert.Equal(2, item.GetProperty("availableCapacity").GetInt32());
                Assert.Equal("project-a", item.GetProperty("activeProjects")[0].GetString());
                Assert.True(item.TryGetProperty("firstRegisteredAtUtc", out _));
                Assert.True(item.TryGetProperty("lastSeenAtUtc", out _));
                Assert.DoesNotContain("token", item.ToString(), StringComparison.OrdinalIgnoreCase);
                using (var streamResponse = await client.GetAsync("/api/v1/events/stream", HttpCompletionOption.ResponseHeadersRead))
                {
                    Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
                    await using var stream = await streamResponse.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream);
                    Assert.Equal("event: workers", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                    Assert.StartsWith("data: ", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                }
                var otherId = Guid.NewGuid().ToString("N");
                var other = new { contractVersion = 1, workerId = otherId, displayName = "second", workerVersion = "1.2.3",
                    platform = "test", capacity = 1, capabilities = new[] { "git" } };
                var otherBootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
                Assert.True(await registry.BootstrapWorkerAsync(otherBootstrap, new WorkerRegistrationRequest(1, otherId, "second", "1.2.3", "test", 1, []), "second-worker-token-with-sufficient-entropy"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "second-worker-token-with-sufficient-entropy");
                Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{otherId}", other)).StatusCode);
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
                using var twoWorkers = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Equal(2, twoWorkers.RootElement.GetArrayLength());
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-worker-token-with-sufficient-entropy");
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/workers/{workerId}", other)).StatusCode);
            }
            await app.StopAsync();
            await app.DisposeAsync();
            await using var restarted = await ServerApplication.BuildAsync(Args(url, database));
            await restarted.StartAsync();
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-management-token");
                using var persisted = JsonDocument.Parse(await client.GetStringAsync("/api/v1/workers"));
                Assert.Equal(2, persisted.RootElement.GetArrayLength());
                Assert.Contains(persisted.RootElement.EnumerateArray(), worker => worker.GetProperty("displayName").GetString() == "renamed worker");
            }
            await restarted.StopAsync();
            await restarted.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", prior);
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task HeartbeatAvailabilityTransitionsStaleAndReconnectsWithDeterministicClock()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), 30, clock);
        await store.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 2, [new("tool", "git")]));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 2, 2,
            [new("tool", "git")], ["one", "two"], "synchronized", "configuration-v1"));
        var initial = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("online", initial.Availability);
        Assert.Equal(0, initial.AvailableCapacity);
        Assert.Equal("synchronized", initial.ConfigurationSynchronization);
        Assert.Equal("configuration-v1", initial.ConfigurationVersion);
        Assert.NotNull(initial.LastHeartbeatAtUtc);
        clock.Advance(TimeSpan.FromSeconds(31));
        var stale = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("stale", stale.Availability);
        Assert.Equal(0, stale.ActiveExecutions);
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.1", "running", 1, 3, [new("tool", "git"), new("runtime", "new-capability")], ["three"]));
        var reconnected = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal("online", reconnected.Availability);
        Assert.Equal(2, reconnected.AvailableCapacity);
        Assert.Contains(reconnected.Capabilities, capability => capability.Name == "new-capability");
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0, 0, false)]
    [InlineData(-60, -60, false)]
    [InlineData(90, 90, true)]
    [InlineData(-300, -300, true)]
    public async Task HeartbeatReportsWorkerClockSkew(int? workerAheadSeconds, int? expectedSkew, bool skewed)
    {
        using var temporary = new TemporaryDirectory();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), 30, new TestTimeProvider(now));
        await store.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, []));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, [], [],
            SentAtUtc: workerAheadSeconds is { } ahead ? now.AddSeconds(ahead) : null));

        var worker = (await store.GetWorkerAsync(workerId))!;
        Assert.Equal((expectedSkew, skewed), (worker.ClockSkewSeconds, worker.ClockSkewed));
        var diagnostics = WorkerDiagnosticsDerivation.Derive(worker, [], [], "version", observedAtUtc: now);
        Assert.Equal(skewed, diagnostics.Reasons.Any(reason => reason.StartsWith("Worker clock differs", StringComparison.Ordinal)));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
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
            using var dashboardResponse = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
            Assert.Contains("Codex Server", await dashboardResponse.Content.ReadAsStringAsync());
            var dashboard = await dashboardResponse.Content.ReadAsStringAsync();
            Assert.Contains("id=\"dashboard\"", dashboard);
            var resources = typeof(ServerApplication).Assembly.GetManifestResourceNames();
            Assert.DoesNotContain("CodexServer.dashboard.html", resources);
            Assert.DoesNotContain("CodexServer.worker-poc.js", resources);
            foreach (var path in new[] { "/home", "/projects", "/projects/a?issue=27&label=review", "/projects/a/issues/27", "/executions", "/settings", "/workers", "/workers/worker-a?step=preparation&project=a", "/dashboard-preview", "/dashboard-preview/home",
                "/dashboard-preview/projects/a?issue=27&label=review", "/dashboard-preview/projects/a/issues/27", "/dashboard-preview/workers/worker-a?step=preparation&project=a",
                "/dashboard-preview/executions/request-a?offset=50", "/dashboard-preview/settings/credential-a" })
            {
                using var previewResponse = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
                var preview = await previewResponse.Content.ReadAsStringAsync();
                Assert.Contains("class=\"dark-mode\"", preview);
                Assert.Contains("id=\"dashboard\"", preview);
                Assert.Contains("src=\"/dashboard-assets/preview/assets/theme.js\"", preview);
                Assert.True(preview.IndexOf("assets/theme.js", StringComparison.Ordinal) < preview.IndexOf("rel=\"stylesheet\"", StringComparison.Ordinal));
                Assert.DoesNotContain("poc-legacy-owner", preview);
                foreach (System.Text.RegularExpressions.Match asset in System.Text.RegularExpressions.Regex.Matches(
                    preview, "(?:src|href)=\"(/dashboard-assets/preview/[^\"]+)\""))
                {
                    var assetPath = asset.Groups[1].Value;
                    using var assetResponse = await client.GetAsync(assetPath);
                    Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
                    Assert.NotEmpty(await assetResponse.Content.ReadAsByteArrayAsync());
                    if (assetPath.EndsWith(".js", StringComparison.Ordinal))
                        Assert.Equal("text/javascript", assetResponse.Content.Headers.ContentType?.MediaType);
                    else if (assetPath.EndsWith(".css", StringComparison.Ordinal))
                        Assert.Equal("text/css", assetResponse.Content.Headers.ContentType?.MediaType);
                    using var assetPost = await client.PostAsync(assetPath, null);
                    Assert.Equal(HttpStatusCode.MethodNotAllowed, assetPost.StatusCode);
                }
                using var previewPost = await client.PostAsync(path, null);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, previewPost.StatusCode);
            }
            Assert.Contains("/dashboard-assets/preview/", dashboard);
            using var pocResponse = await client.GetAsync("/workers/worker-a/poc");
            Assert.Equal(HttpStatusCode.OK, pocResponse.StatusCode);
            var poc = await pocResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("administration-login", poc);
            Assert.Contains("id=\"dashboard\"", poc);
            Assert.DoesNotContain("poc-legacy-owner", poc);
            Assert.DoesNotContain("--ink:#183135", poc);
            Assert.Contains("/dashboard-assets/preview/", poc);
            Assert.DoesNotContain("/dashboard-assets/worker-poc.", poc);
            Assert.DoesNotContain("dashboard-scripts", poc);
            Assert.Contains("src=\"/dashboard-assets/preview/assets/theme.js\"", poc);
            Assert.True(poc.IndexOf("assets/theme.js", StringComparison.Ordinal) < poc.IndexOf("rel=\"stylesheet\"", StringComparison.Ordinal));
            using var themeResponse = await client.GetAsync("/dashboard-assets/preview/assets/theme.js");
            Assert.Equal(HttpStatusCode.OK, themeResponse.StatusCode);
            Assert.Equal("text/javascript", themeResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("codex-dashboard-preferences", await themeResponse.Content.ReadAsStringAsync());
            foreach (var path in new[] { "/old", "/old/home", "/old/projects/a/issues/27", "/old/workers/worker-a?step=preparation", "/old/settings/credential-a" })
            {
                using var legacyResponse = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
                var legacyShell = await legacyResponse.Content.ReadAsStringAsync();
                Assert.Contains("id=\"worker-poc\"", legacyShell);
                Assert.DoesNotContain("/dashboard-assets/preview/", legacyShell);
                var legacyAssets = System.Text.RegularExpressions.Regex.Matches(legacyShell, "(?:src|href)=\"(/dashboard-assets/legacy/[^\"]+)\"");
                Assert.NotEmpty(legacyAssets);
                foreach (System.Text.RegularExpressions.Match asset in legacyAssets)
                {
                    using var assetResponse = await client.GetAsync(asset.Groups[1].Value);
                    Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
                }
                using var legacyPost = await client.PostAsync(path, null);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, legacyPost.StatusCode);
            }
            using (var unknownLegacy = await client.GetAsync("/old/unknown"))
                Assert.Equal(HttpStatusCode.NotFound, unknownLegacy.StatusCode);
            foreach (var asset in new[] { "js", "css" })
            {
                using var obsolete = await client.GetAsync($"/dashboard-assets/worker-poc.{asset}");
                Assert.Equal(HttpStatusCode.NotFound, obsolete.StatusCode);
            }
            using (var redirectClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(url) })
            {
                using var redirect = await redirectClient.GetAsync("/workers/worker-a/poc?step=project&project=a");
                Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
                Assert.Equal("/workers/worker-a?step=project&project=a", redirect.Headers.Location?.OriginalString);
            }
            using var pocPost = await client.PostAsync("/workers/worker-a/poc", null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, pocPost.StatusCode);
            foreach (var path in new[] { "/home", "/projects", "/projects/missing?label=review",
                "/projects/project-a/issues/42", "/executions/request-a?offset=50", "/settings/credential-a" })
            {
                using var deepLink = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
                Assert.Equal("text/html", deepLink.Content.Headers.ContentType?.MediaType);
                Assert.Equal(dashboard, await deepLink.Content.ReadAsStringAsync());
            }
            foreach (var path in new[] { "/unsupported", "/api/unknown", "/api/v1/workers/worker-a/unknown",
                "/health/unknown", "/projects/a/unsupported", "/home/unsupported",
                "/dashboard-preview/unknown", "/dashboard-preview/home/a", "/dashboard-preview/projects/a/unknown",
                "/dashboard-preview/api/v1/workers", "/dashboard-preview/health", "/dashboard-assets/preview/assets/unknown.js",
                "/workers/worker-a/poc/unknown", "/workers/worker-a/other", "/dashboard-assets/unknown.js" })
            {
                using var unknown = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }
            using var unsupportedMethod = await client.PostAsync("/projects/project-a", null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, unsupportedMethod.StatusCode);
            Assert.DoesNotContain("<!-- dashboard-scripts -->", dashboard);
            foreach (var module in new[] { "session", "nodes", "stream", "executions", "issues" })
                Assert.DoesNotContain($"/* dashboard-{module} */", dashboard);
            using var statusResponse = await client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal("ready", status.RootElement.GetProperty("state").GetString());
            Assert.Equal(ServerApplication.DisplayVersion, status.RootElement.GetProperty("version").GetString());
            Assert.True(status.RootElement.TryGetProperty("startedAtUtc", out _));
            Assert.True(status.RootElement.GetProperty("persistenceAvailable").GetBoolean());
            Assert.Equal("ready", status.RootElement.GetProperty("controlPlaneReadiness").GetString());
            Assert.Equal(JsonValueKind.Number, status.RootElement.GetProperty("freshness").GetProperty("projects").ValueKind);
            var timing = string.Join(",", statusResponse.Headers.GetValues("Server-Timing"));
            foreach (var stage in new[] { "registry", "registry_open", "registry_execute", "registry_reader", "registry_projection", "server_headers" })
            {
                var value = Assert.Single(timing.Split(',').Select(value => value.Trim()),
                    value => value.StartsWith(stage + ";dur=", StringComparison.Ordinal));
                Assert.True(double.Parse(value.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture) >= 0);
            }
            var durations = timing.Split(',').Select(value => value.Trim().Split(";dur="))
                .ToDictionary(parts => parts[0], parts => double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            // F3 rounding can add up to 0.0005 ms per stage.
            Assert.True(durations["registry_open"] + durations["registry_execute"] + durations["registry_reader"]
                + durations["registry_projection"] <= durations["registry"] + 0.003);
            Assert.True(durations["registry"] <= durations["server_headers"] + 0.001);
            Assert.False(status.RootElement.TryGetProperty("timingHeader", out _));

            using var versionResponse = await client.GetAsync("/api/version");
            using var version = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
            Assert.Equal(ServerApplication.DisplayVersion, version.RootElement.GetProperty("version").GetString());
            Assert.Equal("Codex Server", version.RootElement.GetProperty("product").GetString());

            using var healthResponse = await client.GetAsync("/health");
            using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            Assert.Equal("healthy", health.RootElement.GetProperty("status").GetString());
            Assert.True(health.RootElement.GetProperty("persistenceAvailable").GetBoolean());
            Assert.True(health.RootElement.GetProperty("controlPlaneInitialized").GetBoolean());

            using var liveResponse = await client.GetAsync("/livez");
            Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
            using var readyResponse = await client.GetAsync("/readyz");
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
            using var ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
            Assert.Equal("ready", ready.RootElement.GetProperty("status").GetString());
            Assert.True(ready.RootElement.GetProperty("controlPlaneInitialized").GetBoolean());

        }
        finally { await app.StopAsync(); }
    }
}
