using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProvisioningPlanExecutorTests
{
    [Fact]
    public async Task SingleAlreadySatisfiedActionCompletesWithoutInstallerWork()
    {
        var reports = new List<ProvisioningWorkerReportContract>();
        var discovery = new WorkerCapabilityDiscovery((executable, _, _, _, _) => Task.FromResult(executable == "git"
            ? new ProcessResult(0, "git version 2.43.0", "")
            : new ProcessResult(1, "", "")));
        var action = new ProvisioningActionContract("git", "tool", "git", "2.43.0", "install");
        var result = await new ProvisioningPlanExecutor(discovery).ExecuteAsync(Plan([action]), "worker-id",
            (report, _) => { reports.Add(report); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Completed", result.State);
        Assert.Equal("git", reports[0].CurrentActionId);
        Assert.Equal("Completed", reports[^1].State);
    }

    [Fact]
    public async Task EmptyPlanCompletesAsNoOp()
    {
        var reports = new List<ProvisioningWorkerReportContract>();
        var executor = new ProvisioningPlanExecutor(new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(1, "", ""))));
        var result = await executor.ExecuteAsync(Plan([]), "worker-id", (report, _) => { reports.Add(report); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Completed", result.State);
        Assert.Equal("Completed", Assert.Single(reports).State);
        Assert.Contains("No provisioning actions", result.Result);
    }

    [Fact]
    public async Task ExistingCapabilitiesAndRefreshActionsAreIdempotentAcrossMultipleActions()
    {
        var reports = new List<ProvisioningWorkerReportContract>();
        var discoveryCalls = 0;
        var discovery = new WorkerCapabilityDiscovery((executable, _, _, _, _) =>
        {
            discoveryCalls++;
            return Task.FromResult(executable == "git" ? new ProcessResult(0, "git version 2.43.0", "") : new ProcessResult(1, "", ""));
        });
        var actions = new[]
        {
            new ProvisioningActionContract("git", "tool", "git", "2.43.0"),
            new ProvisioningActionContract("refresh", "refresh-capabilities", "worker", Operation: "refresh")
        };
        var result = await new ProvisioningPlanExecutor(discovery).ExecuteAsync(Plan(actions), "worker-id",
            (report, _) => { reports.Add(report); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Completed", result.State);
        Assert.Equal("Completed", reports[^1].State);
        Assert.Equal(new[] { "git", "refresh" }, reports.Where(report => report.State == "Running").Select(report => report.CurrentActionId));
        Assert.Equal(14, discoveryCalls);
    }

    [Fact]
    public async Task UnsupportedMissingCapabilityFailsAtTheSpecificAction()
    {
        var reports = new List<ProvisioningWorkerReportContract>();
        var result = await new ProvisioningPlanExecutor(new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(1, "", ""))), []).ExecuteAsync(Plan([new ProvisioningActionContract("postgres", "service", "custom-database")]), "worker-id",
            (report, _) => { reports.Add(report); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Failed", result.State);
        Assert.Equal("postgres", result.CurrentActionId);
        Assert.Contains("no supported installer", result.Failure);
        Assert.Equal("Failed", reports[^1].State);
    }

    [Theory]
    [InlineData("10.0", "10.0.0", true)]
    [InlineData(">=10.0", "10.1", true)]
    [InlineData(">=10.0", "9.9", false)]
    [InlineData("10.0.1", "10.0", false)]
    public void CapabilityMatchingUsesExactAndMinimumNumericVersionSemantics(string requirementVersion, string installedVersion, bool expected)
    {
        var requirement = new ProvisioningRequirement("runtime", "dotnet", requirementVersion);
        Assert.Equal(expected, CapabilityVersionMatcher.Satisfies(new WorkerCapabilityContract("runtime", "dotnet", installedVersion), requirement));
    }

    [Fact]
    public async Task AptInstallerBuildsTrustedElevatedPackageCommandAndSkipsCompatibleCapability()
    {
        string? executable = null;
        IReadOnlyList<string>? arguments = null;
        var installer = new DebianAptDependencyInstaller(() => true, () => true, (name, args, _) =>
        {
            executable = name;
            arguments = args.ToArray();
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        var requirement = new ProvisioningRequirement("tool", "git", ">=2.40");
        var satisfied = installer.Plan(requirement, [new WorkerCapabilityContract("tool", "git", "2.43.0")]);
        Assert.True(satisfied.AlreadySatisfied);
        Assert.False(satisfied.RequiresElevation);

        var plan = installer.Plan(requirement, []);
        Assert.True(plan.Supported);
        Assert.True(plan.RequiresElevation);
        Assert.Equal("sudo", plan.Executable);
        Assert.Equal(new[] { "-n", "apt-get", "install", "-y", "--no-install-recommends", "git" }, plan.Arguments);
        Assert.True((await installer.InstallAsync(plan, CancellationToken.None)).Succeeded);
        Assert.Equal("sudo", executable);
        Assert.Equal(plan.Arguments, arguments);

        var incompatible = installer.Plan(new ProvisioningRequirement("tool", "git", ">=2.50"),
            [new WorkerCapabilityContract("tool", "git", "2.43.0")]);
        Assert.False(incompatible.Supported);
        Assert.Contains("upgrades and downgrades", incompatible.Reason);
    }

    [Fact]
    public void InstallerExplicitlyRejectsUnsupportedPlatformAndDependency()
    {
        var unsupportedPlatform = new DebianAptDependencyInstaller(() => false, () => false,
            (_, _, _) => throw new InvalidOperationException("Must not execute."));
        var platformPlan = unsupportedPlatform.Plan(new ProvisioningRequirement("tool", "git", null), []);
        Assert.False(platformPlan.Supported);
        Assert.Contains("Debian and Ubuntu", platformPlan.Reason);

        var unsupportedDependency = new DebianAptDependencyInstaller(() => true, () => false,
            (_, _, _) => throw new InvalidOperationException("Must not execute."));
        var dependencyPlan = unsupportedDependency.Plan(new ProvisioningRequirement("runtime", "dotnet", null), []);
        Assert.False(dependencyPlan.Supported);
        Assert.Contains("No trusted package mapping", dependencyPlan.Reason);
    }

    [Fact]
    public async Task SuccessfulInstallMustPassCapabilityVerification()
    {
        var gitVersion = 0;
        var discovery = new WorkerCapabilityDiscovery((executable, _, _, _, _) => Task.FromResult(executable == "git"
            ? gitVersion == 0
                ? new ProcessResult(1, "", "")
                : new ProcessResult(0, "git version 2.43.0", "")
            : new ProcessResult(1, "", "")));
        var installer = new DebianAptDependencyInstaller(() => true, () => false, (_, _, _) =>
        {
            gitVersion++;
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        var result = await new ProvisioningPlanExecutor(discovery, [installer]).ExecuteAsync(
            Plan([new ProvisioningActionContract("git", "tool", "git", ">=2.40", "install")]), "worker-id",
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("Completed", result.State);
        Assert.Contains("Installed and verified: tool git 2.43.0", result.Result);
    }

    [Fact]
    public async Task FailedInstallAndUnverifiedInstallProduceExplicitFailure()
    {
        var failedInstaller = new DebianAptDependencyInstaller(() => true, () => false,
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "package manager failed")));
        var discovery = new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(1, "", "")));
        var failed = await new ProvisioningPlanExecutor(discovery, [failedInstaller]).ExecuteAsync(
            Plan([new ProvisioningActionContract("git", "tool", "git")]), "worker-id",
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("Failed", failed.State);
        Assert.Contains("exited with code 1", failed.Failure);

        var successfulButUnverified = new DebianAptDependencyInstaller(() => true, () => false,
            (_, _, _) => Task.FromResult(new ProcessResult(0, "", "")));
        var unverified = await new ProvisioningPlanExecutor(discovery, [successfulButUnverified]).ExecuteAsync(
            Plan([new ProvisioningActionContract("git", "tool", "git")]), "worker-id",
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("Failed", unverified.State);
        Assert.Contains("not detected afterward", unverified.Failure);
    }

    private static ProvisioningPlanContract Plan(IReadOnlyList<ProvisioningActionContract> actions) =>
        new(Guid.NewGuid().ToString("N"), "worker-id", DateTimeOffset.UtcNow, "Accepted", actions);
}
