using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProvisioningPolicyDenialTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("disabled", ProvisioningFailureCode.PolicyProvisioningDisabled)]
    [InlineData("denied", ProvisioningFailureCode.PolicyActionDenied)]
    [InlineData("not-allowlisted", ProvisioningFailureCode.PolicyActionNotAllowlisted)]
    [InlineData("no-elevation", ProvisioningFailureCode.ElevationNotRequested)]
    public void PrivilegedDenialsNameTheBlockingSetting(string scenario, ProvisioningFailureCode expected)
    {
        var request = new ProvisioningCommandRequest("server", "dotnet-sdk", ProvisioningCommandAction.Update,
            AllowElevation: scenario != "no-elevation");
        var policy = new ProvisioningPolicy { Enabled = scenario != "disabled", AllowNonPrivileged = true, AllowCredentials = true,
            AllowedPrivilegedActions = scenario == "not-allowlisted" ? [] : ["tool:dotnet-sdk:update"] };
        if (scenario == "denied") policy.DeniedActions.Add("tool:dotnet-sdk:update");

        var decision = WorkerProvisioning.Decide(request, policy);

        Assert.False(decision.Permitted);
        Assert.Equal(expected, decision.FailureCode);
        if (scenario == "not-allowlisted")
            Assert.Contains("codex-worker config add worker.provisioning.allowedPrivilegedActions tool:dotnet-sdk:update", decision.Remediation);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void CredentialAndNonPrivilegedDenialsNameTheirSettings()
    {
        var login = new ProvisioningCommandRequest("server", "github-cli", ProvisioningCommandAction.Login);
        Assert.Equal(ProvisioningFailureCode.PolicyCredentialsDisabled, WorkerProvisioning.Decide(login,
            new ProvisioningPolicy { Enabled = true, AllowNonPrivileged = true }).FailureCode);
        Assert.Equal(ProvisioningFailureCode.PolicyNonPrivilegedDisabled, WorkerProvisioning.Decide(login,
            new ProvisioningPolicy { Enabled = true, AllowCredentials = true }).FailureCode);
        Assert.Null(WorkerProvisioning.Decide(login, new ProvisioningPolicy { Enabled = true, AllowCredentials = true, AllowNonPrivileged = true }).FailureCode);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task DeniedReportCarriesTheTypedReasonAndOnlyDeniedReportsMayCarryIt()
    {
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), new("server", "dotnet-sdk", ProvisioningCommandAction.Update,
            AllowElevation: true), now, ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddSeconds(120));
        var executor = new NodeProvisioningCommandExecutor(new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "10.0.115"))));

        var report = await executor.ExecuteAsync(command, permitted: false, deniedBecause: ProvisioningFailureCode.PolicyActionNotAllowlisted);

        Assert.Equal(ProvisioningDiagnostic.Denied, report.Diagnostic);
        Assert.Equal(ProvisioningFailureCode.PolicyActionNotAllowlisted, report.FailureDetail?.Code);
        Assert.Contains("worker.provisioning.allowedPrivilegedActions", report.FailureDetail?.Description);
        Assert.True(ProvisioningCommandProtocol.ValidReport(report));
        Assert.False(ProvisioningCommandProtocol.ValidReport(report with { Diagnostic = ProvisioningDiagnostic.ProcessFailed }));
        Assert.False(ProvisioningCommandProtocol.ValidReport(report with
            { FailureDetail = new ProvisioningFailureDetail(ProvisioningFailureCode.ProcessExited, 1) }));
        Assert.True(ProvisioningCommandProtocol.ValidReport(report with { FailureDetail = null }));
    }
}
