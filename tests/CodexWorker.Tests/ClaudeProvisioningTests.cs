namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;
using System.Text.Json;

public sealed class ClaudeProvisioningTests
{
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(false, false, false, InstallationState.Missing, null, null)]
    [InlineData(true, false, false, InstallationState.Installed, RequirementState.Required, RequirementState.Required)]
    [InlineData(true, true, false, InstallationState.Installed, RequirementState.Satisfied, RequirementState.Required)]
    [InlineData(true, true, true, InstallationState.Installed, RequirementState.Satisfied, RequirementState.Satisfied)]
    public async Task InventorySeparatesInstallationPathAndAuthentication(bool installed, bool path, bool authenticated,
        InstallationState installation, RequirementState? configuration, RequirementState? authentication)
    {
        var discovery = Discovery(() => installed, path, authenticated);
        var state = Assert.Single(await discovery.GetAsync(), state => state.Id == "claude-cli");
        Assert.Equal(installation, state.Installation);
        Assert.Equal(configuration ?? RequirementState.Unknown, state.Configuration);
        Assert.Equal(authentication ?? RequirementState.Unknown, state.Authentication);
        Assert.Equal(installed ? "2.1.0" : null, state.DetectedVersion);
        Assert.Equal(installed && path && authenticated,
            CapabilityCatalog.Evaluate(CapabilityCatalog.Definitions.Single(item => item.Id == "claude-cli"), state).Available);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(state), StringComparison.Ordinal);
        Assert.True(CapabilityCatalog.ValidInventory([state]));
        Assert.False(CapabilityCatalog.Definitions.Single(item => item.Id == "claude-cli").RequiredForExecution);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(0, false, ProvisioningCommandStatus.Succeeded)]
    [InlineData(1, false, ProvisioningCommandStatus.Failed)]
    [InlineData(0, true, ProvisioningCommandStatus.Cancelled)]
    public async Task InstallRepairFailureAndCancellationAreBoundedAndNeverElevated(int exit, bool cancel,
        ProvisioningCommandStatus expected)
    {
        var installed = false;
        var discovery = Discovery(() => installed, true, false);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var executor = new NodeProvisioningCommandExecutor(discovery, processRunner: (tool, arguments, token) =>
        {
            calls++;
            Assert.Equal("/bin/bash", tool);
            Assert.Equal("-c", arguments[0]);
            Assert.Contains("https://claude.ai/install.sh", arguments[1], StringComparison.Ordinal);
            Assert.DoesNotContain("sudo", arguments[1], StringComparison.Ordinal);
            if (cancel)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            installed = exit == 0;
            return Task.FromResult(new ProvisioningProcessResult(exit, "private-token"));
        });
        var request = new ProvisioningCommandRequest("server", "claude-cli", ProvisioningCommandAction.Install, AllowElevation: true);
        var command = new ProvisioningCommand("test", request, DateTimeOffset.UtcNow,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1));
        var denied = await executor.ExecuteAsync(command, false);
        Assert.Equal(ProvisioningDiagnostic.Denied, denied.Diagnostic);
        Assert.Equal(0, calls);
        var result = await executor.ExecuteAsync(command, true, cancellation.Token);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        if (expected == ProvisioningCommandStatus.Succeeded)
        {
            var repaired = await executor.ExecuteAsync(command with { Request = request with { Action = ProvisioningCommandAction.Update } }, true);
            Assert.Equal(ProvisioningCommandStatus.Succeeded, repaired.Status);
            Assert.Equal(2, calls);
        }
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void LocalPolicyAndProtocolKeepClaudeIndependentAndAllowlisted()
    {
        var request = new ProvisioningCommandRequest("server", "claude-cli", ProvisioningCommandAction.Install, AllowElevation: true);
        Assert.True(ProvisioningCommandProtocol.Supported(request));
        Assert.DoesNotContain(CapabilityCatalog.ExecutionReadiness([]).BlockingReasons,
            reason => reason.StartsWith("claude-cli:", StringComparison.Ordinal));
        Assert.False(WorkerProvisioning.Permitted(request, new ProvisioningPolicy()));
        var policy = new ProvisioningPolicy { Enabled = true, AllowedPrivilegedActions = ["tool:claude-cli:install"] };
        Assert.False(WorkerProvisioning.Permitted(request, policy));
        policy.AllowNonPrivileged = true;
        Assert.False(WorkerProvisioning.Decide(request, policy).RequiresElevation);
        Assert.True(WorkerProvisioning.Permitted(request with { AllowElevation = false }, policy));
        Assert.True(WorkerProvisioning.Permitted(request, policy));
        policy.DeniedActions.Add("tool:claude-cli:install");
        Assert.False(WorkerProvisioning.Permitted(request, policy));
        Assert.False(ProvisioningCommandProtocol.Supported(request with { Action = ProvisioningCommandAction.Login }));
        Assert.False(ProvisioningCommandProtocol.Supported(request with { Action = ProvisioningCommandAction.Uninstall }));
        Assert.True(ProvisioningCommandProtocol.Supported(request with { Action = ProvisioningCommandAction.CheckAuthentication }));
    }

    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task DiscoveryUsesOnlyLocalClaudeVersionAndAuthenticationObservations()
    {
        var calls = new List<string[]>();
        var executables = new List<string>();
        var discovery = new NodeCapabilityDiscovery((executable, arguments, _) =>
        {
            executables.Add(executable);
            if (executable != ClaudeServiceEnvironment.Executable && executable != "claude")
                return Task.FromException<(int, string)>(new FileNotFoundException());
            calls.Add(arguments.ToArray());
            return Task.FromResult(arguments[0] == "auth" ? (1, "private-diagnostic") : (0, "2.1.0 (Claude Code)"));
        });

        var state = Assert.Single(await discovery.GetAsync(), item => item.Id == "claude-cli");

        Assert.Equal(InstallationState.Installed, state.Installation);
        Assert.Equal("2.1.0", state.DetectedVersion);
        Assert.Equal(RequirementState.Required, state.Authentication);
        Assert.Equal(RequirementState.Satisfied, state.Configuration);
        Assert.Equal(new[] { ClaudeServiceEnvironment.Executable, "claude", ClaudeServiceEnvironment.Executable },
            executables.Where(executable => executable == ClaudeServiceEnvironment.Executable || executable == "claude"));
        Assert.Equal(UpdateState.Unknown, state.Update);
        Assert.DoesNotContain("/usr/bin/npm", executables);
        Assert.Equal(3, calls.Count);
        Assert.Equal(new[] { "--version" }, calls[0]);
        Assert.Equal(new[] { "--version" }, calls[1]);
        Assert.Equal(new[] { "auth", "status" }, calls[2]);
    }

    private static NodeCapabilityDiscovery Discovery(Func<bool> installed, bool path, bool authenticated) => new((tool, args, _) =>
    {
        if (tool != ClaudeServiceEnvironment.Executable && tool != "claude")
            return Task.FromException<(int, string)>(new FileNotFoundException());
        if (!installed() || tool == "claude" && !path)
            return Task.FromException<(int, string)>(new FileNotFoundException());
        return Task.FromResult(args[0] == "auth" ? (authenticated ? 0 : 1, "private-token") : (0, "2.1.0 (Claude Code)"));
    });
}
