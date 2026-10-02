namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Text.Json;

public sealed class ServerProvisioningAdministrationTests
{
    [Fact]
    public async Task LocalCliPreservesMixedPlanAndTypedHistoryAndSupportsCancellationAndQuiescenceReconciliation()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "codex-server.db");
        var registry = new SqliteRegistryStore(database);
        await registry.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "Worker", "1.0.0", "linux", 1, []));
        var plan = await registry.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        var commands = new ProvisioningCommandStore(database, clock);
        var service = new LocalProvisioningCommandAdministrationService(database, registry, commands);
        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new ServerProvisioningCommandCli(new ServerConfigurationAdministrationService(),
            new TestProvisioningServiceFactory(service), output, error);
        var dataDirectory = $"--Server:DataDirectory={temporary.Path}";

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["list", "--json", dataDirectory]));
        using (var empty = JsonDocument.Parse(output.ToString())) Assert.Empty(empty.RootElement.EnumerateArray());
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(
            ["create", "server", "git", "detect", "--timeout-seconds", "5", "--json", dataDirectory]));
        using var createdDocument = JsonDocument.Parse(output.ToString());
        var createdId = createdDocument.RootElement.GetProperty("id").GetString();
        Assert.NotNull(createdId);
        Assert.Equal(plan.Id, (await registry.GetProvisioningPlanAsync(plan.Id))?.Id);
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["list", "--json", dataDirectory]));
        using (var history = JsonDocument.Parse(output.ToString()))
        {
            var typed = Assert.Single(history.RootElement.EnumerateArray());
            Assert.Equal(createdId, typed.GetProperty("id").GetString());
            Assert.Equal("Pending", typed.GetProperty("status").GetString());
        }
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success,
            await cli.RunAsync(["cancel", createdId!, "--json", dataDirectory]));
        using (var cancelled = JsonDocument.Parse(output.ToString()))
            Assert.Equal("Cancelled", cancelled.RootElement.GetProperty("status").GetString());
        output.GetStringBuilder().Clear();

        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(
            ["create", "server", "git", "detect", "--timeout-seconds", "5", "--json", dataDirectory]));
        using var secondDocument = JsonDocument.Parse(output.ToString());
        var runningId = secondDocument.RootElement.GetProperty("id").GetString();
        Assert.NotNull(runningId);
        output.GetStringBuilder().Clear();
        await commands.ClaimAsync("server");
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(ServerAdministrationExitCodes.InvalidArguments,
            await cli.RunAsync(["reconcile", runningId!, dataDirectory]));
        Assert.Equal(ServerAdministrationExitCodes.Success,
            await cli.RunAsync(["reconcile", runningId!, "--node-quiescent", "--json", dataDirectory]));
        using var reconciled = JsonDocument.Parse(output.ToString());
        Assert.Equal("Failed", reconciled.RootElement.GetProperty("status").GetString());
        Assert.Equal("Interrupted", reconciled.RootElement.GetProperty("diagnostic").GetString());
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invalid provisioning administration arguments", error.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(
            ["list", "--limit", "1", "--offset", "1", "--json", dataDirectory]));
        using (var page = JsonDocument.Parse(output.ToString()))
            Assert.Equal(createdId, Assert.Single(page.RootElement.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(ServerAdministrationExitCodes.InvalidArguments, await cli.RunAsync(["list", "--limit", "101", dataDirectory]));

        var restarted = new ProvisioningCommandStore(database, clock);
        await restarted.InitializeAsync();
        Assert.Equal(2, (await restarted.ListAsync()).Count);
        Assert.Equal("Pending", (await registry.GetProvisioningPlanAsync(plan.Id))?.State);
    }

    [Fact]
    public void WorkerDiagnosticsReportTheLatestProvisioningSourceWithoutChangingProjectReadinessReasons()
    {
        var now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var worker = new WorkerRegistrationResponse(2, Guid.NewGuid().ToString("N"), "Worker", "1.0.0", "linux", 1,
            [], now, now, "online", 0, 1, 1, "running", []);
        var plan = new ProvisioningPlan("legacy-plan", worker.WorkerId, now, "Failed", [], Failure: "legacy action failed");
        var command = new ProvisioningCommand("typed-command",
            new(worker.WorkerId, "git", ProvisioningCommandAction.Detect), now.AddMinutes(1),
            ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.ProcessFailed);

        var diagnostics = WorkerDiagnosticsDerivation.Derive(worker, [], [plan], "config-v1", [command]);

        Assert.Equal("Failed", diagnostics.ProvisioningState);
        Assert.Equal("typed-command", diagnostics.LatestProvisioningOperation?.Source);
        Assert.Equal("processfailed", diagnostics.LatestProvisioningOperation?.Diagnostic);
        Assert.Contains("processfailed", Assert.IsType<string>(diagnostics.RecentOperationalError), StringComparison.Ordinal);
        Assert.DoesNotContain("Provisioning failed", diagnostics.Reasons);
        Assert.DoesNotContain("Provisioning required", diagnostics.Reasons);

        var newerPlan = new ProvisioningPlan("newer-plan", worker.WorkerId, now.AddMinutes(2), "Running",
            [new("install", "tool", "git", Operation: "install")], "install");
        var olderCommand = command with { CreatedAtUtc = now.AddMinutes(1) };
        var node = NodeProvisioning.WithCommands(NodeProvisioning.Describe(worker, [newerPlan]), [olderCommand], [newerPlan]);
        Assert.Equal(CapabilityOperationState.Running,
            Assert.Single(node.Capabilities, item => item.Definition.Id == "git").State.Operation.State);

        var newerCommand = command with { CreatedAtUtc = now.AddMinutes(3) };
        var updatedNode = NodeProvisioning.WithCommands(NodeProvisioning.Describe(worker, [newerPlan]), [newerCommand], [newerPlan]);
        var latestOperation = Assert.Single(updatedNode.Capabilities, item => item.Definition.Id == "git").State.Operation;
        Assert.Equal(CapabilityOperationState.Failed, latestOperation.State);
        Assert.Equal("processfailed", latestOperation.DiagnosticCode);

        var successfulCommand = newerCommand with { Status = ProvisioningCommandStatus.Succeeded, Diagnostic = ProvisioningDiagnostic.Completed };
        var recoveredNode = NodeProvisioning.WithCommands(NodeProvisioning.Describe(worker, [newerPlan]), [successfulCommand], [newerPlan]);
        Assert.Equal(CapabilityOperationState.Idle,
            Assert.Single(recoveredNode.Capabilities, item => item.Definition.Id == "git").State.Operation.State);
    }

    private sealed class TestProvisioningServiceFactory(IProvisioningCommandAdministrationService service)
        : IProvisioningCommandAdministrationServiceFactory
    {
        public IProvisioningCommandAdministrationService Create(ServerConfiguration configuration) => service;
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"server-provisioning-admin-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
