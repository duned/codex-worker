namespace CodexWorker.Tests;

using System.Security.Cryptography;
using System.Text.Json;
using CodexProvisioning;
using CodexServer;
using CodexWorker;

public sealed class SharedCredentialServicesTests
{
    [Theory]
    [InlineData("X-Codex-Worker-Token: SENTINEL-bootstrap-secret")]
    [InlineData("X-Worker-Credential-Token: SENTINEL-delivery-secret")]
    [InlineData("Authorization: Bearer SENTINEL-api-secret")]
    [InlineData("{\"secret\":\"SENTINEL secret with spaces\"}")]
    [InlineData("password='SENTINEL password with spaces'")]
    public void SharedRedactionRemovesHeaderAndQuotedSecretsBeforeBounding(string value)
    {
        var safe = SecretSanitizer.Sanitize(value, 1000);
        Assert.DoesNotContain("SENTINEL", safe);
        Assert.DoesNotContain("with spaces", safe);
        Assert.Contains("[redacted]", safe);
        Assert.True(SecretSanitizer.Sanitize(value, 10).Length <= 10);
        Assert.DoesNotContain("SENTINEL", FailureDiagnosticRedactor.Redact(value));
    }

    [Fact]
    public async Task ServerStorageAndWorkerAuthenticationComposeTheSharedCredentialContract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shared-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "credentials.db");
            var registry = new SqliteRegistryStore(database);
            await registry.InitializeAsync();
            var workerId = Guid.NewGuid().ToString("N");
            await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, []));
            var store = new SqliteCredentialStore(database, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var server = new LocalServerCredentialAdministrationService(database, registry, store);
            const string secret = "fake-shared-github-token";
            var metadata = await server.CreateAsync("github", "api-token", new(secret));
            await server.AssignAsync(metadata.Id, workerId);
            var deliveredSecret = await store.RetrieveForWorkerAsync(metadata.Id, workerId);
            Assert.Equal(secret, deliveredSecret);
            var delivery = JsonSerializer.Deserialize<CredentialDeliveryResponse>(JsonSerializer.Serialize(
                new CredentialDeliveryResponse(metadata.Id, metadata.Provider, metadata.Type, 2, deliveredSecret ?? throw new InvalidOperationException("Expected assigned secret."))));
            Assert.NotNull(delivery);
            Assert.DoesNotContain(secret, delivery.ToString(), StringComparison.Ordinal);

            var github = new GitHubClient("team/repo", (arguments, _) => Task.FromResult(new ProcessResult(0,
                arguments.Contains("--version") ? "gh version 2.45.0" : arguments.Contains("--jq") ? "{\"push\":true}" : "[]", "")));
            var calls = 0;
            var worker = new GitHubAuthenticationProvisioner((executable, arguments, _, timeout, _, input) =>
            {
                calls++;
                Assert.Equal("gh", executable);
                Assert.Equal("--with-token", arguments.Last());
                Assert.Equal(TimeSpan.FromSeconds(30), timeout);
                Assert.Equal(secret + "\n", input);
                Assert.DoesNotContain(secret, arguments);
                return Task.FromResult(new ProcessResult(0, "private CLI output", ""));
            }, _ => github, _ => null, (_, _) => Task.FromResult<CredentialDeliveryResponse?>(delivery));
            var result = await worker.ExecuteAsync(new ProvisioningActionContract("auth", "authentication", "github-api",
                Operation: "provision", CredentialId: metadata.Id, Scope: "team/repo"), CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal(1, calls);
            Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);

            await server.RevokeAsync(metadata.Id);
            Assert.Null(await store.RetrieveForWorkerAsync(metadata.Id, workerId));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("github", "api-token", "credential", 1, "fake-token", true)]
    [InlineData("GitHub", "personal-access-token", "credential", 2, "fake-token", true)]
    [InlineData("other", "api-token", "credential", 1, "fake-token", false)]
    [InlineData("github", "password", "credential", 1, "fake-token", false)]
    [InlineData("github", "token", "other-credential", 1, "fake-token", false)]
    [InlineData("github", "token", "credential", 0, "fake-token", false)]
    [InlineData("github", "token", "credential", 1, "", false)]
    public async Task SharedTokenAuthenticationValidatesBeforePassingSecretThroughStandardInput(
        string provider, string type, string id, long version, string secret, bool supported)
    {
        var calls = 0;
        var authentication = new GitHubTokenAuthentication((executable, arguments, input, _, _) =>
        {
            calls++;
            Assert.Equal("gh", executable);
            Assert.Equal(new[] { "auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--with-token" }, arguments);
            Assert.Equal(secret + "\n", input);
            return Task.FromResult(7);
        });
        var report = await authentication.AuthenticateAsync("credential", new(id, provider, type, version, secret));
        Assert.Equal(supported ? 1 : 0, calls);
        Assert.Equal(ProvisioningCommandStatus.Failed, report.Status);
        Assert.Equal(supported ? ProvisioningDiagnostic.ProcessFailed : ProvisioningDiagnostic.Unsupported, report.Diagnostic);
        if (supported)
        {
            Assert.Equal(7, report.FailureDetail?.ProcessExitCode);
            Assert.True(ProvisioningCommandProtocol.ValidReport(report));
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SharedTokenAuthenticationReturnsSafeTimeoutAndProcessFailureReports()
    {
        const string secret = "fake-token-that-must-not-leak";
        foreach (var error in new Exception[] { new TimeoutException(secret), new IOException(secret) })
        {
            var authentication = new GitHubTokenAuthentication((_, _, _, _, _) => Task.FromException<int>(error));
            var report = await authentication.AuthenticateAsync("credential", new("credential", "github", "token", 1, secret));
            Assert.Equal(error is TimeoutException ? ProvisioningCommandStatus.TimedOut : ProvisioningCommandStatus.Failed, report.Status);
            Assert.True(ProvisioningCommandProtocol.ValidReport(report));
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SharedTokenAuthenticationPropagatesCancellationWithoutStartingLogin()
    {
        var authentication = new GitHubTokenAuthentication((_, _, _, _, _) => throw new InvalidOperationException("Login must not start."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authentication.AuthenticateAsync("credential",
            new("credential", "github", "token", 1, "fake-token"), new CancellationToken(true)));
    }
}
