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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"worker-registration-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
