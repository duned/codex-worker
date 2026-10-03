namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;
using System.Text.Json;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerCredentialCliTests
{
    private const string NodeId = "12345678901234567890123456789012";
    private const string PrivateOutput = "private-provider-token";

    [Fact]
    public async Task StatusSeparatesInstallationAuthenticationDependenciesAndReadinessWithoutSecrets()
    {
        var discovery = Discovery(authenticated: false);
        var service = new NodeCredentialAdministration(discovery, (_, _, _) =>
            throw new InvalidOperationException("Status must not execute a command."));
        var result = await service.StatusAsync();
        var git = Assert.Single(result.Credentials, item => item.CapabilityId == "git");
        Assert.Empty(git.AuthenticationDependencies);
        Assert.Null(git.Authentication);
        Assert.True(git.Ready);
        foreach (var item in result.Credentials.Where(item => item.CapabilityId != "git"))
        {
            Assert.Equal(InstallationState.Installed, item.Installation);
            Assert.Equal(RequirementState.Required, item.Authentication);
            Assert.Single(item.AuthenticationDependencies);
            Assert.False(item.Ready);
        }
        Assert.DoesNotContain(PrivateOutput, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public async Task LoginUsesSharedHandlerPublishesPendingChallengeAndVerifiesCompletion(
        int loginExitCode, int verificationExitCode, bool succeeds)
    {
        var discovery = Discovery(succeeds);
        var verified = false;
        var executor = new NodeProvisioningCommandExecutor(discovery, (_, arguments, _) =>
        {
            Assert.Equal(new[] { "login", "status" }, arguments);
            verified = true;
            return Task.FromResult(verificationExitCode);
        }, login: async (publish, token) =>
        {
            await publish(new(CodexDeviceLogin.VerificationUri, "ABCD-EFGHI"), token);
            return loginExitCode;
        });
        var service = Service(discovery, executor, AllowedPolicy());
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await new WorkerCredentialCli(service, NodeId, output, error)
            .RunAsync(WorkerCredentialCli.Parse(["login", "codex-cli", "--json", "--timeout-seconds", "5"]));
        Assert.Equal(succeeds ? 0 : 2, exitCode);
        Assert.Equal(loginExitCode == 0, verified);
        using var progress = JsonDocument.Parse(error.ToString());
        Assert.Equal("Running", progress.RootElement.GetProperty("status").GetString());
        Assert.Equal("ABCD-EFGHI", progress.RootElement.GetProperty("loginInstructions").GetProperty("userCode").GetString());
        using var final = JsonDocument.Parse(output.ToString());
        Assert.Equal(succeeds ? "Succeeded" : "Failed", final.RootElement.GetProperty("report").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, final.RootElement.GetProperty("report").GetProperty("loginInstructions").ValueKind);
        Assert.DoesNotContain(PrivateOutput, output.ToString() + error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalPolicyDeniesLoginWithoutLaunchingProvider()
    {
        var discovery = Discovery(false);
        var executor = new NodeProvisioningCommandExecutor(discovery, login: (_, _) =>
            throw new InvalidOperationException("Denied login must not launch."));
        var result = await Service(discovery, executor, new ProvisioningPolicy()).ExecuteAsync(
            new(NodeId, "codex-cli", ProvisioningCommandAction.Login));
        Assert.Equal(ProvisioningDiagnostic.Denied, result.Report.Diagnostic);
    }

    [Theory]
    [InlineData("check", "github-cli", ProvisioningCommandAction.CheckAuthentication)]
    [InlineData("logout", "codex-cli", ProvisioningCommandAction.Logout)]
    public async Task LifecycleOperationsUseTypedSharedRequests(string verb, string provider, ProvisioningCommandAction action)
    {
        var command = WorkerCredentialCli.Parse([verb, provider]);
        var service = new NodeCredentialAdministration(Discovery(true), (request, _, _) =>
        {
            Assert.Equal(action, request.Action);
            Assert.Equal(provider, request.CapabilityId);
            return Task.FromResult(new ProvisioningCommandReport(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed));
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await new WorkerCredentialCli(service, NodeId, output, error).RunAsync(command));
        Assert.DoesNotContain(PrivateOutput, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingToolsAndCancellationDoNotImplyReadiness()
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => throw new FileNotFoundException(PrivateOutput));
        var service = new NodeCredentialAdministration(discovery, (_, _, _) =>
            Task.FromResult(new ProvisioningCommandReport(ProvisioningCommandStatus.Cancelled, ProvisioningDiagnostic.Cancelled)));
        var status = await service.StatusAsync();
        Assert.All(status.Credentials, item =>
        {
            Assert.Equal(InstallationState.Missing, item.Installation);
            Assert.False(item.Ready);
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(130, await new WorkerCredentialCli(service, NodeId, output, error)
            .RunAsync(WorkerCredentialCli.Parse(["login", "github-cli"])));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StatusAsync(new CancellationToken(true)));
    }

    [Theory]
    [InlineData("install", "github-cli")]
    [InlineData("login", "git")]
    [InlineData("login", "codex-cli", "--allow-elevation")]
    [InlineData("login", "codex-cli", "--secret", "unsafe-input")]
    [InlineData("status", "--json", "--json")]
    public void InvalidOrUnrelatedOperationsAreRejected(params string[] arguments) =>
        Assert.Throws<ArgumentException>(() => WorkerCredentialCli.Parse(arguments));

    [Fact]
    public void NamedCommandAndHelpAreDiscoverableWithoutConfiguration()
    {
        var command = WorkerCommandLine.Parse(["credential", "status", "--config", "worker.yml", "--json"]);
        Assert.Equal("credential", command.Command);
        Assert.Equal("worker.yml", command.ConfigurationPath);
        using var output = new StringWriter();
        WorkerCommandHelp.Write("credential", output);
        Assert.Contains("headless", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledDeviceLoginRetainsTerminalReportAndClearsChallenge()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = Discovery(false);
        var executor = new NodeProvisioningCommandExecutor(discovery, login: async (publish, token) =>
        {
            await publish(new(CodexDeviceLogin.VerificationUri, "ABCD-EFGHI"), token);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return 0;
        });
        var result = await Service(discovery, executor, AllowedPolicy()).ExecuteAsync(
            new(NodeId, "codex-cli", ProvisioningCommandAction.Login), cancellation.Token,
            (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.Cancelled, result.Report.Status);
        Assert.Null(result.Report.LoginInstructions);
        Assert.Empty(result.Credentials);
    }

    [Fact]
    public async Task InvalidChallengeNeverReachesPresentation()
    {
        var published = false;
        var service = new NodeCredentialAdministration(Discovery(false), async (_, token, progress) =>
        {
            Assert.NotNull(progress);
            await progress(new(ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing,
                LoginInstructions: new("https://unsafe.example", PrivateOutput)), token);
            return new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExecuteAsync(
            new(NodeId, "codex-cli", ProvisioningCommandAction.Login), progress: (_, _) =>
            {
                published = true;
                return Task.CompletedTask;
            }));
        Assert.False(published);
    }

    private static NodeCapabilityDiscovery Discovery(bool authenticated) => new((_, arguments, _) =>
        Task.FromResult((arguments.Contains("status") && !authenticated ? 1 : 0,
            arguments.Contains("--version") ? "1.0.0" : PrivateOutput)));

    private static ProvisioningPolicy AllowedPolicy() => new() { Enabled = true, AllowCredentials = true, AllowNonPrivileged = true };

    private static NodeCredentialAdministration Service(NodeCapabilityDiscovery discovery,
        NodeProvisioningCommandExecutor executor, ProvisioningPolicy policy) => new(discovery,
            (request, token, progress) => WorkerProvisioning.ExecuteLocalAsync(request, policy, discovery, token, progress, executor));
}
