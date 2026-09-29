using CodexWorker;
using System.Net;
using System.Text.Json;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerRegistrationTests
{
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
            var registration = new WorkerRegistrationClient(client);
            var failure = await Assert.ThrowsAsync<WorkerStartupException>(() => registration.RegisterAsync(
                new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = Path.Combine(temporary.Path, "worker-id") },
                2, CancellationToken.None));
            Assert.Contains("HTTP 503", failure.Message);
            Assert.Equal("Bearer secret-test-value", handler.Authorization);
            Assert.DoesNotContain("secret-test-value", handler.Body, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(handler.Body!);
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("capacity").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("token", out _));
            Assert.False(json.RootElement.TryGetProperty("secrets", out _));
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", previous); }
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
            var registration = new WorkerRegistrationClient(client);
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
            var renewed = await new WorkerRegistrationClient(client).RenewExecutionLeaseAsync(settings, lease, CancellationToken.None);
            Assert.False(renewed);
            Assert.Equal("POST", handler.Method);
            Assert.Equal("http://127.0.0.1:5090/api/v1/workers/worker-456/executions/execution-123/lease/renew", handler.Uri);
            Assert.Equal("Bearer lease-secret", handler.Authorization);
            using var payload = JsonDocument.Parse(handler.Body!);
            Assert.Equal("worker-456", payload.RootElement.GetProperty("workerId").GetString());
            Assert.Equal(7, payload.RootElement.GetProperty("generation").GetInt64());
            Assert.DoesNotContain("lease-secret", handler.Body!, StringComparison.Ordinal);
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
        public string? Body { get; private set; }
        public string? Method { get; private set; }
        public string? Uri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Uri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode) { Content = new StringContent(_responseBody ?? "") };
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"worker-registration-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
