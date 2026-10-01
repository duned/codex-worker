namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;

public sealed class ProvisioningCliTests
{
    [Theory]
    [InlineData("install", ProvisioningCommandAction.Install)]
    [InlineData("upgrade", ProvisioningCommandAction.Update)]
    [InlineData("uninstall", ProvisioningCommandAction.Uninstall)]
    public void MutationVerbsMapToTypedCommandsAndRequireExplicitElevation(string verb, ProvisioningCommandAction action)
    {
        var withoutElevation = ProvisioningCli.Parse([verb, "git"]);
        Assert.Equal(action, withoutElevation.Action);
        Assert.False(withoutElevation.AllowElevation);

        var authorized = ProvisioningCli.Parse([verb, "git", "--allow-elevation", "--json"]);
        Assert.Equal(action, authorized.Action);
        Assert.True(authorized.AllowElevation);
        Assert.True(authorized.Json);
    }

    [Fact]
    public void StatusAndConfigurationOverrideUseTheNamedCommandGroup()
    {
        Assert.True(ProvisioningCli.Parse(["status", "--json"]).IsStatus);
        var commandLine = WorkerCommandLine.Parse(["provision", "status", "--config", "worker.yml", "--json"]);
        Assert.Equal("worker.yml", commandLine.ConfigurationPath);
        Assert.True(ProvisioningCli.Parse(commandLine.Arguments).Json);
    }

    [Fact]
    public async Task UnknownAndUnsupportedCapabilitiesHaveClearBoundedFailures()
    {
        var unknown = Assert.Throws<ArgumentException>(() => ProvisioningCli.Parse(["install", "made-up-tool"]));
        Assert.Contains("Unknown capability 'made-up-tool'", unknown.Message);

        var command = ProvisioningCli.Parse(["prepare-authentication", "codex-cli"]);
        var result = await ExecuteCliAsync(command, new ProvisioningPolicy(), Discovery());
        Assert.Equal("failed", result.Status);
        Assert.Equal(ProvisioningDiagnostic.Unsupported, result.Diagnostic);
        Assert.Contains("not supported", result.Reason ?? string.Empty);
    }

    [Fact]
    public void PolicyDenialsExplainTheControllingConfigurationKeyOrElevationFlag()
    {
        var request = new ProvisioningCommandRequest("server", "git", ProvisioningCommandAction.Install, AllowElevation: true);
        var disabled = WorkerProvisioning.Decide(request, new ProvisioningPolicy());
        Assert.False(disabled.Permitted);
        Assert.Contains("disabled", disabled.Reason ?? string.Empty);
        Assert.Contains("worker.provisioning.enabled", disabled.Remediation ?? string.Empty);

        var policy = new ProvisioningPolicy { Enabled = true };
        var notAllowlisted = WorkerProvisioning.Decide(request, policy);
        Assert.Contains("not allowlisted", notAllowlisted.Reason ?? string.Empty);
        Assert.Contains("worker.provisioning.allowedPrivilegedActions", notAllowlisted.Remediation ?? string.Empty);

        policy.AllowedPrivilegedActions.Add("tool:git:install");
        var requiresAuthorization = WorkerProvisioning.Decide(request with { AllowElevation = false }, policy);
        Assert.Contains("explicit elevation authorization", requiresAuthorization.Reason ?? string.Empty);
        Assert.Contains("--allow-elevation", requiresAuthorization.Remediation ?? string.Empty);
        Assert.True(WorkerProvisioning.Decide(request, policy).Permitted);
    }

    [Fact]
    public async Task DeniedCliMutationReportsWhyAndDoesNotProbeOrInvokeProvisioning()
    {
        var probes = 0;
        var discovery = new NodeCapabilityDiscovery((_, _, _) =>
        {
            probes++;
            return Task.FromResult((0, "2.0"));
        });
        var command = ProvisioningCli.Parse(["install", "git", "--allow-elevation"]);
        var result = await ExecuteCliAsync(command, new ProvisioningPolicy(), discovery);

        Assert.Equal("failed", result.Status);
        Assert.Equal(ProvisioningDiagnostic.Denied, result.Diagnostic);
        Assert.Contains("Local provisioning is disabled", result.Reason ?? string.Empty);
        Assert.Contains("worker.provisioning.enabled", result.Remediation ?? string.Empty);
        Assert.Equal(0, probes);
    }

