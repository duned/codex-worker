namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;

[Collection("ServerTokenEnvironment")]
public sealed class ProvisioningCliTests
{
    [Theory]
    [InlineData(ProvisioningFailureCode.ElevationDenied)]
    [InlineData(ProvisioningFailureCode.PackageUnavailable)]
    [InlineData(ProvisioningFailureCode.ProcessExited)]
    public void HumanAndJsonOutputRetainSharedSafeFailureDetails(ProvisioningFailureCode code)
    {
        var failure = new ProvisioningFailureDetail(code, code == ProvisioningFailureCode.ProcessExited ? 7 : null);
        var result = new WorkerProvisioningResult("install", "failed", ProvisioningDiagnostic.ProcessFailed,
            "git", ProvisioningCommandAction.Install, Report: new(ProvisioningCommandStatus.Failed,
                ProvisioningDiagnostic.ProcessFailed, FailureDetail: failure));
        using var output = new StringWriter();
        ProvisioningCli.Write(result, json: false, output);
        Assert.Contains(failure.Description, output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();
        ProvisioningCli.Write(result, json: true, output);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Equal(code.ToString(), json.RootElement.GetProperty("report").GetProperty("failureDetail").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CancelledProvisioningRetainsTerminalReportWithoutCollectingPresentationState()
    {
        using var cancellation = new CancellationTokenSource();
        var uncancellableProbes = 0;
        var discovery = new NodeCapabilityDiscovery((_, _, token) =>
        {
            if (!token.CanBeCanceled) uncancellableProbes++;
            return Task.FromResult((0, "1.0.0"));
        });
        var executor = new NodeProvisioningCommandExecutor(discovery, login: (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        });
        var service = new WorkerProvisioningAdministrationService(new ProvisioningPolicy
        {
            Enabled = true, AllowCredentials = true, AllowNonPrivileged = true
        }, discovery, "server", executor);
        var result = await service.ExecuteAsync(new("codex-cli", ProvisioningCommandAction.Login),
            cancellation.Token, (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.Cancelled, result.Report?.Status);
        Assert.Equal(ProvisioningDiagnostic.Cancelled, result.Diagnostic);
        Assert.Null(result.Capability);
        Assert.Equal(0, uncancellableProbes);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("600")]
    public async Task ExplicitDeadlineReachesTheTypedAdministrationOperation(string seconds)
    {
        var command = ProvisioningCli.Parse(["detect", "git", "--timeout-seconds", seconds, "--json"]);
        var service = new DeadlineService();
        await ProvisioningCli.ExecuteAsync(command, service, CancellationToken.None);
        Assert.Equal(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture), service.Operation?.TimeoutSeconds);
        Assert.Equal(120, ProvisioningCli.Parse(["detect", "git"]).TimeoutSeconds);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("601")]
    [InlineData("invalid")]
    [InlineData("--json")]
    public void InvalidDeadlinesAreRejectedWithUsage(string value)
    {
        var failure = Assert.Throws<ArgumentException>(() => ProvisioningCli.Parse(["detect", "git", "--timeout-seconds", value]));
        Assert.Contains("5 to 600", failure.Message);
        Assert.Contains("codex-worker provision --help", failure.Message);
    }

    [Fact]
    public void MissingAndRepeatedDeadlinesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ProvisioningCli.Parse(["detect", "git", "--timeout-seconds"]));
        Assert.Throws<ArgumentException>(() => ProvisioningCli.Parse(["detect", "git", "--timeout-seconds", "5", "--timeout-seconds", "5"]));
        Assert.Throws<ArgumentException>(() => ProvisioningCli.Parse(["status", "--timeout-seconds", "5"]));
    }

    private sealed class DeadlineService : IWorkerProvisioningAdministrationService
    {
        public WorkerProvisioningOperation? Operation { get; private set; }
        public Task<WorkerProvisioningResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Status was not requested.");
        public Task<WorkerProvisioningResult> ExecuteAsync(WorkerProvisioningOperation operation,
            CancellationToken cancellationToken = default, Func<ProvisioningCommandReport, CancellationToken, Task>? progress = null)
        {
            Operation = operation;
            return Task.FromResult(new WorkerProvisioningResult("detect", "succeeded", ProvisioningDiagnostic.Completed));
        }
    }

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
        Assert.Empty(git.AuthenticationDependencies);
        var github = Assert.Single(capabilities, item => item.Id == "github-cli");
        Assert.Equal([AuthenticationDependencyKind.GitHubCliLogin], github.AuthenticationDependencies);
        Assert.Contains(git.Actions, action => action.Action == "install" && !action.Allowed && action.RequiresElevation);
        Assert.Contains(git.Actions, action => action.Action == "upgrade" && action.PolicyKey == "tool:git:update");
        Assert.Contains(git.Actions, action => action.Action == "uninstall");
    }

    [Fact]
    public async Task TextStatusLabelsNodeAuthenticationSeparatelyFromServerAndProviderAuthorization()
    {
        var discovery = new NodeCapabilityDiscovery((_, arguments, _) =>
        {
            if (arguments.SequenceEqual(["--version"])) return Task.FromResult((0, "1.2.3"));
            if (arguments.SequenceEqual(["auth", "status", "--hostname", "github.com"]) ||
                arguments.SequenceEqual(["login", "status"])) return Task.FromResult((1, "provider-token-must-not-appear"));
            return Task.FromResult((0, "configured"));
        });
        var result = await ExecuteCliAsync(ProvisioningCli.Parse(["status"]), new ProvisioningPolicy(), discovery);
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            ProvisioningCli.Write(result, json: false);
        }
        finally { Console.SetOut(originalOutput); }

        Assert.Contains("Node authentication is the Codex/GitHub CLI state observed in the service-account environment", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("github-cli (GitHub CLI):", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("node-authentication=required", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("authentication-dependencies=GitHubCliLogin", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("authentication-dependencies=none", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("codex-cli (Codex CLI):", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-token-must-not-appear", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("credential-delivery", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuccessfulGitHubPreparationPrintsTheTypedDeviceLoginHandoff()
    {
        var result = new WorkerProvisioningResult("prepare-authentication", "succeeded", ProvisioningDiagnostic.Completed,
            "github-cli", ProvisioningCommandAction.PrepareAuthentication);
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            ProvisioningCli.Write(result, json: false);
        }
        finally { Console.SetOut(originalOutput); }

        Assert.Contains("as the Worker service account", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("codex-worker provision login github-cli --timeout-seconds 300", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("one-time code", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("check-authentication github-cli", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token", output.ToString(), StringComparison.OrdinalIgnoreCase);
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
