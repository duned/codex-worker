namespace CodexWorker.Tests;

using CodexProvisioning;

public sealed class ToolProvisioningLifecycleTests
{
    [Theory]
    [InlineData("git", "1.0.0", "1.0.0", UpdateState.Current)]
    [InlineData("github-cli", "1.0.0", "2.0.0", UpdateState.Available)]
    [InlineData("github-cli", "2.0.0", "1.0.0", UpdateState.Current)]
    [InlineData("codex-cli", "1.0.0", "2.0.0", UpdateState.Available)]
    [InlineData("codex-cli", "2.0.0", "2.0.0", UpdateState.Current)]
    [InlineData("codex-cli", "2.0.0", "unavailable", UpdateState.Unknown)]
    public async Task UpdateStateUsesProviderCandidateWithoutExportingOutput(string id, string installed, string candidate, UpdateState expected)
    {
        var discovery = new NodeCapabilityDiscovery((tool, args, _) =>
        {
            if (tool == "/usr/bin/apt-cache") return Task.FromResult((0, $"Installed: {installed}\nCandidate: {candidate}"));
            if (tool == "/usr/bin/dpkg") return Task.FromResult((Version.TryParse(candidate, out var available) &&
                available > Version.Parse(installed) ? 0 : 1, ""));
            if (tool == "/usr/bin/npm") return Task.FromResult((0, candidate));
            return Task.FromResult((0, args[0] == "--version" ? installed : "configured"));
        });
        var state = Assert.Single(await discovery.GetAsync(), state => state.Id == id);
        Assert.Equal(expected, state.Update);
        Assert.Equal(installed, state.DetectedVersion);
        Assert.Equal(InstallationState.Installed, state.Installation);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("github-cli")]
    [InlineData("codex-cli")]
    public async Task InstallerSuccessCannotHideServicePathMismatch(string id)
    {
        var discovery = new NodeCapabilityDiscovery((tool, args, _) => Task.FromResult((0,
            args[0] == "--version" ? tool.StartsWith("/", StringComparison.Ordinal) ? "2.0.0" : "1.0.0" : "")));
        var executor = new NodeProvisioningCommandExecutor(discovery, (_, _, _) => Task.FromResult(0), () => true, () => true);
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), new("server", id, ProvisioningCommandAction.Install, AllowElevation: true),
            now, ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(2));
        Assert.Equal(ProvisioningDiagnostic.ProcessFailed, (await executor.ExecuteAsync(command, true)).Diagnostic);
    }

    [Fact]
    public async Task CodexUninstallOnCleanNodeDoesNotInstallRuntimeOrRunCommands()
    {
        var discovery = new NodeCapabilityDiscovery((_, _, _) => Task.FromException<(int, string)>(new FileNotFoundException()));
        var calls = 0;
        var executor = new NodeProvisioningCommandExecutor(discovery, (_, _, _) => { calls++; return Task.FromResult(0); },
            () => true, () => true, () => false);
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), new("server", "codex-cli", ProvisioningCommandAction.Uninstall, AllowElevation: true),
            now, ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(2));
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await executor.ExecuteAsync(command, true)).Status);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("git", "git")]
    [InlineData("github-cli", "gh")]
    [InlineData("codex-cli", "codex")]
    public async Task LifecycleConvergesAfterRepeatsFailuresInterruptionAndReinstallation(string id, string executable)
    {
        string? installed = null;
        var available = "1.0.0";
        var fail = false;
        var interrupt = false;
        var probes = 0;
        using var cancellation = new CancellationTokenSource();
        var discovery = new NodeCapabilityDiscovery((tool, args, _) =>
        {
            if ((tool == executable || tool == "/usr/bin/" + executable || tool == "/usr/local/bin/" + executable) && args[0] == "--version")
            {
                probes++;
                return installed is null ? Task.FromException<(int, string)>(new FileNotFoundException()) : Task.FromResult((0, installed));
            }
            if (tool == "/usr/bin/apt-cache") return Task.FromResult((0, $"Installed: {installed ?? "(none)"}\nCandidate: {available}"));
            if (tool == "/usr/bin/dpkg") return Task.FromResult((1, ""));
            if (tool == "/usr/bin/npm") return Task.FromResult((0, available));
            return Task.FromResult((0, "1.0.0"));
        });
        var calls = new List<(string Tool, IReadOnlyList<string> Args)>();
        var executor = new NodeProvisioningCommandExecutor(discovery, (tool, args, token) =>
        {
            calls.Add((tool, args));
            var target = id == "codex-cli" ? tool == "/usr/bin/npm" : args.Contains(executable);
            if (target)
            {
                installed = args[0] is "remove" or "uninstall" ? null : available;
                if (interrupt)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                if (fail) return Task.FromResult(1);
            }
            return Task.FromResult(0);
        }, () => true, () => true, () => true);

        async Task<ProvisioningCommandReport> Execute(ProvisioningCommandAction action, CancellationToken token = default)
        {
            var now = DateTimeOffset.UtcNow;
            var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), new("server", id, action, AllowElevation: true),
                now, ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(2));
            return await executor.ExecuteAsync(command, true, token);
        }
        async Task Verify(InstallationState expected, string? version)
        {
            var state = Assert.Single(await discovery.GetAsync(), state => state.Id == id);
            Assert.Equal(expected, state.Installation);
            Assert.Equal(version, state.DetectedVersion);
        }

        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await Execute(ProvisioningCommandAction.Detect)).Status);
        await Verify(InstallationState.Missing, null);
        foreach (var action in new[] { ProvisioningCommandAction.Install, ProvisioningCommandAction.Install,
            ProvisioningCommandAction.Update, ProvisioningCommandAction.Update })
        {
            if (action == ProvisioningCommandAction.Update) available = "2.0.0";
            Assert.Equal(ProvisioningCommandStatus.Succeeded, (await Execute(action)).Status);
            await Verify(InstallationState.Installed, available);
        }
        foreach (var action in new[] { ProvisioningCommandAction.Uninstall, ProvisioningCommandAction.Uninstall })
        {
            Assert.Equal(ProvisioningCommandStatus.Succeeded, (await Execute(action)).Status);
            await Verify(InstallationState.Missing, null);
        }
        fail = true;
        var before = probes;
        Assert.Equal(ProvisioningDiagnostic.ProcessFailed, (await Execute(ProvisioningCommandAction.Install)).Diagnostic);
        Assert.True(probes > before);
        await Verify(InstallationState.Installed, available);
        fail = false;
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await Execute(ProvisioningCommandAction.Install)).Status);
        interrupt = true;
        Assert.Equal(ProvisioningDiagnostic.Cancelled, (await Execute(ProvisioningCommandAction.Uninstall, cancellation.Token)).Diagnostic);
        await Verify(InstallationState.Missing, null);
        interrupt = false;
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await Execute(ProvisioningCommandAction.Install)).Status);
        await Verify(InstallationState.Installed, available);
        Assert.DoesNotContain(calls, call => call.Args.Any(arg => arg is "purge" or "autoremove" or "logout" or "--force"));
        if (id == "codex-cli")
        {
            Assert.Contains(calls, call => call.Tool == "/usr/bin/npm" && call.Args.Contains("@openai/codex@latest") && call.Args.Contains("/usr/local"));
            Assert.DoesNotContain(calls, call => call.Tool == "/usr/bin/apt-get");
        }
    }

    [Fact]
    public async Task CodexInstallUsesAvailableNpmAndLeavesAuthenticationRequired()
    {
        var installed = false;
        var discovery = new NodeCapabilityDiscovery((tool, args, _) =>
        {
            if ((tool == CodexServiceEnvironment.Executable || tool == "/usr/local/bin/codex") && args[0] == "--version")
                return installed ? Task.FromResult((0, "codex 0.160.0")) : Task.FromException<(int, string)>(new FileNotFoundException());
            if (tool == "/usr/bin/npm" && args[0] == "view") return Task.FromResult((0, "0.160.0"));
            return Task.FromResult((args[0] == "login" ? 1 : 0, ""));
        });
        var calls = new List<(string Tool, IReadOnlyList<string> Args)>();
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true,
            isRoot: () => true, npmAvailable: () => true,
            processRunner: (tool, args, _) =>
            {
                calls.Add((tool, args));
                if (tool == "/usr/bin/npm" && args[0] == "install") installed = true;
                if (tool == "/usr/bin/apt-get")
                    return Task.FromResult(new ProvisioningProcessResult(1, "apt index service unavailable"));
                return Task.FromResult(new ProvisioningProcessResult(0));
            });
        var report = await Execute(executor, ProvisioningCommandAction.Install);

        Assert.Equal(ProvisioningCommandStatus.Succeeded, report.Status);
        Assert.DoesNotContain(calls, call => call.Tool == "/usr/bin/apt-get");
        Assert.Contains(calls, call => call.Tool == "/usr/bin/npm" && call.Args.Contains("@openai/codex@latest"));
        var state = Assert.Single(await discovery.GetAsync(), item => item.Id == "codex-cli");
        Assert.Equal(InstallationState.Installed, state.Installation);
        Assert.Equal(RequirementState.Required, state.Authentication);
        Assert.Equal(["authentication-required"], CapabilityCatalog.Evaluate(
            CapabilityCatalog.Definitions.Single(item => item.Id == "codex-cli"), state).BlockingReasons);
    }

    [Fact]
    public async Task CodexInstallerFailureNamesProviderStepWithoutReturningOutput()
    {
        var discovery = new NodeCapabilityDiscovery((_, args, _) => args[0] == "--version"
            ? Task.FromException<(int, string)>(new FileNotFoundException())
            : Task.FromResult((1, "")));
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true,
            isRoot: () => true, npmAvailable: () => true,
            processRunner: (_, args, _) => Task.FromResult(args[0] == "install"
                ? new ProvisioningProcessResult(1, "npm ERR! authToken=private-token registry refused package")
                : new ProvisioningProcessResult(0)));

        var report = await Execute(executor, ProvisioningCommandAction.Install);

        Assert.Equal(ProvisioningFailureCode.ProcessExited, report.FailureDetail?.Code);
        Assert.Equal(ProvisioningProviderStep.NpmPackageInstall, report.FailureDetail?.ProviderStep);
        Assert.Contains("install @openai/codex with npm", report.FailureDetail?.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", report.FailureDetail?.Description, StringComparison.Ordinal);
        var state = Assert.Single(await discovery.GetAsync(), item => item.Id == "codex-cli");
        Assert.Equal(InstallationState.Missing, state.Installation);
    }

    private static async Task<ProvisioningCommandReport> Execute(NodeProvisioningCommandExecutor executor,
        ProvisioningCommandAction action)
    {
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"),
            new("server", "codex-cli", action, AllowElevation: true), now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(2));
        return await executor.ExecuteAsync(command, permitted: true);
    }
}
