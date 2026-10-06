namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;

public sealed class WorkerPreparationTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    internal static CapabilityState[] ReadyInventory(DateTimeOffset observedAt) => CapabilityCatalog.Definitions
        .Where(definition => definition.RequiredForExecution)
        .Select(definition => new CapabilityState(definition.Id, InstallationState.Installed, "1.0", UpdateState.Unknown,
            definition.RequiresAuthentication ? RequirementState.Satisfied : null,
            definition.RequiresConfiguration ? RequirementState.Satisfied : null,
            CapabilityHealth.Healthy, new(CapabilityOperationState.Idle), observedAt)).ToArray();

    private static CentralProject Project => new("project", "Project", "owner/repo", "main", "", [], 1, ObservedAt, ObservedAt);
    private static WorkerRegistrationResponse Worker => new(2, Guid.NewGuid().ToString("N"), "Worker", "1", "linux", 1,
        [new("authentication", "github-api", Scope: "owner/repo"), new("authentication", "git-repository", Scope: "owner/repo"),
         new("agent-provider", "codex")], ObservedAt, ObservedAt, "online", 0, 1, 1, "running", [], ObservedAt,
        "synchronized", "version", ReadyInventory(ObservedAt), WorkerSchedulingPolicy.Disabled,
        AuthenticationCredentialStatus: "active", ManagedDiagnostics: new("retrieved", "synchronized", "server-retrieved",
            "version", "version", null, null, [new("project", 1, "not-materialized")]));

    private static WorkerDiagnostics Describe(WorkerRegistrationResponse worker, CentralProject? project = null,
        IReadOnlyList<ProvisioningCommand>? commands = null) =>
        WorkerDiagnosticsDerivation.Derive(worker, [project ?? Project], [], "version", commands, ObservedAt);

    [Fact]
    public void ActivationPermitsLazyCheckoutButDoesNotChangeSchedulingPolicy()
    {
        var worker = Worker;
        var diagnostics = Describe(worker);
        Assert.True(diagnostics.CanActivate);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(diagnostics.ActivationBlockingReasons));
        Assert.Equal(WorkerSchedulingPolicy.Disabled, worker.SchedulingPolicy);
        Assert.Equal("not-materialized", Assert.Single(diagnostics.Projects).MaterializationState);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("cached")]
    [InlineData("unknown")]
    [InlineData("codex-unavailable")]
    [InlineData("repository-access-missing")]
    [InlineData("inventory-stale")]
    [InlineData("inventory-unknown")]
    [InlineData("local-configuration-failed")]
    [InlineData("authentication-revoked")]
    public void PartialOrStalePreparationCannotActivate(string condition)
    {
        var worker = Worker;
        worker = condition switch
        {
            "stale" => worker with { Availability = "stale" },
            "cached" => worker with { ConfigurationSynchronization = "cached" },
            "unknown" => worker with { ManagedDiagnostics = null },
            "codex-unavailable" => worker with { Capabilities = worker.Capabilities.Where(c => c.Name != "codex").ToArray() },
            "repository-access-missing" => worker with { Capabilities = worker.Capabilities.Where(c => c.Name != "git-repository").ToArray() },
            "inventory-stale" => worker with { CapabilityInventory = ReadyInventory(ObservedAt.AddMinutes(-7)) },
            "inventory-unknown" => worker with { CapabilityInventory = null },
            "authentication-revoked" => worker with { AuthenticationCredentialStatus = "revoked" },
            _ => worker with { ConfigurationSynchronization = "error" }
        };
        Assert.False(Describe(worker).CanActivate);
    }

    [Fact]
    public void RevisionChangesDisabledProjectsAndMissingProjectsBlockActivation()
    {
        Assert.False(Describe(Worker, Project with { Revision = 2 }).CanActivate);
        Assert.False(Describe(Worker, Project with { Enabled = false }).CanActivate);
        Assert.False(WorkerDiagnosticsDerivation.Derive(Worker, [], [], "version", observedAtUtc: ObservedAt).CanActivate);
    }

    [Fact]
    public void FailedProjectDoesNotHideHealthyProjectEvidence()
    {
        var worker = Worker;
        worker = worker with { ManagedDiagnostics = Assert.IsType<ManagedWorkerDiagnostics>(worker.ManagedDiagnostics) with
        {
            Projects = [new("project", 1, "not-materialized"), new("failed", 1, "failed", "project-preparation-failed")]
        } };
        var diagnostics = WorkerDiagnosticsDerivation.Derive(worker, [Project, Project with { Id = "failed" }], [], "version", observedAtUtc: ObservedAt);
        Assert.True(diagnostics.CanActivate);
        Assert.Contains(diagnostics.Reasons, reason => reason.Contains("project-preparation-failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ProvisioningCommandStatus.Pending, false)]
    [InlineData(ProvisioningCommandStatus.Running, false)]
    [InlineData(ProvisioningCommandStatus.Cancelled, true)]
    [InlineData(ProvisioningCommandStatus.Failed, true)]
    public void RetainedOperationsMustBeTerminalBeforeActivation(ProvisioningCommandStatus status, bool canActivate)
    {
        var worker = Worker;
        var command = new ProvisioningCommand("command", new(worker.WorkerId, "git", ProvisioningCommandAction.Detect), ObservedAt,
            status, ProvisioningDiagnostic.Interrupted, DeadlineUtc: ObservedAt.AddMinutes(-1));
        Assert.Equal(canActivate, Describe(worker, commands: [command]).CanActivate);
    }
}
