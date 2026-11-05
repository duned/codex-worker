namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
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
            using (var unauthorized = await client.PostAsJsonAsync("/api/v1/observers/pairing/authorize", request))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using (var unauthorized = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/revoke", null))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "observer-test-management-token");
            using (var revoked = await client.PostAsync($"/api/v1/observers/{request.ObserverId}/revoke", null))
                Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            using (var revoked = await client.GetAsync($"/api/v1/observers/{request.ObserverId}/registration"))
                Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }
}
