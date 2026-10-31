namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;

public sealed class ClaudeProvisioningTests
{
    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task InstallationIsRepeatableAndRefreshesServiceUserMetadata()
    {
        var installed = false;
        var discovery = new NodeCapabilityDiscovery((executable, arguments, _) =>
        {
            if (executable == ClaudeServiceEnvironment.Executable && installed)
                return Task.FromResult(arguments[0] == "auth" ? (1, "private-auth-diagnostic") : (0, "2.1.0 (Claude Code)"));
            if (executable == "/usr/bin/npm") return Task.FromResult((0, "2.1.0"));
            return Task.FromException<(int, string)>(new FileNotFoundException());
        });
        var calls = 0;
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true,
            npmAvailable: () => true, run: (executable, arguments, _) =>
            {
                Assert.Equal("/usr/bin/npm", executable);
                Assert.Contains(ClaudeServiceEnvironment.Prefix, arguments);
                Assert.Contains("@anthropic-ai/claude-code@latest", arguments);
                installed = true;
                calls++;
                return Task.FromResult(0);
            });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await executor.ExecuteAsync(Command(ProvisioningCommandAction.Install), true);
            Assert.Equal(ProvisioningCommandStatus.Succeeded, result.Status);
        }
        Assert.Equal(2, calls);
        var state = Assert.Single(await discovery.GetAsync(), item => item.Id == "claude-cli");
        Assert.Equal("2.1.0", state.DetectedVersion);
        Assert.Equal(RequirementState.Required, state.Authentication);
        Assert.False(CapabilityCatalog.Evaluate(CapabilityCatalog.Definitions.Single(item => item.Id == state.Id), state).Available);
        Assert.DoesNotContain("private-auth-diagnostic", System.Text.Json.JsonSerializer.Serialize(state), StringComparison.Ordinal);
        Assert.False(CapabilityCatalog.Definitions.Single(item => item.Id == state.Id).RequiredForExecution);
    }

    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task DeniedStaleAndCancelledRequestsDoNotInstall()
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromException<(int, string)>(new FileNotFoundException()));
        var executor = new NodeProvisioningCommandExecutor(discovery, run: (_, _, _) => throw new InvalidOperationException("Must not run"));
        var command = Command(ProvisioningCommandAction.Update);
        Assert.False(WorkerProvisioning.Permitted(command.Request, new ProvisioningPolicy()));
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(command, false)).Diagnostic);
        Assert.Equal(ProvisioningCommandStatus.TimedOut,
            (await executor.ExecuteAsync(command with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, true)).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(ProvisioningCommandStatus.Cancelled, (await executor.ExecuteAsync(command, true, cancellation.Token)).Status);
        Assert.False(ProvisioningCommandProtocol.Supported(command.Request with { Action = ProvisioningCommandAction.Login }));
    }

    [Theory]
    [InlineData(false, ProvisioningDiagnostic.Unsupported)]
    [InlineData(true, ProvisioningDiagnostic.ProcessFailed)]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task MissingRuntimeAndInstallerFailureAreExplicit(bool npmAvailable, ProvisioningDiagnostic expected)
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromException<(int, string)>(new FileNotFoundException()));
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true,
            npmAvailable: () => npmAvailable, processRunner: (_, _, _) =>
                Task.FromResult(new ProvisioningProcessResult(1, "private-provider-diagnostic")));
        var result = await executor.ExecuteAsync(Command(ProvisioningCommandAction.Install), true);
        Assert.Equal(expected, result.Diagnostic);
        Assert.DoesNotContain("private-provider-diagnostic", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
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
            if (executable != ClaudeServiceEnvironment.Executable)
                return Task.FromException<(int, string)>(new FileNotFoundException());
            calls.Add(arguments.ToArray());
            return Task.FromResult(arguments[0] == "auth" ? (1, "private-diagnostic") : (0, "2.1.0 (Claude Code)"));
        });

        var state = Assert.Single(await discovery.GetAsync(), item => item.Id == "claude-cli");

        Assert.Equal(InstallationState.Installed, state.Installation);
        Assert.Equal("2.1.0", state.DetectedVersion);
        Assert.Equal(RequirementState.Required, state.Authentication);
        Assert.Equal(UpdateState.Unknown, state.Update);
        Assert.DoesNotContain("/usr/bin/npm", executables);
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "--version" }, calls[0]);
        Assert.Equal(new[] { "auth", "status" }, calls[1]);
    }

    private static ProvisioningCommand Command(ProvisioningCommandAction action)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid().ToString("N"), new("server", "claude-cli", action), now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddSeconds(30));
    }
}
