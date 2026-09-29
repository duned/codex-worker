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
        Assert.Equal(12, discoveryCalls);
    }

    [Fact]
    public async Task UnsupportedMissingCapabilityFailsAtTheSpecificAction()
    {
        var reports = new List<ProvisioningWorkerReportContract>();
        var executor = new ProvisioningPlanExecutor(new WorkerCapabilityDiscovery((_, _, _, _, _) => Task.FromResult(new ProcessResult(1, "", ""))));
        var result = await executor.ExecuteAsync(Plan([new ProvisioningActionContract("postgres", "service", "postgresql")]), "worker-id",
            (report, _) => { reports.Add(report); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("Failed", result.State);
        Assert.Equal("postgres", result.CurrentActionId);
        Assert.Contains("no local provisioner", result.Failure);
        Assert.Equal("Failed", reports[^1].State);
    }

    private static ProvisioningPlanContract Plan(IReadOnlyList<ProvisioningActionContract> actions) =>
        new(Guid.NewGuid().ToString("N"), "worker-id", DateTimeOffset.UtcNow, "Accepted", actions);
}
