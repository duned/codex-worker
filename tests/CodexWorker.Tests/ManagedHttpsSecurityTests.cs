using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CodexProvisioning;
using CodexServer;
using CodexWorker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class ManagedHttpsSecurityTests
{
    [Theory]
    [InlineData("enroll")]
    [InlineData("rotate")]
    [InlineData("recover")]
    [InlineData("associate")]
    public async Task AcceptedEnrollmentWithLostTlsResponseRecoversAcrossBothRestarts(string operation)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"managed-https-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var certificates = new IsolatedHttpsCertificates();
        var certificatePath = Path.Combine(directory, "server.pfx");
        await File.WriteAllBytesAsync(certificatePath, certificates.Server.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(certificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        listener.Stop();
        string[] args = [$"--Server:ListenUrl={url}", $"--Server:DatabasePath={Path.Combine(directory, "registry.db")}",
            $"--Kestrel:Certificates:Default:Path={certificatePath}", "--Logging:LogLevel:Default=Warning"];
        var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = Path.Combine(directory, "identity") };
        string? oldToken = null;
        var lostResponses = 0;
        var previousKey = Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY");
        var previousDeliveryToken = Environment.GetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        try
        {
            await using (var app = await ServerApplication.BuildAsync(args, capabilityDiscovery: TestCapabilityDiscovery.Create(),
                githubReadService: new HttpsFlowGitHubReads()))
            {
                var dropNext = 0;
                var associating = false;
                app.Use(async (context, next) =>
                {
                    if (associating)
                        Assert.NotEqual($"Bearer {oldToken}", context.Request.Headers.Authorization.ToString());
                    if (context.Request.Path != "/api/v1/workers/register" || Interlocked.Exchange(ref dropNext, 0) == 0)
                    {
                        await next(context);
                        return;
                    }
                    // Commit the real route but withhold its entire response, then break the TLS connection.
                    var original = context.Response.Body;
                    await using var withheld = new MemoryStream();
                    context.Response.Body = withheld;
                    try
                    {
                        await next(context);
                        Assert.Equal(200, context.Response.StatusCode);
                        Interlocked.Increment(ref lostResponses);
                        context.Abort();
                    }
                    finally { context.Response.Body = original; }
                });
                await app.StartAsync();
                var store = app.Services.GetRequiredService<IRegistryStore>();
                using var http = certificates.CreateClient();
                var client = CreateWorker(http);
                if (operation != "enroll")
                {
                    await client.BootstrapAsync(settings, 1, await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(5)), CancellationToken.None);
                    oldToken = WorkerAuthentication.GetToken(settings);
                    if (operation == "recover") await store.RevokeWorkerTokenAsync(await WorkerIdentity.LoadAsync(settings.IdentityFile));
                }
                if (operation == "associate")
                {
                    // Model a retained association at the previous origin. Its credential
                    // must never be probed at the new HTTPS endpoint, including on retry.
                    await File.WriteAllTextAsync(settings.IdentityFile + ".server", "https://previous.example");
                    associating = true;
                }
                var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
                var authorization = operation == "enroll" ? await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(5)) :
                    await store.CreateWorkerAuthorizationAsync(workerId, operation, TimeSpan.FromMinutes(5));
                Interlocked.Exchange(ref dropNext, 1);
                var error = await Assert.ThrowsAsync<WorkerStartupException>(() => client.BootstrapAsync(settings, 1, authorization, operation, CancellationToken.None));
                Assert.DoesNotContain(authorization, error.ToString());
                if (operation == "associate")
                {
                    Assert.Equal("https://previous.example", settings.EffectiveUrl);
                    Assert.Equal(oldToken, WorkerAuthentication.GetToken(settings));
                }
                var pending = Assert.IsType<WorkerPendingRegistration>(WorkerPendingRegistration.Load(settings.IdentityFile));
                Assert.True(await store.IsWorkerTokenValidAsync(workerId, pending.Token));
                Assert.Equal(1, Volatile.Read(ref lostResponses));
                await app.StopAsync();
            }
            // Both the Server and Worker client are recreated; only durable state survives.
            await using var restarted = await ServerApplication.BuildAsync(args, capabilityDiscovery: TestCapabilityDiscovery.Create(),
                githubReadService: new HttpsFlowGitHubReads());
            await restarted.StartAsync();
            var registry = restarted.Services.GetRequiredService<IRegistryStore>();
            var unused = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(5));
            using var recoveredHttp = certificates.CreateClient();
            var recovered = CreateWorker(recoveredHttp);
            var retained = Assert.IsType<WorkerPendingRegistration>(WorkerPendingRegistration.Load(settings.IdentityFile));
            await recovered.BootstrapAsync(settings, 1, unused, operation, CancellationToken.None);
            Assert.Null(WorkerPendingRegistration.Load(settings.IdentityFile));
            Assert.Equal(retained.Token, WorkerAuthentication.GetToken(settings));
            Assert.True(await registry.RevokeWorkerBootstrapTokenAsync(unused));
            var registered = Assert.Single(await registry.GetWorkersAsync());
            Assert.Equal(operation == "associate" ? WorkerSchedulingPolicy.Disabled : WorkerSchedulingPolicy.Enabled,
                registered.SchedulingPolicy);
            await recovered.HeartbeatAsync(settings, 1, 0, [], "running", CancellationToken.None);
            Assert.NotNull(await recovered.GetManagedConfigurationAsync(settings, CancellationToken.None));
            if (oldToken is not null)
            {
                Assert.NotEqual(oldToken, retained.Token);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/api/v1/workers/{retained.WorkerId}/configuration");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
                using var response = await recoveredHttp.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            }
            if (operation == "associate")
            {
                // Connection recovery preserves the preparation gate; scheduling needs explicit operator approval.
                var enabled = await registry.SetWorkerSchedulingPolicyAsync(retained.WorkerId, WorkerSchedulingPolicy.Enabled);
                Assert.Equal(WorkerSchedulingPolicy.Enabled, enabled?.SchedulingPolicy);
            }
            await VerifyManagedFlowAsync(recovered, recoveredHttp, settings, registry, restarted.Services);
            await restarted.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY", previousKey);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN", previousDeliveryToken);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class HttpsFlowGitHubReads : IServerGitHubReadService
    {
        private readonly AlwaysEligibleServerGitHubReadService _eligible = new();
        public async Task<ManagedGitHubIssuePage> ReadDiscoveryPageAsync(CentralProject project,
            GitHubIssueDiscoveryQuery query, CancellationToken cancellationToken = default) =>
            new([await _eligible.GetIssueAsync(project, 1, cancellationToken)
                ?? throw new InvalidOperationException("Fixture Issue is missing.")], null);
        public Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default) =>
            _eligible.CheckAccessAsync(project, cancellationToken);
        public Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query,
            CancellationToken cancellationToken = default) => _eligible.ListIssuesAsync(project, query, cancellationToken);
        public Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) =>
            _eligible.GetIssueAsync(project, issueNumber, cancellationToken);
        public Task<GitHubIssueRelationships?> GetIssueRelationshipsAsync(CentralProject project, int issueNumber,
            CancellationToken cancellationToken = default) => _eligible.GetIssueRelationshipsAsync(project, issueNumber, cancellationToken);
    }

    private static WorkerRegistrationClient CreateWorker(HttpClient http) => new(http,
        new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "10.0.0"))),
        new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromException<ProcessResult>(new FileNotFoundException())));

    private static async Task VerifyManagedFlowAsync(WorkerRegistrationClient worker, HttpClient http,
        WorkerServerSettings settings, IRegistryStore registry, IServiceProvider services)
    {
        var project = await registry.CreateProjectAsync(new CentralProjectDefinition("TLS flow", "team/disposable", "main", "", []));
        var snapshot = await worker.GetManagedConfigurationAsync(settings, CancellationToken.None);
        Assert.Equal(project.Revision, Assert.Single(snapshot.Projects).Revision);
        var synchronizer = new ManagedConfigurationSynchronizer(settings.IdentityFile + ".cache", new ManagedProjectRuntimeSettings
        {
            CheckoutDirectory = Path.Combine(Path.GetDirectoryName(settings.IdentityFile) ?? throw new InvalidOperationException(), "checkouts")
        });
        synchronizer.Apply(snapshot);
        WorkerCapabilityContract[] capabilities = [new("tool", "git"),
            .. WorkerAuthenticationCapabilities.ForRepository(project.Repository), WorkerAgentCapabilities.AuthenticatedProvider("codex")];
        await worker.HeartbeatAsync(settings, 1, 0, [], "running", CancellationToken.None, capabilities);
        var github = services.GetRequiredService<IServerGitHubAdministrationService>();
        var discovery = Assert.IsType<ManagedGitHubIssueDiscovery>(await github.DiscoverIssuesAsync(project.Id, new()));
        var candidate = Assert.Single(discovery.Candidates);
        Assert.Equal("eligible", candidate.Classification);
        var execution = await github.EnqueueIssueAsync(project.Id, candidate.WorkReference);
        var response = await worker.RequestAssignmentAsync(settings, true, 1, new Dictionary<string, int> { [project.Id] = 1 }, CancellationToken.None);
        var assignment = Assert.IsType<WorkerAssignmentContract>(response.Assignment);
        Assert.Equal(project.Revision, assignment.Project.Revision);
        var lease = Assert.IsType<ServerExecutionLeaseContract>(assignment.Lease);
        Assert.NotNull(await worker.RenewExecutionLeaseAsync(settings, lease, CancellationToken.None));
        Assert.Null(await worker.RenewExecutionLeaseAsync(settings, lease with { Generation = lease.Generation + 1 }, CancellationToken.None));
        var commands = services.GetRequiredService<ProvisioningCommandStore>();
        var command = await commands.CreateAsync(new(assignment.WorkerId, "git", ProvisioningCommandAction.Detect));
        Assert.True(await worker.ExecuteProvisioningCommandAsync(settings, new ProvisioningPolicy(), CancellationToken.None));
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await commands.GetAsync(command.Id))?.Status);
        using var report = new HttpRequestMessage(HttpMethod.Post,
            $"{settings.Url}/api/v1/workers/{assignment.WorkerId}/executions/{execution.Id}/report");
        report.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkerAuthentication.GetToken(settings));
        report.Content = JsonContent.Create(new WorkerExecutionReport(assignment.WorkerId, assignment.AssignmentId,
            "deterministic-run", "Completed", "Reporting", Generation: lease.Generation));
        using var acknowledgement = await WorkerServerHttpTransport.SendAsync(http, report, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, acknowledgement.StatusCode);
        Assert.Equal("Completed", (await registry.GetExecutionAsync(execution.Id))?.State);
        await VerifyCredentialDeliveryAsync(worker, http, settings, services.GetRequiredService<ICredentialStore>());
        Assert.DoesNotContain(WorkerAuthentication.GetToken(settings), await File.ReadAllTextAsync(settings.IdentityFile + ".cache"));
    }

    private static async Task VerifyCredentialDeliveryAsync(WorkerRegistrationClient worker, HttpClient http,
        WorkerServerSettings settings, ICredentialStore credentials)
    {
        var identityPath = settings.IdentityFile ?? throw new InvalidOperationException("Fixture identity path is missing.");
        var workerId = await WorkerIdentity.LoadAsync(identityPath);
        const string deliveryToken = "isolated-https-delivery-token-with-sufficient-entropy";
        const string sentinel = "isolated-https-provider-secret-sentinel";
        Environment.SetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN", deliveryToken);
        var credential = await credentials.CreateAsync(new("test-provider", "api-token", new(sentinel)));
        await credentials.SetWorkerDeliveryTokenAsync(workerId, new(deliveryToken));
        Assert.Null(await worker.RetrieveCredentialAsync(settings, credential.Id, CancellationToken.None));
        await credentials.AssignAsync(credential.Id, workerId);
        var delivered = Assert.IsType<CredentialDeliveryResponse>(await worker.RetrieveCredentialAsync(settings, credential.Id, CancellationToken.None));
        Assert.Equal(sentinel, delivered.Secret);
        Assert.DoesNotContain(sentinel, delivered.ToString());

        // Conditional requests still receive a fresh non-cacheable representation.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.Url}/api/v1/workers/{workerId}/credentials/{credential.Id}");
        request.Headers.Add("X-Worker-Credential-Token", deliveryToken);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await WorkerServerHttpTransport.SendAsync(http, request, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Null(response.Headers.ETag);
        Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).ToString());

        await credentials.AssignAsync(credential.Id, Guid.NewGuid().ToString("N"));
        Assert.Null(await worker.RetrieveCredentialAsync(settings, credential.Id, CancellationToken.None));
        await credentials.RevokeWorkerDeliveryTokenAsync(workerId);
        var rejected = await Assert.ThrowsAsync<HttpRequestException>(() => worker.RetrieveCredentialAsync(settings, credential.Id, CancellationToken.None));
        Assert.DoesNotContain(deliveryToken, rejected.ToString());
        Assert.DoesNotContain(sentinel, rejected.ToString());
        await worker.HeartbeatAsync(settings, 1, 0, [], "running", CancellationToken.None);
        var cache = await File.ReadAllTextAsync(identityPath + ".cache");
        Assert.DoesNotContain(sentinel, cache);
        Assert.DoesNotContain(deliveryToken, cache);
    }
}
