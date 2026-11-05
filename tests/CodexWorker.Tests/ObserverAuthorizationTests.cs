namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TemporaryDirectory = ObserverReportingTests.TemporaryDirectory;

[Collection("ServerTokenEnvironment")]
public sealed class ObserverAuthorizationTests
{
    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task PairingRequiresAdministrationAndObserverCredentialOnlyRecoversItsOwnRegistration()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"));
        await store.InitializeAsync();
        var settings = new ServerConfiguration { AdministrationOrigin = "https://observer.example" };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AdministrationSessions>();
        builder.Services.AddSingleton<IRegistryStore>(store);
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "observer-test-management-token");
        try
        {
            await using var app = builder.Build();
            ServerApplication.MapObserverPairing(app);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var request = new ObserverPairingRequest(1, ObserverProtocol.Mode,
                "obs_0123456789abcdef0123456789abcdef", settings.AdministrationOrigin);
            using (var rejected = await client.PostAsJsonAsync("/api/v1/observers/pairing/authorize", request))
                Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "observer-test-management-token");
            using (var rejected = await client.PostAsJsonAsync("/api/v1/observers/pairing/authorize", request with { Server = "https://wrong.example" }))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var authorized = await client.PostAsJsonAsync("/api/v1/observers/pairing/authorize", request);
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            Assert.True(authorized.Headers.CacheControl?.NoStore);
            var pairing = Assert.IsType<ObserverPairingAuthorization>(await authorized.Content.ReadFromJsonAsync<ObserverPairingAuthorization>());
            var credential = "obr_" + new string('a', 64);
            client.DefaultRequestHeaders.Authorization = new("Bearer", pairing.Authorization);
            using (var unauthorizedReport = await client.PostAsJsonAsync($"/api/v1/observers/{request.ObserverId}/snapshots", new { }))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedReport.StatusCode);
            client.DefaultRequestHeaders.Add("X-Codex-Observer-Token", credential);
            using (var wrongServer = await client.PostAsJsonAsync("/api/v1/observers/pairing/redeem", request with { Server = "https://wrong.example" }))
                Assert.Equal(HttpStatusCode.BadRequest, wrongServer.StatusCode);
            using (var redeemed = await client.PostAsJsonAsync("/api/v1/observers/pairing/redeem", request))
                Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
            using (var replayed = await client.PostAsJsonAsync("/api/v1/observers/pairing/redeem", request))
                Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
            client.DefaultRequestHeaders.Remove("X-Codex-Observer-Token");
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            using (var recovered = await client.GetAsync($"/api/v1/observers/{request.ObserverId}/registration"))
                Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            using (var wrongIdentity = await client.GetAsync("/api/v1/observers/obs_1123456789abcdef0123456789abcdef/registration"))
                Assert.Equal(HttpStatusCode.Unauthorized, wrongIdentity.StatusCode);
            var now = DateTimeOffset.UtcNow;
            var execution = new ObserverExecution("0123456789abcdef0123456789abcdef", "Project", "owner/repo", 1,
                "Issue", "Implementing", null, null, now.AddMinutes(-1));
            var snapshot = new ObserverStatusSnapshot(1, request.ObserverId, "1123456789abcdef0123456789abcdef", now, "running", [execution]);
            var eventReport = new ObserverExecutionEvent(1, request.ObserverId, "2123456789abcdef0123456789abcdef", now, execution);
            using (var acceptedEvent = await client.PostAsJsonAsync($"/api/v1/observers/{request.ObserverId}/events", eventReport))
                Assert.Equal(HttpStatusCode.OK, acceptedEvent.StatusCode);
            Assert.Equal("awaiting-first-report", (await store.GetObserverInspectionAsync(request.ObserverId))?.Registration.State);
            using (var malformed = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/snapshots", new StringContent("{", Encoding.UTF8, "application/json")))
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using (var unknown = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/snapshots",
                new StringContent(json.Insert(1, "\"rawOutput\":\"excluded\","), Encoding.UTF8, "application/json")))
                Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            using (var duplicateField = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/snapshots",
                new StringContent(json.Insert(1, "\"contractVersion\":1,"), Encoding.UTF8, "application/json")))
                Assert.Equal(HttpStatusCode.BadRequest, duplicateField.StatusCode);
            using (var wrongIdentity = await client.PostAsJsonAsync($"/api/v1/observers/{request.ObserverId}/snapshots", snapshot with { ObserverId = "obs_1123456789abcdef0123456789abcdef" }))
                Assert.Equal(HttpStatusCode.BadRequest, wrongIdentity.StatusCode);
            using (var oversized = await client.PostAsJsonAsync($"/api/v1/observers/{request.ObserverId}/snapshots",
                snapshot with { Executions = [execution with { IssueTitle = new string('x', ObserverProtocol.MaximumPayloadBytes + 1) }] }))
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            using (var content = new ByteArrayContent(new byte[ObserverProtocol.MaximumPayloadBytes + 1]))
            {
                content.Headers.ContentType = new("application/json");
                using var oversized = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/snapshots", content);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            }
            using (var acceptedSnapshot = await client.PostAsJsonAsync($"/api/v1/observers/{request.ObserverId}/snapshots", snapshot))
                Assert.Equal(HttpStatusCode.OK, acceptedSnapshot.StatusCode);
            using (var duplicate = await client.PostAsJsonAsync($"/api/v1/observers/{request.ObserverId}/snapshots", snapshot))
            {
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
                using var body = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
                Assert.Equal("duplicate", body.RootElement.GetProperty("state").GetString());
            }
            Assert.Equal("reporting", (await store.GetObserverInspectionAsync(request.ObserverId))?.Registration.State);
            using (var forbiddenInspection = await client.GetAsync($"/api/v1/observers/{request.ObserverId}/history"))
                Assert.Equal(HttpStatusCode.Unauthorized, forbiddenInspection.StatusCode);
            using (var unauthorized = await client.PostAsJsonAsync("/api/v1/observers/pairing/authorize", request))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using (var unauthorized = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/revoke", null))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "observer-test-management-token");
            using (var inspected = await client.GetAsync($"/api/v1/observers/{request.ObserverId}"))
                Assert.Equal(HttpStatusCode.OK, inspected.StatusCode);
            using (var history = await client.GetAsync($"/api/v1/observers/{request.ObserverId}/history?limit=2"))
            {
                Assert.Equal(HttpStatusCode.OK, history.StatusCode);
                Assert.Single(await history.Content.ReadFromJsonAsync<ObserverExecutionObservation[]>() ?? []);
            }
            using (var revoked = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/revoke", null))
                Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            using (var revoked = await client.GetAsync($"/api/v1/observers/{request.ObserverId}/registration"))
                Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using (var revokedReport = await client.PostAsJsonAsync($"/api/v1/observers/{request.ObserverId}/snapshots", snapshot))
                Assert.Equal(HttpStatusCode.Unauthorized, revokedReport.StatusCode);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }
}
