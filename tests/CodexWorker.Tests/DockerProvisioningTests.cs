namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;

public sealed class DockerProvisioningTests
{
    [Theory]
    [InlineData(ProvisioningCommandAction.Install)]
    [InlineData(ProvisioningCommandAction.Update)]
    [InlineData(ProvisioningCommandAction.Configure)]
    public async Task DockerLifecycleReconcilesAccessThroughFixedElevatedHelper(ProvisioningCommandAction action)
    {
        var configured = false;
        var calls = new List<string>();
        var discovery = Discovery(() => configured);
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true, isRoot: () => false,
            processRunner: (tool, args, _) =>
            {
                Assert.Equal("/usr/bin/sudo", tool);
                calls.Add(string.Join(' ', args));
                if (args[1] == "/usr/local/libexec/codex-provisioning-docker")
                {
                    Assert.Equal(["-n", "/usr/local/libexec/codex-provisioning-docker", "configure"], args);
                    configured = true;
                }
                return Task.FromResult(new ProvisioningProcessResult(0));
            });
        Assert.Equal(RequirementState.Required, Assert.Single(await discovery.GetAsync(), s => s.Id == "docker").Configuration);
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(ProvisioningCommandStatus.Succeeded, (await executor.ExecuteAsync(Command(action), true)).Status);
        Assert.Equal(RequirementState.Satisfied, Assert.Single(await discovery.GetAsync(), s => s.Id == "docker").Configuration);
        Assert.Collection(calls.Where(c => c.Contains("codex-provisioning-docker", StringComparison.Ordinal)),
            call => Assert.EndsWith(" configure", call, StringComparison.Ordinal),
            call => Assert.EndsWith(" configure", call, StringComparison.Ordinal));
        if (action == ProvisioningCommandAction.Configure)
            Assert.DoesNotContain(calls, c => c.Contains("apt-get", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10, "", ProvisioningFailureCode.DockerServiceRestartRequired)]
    [InlineData(1, "Cannot connect to the Docker daemon", ProvisioningFailureCode.DockerDaemonUnavailable)]
    [InlineData(1, "permission denied", ProvisioningFailureCode.DockerDaemonAccessRequired)]
    [InlineData(1, "a password is required", ProvisioningFailureCode.ElevationDenied)]
    public async Task ConfigurationDoesNotReportSuccessUntilDaemonAndServiceContextAreReady(int exitCode, string error,
        ProvisioningFailureCode expected)
    {
        var executor = new NodeProvisioningCommandExecutor(Discovery(() => false), supportsApt: () => true,
            isRoot: () => false, processRunner: (_, _, _) => Task.FromResult(new ProvisioningProcessResult(exitCode, error)));
        var report = await executor.ExecuteAsync(Command(ProvisioningCommandAction.Configure), true);
        Assert.Equal(ProvisioningCommandStatus.Failed, report.Status);
        Assert.Equal(expected, report.FailureDetail?.Code);
        Assert.True(ProvisioningCommandProtocol.ValidReport(report));
    }

