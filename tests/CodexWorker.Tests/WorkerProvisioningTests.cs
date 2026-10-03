using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerProvisioningTests
{
    [Theory]
    [InlineData(ProvisioningCommandAction.Install)]
    [InlineData(ProvisioningCommandAction.Update)]
    [InlineData(ProvisioningCommandAction.Uninstall)]
    public async Task AdministrationUsesSharedLifecycleAndKeepsInstallationSeparateFromAuthentication(ProvisioningCommandAction action)
    {
        var installed = action == ProvisioningCommandAction.Uninstall;
        var commands = new List<string>();
        var discovery = new NodeCapabilityDiscovery((executable, arguments, _) =>
        {
            if (arguments[0] == "--version")
                return installed ? Task.FromResult((0, "1.0.0")) : Task.FromException<(int, string)>(new FileNotFoundException());
            if (executable == "/usr/bin/apt-cache") return Task.FromResult((0, "Installed: 1.0.0\nCandidate: 1.0.0"));
            return Task.FromResult((1, "secret-output"));
        });
        var executor = new NodeProvisioningCommandExecutor(discovery, (executable, arguments, _) =>
        {
            commands.Add(executable);
            if (executable == "/usr/bin/apt-get" && arguments.Contains("gh"))
                installed = arguments[0] != "remove";
            return Task.FromResult(0);
        }, supportsApt: () => true, isRoot: () => true);
        var policy = new ProvisioningPolicy { Enabled = true,
            AllowedPrivilegedActions = [$"tool:github-cli:{action}".ToLowerInvariant()] };
        var service = new WorkerProvisioningAdministrationService(policy, discovery, new string('a', 32), executor);

        var result = await service.ExecuteAsync(new("github-cli", action, AllowElevation: true));

        Assert.Equal("succeeded", result.Status);
        Assert.Contains("/usr/bin/apt-get", commands);
        Assert.Equal(action == ProvisioningCommandAction.Uninstall ? InstallationState.Missing : InstallationState.Installed,
            result.Capability?.Installation);
        Assert.Equal(action == ProvisioningCommandAction.Uninstall ? RequirementState.Unknown : RequirementState.Required,
            result.Capability?.Authentication);
        Assert.Equal([AuthenticationDependencyKind.GitHubCliLogin], result.AuthenticationDependencies);
        Assert.False(CapabilityCatalog.Ready([result.Capability ?? throw new InvalidOperationException("Missing capability")]));
        Assert.DoesNotContain("secret-output", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedInstallerFailureReturnsAnOutcomeAndAdministrationRemainsUsable()
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromException<(int, string)>(new FileNotFoundException()));
        var executor = new NodeProvisioningCommandExecutor(discovery, (_, _, _) => Task.FromResult(23),
            supportsApt: () => true, isRoot: () => true);
        var service = new WorkerProvisioningAdministrationService(new ProvisioningPolicy
        {
            Enabled = true, AllowedPrivilegedActions = ["tool:git:install"]
        }, discovery, new string('a', 32), executor);

        var result = await service.ExecuteAsync(new("git", ProvisioningCommandAction.Install, AllowElevation: true));

        Assert.Equal("failed", result.Status);
        Assert.Equal(ProvisioningDiagnostic.ProcessFailed, result.Diagnostic);
        Assert.Equal(23, result.Report?.FailureDetail?.ProcessExitCode);
        Assert.Equal(InstallationState.Missing, result.Capability?.Installation);
        Assert.Equal("succeeded", (await service.GetStatusAsync()).Status);
    }

    [Fact]
    public async Task LocalDetectionWorksWithoutServerOrExecutionTools()
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromException<(int, string)>(new FileNotFoundException()));
        var request = new ProvisioningCommandRequest(new string('a', 32), "codex-cli", ProvisioningCommandAction.Detect);
        var result = await WorkerProvisioning.ExecuteLocalAsync(request, new ProvisioningPolicy(), discovery, CancellationToken.None);
        Assert.Equal(ProvisioningCommandStatus.Succeeded, result.Status);
        Assert.All(await discovery.GetAsync(), state => Assert.Equal(InstallationState.Missing, state.Installation));
    }

    [Theory]
    [InlineData(ProvisioningCommandAction.Install)]
    [InlineData(ProvisioningCommandAction.Login)]
    public async Task LocalMutationsRespectTheSameNodePolicyAsServerCommands(ProvisioningCommandAction action)
    {
        var probes = 0;
        var discovery = new NodeCapabilityDiscovery((_, _, _) => { probes++; return Task.FromResult((0, "1.0.0")); });
        var request = new ProvisioningCommandRequest(new string('a', 32), "codex-cli", action, AllowElevation: true);
        var policy = new ProvisioningPolicy();
        Assert.False(WorkerProvisioning.Permitted(request, policy));
        var result = await WorkerProvisioning.ExecuteLocalAsync(request, policy, discovery, CancellationToken.None);
        Assert.Equal(ProvisioningDiagnostic.Denied, result.Diagnostic);
        Assert.Equal(0, probes);
    }
}
