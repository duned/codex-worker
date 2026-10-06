namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using CodexServer;
using Microsoft.AspNetCore.Http;

[Collection("ServerTokenEnvironment")]
public sealed class AdministrationSessionTests
{
    [Fact]
    public void SessionsExpireRotateAndCancelWithoutPersistingManagementCredentials()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "session-test-token");
        try
        {
            var clock = new Clock();
            using var sessions = new AdministrationSessions(new() { AdministrationOrigin = "https://admin.example" }, clock);
            var context = Context();
            Assert.True(sessions.OriginAllowed(context));
            context.Request.Headers["X-Forwarded-Proto"] = "http";
            context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
            Assert.True(sessions.OriginAllowed(context)); // Forwarding claims cannot alter configured trust.
            context.Request.Host = new("attacker.example");
            Assert.False(sessions.OriginAllowed(context));
            context.Request.Host = new("admin.example");
            var first = Assert.IsType<AdministrationSessions.Session>(sessions.Create(context));
            var cookie = context.Response.Headers.SetCookie.ToString();
            Assert.Contains("__Host-CodexAdministration=", cookie);
            Assert.Contains("secure", cookie);
            Assert.Contains("httponly", cookie);
            Assert.Contains("samesite=strict", cookie);
            Assert.Contains("expires=", cookie);
            Assert.DoesNotContain("session-test-token", cookie);
            context.Request.Headers.Cookie = cookie.Split(';')[0];
            Assert.Same(first, sessions.Validate(context));
            context.Request.Method = "POST";
            Assert.False(sessions.Authorized(context));
            context.Request.Headers[AdministrationSessions.CsrfHeader] = "wrong-csrf";
            Assert.False(sessions.Authorized(context));
            context.Request.Headers[AdministrationSessions.CsrfHeader] = first.CsrfToken;
            Assert.True(sessions.Authorized(context));
            context.Request.Headers.Remove("Origin");
            Assert.False(sessions.Authorized(context));
            context.Request.Headers.Origin = "https://other.example";
            Assert.False(sessions.Authorized(context));
            context.Request.Headers.Origin = "https://admin.example";
            context.Request.Headers.Authorization = "Bearer worker-token";
            Assert.False(sessions.Authorized(context));
            context.Request.Headers.Authorization = "Bearer session-test-token";
            context.Request.Headers.Remove("Origin");
            Assert.True(sessions.Authorized(context)); // Supported non-browser bearer clients need no CSRF.
            context.Request.Headers.Remove("Authorization");
            clock.Now += TimeSpan.FromHours(8);
            Assert.Null(sessions.Validate(context));
            Assert.True(first.CancellationToken.IsCancellationRequested);
            context.Response.Headers.Clear();
            var second = Assert.IsType<AdministrationSessions.Session>(sessions.Create(context));
            context.Request.Headers.Cookie = context.Response.Headers.SetCookie.ToString().Split(';')[0];
            Assert.Same(second, sessions.Validate(context));
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "rotated-test-token");
            Assert.Null(sessions.Validate(context));
            Assert.True(second.CancellationToken.IsCancellationRequested);
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "session-test-token");
            Assert.Null(sessions.Validate(context)); // Returning to the old token cannot restore an invalidated session.
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Theory]
    [InlineData("http://admin.example", false)]
    [InlineData("http://127.0.0.1:5090", true)]
    [InlineData("http://127.0.0.1:5091", false)]
    [InlineData("https://admin.example", true)]
    [InlineData("https://admin.example/", false)]
    public void SessionOriginRequiresHttpsOrExplicitDirectLoopback(string origin, bool valid)
    {
        var configuration = new ServerConfiguration { AdministrationOrigin = origin };
        if (valid) configuration.Validate();
        else Assert.Throws<InvalidDataException>(configuration.Validate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealApiLoginReloadCsrfLogoutAndCookieBearerIsolation(bool tlsProxy)
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "api-session-test-token");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var origin = $"http://127.0.0.1:{port}";
            var browserOrigin = tlsProxy ? $"https://127.0.0.1:{port}" : origin;
            await using var app = await ServerApplication.BuildAsync([
                $"--Server:ListenUrl={origin}", $"--Server:AdministrationOrigin={browserOrigin}",
                $"--Server:DatabasePath={Path.Combine(temporary.Path, "registry.db")}", "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            using var handler = new SocketsHttpHandler { UseCookies = false, MaxResponseDrainSize = 0 };
            using var client = new HttpClient(handler) { BaseAddress = new(origin) };
            client.DefaultRequestHeaders.Add("Origin", browserOrigin);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "worker-token");
            using (var rejected = await client.PostAsync("/api/v1/administration/session", null))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                Assert.True(rejected.Headers.CacheControl?.NoStore);
                Assert.False(rejected.Headers.Contains("Set-Cookie"));
            }
            client.DefaultRequestHeaders.Authorization = new("Bearer", "api-session-test-token");
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
            using (var rejected = await client.PostAsync("/api/v1/administration/session", null))
                Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", browserOrigin);
            using var login = await client.PostAsync("/api/v1/administration/session", null);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
            Assert.Equal(tlsProxy, setCookie.Contains("; secure", StringComparison.Ordinal));
            Assert.Contains("httponly", setCookie);
            Assert.Contains("samesite=strict", setCookie);
            Assert.True(login.Headers.CacheControl?.NoStore);
            var cookie = setCookie.Split(';')[0];
            using var session = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var csrf = session.RootElement.GetProperty("csrfToken").GetString();
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            using (var restored = await client.GetAsync("/api/v1/administration/session"))
                Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            using (var workers = await client.GetAsync("/api/v1/workers"))
                Assert.Equal(HttpStatusCode.OK, workers.StatusCode);
            using (var denied = await client.DeleteAsync("/api/v1/administration/session"))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await client.PostAsJsonAsync("/api/v1/workers/onboarding/authorize",
                new CodexProvisioning.WorkerPairingRequest(1, Guid.NewGuid().ToString("N"), "enroll", browserOrigin)))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Add(AdministrationSessions.CsrfHeader, csrf);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "worker-token");
            using (var denied = await client.GetAsync("/api/v1/workers"))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            using (var denied = await client.GetAsync($"/api/v1/workers/{Guid.NewGuid():N}/configuration"))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); // Cookies cannot authorize Worker APIs.
            using var stream = await client.GetAsync("/api/v1/events/stream", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
            Assert.Equal("event: workers", await reader.ReadLineAsync());
            Assert.Equal("data: []", await reader.ReadLineAsync());
            Assert.Equal("", await reader.ReadLineAsync());
            var ended = reader.ReadToEndAsync();
            using (var logout = await client.DeleteAsync("/api/v1/administration/session"))
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            await ended.WaitAsync(TimeSpan.FromSeconds(3));
            using (var rejected = await client.GetAsync("/api/v1/administration/session"))
                Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            using (var rejected = await client.GetAsync("/api/v1/events/stream"))
                Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutAndDeadlineCancelAStreamBlockedInRegistryRead(bool expire)
    {
        var clock = new Clock();
        using var sessions = new AdministrationSessions(new() { AdministrationOrigin = "https://admin.example" }, clock);
        var context = Context();
        var session = Assert.IsType<AdministrationSessions.Session>(sessions.Create(context));
        context.Request.Headers.Cookie = context.Response.Headers.SetCookie.ToString().Split(';')[0];
        using var services = new ServiceCollection().AddOptions().AddSingleton(sessions).BuildServiceProvider();
        context.RequestServices = services;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = DispatchProxy.Create<IRegistryStore, ServerEventStreamTests.StreamRegistryProxy>();
        ((ServerEventStreamTests.StreamRegistryProxy)store).Read = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Array.Empty<WorkerRegistrationResponse>();
        };
        var streaming = ServerApplication.StreamWorkerUpdatesAsync(context, store, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (expire) clock.Expire();
            else sessions.Logout(context);
            await streaming.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(session.CancellationToken.IsCancellationRequested);
            Assert.True(streaming.IsCompletedSuccessfully);
            Assert.Null(sessions.Validate(context));
        }
        finally { sessions.Logout(context); }
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new("admin.example");
        context.Request.Headers.Origin = "https://admin.example";
        return context;
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"administration-session-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private TestTimer? timer;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new TestTimer(callback, state);
            return timer;
        }
        public void Expire() { Now += TimeSpan.FromHours(8); timer?.Fire(); }
        private sealed class TestTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
