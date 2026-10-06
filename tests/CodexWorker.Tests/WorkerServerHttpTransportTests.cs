using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerServerHttpTransportTests
{
    public static TheoryData<int, bool, bool> RedirectCases
    {
        get
        {
            var cases = new TheoryData<int, bool, bool>();
            foreach (var status in new[] { 301, 302, 303, 307, 308 })
                foreach (var crossOrigin in new[] { false, true })
                    foreach (var bootstrap in new[] { false, true })
                        cases.Add(status, crossOrigin, bootstrap);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(RedirectCases))]
    public async Task RedirectsNeverDeliverCredentialsOrBodiesToDestination(int status, bool crossOrigin, bool bootstrap)
    {
        using var temporary = new TemporaryDirectory();
        using var certificates = new IsolatedHttpsCertificates();
        using var http = certificates.CreateClient();
        await using var destination = new HttpFixture("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", certificates.Server);
        await using var origin = new HttpFixture(url => $"HTTP/1.1 {status} Redirect\r\nLocation: {(crossOrigin ? destination.Url : url)}/destination\r\nContent-Length: 0\r\n\r\n", certificates.Server);
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN", "credential-test-secret");
        try
        {
            var identity = Path.Combine(temporary.Path, "identity");
            var settings = new WorkerServerSettings { Enabled = true, Url = origin.Url, IdentityFile = identity };
            // Credential delivery uses an enrolled identity and must not create one implicitly.
            if (!bootstrap) await WorkerIdentity.LoadOrCreateAsync(identity);
            var client = new WorkerRegistrationClient(http, provisioningDiscovery: TestCapabilityDiscovery.Create(),
                capabilityDiscovery: new WorkerCapabilityDiscovery((_, _, _, _, _) =>
                    Task.FromException<ProcessResult>(new FileNotFoundException())));
            Exception failure;
            if (bootstrap)
                failure = await Assert.ThrowsAsync<WorkerStartupException>(() => client.BootstrapAsync(settings, 1, "bootstrap-test-secret", CancellationToken.None));
            else
            {
                var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.RetrieveCredentialAsync(settings, "credential", CancellationToken.None));
                Assert.Equal((HttpStatusCode)status, error.StatusCode);
                failure = error;
            }
            Assert.Contains($"HTTP {status}", failure.Message);
            Assert.Contains("Redirects are disabled", failure.Message);
            Assert.DoesNotContain("test-secret", failure.Message);
            Assert.Equal(1, origin.Requests);
            Assert.Equal(0, destination.Requests);
            Assert.Contains(bootstrap ? "X-Codex-Worker-Token:" : "X-Worker-Credential-Token:", origin.LastRequest);
            Assert.Equal(bootstrap, origin.BodyBytes > 0);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN", previous); }
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com?token=secret")]
    [InlineData("https://example.com#secret")]
    [InlineData("http://localhost.example.com")]
    public async Task InvalidPersistedEndpointsFailBeforeSending(string endpoint)
    {
        using var temporary = new TemporaryDirectory();
        var identity = Path.Combine(temporary.Path, "identity");
        await WorkerIdentity.LoadOrCreateAsync(identity);
        await File.WriteAllTextAsync(identity + ".server", endpoint);
        await File.WriteAllTextAsync(identity + ".token", new string('a', 32));
        await using var server = new HttpFixture("HTTP/1.1 204 No Content\r\n\r\n");
        var client = new WorkerRegistrationClient();
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetManagedConfigurationAsync(
            new WorkerServerSettings { Enabled = true, Url = server.Url, IdentityFile = identity }, CancellationToken.None));
        Assert.Equal(0, server.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("http://[::1]", true)]
    [InlineData("http://localhost", true)]
    [InlineData("http://0.0.0.0", false)]
    [InlineData("http://192.0.2.1", false)]
    [InlineData("http://localhost.example.com", false)]
    public void ServerHttpBackendRemainsLoopbackOnly(string url, bool permitted)
    {
        var configuration = new CodexServer.ServerConfiguration { ListenUrl = url };
        if (permitted) configuration.Validate();
        else Assert.Throws<InvalidDataException>(configuration.Validate);
    }

    [Theory]
    [InlineData("trusted")]
    [InlineData("untrusted")]
    [InlineData("expired")]
    [InlineData("wrong-host")]
    public async Task RealTlsEnforcesChainValidityAndHostname(string scenario)
    {
        using var certificates = new IsolatedHttpsCertificates(scenario);
        await using var server = new HttpFixture("HTTP/1.1 204 No Content\r\n\r\n", certificates.Server);
        using var client = certificates.CreateClient(trustRoot: scenario != "untrusted");
        using var message = new HttpRequestMessage(HttpMethod.Get, server.Url);
        message.Headers.Add("X-Worker-Credential-Token", "tls-test-secret");
        if (scenario == "trusted")
        {
            using var response = await WorkerServerHttpTransport.SendAsync(client, message, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(1, server.Requests);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => WorkerServerHttpTransport.SendAsync(client, message, TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Equal(0, server.Requests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollingAndReportingRetainBoundedRedactedErrorsAndCorrelation(bool reporting)
    {
        using var temporary = new TemporaryDirectory();
        var identity = Path.Combine(temporary.Path, "identity");
        var workerId = await WorkerIdentity.LoadOrCreateAsync(identity);
        var token = new string('a', 32);
        await File.WriteAllTextAsync(identity + ".token", token);
        using var client = new HttpClient(new ResponseHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":\"Rejected " + token + "\"}")
            };
            response.Headers.Add("X-Codex-Request-Id", "request-123");
            return response;
        }));
        var registration = new WorkerRegistrationClient(client);
        var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1", IdentityFile = identity };
        var failure = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (reporting)
                await registration.ReportProvisioningPlanAsync(settings, "plan", new(workerId, "Failed"), CancellationToken.None);
            else
                await registration.GetManagedConfigurationAsync(settings, CancellationToken.None);
        });
        Assert.Contains("HTTP 503", failure.Message);
        Assert.Contains("request-123", failure.Message);
        Assert.Contains("[redacted]", failure.Message);
        Assert.DoesNotContain(token, failure.Message);
        Assert.True(failure.Message.Length < 1000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineAndCallerCancellationIncludeResponseBody(bool callerCancellation)
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new WaitingStream(reading);
        using var client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/configuration");
        using var cancellation = new CancellationTokenSource();
        var pending = WorkerServerHttpTransport.SendAsync(client, request,
            callerCancellation ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(100), cancellation.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation) await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(callerCancellation, cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task OversizedSuccessfulResponseIsRejected()
    {
        using var client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1])
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/configuration");
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => WorkerServerHttpTransport.SendAsync(
            client, request, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Contains("size limit", failure.Message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"worker-transport-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private sealed class WaitingStream(TaskCompletionSource reading) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HttpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private int _requests;
        public string Url { get; }
        public int Requests => Volatile.Read(ref _requests);
        public string LastRequest { get; private set; } = "";
        public int BodyBytes { get; private set; }

        public HttpFixture(string response, X509Certificate2? certificate = null) : this(_ => response, certificate) { }
        public HttpFixture(Func<string, string> response, X509Certificate2? certificate = null)
        {
            _listener.Start();
            Url = $"{(certificate is null ? "http" : "https")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _run = RunAsync(response, certificate);
        }

        private async Task RunAsync(Func<string, string> response, X509Certificate2? certificate)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using Stream stream = certificate is null ? connection.GetStream() : new SslStream(connection.GetStream());
                    try
                    {
                        if (stream is SslStream tls)
                            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _stop.Token);
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var headers = new StringBuilder();
                        var length = 0;
                        var chunked = false;
                        while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                        {
                            headers.AppendLine(line);
                            if (line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                        }
                        if (headers.Length == 0) continue;
                        var body = new char[length];
                        BodyBytes = length == 0 ? 0 : await reader.ReadBlockAsync(body.AsMemory(), _stop.Token);
                        if (chunked)
                        {
                            while (await reader.ReadLineAsync(_stop.Token) is { } chunkHeader)
                            {
                                var chunkLength = Convert.ToInt32(chunkHeader, 16);
                                if (chunkLength == 0) { await reader.ReadLineAsync(_stop.Token); break; }
                                var chunk = new char[chunkLength];
                                BodyBytes += await reader.ReadBlockAsync(chunk.AsMemory(), _stop.Token);
                                await reader.ReadLineAsync(_stop.Token);
                            }
                        }
                        LastRequest = headers.ToString();
                        Interlocked.Increment(ref _requests);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response(Url)), _stop.Token);
                    }
                    catch (Exception ex) when (ex is AuthenticationException or IOException) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run;
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
