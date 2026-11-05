namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TemporaryDirectory = ObserverReportingTests.TemporaryDirectory;

[Collection("ServerTokenEnvironment")]
public sealed class ObserverReadApiTests
{
    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DashboardSessionReadsBoundedIsolatedObservationsWithoutGrantingReadiness()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new Clock();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), timeProvider: clock);
        await store.InitializeAsync();
        const string managedWorkerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await store.RegisterWorkerAsync(new(1, managedWorkerId, "Managed", "1.0", "test", 2, []));
        var project = await store.CreateProjectAsync(new("Managed", "owner/managed", "main", ""));
        var managedExecution = await store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        var settings = new ServerConfiguration { AdministrationOrigin = "https://observer.example" };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<AdministrationSessions>();
        builder.Services.AddSingleton<IRegistryStore>(store);
        var previous = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "observer-read-test-management");
        try
        {
            await using var app = builder.Build();
            ServerApplication.MapObserverPairing(app);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            const string id = "obs_0123456789abcdef0123456789abcdef";
            const string otherId = "obs_1123456789abcdef0123456789abcdef";
            const string credential = "obr_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var paths = new[] { "/api/v1/observers", $"/api/v1/observers/{id}", $"/api/v1/observers/{id}/history" };
            foreach (var path in paths)
            {
                using var denied = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            foreach (var path in paths)
            {
                using var denied = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Host = "observer.example";
            var context = new DefaultHttpContext();
            context.Request.Host = new("observer.example");
            var sessions = app.Services.GetRequiredService<AdministrationSessions>();
            var session = Assert.IsType<AdministrationSessions.Session>(sessions.Create(context));
            client.DefaultRequestHeaders.Add("Cookie", context.Response.Headers.SetCookie.ToString().Split(';')[0]);
            client.DefaultRequestHeaders.Add("Origin", settings.AdministrationOrigin);
            client.DefaultRequestHeaders.Add(AdministrationSessions.CsrfHeader, session.CsrfToken);
            Assert.Empty(await client.GetFromJsonAsync<ObserverSummary[]>(paths[0]) ?? []);
            using (var missing = await client.GetAsync(paths[1])) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using (var missing = await client.GetAsync(paths[2])) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            foreach (var observerId in new[] { id, otherId })
            {
                var pairing = new ObserverPairingRequest(1, ObserverProtocol.Mode, observerId, settings.AdministrationOrigin);
                using var response = await client.PostAsJsonAsync("/api/v1/observers/pairing/authorize", pairing);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                var authorization = Assert.IsType<ObserverPairingAuthorization>(await response.Content.ReadFromJsonAsync<ObserverPairingAuthorization>());
                var pending = (await client.GetFromJsonAsync<ObserverSummary[]>(paths[0]) ?? []).Single(item => item.ObserverId == observerId);
                Assert.Equal("pairing-pending", pending.RegistrationState);
                Assert.Equal(authorization.ExpiresAtUtc, pending.PairingExpiresAtUtc);
                Assert.Null(pending.LastReceivedAtUtc);
                Assert.Null(pending.MaximumCapacity);
                var pendingJson = await client.GetStringAsync(paths[0]);
                Assert.DoesNotContain(authorization.Authorization, pendingJson, StringComparison.Ordinal);
                Assert.DoesNotContain("authorization", pendingJson, StringComparison.OrdinalIgnoreCase);
                Assert.Null(await store.AuthenticateObserverAsync(observerId, settings.AdministrationOrigin, credential));
                Assert.NotNull(await store.RedeemObserverAuthorizationAsync(pairing, authorization.Authorization, credential));
            }
            var empty = Assert.IsType<ObserverDetail>(await client.GetFromJsonAsync<ObserverDetail>(paths[1]));
            Assert.Equal("unknown", empty.Observer.Freshness);
            Assert.Null(empty.Observer.ReportedLifecycle);
            Assert.Null(empty.Observer.Name);
            Assert.Null(empty.Observer.MaximumCapacity);
            Assert.Empty(empty.CurrentExecutions);
            Assert.Empty(await client.GetFromJsonAsync<ObserverExecutionObservation[]>(paths[2]) ?? []);
            var execution = new ObserverExecution("0123456789abcdef0123456789abcdef", "Project", "owner/repo", 1,
                "Issue", "Implementing", null, null, clock.Now.AddMinutes(-1));
            var snapshot = new ObserverStatusSnapshot(1, id, "1123456789abcdef0123456789abcdef", clock.Now.AddSeconds(-30),
                "running", [execution], "Standalone one", "1.2.3", "not-ready", 4, 1);
            Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverSnapshotAsync(settings.AdministrationOrigin, credential, snapshot));
            var fresh = Assert.IsType<ObserverDetail>(await client.GetFromJsonAsync<ObserverDetail>(paths[1]));
            Assert.Equal("fresh", fresh.Observer.Freshness);
            Assert.Equal(clock.Now, fresh.Observer.LastReceivedAtUtc);
            Assert.Equal(snapshot.ObservedAtUtc, fresh.Observer.ObservedAtUtc);
            Assert.Equal("not-ready", fresh.Observer.ReportedReadiness);
            Assert.Equal(4, fresh.Observer.MaximumCapacity);
            Assert.Single(fresh.CurrentExecutions);
            var completed = execution with { Stage = "Completed", Outcome = "success", CompletedAtUtc = clock.Now, DurationMilliseconds = 60000 };
            Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(settings.AdministrationOrigin, credential,
                new(1, id, "2123456789abcdef0123456789abcdef", clock.Now, completed)));
            var second = execution with { LocalExecutionId = "3123456789abcdef0123456789abcdef", IssueNumber = 2 };
            Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(settings.AdministrationOrigin, credential,
                new(1, id, "4123456789abcdef0123456789abcdef", clock.Now.AddTicks(1), second)));
            var page = Assert.Single(await client.GetFromJsonAsync<ObserverExecutionObservation[]>(paths[2] + "?limit=1&offset=1") ?? []);
            Assert.Equal("success", page.Execution.Outcome);
            Assert.Equal(60000, page.Execution.DurationMilliseconds);
            Assert.Empty(await client.GetFromJsonAsync<ObserverExecutionObservation[]>(paths[2] + "?offset=10000") ?? []);
            Assert.Empty(await client.GetFromJsonAsync<ObserverSummary[]>(paths[0] + "?offset=10000") ?? []);
            Assert.Equal(2, (await client.GetFromJsonAsync<ObserverExecutionObservation[]>(paths[2] + "?limit=100"))?.Length);
            var first = Assert.Single(await client.GetFromJsonAsync<ObserverSummary[]>(paths[0] + "?limit=1") ?? []);
            var next = Assert.Single(await client.GetFromJsonAsync<ObserverSummary[]>(paths[0] + "?limit=1&offset=1") ?? []);
            Assert.Equal(id, first.ObserverId);
            Assert.Equal(otherId, next.ObserverId);
            Assert.Equal("Standalone one", first.Name);
            Assert.Equal("1.2.3", first.Version);
            foreach (var path in new[] { paths[0], paths[2] })
                foreach (var query in new[] { "?limit=0", "?limit=101", "?offset=-1", "?offset=10001", "?limit=bad" })
                {
                    using var invalid = await client.GetAsync(path + query);
                    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                }
            clock.Now = clock.Now.AddSeconds(settings.WorkerStaleAfterSeconds + 1);
            // A new execution event does not refresh the old lifecycle/capacity snapshot.
            Assert.Equal(ObserverIngestionResult.Accepted, await store.IngestObserverEventAsync(settings.AdministrationOrigin, credential,
                new(1, id, "5123456789abcdef0123456789abcdef", clock.Now, second)));
            var stale = Assert.IsType<ObserverDetail>(await client.GetFromJsonAsync<ObserverDetail>(paths[1]));
            Assert.Equal("stale", stale.Observer.Freshness);
            Assert.Equal("running", stale.Observer.ReportedLifecycle);
            Assert.Equal("not-ready", stale.Observer.ReportedReadiness);
            Assert.Equal(clock.Now, stale.Observer.LastReceivedAtUtc);
            foreach (var path in paths)
            {
                using var response = await client.GetAsync(path);
                Assert.True(response.Headers.CacheControl?.NoStore);
                var json = await response.Content.ReadAsStringAsync();
                foreach (var forbidden in new[] { credential, "credential", "hash", "localPath", "rawOutput", "transcript", "assignment", "lease", "actions", temporary.Path })
                    Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
                using var document = JsonDocument.Parse(json);
                Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
            }
            Assert.Equal(managedWorkerId, Assert.Single(await store.GetWorkersAsync()).WorkerId);
            Assert.Equal(managedExecution.Id, Assert.Single(await store.GetExecutionsAsync()).Id);
            Assert.Null(await store.GetWorkerAsync(id));
            Assert.Null(await store.GetExecutionAsync(execution.LocalExecutionId));
            using (var revokedResponse = await client.PostAsync($"/api/v1/observers/{id}/revoke", null))
                Assert.Equal(HttpStatusCode.NoContent, revokedResponse.StatusCode);
            var revoked = Assert.IsType<ObserverDetail>(await client.GetFromJsonAsync<ObserverDetail>(paths[1]));
            Assert.Equal("revoked", revoked.Observer.RegistrationState);
            Assert.Equal("stale", revoked.Observer.Freshness);
            Assert.Equal(2, (await client.GetFromJsonAsync<ObserverExecutionObservation[]>(paths[2]))?.Length);
            var pendingRequest = new ObserverPairingRequest(1, ObserverProtocol.Mode,
                "obs_2123456789abcdef0123456789abcdef", settings.AdministrationOrigin);
            var expiring = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(pendingRequest));
            clock.Now = expiring.ExpiresAtUtc;
            var expired = (await client.GetFromJsonAsync<ObserverSummary[]>(paths[0]) ?? []).Single(item => item.ObserverId == pendingRequest.ObserverId);
            Assert.Equal("pairing-expired", expired.RegistrationState);
            Assert.Null(await store.RedeemObserverAuthorizationAsync(pendingRequest, expiring.Authorization, credential));
            var renewed = Assert.IsType<ObserverPairingAuthorization>(await store.CreateObserverAuthorizationAsync(pendingRequest));
            Assert.NotEqual(expiring.Authorization, renewed.Authorization);
            using (var revokePending = await client.PostAsync($"/api/v1/observers/{pendingRequest.ObserverId}/revoke", null))
                Assert.Equal(HttpStatusCode.NoContent, revokePending.StatusCode);
            Assert.Null(await store.RedeemObserverAuthorizationAsync(pendingRequest, renewed.Authorization, credential));
            var revokedPending = (await client.GetFromJsonAsync<ObserverSummary[]>(paths[0]) ?? []).Single(item => item.ObserverId == pendingRequest.ObserverId);
            Assert.Equal("revoked", revokedPending.RegistrationState);
            Assert.DoesNotContain(renewed.Authorization, await client.GetStringAsync(paths[0]), StringComparison.Ordinal);
            await app.StopAsync();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", previous); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
