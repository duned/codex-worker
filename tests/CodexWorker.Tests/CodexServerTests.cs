namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodexProvisioning;
using CodexServer;
using CodexWorker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServerWorkerCapability = CodexServer.WorkerCapability;
using WorkerCapability = CodexServer.WorkerCapability;

[CollectionDefinition("ServerTokenEnvironment", DisableParallelization = true)]
public sealed class ServerTokenEnvironmentCollection { }

[Collection("ServerTokenEnvironment")]
public sealed partial class CodexServerTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("file:///tmp/server")]
    [InlineData("http://user:pass@127.0.0.1:5090")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://0.0.0.0:5090")]
    public async Task InvalidListenConfigurationIsRejected(string listenUrl)
    {
        using var temporary = new TemporaryDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(Args(listenUrl, Path.Combine(temporary.Path, "server.db"))));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task EmptyPersistenceLocationIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(["--Server:ListenUrl=http://127.0.0.1:5090", "--Server:DatabasePath="]));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task EmptyDataDirectoryIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ServerApplication.BuildAsync(
            ["--Server:ListenUrl=http://127.0.0.1:5090", "--Server:DataDirectory="]));
    }

    private static string[] Args(string url, string databasePath)
    {
        // Keep successful ASP.NET Core lifetime and request logs out of normal test output.
        // Setting CODEXSERVER_TEST_LOG_LEVEL to Information or Debug restores verbose diagnostics.
        var logLevel = Environment.GetEnvironmentVariable("CODEXSERVER_TEST_LOG_LEVEL") ?? "Warning";
        return
        [
            $"--Server:ListenUrl={url}",
            $"--Server:DatabasePath={databasePath}",
            $"--Logging:LogLevel:Default={logLevel}",
            $"--Logging:LogLevel:Microsoft.AspNetCore={logLevel}"
        ];
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ServerWorkerCapability[] AuthenticationCapabilities(string repository) =>
        [.. WorkerAuthenticationRequirements.ForRepository(repository).Select(requirement =>
            new ServerWorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope)), new ServerWorkerCapability("agent-provider", "codex")];

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-server-test-{Guid.NewGuid():N}");
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
