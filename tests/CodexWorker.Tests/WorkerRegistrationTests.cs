using CodexWorker;
using System.Net;
using System.Text.Json;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerRegistrationTests
{
    [Theory]
    [InlineData("   ")]
    [InlineData("token description")]
    [InlineData("token\tvalue")]
    [InlineData("token\nvalue")]
    public async Task MalformedBootstrapInputFailsBeforeCreatingLocalCredentials(string token)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "worker-id");
        var failure = await Assert.ThrowsAsync<WorkerStartupException>(() => new WorkerRegistrationClient().BootstrapAsync(
            new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = path },
            1, token, CancellationToken.None));
        Assert.Contains("single nonempty value", failure.Message);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".token"));
        Assert.False(File.Exists(path + ".server"));
    }

    [Fact]
    public async Task IdentityPersistsAndSeparateFilesProduceDistinctIdentities()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "worker-id");
        var first = await WorkerIdentity.LoadOrCreateAsync(path);
        var restarted = await WorkerIdentity.LoadOrCreateAsync(path);
        var second = await WorkerIdentity.LoadOrCreateAsync(Path.Combine(temporary.Path, "other", "worker-id"));
        Assert.Equal(first, restarted);
        Assert.NotEqual(first, second);
        Assert.True(Guid.TryParseExact(first, "N", out _));
    }

    [Fact]
    public async Task InvalidPersistedIdentityIsRejectedInsteadOfSilentlyChangingWorker()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "worker-id");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not-an-id");
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerIdentity.LoadOrCreateAsync(path));
    }

    [Fact]
    public void StandaloneModeIsDisabledByDefault()
    {
        Assert.False(new GlobalWorkerConfiguration().Server.Enabled);
    }

    [Fact]
    public void AssignmentProjectMustMatchKnownWorkerConfiguration()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "Compiler Tools", Repository = "owner/compiler", Directory = "/tmp/compiler" },
            Git = new GitSettings { BaseBranch = "main" }
        };
        var known = new ServerProjectContract("compiler-tools", "Compiler Tools", "owner/compiler", "main", "untrusted description", [], 2, now, now);

        Assert.True(WorkerHost.MatchesServerProject(configuration, known));
        Assert.False(WorkerHost.MatchesServerProject(configuration, known with { Repository = "attacker/repository" }));
        Assert.False(WorkerHost.MatchesServerProject(configuration, known with { DefaultBranch = "attacker-branch" }));
        Assert.False(WorkerHost.MatchesServerProject(configuration, known with { Id = "other-project" }));
    }

    [Fact]
    public async Task RegistrationFailureIsStartupFailureAndPayloadContainsNoSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "secret-test-value");
        try
        {
            var handler = new CaptureHandler();
            using var client = new HttpClient(handler);
            var registration = new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create());
            var failure = await Assert.ThrowsAsync<WorkerStartupException>(() => registration.RegisterAsync(
                new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = Path.Combine(temporary.Path, "worker-id") },
                2, CancellationToken.None));
            Assert.Contains("HTTP 503", failure.Message);
            Assert.Equal("Bearer secret-test-value", handler.Authorization);
            Assert.DoesNotContain("secret-test-value", handler.Body, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(handler.Body!);
            Assert.Equal(2, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("capacity").GetInt32());
            var capability = json.RootElement.GetProperty("capabilities")[0];
            Assert.Equal("integration", capability.GetProperty("type").GetString());
            Assert.Equal("github-issues", capability.GetProperty("name").GetString());
            Assert.False(json.RootElement.TryGetProperty("token", out _));
            Assert.False(json.RootElement.TryGetProperty("secrets", out _));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", previous); }
    }

    [Fact]
    public async Task RegistrationFailureIncludesSafeServerReasonAndRedactsBootstrapToken()
    {
        using var temporary = new TemporaryDirectory();
        const string token = "registration-secret-value";
        var handler = new CaptureHandler(HttpStatusCode.BadRequest,
            "{\"error\":\"Invalid worker contract; bearer registration-secret-value\"}");
        using var client = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<WorkerStartupException>(() => new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(
            new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(temporary.Path, "worker-id") },
            1, token, CancellationToken.None));

        Assert.Contains("HTTP 400", failure.Message);
        Assert.Contains("Invalid worker contract", failure.Message);
        Assert.Contains("[redacted]", failure.Message);
        Assert.DoesNotContain(token, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeartbeatFailureUsesSharedSafeDiagnosticAndCorrelation()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(temporary.Path, "worker-id") };
        var token = await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile, CancellationToken.None);
        var handler = new CaptureHandler(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { error = $"Capacity must be 1..8; credential={token}; password=hidden; https://user:hidden@host" }));
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).HeartbeatAsync(
            settings, 9, 0, [], "idle", CancellationToken.None, []));

        Assert.Contains("HTTP 400", failure.Message);
        Assert.Contains("Capacity must be 1..8", failure.Message);
        Assert.Contains("Request ID: request-123", failure.Message);
        Assert.DoesNotContain(token, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html>private server payload</html>")]
    [InlineData("{\"error\":{\"secret\":\"private server payload\"}}")]
    [InlineData("{invalid JSON: private server payload}")]
    public async Task BootstrapOmitsUnstructuredBodiesButPreservesStatusAndCorrelation(string body)
    {
        using var temporary = new TemporaryDirectory();
        using var client = new HttpClient(new CaptureHandler(HttpStatusCode.BadRequest, body));
        var failure = await Assert.ThrowsAsync<WorkerStartupException>(() => new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(
            new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(temporary.Path, "worker-id") },
            1, "bootstrap-secret", CancellationToken.None));

        Assert.Contains("HTTP 400", failure.Message);
        Assert.Contains("Request ID: request-123", failure.Message);
        Assert.DoesNotContain("private server payload", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapOmitsOversizedStructuredResponse()
    {
        using var temporary = new TemporaryDirectory();
        using var client = new HttpClient(new CaptureHandler(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { error = "private server payload", padding = new string('x', 16 * 1024) })));
        var failure = await Assert.ThrowsAsync<WorkerStartupException>(() => new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(
            new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(temporary.Path, "worker-id") },
            1, "bootstrap-secret", CancellationToken.None));

        Assert.Contains("HTTP 400", failure.Message);
        Assert.DoesNotContain("private server payload", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedBootstrapRegistersTheSamePersistentIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "bootstrap-test-token");
        try
        {
            var handler = new CaptureHandler(HttpStatusCode.OK, "{}");
            using var client = new HttpClient(handler);
            var settings = new WorkerServerSettings
            {
                Enabled = true,
                Url = "http://127.0.0.1:5090",
                IdentityFile = Path.Combine(temporary.Path, "state", "worker-id")
            };
            var registration = new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create());

            await registration.RegisterAsync(settings, 1, CancellationToken.None);
            var firstIdentity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
            var firstUri = handler.Uri;
            await registration.RegisterAsync(settings, 1, CancellationToken.None);

            Assert.Equal(firstIdentity, await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile));
            Assert.Equal(firstUri, handler.Uri);
            Assert.Equal($"http://127.0.0.1:5090/api/v1/workers/{firstIdentity}", handler.Uri);
            Assert.Equal("PUT", handler.Method);
            Assert.DoesNotContain("bootstrap-test-token", handler.Body, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", previous); }
    }

    [Fact]
    public async Task BootstrapPersistsDurableCredentialAndServerUrlWithoutReturningEitherInPayload()
    {
        using var temporary = new TemporaryDirectory();
        var handler = new CaptureHandler(HttpStatusCode.OK, "{\"workerId\":\"worker\"}");
        using var client = new HttpClient(handler);
        var identityPath = Path.Combine(temporary.Path, "worker-id");
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = identityPath };
        await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, "one-time-bootstrap-secret", CancellationToken.None);

        var durableToken = WorkerAuthentication.GetToken(settings);
        Assert.NotEqual("one-time-bootstrap-secret", durableToken);
        Assert.Equal("https://server.example", settings.EffectiveUrl);
        Assert.Equal("Bearer one-time-bootstrap-secret", handler.Authorization);
        Assert.NotNull(handler.WorkerAuthorization);
        Assert.DoesNotContain(durableToken, handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-bootstrap-secret", handler.Body, StringComparison.Ordinal);
        Assert.Equal($"https://server.example/api/v1/workers/register", handler.Uri);
        Assert.Equal(durableToken, WorkerAuthentication.GetToken(settings));
    }

    [Fact]
    public async Task BootstrapWithoutExecutionDependenciesReportsMissingCapabilities()
    {
        using var temporary = new TemporaryDirectory();
        var inventory = new CodexProvisioning.NodeCapabilityDiscovery((_, _, _) =>
            Task.FromException<(int, string)>(new FileNotFoundException()));
        var capabilities = new WorkerCapabilityDiscovery((executable, _, _, _, _) =>
            Task.FromException<ProcessResult>(new InvalidOperationException($"Executable '{executable}' is not installed/resolvable in the effective service PATH.")));
        var handler = new CaptureHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "identity") };

        await new WorkerRegistrationClient(client, inventory, capabilities).BootstrapAsync(settings, 1,
            "bootstrap-test-token", CancellationToken.None);

        var registration = JsonSerializer.Deserialize<WorkerRegistrationContract>(handler.Body ?? "",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException();
        Assert.DoesNotContain(registration.Capabilities, capability => capability.Type is "agent-provider" or "runtime" or "tool");
        Assert.All(registration.CapabilityInventory ?? [], state =>
            Assert.Equal(CodexProvisioning.InstallationState.Missing, state.Installation));
        Assert.Equal(3, registration.CapabilityInventory?.Count);
        Assert.True(File.Exists(settings.IdentityFile + ".token"));
    }

    [Fact]
    public async Task MissingCodexDoesNotInvalidateSuccessfulBootstrapOrPersistentIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", Path.Combine(temporary.Path, "absent-codex"));
            var handler = new CaptureHandler(HttpStatusCode.OK, "{}");
            using var client = new HttpClient(handler);
            var identityPath = Path.Combine(temporary.Path, "worker-id");
            var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = identityPath };
            await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, "bootstrap-test-token", CancellationToken.None);
            var identity = await WorkerIdentity.LoadOrCreateAsync(identityPath);
            var token = await File.ReadAllTextAsync(identityPath + ".token");
            var url = await File.ReadAllTextAsync(identityPath + ".server");

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
                    new CodexExecutor(new ProcessRunner(), new CodexSettings()).PreflightAsync(CancellationToken.None));
                Assert.Contains("registration/identity remain valid", failure.Message);
                Assert.DoesNotContain("registration failed", failure.Message);
                Assert.Equal(identity, await WorkerIdentity.LoadOrCreateAsync(identityPath));
                Assert.Equal(token, await File.ReadAllTextAsync(identityPath + ".token"));
                Assert.Equal(url, await File.ReadAllTextAsync(identityPath + ".server"));
            }
        }
        finally { Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previous); }
    }

    [Fact]
    public async Task BootstrapCanRetryAfterTheOneTimeTokenWasAlreadyConsumed()
    {
        using var temporary = new TemporaryDirectory();
        var handler = new RetryHandler();
        using var client = new HttpClient(handler);
        var settings = new WorkerServerSettings
        {
            Enabled = true,
            Url = "https://server.example",
            IdentityFile = Path.Combine(temporary.Path, "worker-id")
        };

        await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).BootstrapAsync(settings, 1, "already-used-bootstrap-token", CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("PUT", handler.LastMethod);
        Assert.Equal("Bearer " + WorkerAuthentication.GetToken(settings), handler.LastAuthorization);
        Assert.Equal("https://server.example", settings.EffectiveUrl);
    }

    [Fact]
    public async Task AssignmentRequestUsesRegisteredIdentityAndDoesNotCallServerWithoutCapacity()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "assignment-secret");
        try
        {
            var handler = new CaptureHandler(HttpStatusCode.OK, "{\"hasWork\":false,\"assignment\":null}");
            using var client = new HttpClient(handler);
            var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = Path.Combine(temporary.Path, "worker-id") };
            var registration = new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create());
            var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile!);
            var noCapacity = await registration.RequestAssignmentAsync(settings, true, 0,
                new Dictionary<string, int> { ["compiler"] = 1 }, CancellationToken.None);
            Assert.False(noCapacity.HasWork);
            Assert.Null(handler.Body);

            var response = await registration.RequestAssignmentAsync(settings, true, 1,
                new Dictionary<string, int> { ["compiler"] = 1 }, CancellationToken.None);
            Assert.False(response.HasWork);
            Assert.Equal("POST", handler.Method);
            Assert.Equal($"http://127.0.0.1:5090/api/v1/workers/{identity}/assignments/request", handler.Uri);
            Assert.Equal("Bearer assignment-secret", handler.Authorization);
            using var payload = JsonDocument.Parse(handler.Body!);
            Assert.Equal(identity, payload.RootElement.GetProperty("workerId").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("availableCapacity").GetInt32());
            Assert.Equal(1, payload.RootElement.GetProperty("projectCapacities").GetProperty("compiler").GetInt32());
            Assert.DoesNotContain("assignment-secret", handler.Body!, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", previous); }
    }

    [Fact]
    public async Task LeaseRenewalUsesExecutionWorkerAndGenerationAndSurfacesStaleOwnership()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "lease-secret");
        try
        {
            var handler = new CaptureHandler(HttpStatusCode.Conflict, "{\"error\":\"stale\"}");
            using var client = new HttpClient(handler);
            var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090" };
            var lease = new ServerExecutionLeaseContract("execution-123", "worker-456", 7,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(15), "Active", 60);
            var renewed = await new WorkerRegistrationClient(client, TestCapabilityDiscovery.Create()).RenewExecutionLeaseAsync(settings, lease, CancellationToken.None);
            Assert.Null(renewed);
            Assert.Equal("POST", handler.Method);
            Assert.Equal("http://127.0.0.1:5090/api/v1/workers/worker-456/executions/execution-123/lease/renew", handler.Uri);
            Assert.Equal("Bearer lease-secret", handler.Authorization);
            using var payload = JsonDocument.Parse(handler.Body!);
            Assert.Equal("worker-456", payload.RootElement.GetProperty("workerId").GetString());
            Assert.Equal(7, payload.RootElement.GetProperty("generation").GetInt64());
            Assert.DoesNotContain("lease-secret", handler.Body!, StringComparison.Ordinal);

            var renewedExpiry = DateTimeOffset.UtcNow.AddMinutes(15);
            var successHandler = new CaptureHandler(HttpStatusCode.OK,
                JsonSerializer.Serialize(lease with { ExpiresAtUtc = renewedExpiry }));
            using var successClient = new HttpClient(successHandler);
            Assert.Equal(renewedExpiry, await new WorkerRegistrationClient(successClient, TestCapabilityDiscovery.Create())
                .RenewExecutionLeaseAsync(settings, lease, CancellationToken.None));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", previous); }
    }

    [Fact]
    public async Task ProvisioningProtocolAcceptsStructuredPlansAndReportsProgressWithoutSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "provisioning-secret");
        try
        {
            var workerId = await WorkerIdentity.LoadOrCreateAsync(Path.Combine(temporary.Path, "worker-id"));
            var planId = Guid.NewGuid().ToString("N");
            var plan = new ProvisioningPlanContract(planId, workerId, DateTimeOffset.UtcNow, "Accepted",
                [new ProvisioningActionContract("git", "tool", "git", Operation: "ensure")]);
            var requestHandler = new CaptureHandler(HttpStatusCode.OK, JsonSerializer.Serialize(plan));
            using var requestClient = new HttpClient(requestHandler);
            var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = Path.Combine(temporary.Path, "worker-id") };
            var registration = new WorkerRegistrationClient(requestClient, TestCapabilityDiscovery.Create());
            var received = await registration.RequestProvisioningPlanAsync(settings, CancellationToken.None);
            Assert.Equal(planId, received!.Id);
            Assert.Equal($"http://127.0.0.1:5090/api/v1/workers/{workerId}/provisioning/request", requestHandler.Uri);
            Assert.Equal("Bearer provisioning-secret", requestHandler.Authorization);

            var reportHandler = new CaptureHandler(HttpStatusCode.OK, "{}");
            using var reportClient = new HttpClient(reportHandler);
            await new WorkerRegistrationClient(reportClient, TestCapabilityDiscovery.Create()).ReportProvisioningPlanAsync(settings, planId,
                new ProvisioningWorkerReportContract(workerId, "Running", "git"), CancellationToken.None);
            Assert.Equal($"http://127.0.0.1:5090/api/v1/workers/{workerId}/provisioning/{planId}/report", reportHandler.Uri);
            using var report = JsonDocument.Parse(reportHandler.Body!);
            Assert.Equal("Running", report.RootElement.GetProperty("state").GetString());
            Assert.Equal("git", report.RootElement.GetProperty("currentActionId").GetString());
            Assert.DoesNotContain("provisioning-secret", reportHandler.Body!, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", previous); }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _responseBody;
        public CaptureHandler(HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable, string? responseBody = null)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }
        public string? Authorization { get; private set; }
        public string? WorkerAuthorization { get; private set; }
        public string? Body { get; private set; }
        public string? Method { get; private set; }
        public string? Uri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Uri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            WorkerAuthorization = request.Headers.TryGetValues("X-Codex-Worker-Token", out var values) ? values.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(_statusCode) { Content = new StringContent(_responseBody ?? "") };
            response.Headers.Add("X-Codex-Request-Id", "request-123");
            return response;
        }
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method.Method;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(RequestCount == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"worker-registration-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
