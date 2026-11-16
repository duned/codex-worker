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

public sealed partial class CodexServerTests
{
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task DirectTokenCommandRejectsImplicitHomeState()
    {
        var priorDirectory = Environment.GetEnvironmentVariable("Server__DataDirectory");
        var priorDatabase = Environment.GetEnvironmentVariable("Server__DatabasePath");
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", null);
            Environment.SetEnvironmentVariable("Server__DatabasePath", null);
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(1, await CodexServer.Program.Main(["worker-token", "create"]));
            Assert.Empty(output.ToString());
            Assert.Contains("service state configuration", error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("Server__DataDirectory", priorDirectory);
            Environment.SetEnvironmentVariable("Server__DatabasePath", priorDatabase);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("--help", 0)]
    [InlineData("-h", 0)]
    [InlineData("--version", 0)]
    [InlineData("unknown-private-secret", 2)]
    [InlineData("--unknown", 2)]
    [InlineData("worker-token", 2)]
    [InlineData("backup", 2)]
    public async Task CliDispatchDoesNotStartHostOrCreateStateWhenDefaultPortIsOccupied(string argument, int expectedExit)
    {
        using var temporary = new TemporaryDirectory();
        var state = Path.Combine(temporary.Path, "must-not-exist");
        var priorDirectory = Environment.GetEnvironmentVariable("Server__DataDirectory");
        var priorUrl = Environment.GetEnvironmentVariable("Server__ListenUrl");
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var listener = new TcpListener(IPAddress.Loopback, 5090);
        // If a real Server already occupies the port, it supplies the same condition.
        try { listener.Start(); }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse) { }
        try
        {
            Environment.SetEnvironmentVariable("Server__DataDirectory", state);
            Environment.SetEnvironmentVariable("Server__ListenUrl", "http://127.0.0.1:5090");
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(expectedExit, await CodexServer.Program.Main([argument]));
            Assert.False(Directory.Exists(state));
            if (argument is "--help" or "-h")
            {
                Assert.Contains("Usage:", output.ToString());
                Assert.Contains("sudo codex-server worker-token create", output.ToString());
            }
            else if (argument == "--version") Assert.Equal($"Codex Server {ServerApplication.DisplayVersion}", output.ToString().Trim());
            else Assert.Contains("--help", error.ToString());
            Assert.DoesNotContain("unknown-private-secret", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("initialized", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("Server__DataDirectory", priorDirectory);
            Environment.SetEnvironmentVariable("Server__ListenUrl", priorUrl);
        }
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void CapabilityContractSupportsLegacyStringsAndUnknownFutureTypes()
    {
        Assert.Equal(new ServerWorkerCapability("tool", "git"), JsonSerializer.Deserialize<ServerWorkerCapability>("\"git\""));
        Assert.Equal(new ServerWorkerCapability("future-runtime", "specialized", "4.2"),
            JsonSerializer.Deserialize<ServerWorkerCapability>("{\"type\":\"future-runtime\",\"name\":\"specialized\",\"version\":\"4.2\"}"));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("{\"type\":\"tool\",\"name\":\"git\",\"extra\":true}")]
    [InlineData("{\"type\":\"tool\",\"name\":7}")]
    [InlineData("{\"type\":\"tool\"}")]
    public void MalformedCapabilityDataIsRejected(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ServerWorkerCapability>(json));

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ServerLeaseTimingDefaultsAreConservativeAndRenewalMustPrecedeExpiry()
    {
        var configuration = new ServerConfiguration();
        configuration.Validate();
        Assert.Equal(900, configuration.ExecutionLeaseDurationSeconds);
        Assert.Equal(60, configuration.ExecutionLeaseRenewalIntervalSeconds);
        configuration.ExecutionLeaseDurationSeconds = 120;
        configuration.ExecutionLeaseRenewalIntervalSeconds = 40;
        Assert.Throws<InvalidDataException>(configuration.Validate);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task BootstrapTokensAreShortLivedOneTimeAndCreatePerWorkerCredentials()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "registry.db"), timeProvider: clock);
        await store.InitializeAsync();
        var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        var workerId = Guid.NewGuid().ToString("N");
        Assert.False(await EnrollWorkerAsync(store, "invalid", workerId, "worker-secret-with-sufficient-entropy"));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(await EnrollWorkerAsync(store, bootstrap, workerId, "worker-secret-with-sufficient-entropy"));

        var validBootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(15));
        Assert.True(await EnrollWorkerAsync(store, validBootstrap, workerId, "worker-secret-with-sufficient-entropy"));
        Assert.False(await EnrollWorkerAsync(store, validBootstrap, workerId, "other-secret-with-sufficient-entropy"));
        Assert.True(await store.IsWorkerTokenValidAsync(workerId, "worker-secret-with-sufficient-entropy"));
        Assert.False(await store.IsWorkerTokenValidAsync(workerId, "other-secret-with-sufficient-entropy"));
        Assert.True(await store.RevokeWorkerTokenAsync(workerId));
        Assert.False(await store.IsWorkerTokenValidAsync(workerId, "worker-secret-with-sufficient-entropy"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void DataDirectoryAndRelativeDatabasePathResolveOutsideTheWorkingDirectory()
    {
        using var temporary = new TemporaryDirectory();
        var configuration = new ServerConfiguration { DataDirectory = Path.Combine(temporary.Path, "state") };
        Assert.Equal(Path.Combine(temporary.Path, "state"), configuration.ResolveDataDirectory());
        Assert.Equal(Path.Combine(temporary.Path, "state", "codex-server.db"), configuration.ResolveDatabasePath());
        configuration.DatabasePath = "custom/server.db";
        Assert.Equal(Path.Combine(temporary.Path, "state", "custom", "server.db"), configuration.ResolveDatabasePath());
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task BackupRoundTripPreservesControlPlaneMetadataAndExcludesSecrets()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var restoredDatabase = Path.Combine(temporary.Path, "restored.db");
        var archivePath = Path.Combine(temporary.Path, "backup.zip");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(new CentralProjectDefinition("Example", "owner/repo", "main", "description"));
        var encryptionKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var credentials = new SqliteCredentialStore(database, encryptionKey);
        await credentials.InitializeAsync();
        const string secret = "credential-secret-value";
        const string deliveryToken = "worker-delivery-token-value-that-is-long-enough";
        var workerId = Guid.NewGuid().ToString("N");
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "backup worker", "1.0.0", "test", 1, [new WorkerCapability("tool", "git")]));
        var legacyPlan = await registry.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
        var typedCommands = new ProvisioningCommandStore(database);
        await typedCommands.InitializeAsync();
        var typedCommand = await typedCommands.CreateAsync(new(workerId, "git", ProvisioningCommandAction.Detect));
        var workerBootstrap = await registry.CreateWorkerAuthorizationAsync(workerId, "rotate", TimeSpan.FromMinutes(1));
        Assert.True(await EnrollWorkerAsync(registry, workerBootstrap, workerId, "backup-worker-api-token-with-sufficient-entropy"));
        await registry.SetWorkerSchedulingPolicyAsync(workerId, WorkerSchedulingPolicy.Draining);
        var credential = await credentials.CreateAsync(new CreateCredentialRequest("github", "api", new CredentialSecretInput(secret)));
        await credentials.AssignAsync(credential.Id, workerId);
        var credentialForRevocation = await credentials.CreateAsync(new CreateCredentialRequest("github", "api",
            new CredentialSecretInput("second-backup-credential-secret")));
        await credentials.SetWorkerDeliveryTokenAsync(workerId, new CredentialSecretInput(deliveryToken));

        await new ServerBackup(database).ExportAsync(archivePath);
        await new ServerBackup(database).ValidateAsync(archivePath);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
        {
            var manifestEntry = Assert.IsType<System.IO.Compression.ZipArchiveEntry>(archive.GetEntry("manifest.json"));
            await using (var manifestStream = manifestEntry.Open())
            using (var manifest = await JsonDocument.ParseAsync(manifestStream))
            {
                Assert.Equal(1, manifest.RootElement.GetProperty("formatVersion").GetInt32());
                Assert.Equal(SqliteRegistryStore.CurrentSchemaVersion, manifest.RootElement.GetProperty("registrySchemaVersion").GetInt32());
                Assert.Contains("legacy provisioning plans", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
                Assert.Contains("typed provisioning command history when present", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
                Assert.Contains("credential metadata; ready credentials require re-provisioning", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
                Assert.Contains("revoked Worker API-token metadata", manifest.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetString()));
            }
            var entry = Assert.IsType<System.IO.Compression.ZipArchiveEntry>(archive.GetEntry("state.sqlite"));
            await using var stream = entry.Open();
            using var contents = new MemoryStream();
            await stream.CopyToAsync(contents);
            var databaseContents = System.Text.Encoding.Latin1.GetString(contents.ToArray());
            Assert.DoesNotContain(secret, databaseContents, StringComparison.Ordinal);
            Assert.DoesNotContain("second-backup-credential-secret", databaseContents, StringComparison.Ordinal);
            Assert.DoesNotContain(deliveryToken, databaseContents, StringComparison.Ordinal);
            Assert.DoesNotContain(workerBootstrap, databaseContents, StringComparison.Ordinal);
            Assert.DoesNotContain("backup-worker-api-token-with-sufficient-entropy", databaseContents, StringComparison.Ordinal);
            Assert.DoesNotContain(encryptionKey, databaseContents, StringComparison.Ordinal);
        }

        await new ServerBackup(restoredDatabase).RestoreOfflineAsync(archivePath);
        var restoredRegistry = new SqliteRegistryStore(restoredDatabase);
        await restoredRegistry.InitializeAsync();
        var restoredProject = Assert.IsType<CentralProject>(await restoredRegistry.GetProjectAsync(project.Id));
        Assert.Equal(project.Id, restoredProject.Id);
        Assert.Equal(project.Name, restoredProject.Name);
        Assert.Equal(project.Repository, restoredProject.Repository);
        var restoredWorker = await restoredRegistry.GetWorkerAsync(workerId);
        Assert.Equal("backup worker", restoredWorker?.DisplayName);
        Assert.Equal(WorkerSchedulingPolicy.Draining, restoredWorker?.SchedulingPolicy);
        Assert.Equal("Cancelled", (await restoredRegistry.GetProvisioningPlanAsync(legacyPlan.Id))?.State);
        var restoredCommands = new ProvisioningCommandStore(restoredDatabase);
        await restoredCommands.InitializeAsync();
        var restoredTypedCommand = Assert.Single(await restoredCommands.ListAsync());
        Assert.Equal(typedCommand.Id, restoredTypedCommand.Id);
        Assert.Equal(ProvisioningCommandStatus.Cancelled, restoredTypedCommand.Status);
        Assert.Null(await restoredCommands.ClaimAsync(workerId));
        Assert.Equal("revoked", restoredWorker?.AuthenticationCredentialStatus);

        await using (var metadataConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = restoredDatabase, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await metadataConnection.OpenAsync();
            await using var metadataCommand = metadataConnection.CreateCommand();
            metadataCommand.CommandText = "SELECT COUNT(*) FROM worker_auth_tokens WHERE revoked_at_utc IS NOT NULL AND token_hash=zeroblob(length(token_hash));";
            Assert.Equal(1L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
            metadataCommand.CommandText = "SELECT COUNT(*) FROM worker_bootstrap_tokens;";
            Assert.Equal(0L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
            metadataCommand.CommandText = "SELECT COUNT(*) FROM worker_credential_auth;";
            Assert.Equal(0L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
            metadataCommand.CommandText = "SELECT COUNT(*) FROM credentials WHERE status='NeedsReprovision' AND assigned_worker_id IS NULL AND length(nonce)=0 AND length(ciphertext)=0 AND length(tag)=0;";
            Assert.Equal(2L, Assert.IsType<long>(await metadataCommand.ExecuteScalarAsync()));
        }

        var restoredCredentials = new SqliteCredentialStore(restoredDatabase, encryptionKey);
        await restoredCredentials.InitializeAsync();
        var restoredCredentialMetadata = await restoredCredentials.ListAsync();
        var metadata = Assert.Single(restoredCredentialMetadata, item => item.Id == credential.Id);
        var metadataForRevocation = Assert.Single(restoredCredentialMetadata, item => item.Id == credentialForRevocation.Id);
        Assert.Equal("NeedsReprovision", metadata.Status);
        Assert.Equal("NeedsReprovision", metadataForRevocation.Status);
        Assert.False(await restoredCredentials.IsWorkerDeliveryTokenValidAsync(workerId, deliveryToken));
        Assert.Null(await restoredCredentials.RetrieveForWorkerAsync(credential.Id, workerId));
        var revokedAfterRestore = await restoredCredentials.RevokeAsync(credentialForRevocation.Id);
        Assert.Equal("Revoked", revokedAfterRestore?.Status);
        Assert.Null(revokedAfterRestore?.AssignedWorkerId);
        Assert.Equal("Ready", (await restoredCredentials.ReplaceSecretAsync(credential.Id, new CredentialSecretInput("re-entered-test-secret")))?.Status);
        await restoredCredentials.AssignAsync(credential.Id, workerId);
        Assert.Equal("re-entered-test-secret", await restoredCredentials.RetrieveForWorkerAsync(credential.Id, workerId));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task BackupRejectsTypedProvisioningHistoryThatDoesNotMatchItsCurrentSchema()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var archivePath = Path.Combine(temporary.Path, "backup.zip");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var commands = new ProvisioningCommandStore(database);
        await commands.InitializeAsync();
        await commands.CreateAsync(new("server", "git", ProvisioningCommandAction.Detect));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE provisioning_commands SET body='{}';";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerBackup(database).ExportAsync(archivePath));
        Assert.False(File.Exists(archivePath));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task BackupRestoreRequiresExclusiveOfflineServerDatabaseAccess()
    {
        using var temporary = new TemporaryDirectory();
        var sourceDatabase = Path.Combine(temporary.Path, "source.db");
        var targetDatabase = Path.Combine(temporary.Path, "target.db");
        var archivePath = Path.Combine(temporary.Path, "backup.zip");
        var sourceRegistry = new SqliteRegistryStore(sourceDatabase);
        await sourceRegistry.InitializeAsync();
        var project = await sourceRegistry.CreateProjectAsync(new CentralProjectDefinition("Backup", "owner/repo", "main", "snapshot"));
        await new SqliteCredentialStore(sourceDatabase, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))).InitializeAsync();
        await new ProvisioningCommandStore(sourceDatabase).InitializeAsync();
        await new ServerBackup(sourceDatabase).ExportAsync(archivePath);

        var targetRegistry = new SqliteRegistryStore(targetDatabase);
        await targetRegistry.InitializeAsync();
        var retained = await targetRegistry.CreateProjectAsync(new CentralProjectDefinition("Retained", "owner/retained", "main", "keep"));
        await using (var app = await ServerApplication.BuildAsync(Args($"http://127.0.0.1:{ReservePort()}", targetDatabase)))
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => new ServerBackup(targetDatabase).RestoreOfflineAsync(archivePath));
            Assert.Contains("Stop the Server", exception.Message, StringComparison.Ordinal);
            Assert.Equal(retained.Id, (await targetRegistry.GetProjectAsync(retained.Id))?.Id);
        }

        await new ServerBackup(targetDatabase).RestoreOfflineAsync(archivePath);
        var restored = Assert.IsType<CentralProject>(await new SqliteRegistryStore(targetDatabase).GetProjectAsync(project.Id));
        Assert.Equal(project.Name, restored.Name);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task InvalidBackupIsRejectedBeforeRestoreChangesExistingDatabase()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "server.db");
        var archivePath = Path.Combine(temporary.Path, "invalid.zip");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var project = await registry.CreateProjectAsync(new CentralProjectDefinition("Existing", "owner/existing", "main", "keep"));
        await using (var file = File.Create(archivePath))
        using (var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json");
            await using var stream = manifest.Open();
            await JsonSerializer.SerializeAsync(stream, new { formatVersion = 999, registrySchemaVersion = SqliteRegistryStore.CurrentSchemaVersion });
        }

        var backup = new ServerBackup(database);
        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreOfflineAsync(archivePath));
        var existingProject = Assert.IsType<CentralProject>(await registry.GetProjectAsync(project.Id));
        Assert.Equal(project.Name, existingProject.Name);
    }
}
