namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

[Collection("ServerTokenEnvironment")]
public sealed class ProjectResumeTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void PauseDetailIsBoundedAndSingleLine()
    {
        Assert.Null(ProjectResumeProtocol.Detail("  \n "));
        Assert.Equal("GitHub failed: retry", ProjectResumeProtocol.Detail("GitHub\tfailed:\r\n retry"));
        var detail = ProjectResumeProtocol.Detail(new string('x', 1000));
        Assert.Equal(ProjectResumeProtocol.DetailLength, detail?.Length);
        Assert.True(ManagedWorkerDiagnostics.Valid(new("retrieved", "synchronized", "server-retrieved", null, null, null, null,
            [new("project", 1, "blocked", ProjectResumeProtocol.PausedCode, detail)])));
        Assert.False(ManagedWorkerDiagnostics.Valid(new("retrieved", "synchronized", "server-retrieved", null, null, null, null,
            [new("project", 1, "blocked", ProjectResumeProtocol.PausedCode, "line\nbreak")])));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ExpiredResumeRequestsAreNotReplayed()
    {
        var database = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var clock = new TestClock();
            var resumes = new ProjectResumeStore(database, clock);
            await resumes.InitializeAsync();
            var worker = Guid.NewGuid().ToString("N");
            await resumes.CreateAsync(worker, "project", CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => resumes.CreateAsync(worker, "project", CancellationToken.None));
            var running = Assert.IsType<ProjectResumeOperation>(await resumes.ClaimAsync(worker, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resumes.ReportAsync(worker, new(running.Id, "anything"), CancellationToken.None));
            clock.Now = clock.Now.AddSeconds(ProjectResumeProtocol.TimeoutSeconds + 1);
            Assert.Null(await resumes.ClaimAsync(worker, CancellationToken.None));
            var expired = Assert.IsType<ProjectResumeOperation>(await resumes.GetAsync(worker, CancellationToken.None));
            Assert.Equal(("failed", "delivery-unavailable"), (expired.Status, expired.Diagnostic));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resumes.ReportAsync(worker, new(running.Id, "resumed"), CancellationToken.None));
            Assert.Equal("pending", (await resumes.CreateAsync(worker, "project", CancellationToken.None))?.Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(database); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("resumed", "succeeded")]
    [InlineData("not-paused", "failed")]
    public async Task OperatorResumesAProjectPausedOnTheWorker(string workerOutcome, string status)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var priorManagement = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "project-resume-management");
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync([
                $"--Server:ListenUrl={url}", $"--Server:DatabasePath={Path.Combine(directory, "server.db")}",
                "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            var registry = app.Services.GetRequiredService<IRegistryStore>();
            var identity = Path.Combine(directory, "identity");
            var id = await WorkerIdentity.LoadOrCreateAsync(identity);
            var token = await WorkerAuthentication.LoadOrCreateTokenAsync(identity);
            var bootstrap = await registry.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
            Assert.True(await registry.BootstrapWorkerAsync(bootstrap, new(2, id, "Worker", "1", "linux", 1, []), token));
            var project = await registry.CreateProjectAsync(new CentralProjectDefinition("Central", "owner/repo", "main", "description"));
            async Task HeartbeatAsync(string state, string? code) => await registry.HeartbeatWorkerAsync(new(2, id, "1", "running", 0, 1, [], [],
                ManagedDiagnostics: new("retrieved", "synchronized", "server-retrieved", null, null, null, null,
                    [new(project.Id, 1, state, code, code is null ? null : "Completion reconciliation required for Issue #7.")])));
            using var management = new HttpClient { BaseAddress = new Uri(url) };
            var path = $"/api/v1/workers/{id}/projects/{project.Id}/resume";
            using (var unauthorized = await management.PostAsync(path, null))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "project-resume-management");
            await HeartbeatAsync("ready", null);
            using (var notPaused = await management.PostAsync(path, null))
                Assert.Equal(HttpStatusCode.Conflict, notPaused.StatusCode);
            await HeartbeatAsync("blocked", ProjectResumeProtocol.PausedCode);
            var diagnostics = await management.GetFromJsonAsync<WorkerDiagnostics>($"/api/v1/workers/{id}/diagnostics");
            Assert.Equal("Completion reconciliation required for Issue #7.", Assert.Single(diagnostics!.Projects).Detail);
            using (var queued = await management.PostAsync(path, null)) queued.EnsureSuccessStatusCode();
            using (var duplicate = await management.PostAsync(path, null))
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

            using var workerHttp = new HttpClient();
            var worker = new WorkerRegistrationClient(workerHttp);
            var settings = new WorkerServerSettings { Enabled = true, Url = url, IdentityFile = identity };
            var requested = new List<string>();
            Assert.True(await worker.ExecuteProjectResumeAsync(settings, project => { requested.Add(project); return workerOutcome; }, CancellationToken.None));
            Assert.Equal(project.Id, Assert.Single(requested));
            var result = await management.GetFromJsonAsync<ProjectResumeOperation>($"/api/v1/workers/{id}/projects/resume");
            Assert.Equal((status, workerOutcome), (result?.Status, result?.Diagnostic));
            Assert.False(await worker.ExecuteProjectResumeAsync(settings, _ => throw new InvalidOperationException("Not replayed."), CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", priorManagement);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
