namespace CodexWorker.Tests;

using CodexServer;
using System.Text.Json;

public sealed class ServerAdministrationTests
{
    [Fact]
    public void ConfigurationAdministrationUsesServerConfigurationPrecedenceAndRedactsSensitiveValues()
    {
        using var temporary = new TemporaryDirectory();
        const string secret = "private-server-value";
        var result = new ServerConfigurationAdministrationService().Inspect(
        [
            $"--Server:DataDirectory={Path.Combine(temporary.Path, "token=" + secret)}",
            "--Server:DatabasePath=state/server.db",
            "--Server:ListenUrl=http://operator:private-server-value@127.0.0.1:5090/path?api_key=private-server-value"
        ]);

        Assert.NotNull(result.Configuration);
        Assert.Equal(Path.Combine(temporary.Path, "token=" + secret), result.Configuration.ResolveDataDirectory());
        Assert.Equal(Path.Combine(temporary.Path, "token=" + secret, "state", "server.db"), result.Configuration.ResolveDatabasePath());
        Assert.False(result.Document.IsValid);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result.Document), StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:5090", result.Document.ListenUrl);
    }

    [Fact]
    public async Task LocalStatusAndDiagnosticsUseRegistryServicesAndKeepReadinessMeaningExplicit()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var configuration = new ServerConfiguration { DataDirectory = temporary.Path, DatabasePath = database };
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, Guid.NewGuid().ToString("N"),
            "test worker", "1.0.0", "test", 2, []));
        await registry.CreateProjectAsync(new CentralProjectDefinition("Example", "owner/repo", "main", "Example project"));
        var service = new LocalServerAdministrationService(configuration, registry, new ServerHealthService(registry));

        var status = await service.GetStatusAsync();
        var diagnostics = await service.GetDiagnosticsAsync();

        Assert.Equal("not-observed", status.ProcessHealth);
        Assert.Equal("ready", status.ControlPlaneReadiness);
        Assert.True(status.PersistenceAvailable);
        Assert.Equal(1, diagnostics.ContractVersion);
        Assert.Equal(1, diagnostics.RegisteredWorkers);
        Assert.Equal(1, diagnostics.Projects);
        Assert.Equal(2, diagnostics.MaximumCapacity);
        Assert.Contains(diagnostics.Notes, note => note.Contains("project eligibility", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingLocalDatabaseReturnsUnhealthyWithoutCreatingIt()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "missing.db");
        var configuration = new ServerConfiguration { DataDirectory = temporary.Path, DatabasePath = database };
        var registry = new SqliteRegistryStore(database);
        var service = new LocalServerAdministrationService(configuration, registry, new ServerHealthService(registry));

        var status = await service.GetStatusAsync();
        var diagnostics = await service.GetDiagnosticsAsync();

        Assert.Equal("not-ready", status.ControlPlaneReadiness);
        Assert.False(status.PersistenceAvailable);
        Assert.False(File.Exists(database));
        Assert.Null(diagnostics.RegisteredWorkers);
        Assert.NotNull(diagnostics.Diagnostic);
    }

    [Fact]
    public async Task CliProducesVersionedJsonAndStableFailureExitCodes()
    {
        var configuration = new ServerConfigurationAdministrationService();
        var factory = new StubAdministrationFactory();
        using var temporary = new TemporaryDirectory();
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new ServerAdministrationCli(configuration, factory, output, error);

        var statusCode = await cli.RunAsync(["status", "--json", $"--Server:DataDirectory={temporary.Path}"]);
        using var statusJson = JsonDocument.Parse(output.ToString());
        output.GetStringBuilder().Clear();
        var diagnosticsCode = await cli.RunAsync(["diagnostics", "--json", $"--Server:DataDirectory={temporary.Path}"]);
        using var diagnosticsJson = JsonDocument.Parse(output.ToString());
        var usageCode = await cli.RunAsync(["status", "--unexpected"]);

        Assert.Equal(ServerAdministrationExitCodes.Success, statusCode);
        Assert.Equal(1, statusJson.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("not-observed", statusJson.RootElement.GetProperty("processHealth").GetString());
        Assert.False(statusJson.RootElement.GetProperty("loopbackContacted").GetBoolean());
        Assert.Equal(ServerAdministrationExitCodes.Success, diagnosticsCode);
        Assert.Equal(1, diagnosticsJson.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.False(diagnosticsJson.RootElement.GetProperty("loopbackContacted").GetBoolean());
        Assert.Equal(ServerAdministrationExitCodes.InvalidArguments, usageCode);
        Assert.Contains("Invalid Server administration options", error.ToString(), StringComparison.Ordinal);

        factory.Service.PersistenceAvailable = false;
        Assert.Equal(ServerAdministrationExitCodes.OperationalFailure,
            await cli.RunAsync(["status", "--json", $"--Server:DataDirectory={temporary.Path}"]));
    }

    [Fact]
    public async Task ConfigShowAndValidateAreOfflineAndDoNotExposeCredentialsOrPaths()
    {
        const string secret = "private-admin-secret";
        using var temporary = new TemporaryDirectory();
        var output = new StringWriter();
        var error = new StringWriter();
        var factory = new StubAdministrationFactory();
        var cli = new ServerAdministrationCli(new ServerConfigurationAdministrationService(), factory, output, error);

        var showCode = await cli.RunAsync(["config", "show", "--json",
            $"--Server:DataDirectory={Path.Combine(temporary.Path, "password=" + secret)}",
            "--Server:ListenUrl=http://127.0.0.1:5090"]);
        using var json = JsonDocument.Parse(output.ToString());
        var validateCode = await cli.RunAsync(["config", "validate", "--json", "--Server:WorkerStaleAfterSeconds=5"]);

        Assert.Equal(ServerAdministrationExitCodes.Success, showCode);
        Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("[redacted]", json.RootElement.GetProperty("dataDirectory").GetString());
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(ServerAdministrationExitCodes.OperationalFailure, validateCode);
        Assert.Contains("invalid-server-configuration", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public async Task CliMapsCancellationToItsStableCanceledExitCode()
    {
        using var temporary = new TemporaryDirectory();
        var factory = new StubAdministrationFactory();
        var cli = new ServerAdministrationCli(new ServerConfigurationAdministrationService(), factory,
            new StringWriter(), new StringWriter());

        Assert.Equal(ServerAdministrationExitCodes.Canceled, await cli.RunAsync(
            ["status", $"--Server:DataDirectory={temporary.Path}"], new CancellationToken(canceled: true)));
    }

    private sealed class StubAdministrationFactory : IServerAdministrationServiceFactory
    {
        public StubAdministrationService Service { get; } = new();
        public int CreateCalls { get; private set; }
        public IServerAdministrationService Create(ServerConfiguration configuration)
        {
            CreateCalls++;
            return Service;
        }
    }

    private sealed class StubAdministrationService : IServerAdministrationService
    {
        public bool PersistenceAvailable { get; set; } = true;

        public Task<ServerAdministrationStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ServerAdministrationStatusDocument(1, "test", "not-observed",
                PersistenceAvailable ? "ready" : "not-ready", PersistenceAvailable, false, "http://127.0.0.1:5090",
                "[redacted]", "[redacted]", null));
        }

        public Task<ServerAdministrationDiagnosticsDocument> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ServerAdministrationDiagnosticsDocument(1, DateTimeOffset.UnixEpoch, "ready", true, false,
                0, new Dictionary<string, int>(), new Dictionary<string, int>(), 0, 0, 0, 0, [], null));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"server-administration-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
