using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerProvisioningTests
{
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
