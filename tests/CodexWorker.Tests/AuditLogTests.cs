namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodexServer;

[Collection("ServerTokenEnvironment")]
public sealed class AuditLogTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task AdministrativeChangesAreRecordedWithTheOperatorWhoMadeThem()
    {
        using var temporary = new TemporaryDirectory();
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "audit-test-token");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var origin = $"http://127.0.0.1:{port}";
            await using var app = await ServerApplication.BuildAsync([
                $"--Server:ListenUrl={origin}", $"--Server:AdministrationOrigin={origin}",
                $"--Server:DatabasePath={Path.Combine(temporary.Path, "registry.db")}", "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();

            // Dashboard: sign in with a (percent-encoded) name; the session carries it into every change.
            using var browser = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new(origin) };
            browser.DefaultRequestHeaders.Add("Origin", origin);
            using var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/v1/administration/session");
            signIn.Headers.Authorization = new("Bearer", "audit-test-token");
            signIn.Headers.Add(OperatorIdentity.Header, Uri.EscapeDataString("Ana Núñez"));
            using var login = await browser.SendAsync(signIn);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var session = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            Assert.Equal("Ana Núñez", session.RootElement.GetProperty("operator").GetString());
            browser.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0]);
            browser.DefaultRequestHeaders.Add(AdministrationSessions.CsrfHeader, session.RootElement.GetProperty("csrfToken").GetString());
            using (var created = await browser.PostAsJsonAsync("/api/v1/projects", new CentralProjectDefinition("Audited", "team/audited", "main", "")))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using (var refused = await browser.PostAsJsonAsync("/api/v1/projects", new CentralProjectDefinition("Audited", "team/audited", "main", "")))
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            // Previews change nothing and are not recorded.
            using (await browser.PostAsJsonAsync("/api/v1/projects/audited/github/issues", new { title = "t", body = "b", previewOnly = true })) { }

            // Bearer client naming itself in the header; a Worker-style unauthorized call is not an operator action.
            using var api = new HttpClient { BaseAddress = new(origin) };
            api.DefaultRequestHeaders.Authorization = new("Bearer", "audit-test-token");
            api.DefaultRequestHeaders.Add(OperatorIdentity.Header, "release-bot");
            using (var paused = await api.PutAsJsonAsync("/api/v1/project-scheduling/audited", new { paused = true })) { }
            using var stranger = new HttpClient { BaseAddress = new(origin) };
            stranger.DefaultRequestHeaders.Authorization = new("Bearer", "not-the-token");
            using (var denied = await stranger.PutAsJsonAsync("/api/v1/project-scheduling/audited", new { paused = false }))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            using (var anonymous = await stranger.GetAsync("/api/v1/audit"))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var page = await api.GetFromJsonAsync<AuditPage>("/api/v1/audit?limit=2");
            Assert.NotNull(page);
            Assert.Equal(2, page.Events.Count);
            Assert.NotNull(page.NextBefore);
            var all = await api.GetFromJsonAsync<AuditPage>("/api/v1/audit?limit=50");
            Assert.NotNull(all);
            var events = all.Events.Reverse().ToArray();
            Assert.Equal(["POST /api/v1/administration/session", "POST /api/v1/projects", "POST /api/v1/projects", "PUT /api/v1/project-scheduling/audited"],
                events.Select(item => $"{item.Method} {item.Action}"));
            Assert.All(events.Take(3), item => Assert.Equal(("dashboard", "Ana Núñez"), (item.Channel, item.Operator)));
            Assert.Equal(["succeeded", "succeeded", "refused"], events.Take(3).Select(item => item.Outcome));
            Assert.Equal(("api", "release-bot"), (events[3].Channel, events[3].Operator));
            Assert.All(events, item => Assert.False(string.IsNullOrEmpty(item.RequestId)));
            var mine = await api.GetFromJsonAsync<AuditPage>("/api/v1/audit?operator=ana%20n%C3%BA%C3%B1ez");
            Assert.Equal(3, mine?.Events.Count);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("  Ana  ", "Ana")]
    [InlineData("", null)]
    [InlineData("line\nbreak", null)]
    public void OperatorNamesAreBoundedAndPrintable(string supplied, string? expected)
    {
        Assert.Equal(expected, OperatorIdentity.Normalize(supplied));
        Assert.Null(OperatorIdentity.Normalize(new string('a', OperatorIdentity.MaximumNameLength + 1)));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task TheLogKeepsOnlyTheRetainedWindow()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock { Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z") };
        var store = new AuditLogStore(Path.Combine(temporary.Path, "audit.db"), clock);
        await store.InitializeAsync();
        await store.RecordAsync(new("api", null), "POST", "/api/v1/old", 200, "r1");
        clock.Now += AuditLogStore.RetainedAge + TimeSpan.FromDays(1);
        await store.RecordAsync(new("api", null), "POST", "/api/v1/new", 200, "r2");
        var page = await store.ListAsync(10, null, null);
        Assert.Equal("/api/v1/new", Assert.Single(page.Events).Action);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"audit-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