    [Fact]
    public void CredentialAndNonPrivilegedDenialsIdentifyTheirPolicyControls()
    {
        var credential = WorkerProvisioning.Decide(new("server", "github-cli", ProvisioningCommandAction.PrepareAuthentication),
            new ProvisioningPolicy { Enabled = true, AllowNonPrivileged = true });
        Assert.Contains("Credential operations", credential.Reason ?? string.Empty);
        Assert.Contains("worker.provisioning.allowCredentials", credential.Remediation ?? string.Empty);

        var nonPrivileged = WorkerProvisioning.Decide(new("server", "git", ProvisioningCommandAction.GenerateSshKey),
            new ProvisioningPolicy { Enabled = true, AllowCredentials = true });
        Assert.Contains("Non-privileged", nonPrivileged.Reason ?? string.Empty);
        Assert.Contains("worker.provisioning.allowNonPrivileged", nonPrivileged.Remediation ?? string.Empty);
    }

    [Fact]
    public async Task StatusRefreshesCapabilitiesAndShowsPolicyForEachSupportedAction()
    {
        var command = ProvisioningCli.Parse(["status"]);
        var result = await ExecuteCliAsync(command, new ProvisioningPolicy(), Discovery());

        Assert.Equal("succeeded", result.Status);
        var capabilities = result.Capabilities ?? throw new InvalidOperationException("Status result did not include capabilities.");
        Assert.Equal(CapabilityCatalog.Definitions.Count, capabilities.Count);
        var git = Assert.Single(capabilities, item => item.Id == "git");
        Assert.Contains(git.Actions, action => action.Action == "install" && !action.Allowed && action.RequiresElevation);
        Assert.Contains(git.Actions, action => action.Action == "upgrade" && action.PolicyKey == "tool:git:update");
        Assert.Contains(git.Actions, action => action.Action == "uninstall");
    }

    [Fact]
    public async Task StatusShowsPolicyAllowedMutationsAndTheirExplicitElevationRequirement()
    {
        var policy = new ProvisioningPolicy
        {
            Enabled = true,
            AllowNonPrivileged = true,
            AllowCredentials = true,
            AllowedPrivilegedActions = ["tool:git:install", "tool:git:update", "tool:git:uninstall"]
        };
        var result = await ExecuteCliAsync(ProvisioningCli.Parse(["status"]), policy, Discovery());
        var capabilities = result.Capabilities ?? throw new InvalidOperationException("Status result did not include capabilities.");
        var git = Assert.Single(capabilities, item => item.Id == "git");
        var packageOperationsSupported = NodeProvisioningCommandExecutor.SupportsPackageProvisioning();

        Assert.Contains(git.Actions, action => action.Action == "install" && action.Allowed == packageOperationsSupported && action.RequiresElevation);
        Assert.Contains(git.Actions, action => action.Action == "upgrade" && action.Allowed == packageOperationsSupported && action.RequiresElevation);
        Assert.Contains(git.Actions, action => action.Action == "uninstall" && action.Allowed == packageOperationsSupported && action.RequiresElevation);
    }

    private static NodeCapabilityDiscovery Discovery() => new((_, _, _) => Task.FromResult((0, "2.0")));

    private static Task<WorkerProvisioningResult> ExecuteCliAsync(ProvisioningCliCommand command, ProvisioningPolicy policy,
        NodeCapabilityDiscovery discovery) => ProvisioningCli.ExecuteAsync(command,
        new WorkerProvisioningAdministrationService(policy, discovery, "server"), CancellationToken.None);
}
