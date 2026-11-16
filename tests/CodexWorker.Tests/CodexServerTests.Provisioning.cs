namespace CodexWorker.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodexProvisioning;
using CodexServer;
using CodexWorker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServerWorkerCapability = CodexServer.WorkerCapability;
using WorkerCapability = CodexServer.WorkerCapability;

public sealed partial class CodexServerTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing)]
    [InlineData(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed)]
    [InlineData(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.ProcessFailed)]
    [InlineData(ProvisioningCommandStatus.TimedOut, ProvisioningDiagnostic.TimedOut)]
    public async Task RestoreRetainsDispatchedProvisioningWithoutReplay(ProvisioningCommandStatus status,
        ProvisioningDiagnostic diagnostic)
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "source.db");
        var target = Path.Combine(temporary.Path, "restored.db");
        var archive = Path.Combine(temporary.Path, "backup.zip");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var registry = new SqliteRegistryStore(database, timeProvider: clock);
        await registry.InitializeAsync();
        var credentials = new SqliteCredentialStore(database, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        await credentials.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await registry.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, []));
        var plan = await registry.CreateProvisioningPlanAsync(new(workerId, [new ProvisioningAction("refresh", "refresh-capabilities", "worker", Operation: "refresh")]));
        await registry.AcceptProvisioningPlanAsync(workerId);
        if (status == ProvisioningCommandStatus.Running)
            await registry.ReportProvisioningPlanAsync(plan.Id, new(workerId, "Running", "refresh"));
        var commands = new ProvisioningCommandStore(database, clock);
        await commands.InitializeAsync();
        var pending = await commands.CreateAsync(new("server", "git", ProvisioningCommandAction.Detect));
        var dispatched = Assert.IsType<ProvisioningCommand>(await commands.ClaimAsync("server"));
        if (status != ProvisioningCommandStatus.Running)
            dispatched = Assert.IsType<ProvisioningCommand>(await commands.ReportAsync(pending.Id, "server", new(status, diagnostic)));
        await new ServerBackup(database).ExportAsync(archive);
        await new ServerBackup(target).RestoreOfflineAsync(archive);
        var restored = new ProvisioningCommandStore(target, clock);
        await restored.InitializeAsync();
        Assert.Equal(dispatched, await restored.GetAsync(pending.Id));
        Assert.Null(await restored.ClaimAsync("server"));
        if (status == ProvisioningCommandStatus.Running)
            await Assert.ThrowsAsync<InvalidOperationException>(() => restored.CreateAsync(pending.Request));
        var restoredRegistry = new SqliteRegistryStore(target, timeProvider: clock);
        await restoredRegistry.InitializeAsync();
        Assert.Equal(status == ProvisioningCommandStatus.Running ? "Running" : "Accepted",
            (await restoredRegistry.GetProvisioningPlanAsync(plan.Id))!.State);
        Assert.Null(await restoredRegistry.AcceptProvisioningPlanAsync(workerId));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ProvisioningPlansPersistLifecycleWithoutReplayingInterruptedWorkAndSanitizeFailure()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "provisioning.db");
        var workerId = Guid.NewGuid().ToString("N");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, [new("tool", "git")]));

        var noOp = await store.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
        Assert.Equal("Pending", noOp.State);
        Assert.Empty(noOp.Actions);
        var noOpClaim = Assert.IsType<ProvisioningPlan>(await store.AcceptProvisioningPlanAsync(workerId));
        var noOpCompleted = await store.ReportProvisioningPlanAsync(noOp.Id,
            new ProvisioningWorkerReport(workerId, "Completed", Result: "No provisioning actions were required."));
        Assert.Equal("Completed", noOpCompleted!.State);
        Assert.NotNull(noOpCompleted.StartedAtUtc);
        Assert.NotNull(noOpCompleted.CompletedAtUtc);
        Assert.Equal(noOpClaim.Id, noOpCompleted.Id);

        var actions = new[] { new ProvisioningAction("runtime-dotnet", "runtime", "dotnet", "10.0"), new ProvisioningAction("refresh", "refresh-capabilities", "worker", Operation: "refresh") };
        var plan = await store.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, actions));
        Assert.Equal(actions, plan.Actions);
        var claimed = Assert.IsType<ProvisioningPlan>(await store.AcceptProvisioningPlanAsync(workerId));
        Assert.Equal("Accepted", claimed.State);
        var restartedStore = new SqliteRegistryStore(database);
        await restartedStore.InitializeAsync();
        Assert.Null(await restartedStore.AcceptProvisioningPlanAsync(workerId));
        var interrupted = await restartedStore.ReportProvisioningPlanAsync(plan.Id,
            new ProvisioningWorkerReport(workerId, "Running", "runtime-dotnet"));
        Assert.Equal("Running", interrupted!.State);
        Assert.Equal("runtime-dotnet", interrupted.CurrentActionId);
        var recovered = await new SqliteRegistryStore(database).AcceptProvisioningPlanAsync(workerId);
        Assert.Null(recovered);
        Assert.Equal("Running", (await restartedStore.GetProvisioningPlanAsync(plan.Id))!.State);
        var failed = await restartedStore.ReportProvisioningPlanAsync(plan.Id,
            new ProvisioningWorkerReport(workerId, "Failed", "runtime-dotnet", Failure: "runtime install token=secret-value failed"));
        Assert.Equal("Failed", failed!.State);
        Assert.Contains("[redacted]", failed.Failure);
        Assert.DoesNotContain("secret-value", JsonSerializer.Serialize(failed), StringComparison.Ordinal);
        Assert.NotNull(failed.StartedAtUtc);
        Assert.NotNull(failed.CompletedAtUtc);

        var cancelled = await restartedStore.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, []));
        var transition = await restartedStore.TransitionProvisioningPlanAsync(cancelled.Id, new ProvisioningStateTransition("Cancelled"));
        Assert.Equal("Cancelled", transition!.State);
        Assert.NotNull(transition.CompletedAtUtc);
        Assert.Equal("Completed", (await restartedStore.GetProvisioningPlanAsync(noOp.Id))!.State);
        Assert.Equal(3, (await restartedStore.GetProvisioningPlansAsync()).Count);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ProvisioningActionsRequireExplicitSafeKindsAndRejectEmbeddedCredentialValues()
    {
        var workerId = Guid.NewGuid().ToString("N");
        Assert.Null(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("dotnet", "runtime", "dotnet", "10.0", "install"),
            new("refresh", "refresh-capabilities", "worker", Operation: "refresh")])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("secret", "tool", "token=private-value")])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("refresh", "refresh-capabilities", "worker")])));
        Assert.Null(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("auth", "authentication", "github-api", Operation: "provision", CredentialId: Guid.NewGuid().ToString("N"), Scope: "team/repo")])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("auth", "authentication", "github-api", Operation: "provision", CredentialId: Guid.NewGuid().ToString("N"))])));
        Assert.NotNull(ProvisioningPlanValidation.Error(new CreateProvisioningPlanRequest(workerId, [
            new("auth", "authentication", "github-api", Operation: "provision", Scope: "team/repo")] )));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task StaleWorkerTurnsInterruptedProvisioningIntoAnExplicitFailure()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "provisioning.db"), 10, clock);
        await store.InitializeAsync();
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 1, [new("tool", "git")]));
        var plan = await store.CreateProvisioningPlanAsync(new CreateProvisioningPlanRequest(workerId, [new("git", "tool", "git")]));
        await store.AcceptProvisioningPlanAsync(workerId);
        await store.ReportProvisioningPlanAsync(plan.Id, new ProvisioningWorkerReport(workerId, "Running", "git"));

        clock.Advance(TimeSpan.FromSeconds(11));
        await store.ExpireLeasesAsync();

        var failed = await store.GetProvisioningPlanAsync(plan.Id);
        Assert.Equal("Failed", failed!.State);
        Assert.Equal("Worker heartbeat expired while provisioning was active.", failed.Failure);
        Assert.NotNull(failed.CompletedAtUtc);
    }

    private static async Task<CentralProject?> AttemptUpdateAsync(SqliteRegistryStore store, string id, CentralProjectDefinition definition)
    {
        try { return await store.UpdateProjectAsync(id, definition, 1); }
        catch (ProjectRevisionConflictException) { return null; }
    }
}
