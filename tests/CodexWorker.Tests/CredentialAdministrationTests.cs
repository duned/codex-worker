namespace CodexWorker.Tests;

using System.Text.Json;
using CodexServer;

public sealed class CredentialAdministrationTests
{
    [Fact]
    public async Task LocalCredentialCliListsShowsAssignsReplacesAndRevokesWithoutPrintingSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await registry.RegisterWorkerAsync(new(2, workerId, "credential cli worker", "1.0", "test", 1, []));
        var credentials = new SqliteCredentialStore(database,
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        await credentials.InitializeAsync();
        var service = new LocalServerCredentialAdministrationService(database, registry, credentials);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var configuration = new ServerConfigurationAdministrationService();
        using var input = new StringReader("first-secret-value\r\n");
        var cli = new ServerCredentialAdministrationCli(configuration, new CredentialServiceFactory(service),
            input, output, error);
        string[] options = [$"--Server:DataDirectory={temporary.Path}", $"--Server:DatabasePath={database}"];

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(
            ["create", "github", "api-token", "--secret-stdin", "--json", .. options]));
        using var create = JsonDocument.Parse(output.ToString());
        var credentialId = Assert.IsType<string>(create.RootElement.GetProperty("id").GetString());
        Assert.Equal("Ready", create.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("first-secret-value", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["list", "--json", .. options]));
        using (var list = JsonDocument.Parse(output.ToString()))
        {
            var metadata = Assert.Single(list.RootElement.EnumerateArray());
            Assert.Equal(credentialId, metadata.GetProperty("id").GetString());
            Assert.False(metadata.TryGetProperty("secret", out _));
        }
        Assert.DoesNotContain("first-secret-value", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success,
            await cli.RunAsync(["assign", credentialId, workerId, "--json", .. options]));
        using (var assigned = JsonDocument.Parse(output.ToString()))
            Assert.Equal(workerId, assigned.RootElement.GetProperty("assignedWorkerId").GetString());
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success,
            await cli.RunAsync(["show", credentialId, "--json", .. options]));
        using (var shown = JsonDocument.Parse(output.ToString()))
        {
            Assert.Equal("github", shown.RootElement.GetProperty("provider").GetString());
            Assert.False(shown.RootElement.TryGetProperty("secret", out _));
        }
        output.GetStringBuilder().Clear();

        using var replacementInput = new StringReader("replacement-secret-value\n");
        var replacementCli = new ServerCredentialAdministrationCli(configuration,
            new CredentialServiceFactory(service), replacementInput, output, error);
        Assert.Equal(ServerAdministrationExitCodes.Success,
            await replacementCli.RunAsync(["replace", credentialId, "--secret-stdin", "--json", .. options]));
        using (var replaced = JsonDocument.Parse(output.ToString()))
            Assert.Equal(3, replaced.RootElement.GetProperty("version").GetInt64());
        Assert.DoesNotContain("replacement-secret-value", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("replacement-secret-value", await credentials.RetrieveForWorkerAsync(credentialId, workerId));
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success,
            await cli.RunAsync(["revoke", credentialId, "--json", .. options]));
        using (var revoked = JsonDocument.Parse(output.ToString()))
        {
            Assert.Equal("Revoked", revoked.RootElement.GetProperty("status").GetString());
            Assert.Null(revoked.RootElement.GetProperty("assignedWorkerId").GetString());
        }
        Assert.DoesNotContain("replacement-secret-value", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("first-secret-value", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task LocalCredentialCliReportsEncryptionFailureWithoutLeakingInput()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var credentials = new SqliteCredentialStore(database, string.Empty);
        await credentials.InitializeAsync();
        var service = new LocalServerCredentialAdministrationService(database, registry, credentials);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new StringReader("never-print-this-secret");
        var cli = new ServerCredentialAdministrationCli(new ServerConfigurationAdministrationService(),
            new CredentialServiceFactory(service), input, output, error);

        var result = await cli.RunAsync(["create", "provider", "token", "--secret-stdin", "--json",
            $"--Server:DataDirectory={temporary.Path}", $"--Server:DatabasePath={database}"]);

        Assert.Equal(ServerAdministrationExitCodes.OperationalFailure, result);
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("never-print-this-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("encryption is not configured", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalCredentialCliReadsInteractiveSecretWithoutStdinOrEchoingIt()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var credentials = new SqliteCredentialStore(database,
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        await credentials.InitializeAsync();
        var service = new LocalServerCredentialAdministrationService(database, registry, credentials);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var secret = "interactive-secret-value";
        var cli = new ServerCredentialAdministrationCli(new ServerConfigurationAdministrationService(),
            new CredentialServiceFactory(service), output: output, error: error,
            interactiveSecretReader: _ => Task.FromResult(secret));

        var result = await cli.RunAsync(["create", "github", "api-token", "--json",
            $"--Server:DataDirectory={temporary.Path}", $"--Server:DatabasePath={database}"]);

        Assert.Equal(ServerAdministrationExitCodes.Success, result);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Secret:", error.ToString(), StringComparison.Ordinal);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("Ready", response.RootElement.GetProperty("status").GetString());
    }

    private sealed class CredentialServiceFactory(IServerCredentialAdministrationService service)
        : IServerCredentialAdministrationServiceFactory
    {
        public IServerCredentialAdministrationService Create(ServerConfiguration configuration) => service;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"credential-admin-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