    [Fact]
    public async Task LocalAndServerDispatchedConfigurationRequirePolicyAndExplicitElevation()
    {
        var request = Command(ProvisioningCommandAction.Configure).Request;
        var policy = new ProvisioningPolicy { Enabled = true, AllowNonPrivileged = true };
        Assert.False(WorkerProvisioning.Permitted(request, policy));
        policy.AllowedPrivilegedActions.Add("tool:docker:configure");
        Assert.True(WorkerProvisioning.Permitted(request, policy));
        Assert.False(WorkerProvisioning.Permitted(request with { AllowElevation = false }, policy));
        var executor = new NodeProvisioningCommandExecutor(Discovery(() => false), supportsApt: () => true,
            processRunner: (_, _, _) => throw new InvalidOperationException("Denied commands must not run"));
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(Command(request.Action), false)).Diagnostic);
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(Command(request.Action) with
            { Request = request with { AllowElevation = false } }, true)).Diagnostic);
        Assert.Equal(ProvisioningDiagnostic.Denied, (await WorkerProvisioning.ExecuteLocalAsync(
            request with { AllowElevation = false }, policy, Discovery(() => false), default, executor: executor)).Diagnostic);
        Assert.False(ProvisioningCommandProtocol.Supported(request with { CapabilityId = "git" }));
        Assert.False(ProvisioningCommandProtocol.Valid(request with { Repository = "other-user/other-group" }));
        var parsed = ProvisioningCli.Parse(["configure", "docker", "--allow-elevation"]);
        Assert.Equal(ProvisioningCommandAction.Configure, parsed.Action);
        Assert.True(parsed.AllowElevation);
    }

    [Fact]
    public async Task FreshAdministrativeProbeCannotHidePendingWorkerServiceRefresh()
    {
        var discovery = Discovery(() => true);
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true, isRoot: () => true,
            processRunner: (_, _, _) => Task.FromResult(new ProvisioningProcessResult(10)));
        var policy = new ProvisioningPolicy { Enabled = true, AllowedPrivilegedActions = ["tool:docker:configure"] };
        var service = new WorkerProvisioningAdministrationService(policy, discovery, new string('a', 32), executor);
        var result = await service.ExecuteAsync(new("docker", ProvisioningCommandAction.Configure, AllowElevation: true));
        Assert.Equal("failed", result.Status);
        Assert.Equal(RequirementState.Required, result.Capability?.Configuration);
        Assert.Equal(ProvisioningFailureCode.DockerServiceRestartRequired, result.Report?.FailureDetail?.Code);
        Assert.Contains("systemctl restart codex-worker", result.Remediation ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterruptedConfigurationRefreshesObservationsWithoutClaimingReadiness()
    {
        using var cancellation = new CancellationTokenSource();
        var discovery = Discovery(() => false);
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true, isRoot: () => true,
            processRunner: (_, _, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new ProvisioningProcessResult(0));
            });
        var report = await executor.ExecuteAsync(Command(ProvisioningCommandAction.Configure), true, cancellation.Token);
        Assert.Equal(ProvisioningCommandStatus.Cancelled, report.Status);
        Assert.Equal(RequirementState.Required, Assert.Single(await discovery.GetAsync(), s => s.Id == "docker").Configuration);
    }

    [Fact]
    public async Task UninstallDoesNotMutateAccountGroups()
    {
        var installed = true;
        var discovery = Discovery(() => true, () => installed);
        var executor = new NodeProvisioningCommandExecutor(discovery, supportsApt: () => true, isRoot: () => true,
            processRunner: (tool, args, _) =>
            {
                Assert.Equal("/usr/bin/apt-get", tool);
                Assert.Equal(["remove", "-y", "docker.io"], args);
                installed = false;
                return Task.FromResult(new ProvisioningProcessResult(0));
            });
        Assert.Equal(ProvisioningCommandStatus.Succeeded,
            (await executor.ExecuteAsync(Command(ProvisioningCommandAction.Uninstall), true)).Status);
    }

    private static NodeCapabilityDiscovery Discovery(Func<bool> configured, Func<bool>? installed = null) => new((tool, args, _) =>
    {
        if (tool == "/usr/bin/apt-cache") return Task.FromResult((0, "Installed: 29.1.3\nCandidate: 29.1.3"));
        if (tool is not ("docker" or "/usr/bin/docker" or "/usr/sbin/runuser") || installed?.Invoke() == false)
            return Task.FromException<(int, string)>(new FileNotFoundException());
        return Task.FromResult(args.Contains("info") ? configured() ? (0, "29.1.3") : (1, "permission denied")
            : (0, "Docker version 29.1.3"));
    });

    private static ProvisioningCommand Command(ProvisioningCommandAction action)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid().ToString("N"), new(new string('a', 32), "docker", action, AllowElevation: true), now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(2));
    }
}
