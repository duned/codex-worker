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
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void WorkerEligibilityMatchesAllRequirementsAndExplainsMissingOrIncompatibleCapabilities()
    {
        ProjectRequirement[] requirements =
        [
            new("runtime", ".NET", ">=10"),
            new("tool", "Docker"),
            new("service", "PostgreSQL", "18.0")
        ];
        Assert.True(WorkerEligibility.Evaluate(null, null).IsEligible);
        Assert.True(WorkerEligibility.Evaluate(requirements,
        [
            new("runtime", "dotnet", "10.0.0"),
            new("tool", "docker"),
            new("service", "postgresql", "18")
        ]).IsEligible);

        var result = WorkerEligibility.Evaluate(requirements,
        [
            new("runtime", "dotnet", "9"),
            new("service", "postgresql", "17.9")
        ]);
        Assert.False(result.IsEligible);
        Assert.Equal(3, result.MissingRequirements.Count);
        Assert.Contains("requires .NET >=10; worker reports .NET 9", result.MissingRequirements);
        Assert.Contains("requires Docker; capability unavailable", result.MissingRequirements);
        Assert.Contains("requires PostgreSQL 18.0; worker reports PostgreSQL 17.9", result.MissingRequirements);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void WorkerDiagnosticsExplainHealthyAndUnhealthyReadiness()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var project = new CentralProject("project", "Project", "team/repository", "main", "", [], 1, now, now);
        WorkerCapability[] readyCapabilities =
        [
            new("authentication", "github-api", Scope: "team/repository"),
            new("authentication", "git-repository", Scope: "team/repository"),
            new("agent-provider", "codex")
        ];
        var healthy = new WorkerRegistrationResponse(2, "worker", "worker", "1.2.3", "linux", 2,
            readyCapabilities, now, now, "online", 0, 2, 2, "running", [], now, "synchronized", "config-v1");

        var healthyDiagnostics = WorkerDiagnosticsDerivation.Derive(healthy, [project], [], "config-v1");

        Assert.Empty(healthyDiagnostics.Reasons);
        Assert.True(healthyDiagnostics.GitHubReady);
        Assert.True(healthyDiagnostics.GitReady);
        Assert.True(healthyDiagnostics.AiAgentReady);
        Assert.True(Assert.Single(healthyDiagnostics.Projects).IsEligible);
        Assert.Equal(now, healthyDiagnostics.LastHeartbeatAtUtc);
        Assert.Equal("synchronized", healthyDiagnostics.ConfigurationSynchronization);

        var unhealthy = healthy with
        {
            Availability = "stale",
            LifecycleState = "draining",
            ActiveExecutions = 2,
            AvailableCapacity = 0,
            Capabilities = [new("tool", "git")]
        };
        var unhealthyDiagnostics = WorkerDiagnosticsDerivation.Derive(unhealthy, [project], [], "config-v1");

        Assert.Contains("Offline", unhealthyDiagnostics.Reasons);
        Assert.Contains("GitHub unavailable", unhealthyDiagnostics.Reasons);
        Assert.Contains("Git unavailable", unhealthyDiagnostics.Reasons);
        Assert.Contains("AI agent unavailable", unhealthyDiagnostics.Reasons);
        Assert.False(Assert.Single(unhealthyDiagnostics.Projects).IsEligible);
        Assert.Contains("requires github-api for team/repository; capability unavailable",
            Assert.Single(unhealthyDiagnostics.Projects).MissingRequirements);

        var draining = healthy with { Availability = "draining", LifecycleState = "draining" };
        Assert.Contains("Draining", WorkerDiagnosticsDerivation.Derive(draining, [project], [], "config-v1").Reasons);
        var full = healthy with { ActiveExecutions = 2, AvailableCapacity = 0 };
        Assert.Contains("At capacity", WorkerDiagnosticsDerivation.Derive(full, [project], [], "config-v1").Reasons);
        var outOfSync = healthy with { ConfigurationVersion = "older-config" };
        var syncDiagnostics = WorkerDiagnosticsDerivation.Derive(outOfSync, [project], [], "config-v1");
        Assert.Equal("out-of-sync", syncDiagnostics.ConfigurationSynchronization);
        Assert.Contains("Configuration synchronization required", syncDiagnostics.Reasons);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void AuthenticationEligibilityRequiresBothApiAndGitReadinessForTheExactRepository()
    {
        var required = WorkerAuthenticationRequirements.ForRepository("team/compiler");
        WorkerCapability[] ready =
        [
            new("authentication", "github-api", Scope: "team/compiler"),
            new("authentication", "git-repository", Scope: "team/compiler")
        ];

        Assert.True(WorkerEligibility.Evaluate(required, ready).IsEligible);
        Assert.False(WorkerEligibility.Evaluate(required, [ready[0]]).IsEligible);
        Assert.False(WorkerEligibility.Evaluate(required, [ready[1]]).IsEligible);
        Assert.False(WorkerEligibility.Evaluate(required, ready.Select(capability => capability with { Scope = "team/other" })).IsEligible);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ProjectEligibilityRequiresCodexAgentReadiness()
    {
        var project = new CentralProject("project-id", "Project", "team/compiler", "main", "", [], 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var requirements = WorkerAuthenticationRequirements.ForProject(project);
        var repositoryReady = WorkerAuthenticationRequirements.ForRepository(project.Repository)
            .Select(requirement => new WorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope)).ToArray();

        var missingAgent = WorkerEligibility.Evaluate(requirements, repositoryReady);
        Assert.False(missingAgent.IsEligible);
        Assert.Contains("requires codex; capability unavailable", missingAgent.MissingRequirements);
        Assert.True(WorkerEligibility.Evaluate(requirements, [.. repositoryReady, new WorkerCapability("agent-provider", "codex")]).IsEligible);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task AssignmentSkipsIncompatibleWorkersKeepsWorkQueuedAndReevaluatesUpdatedCapabilities()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "eligibility.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Capability project", "team/project", "main", "",
            [new("runtime", ".NET", ">=10"), new("tool", "docker")]));
        var execution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "49")));
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "old worker", "1.0", "test", 1,
            [new("runtime", ".NET", "9")]));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1,
            [new("runtime", ".NET", "9")], []));

        var request = new WorkerAssignmentRequest(workerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        var pending = Assert.Single(await store.GetExecutionsAsync());
        Assert.Equal("Queued", pending.State);
        Assert.Equal("no compatible worker", pending.PendingReason);
        Assert.Contains("requires .NET >=10; worker reports .NET 9", pending.MissingRequirements!);
        Assert.Contains("requires Docker; capability unavailable", pending.MissingRequirements!);

        var laterWorkerId = Guid.NewGuid().ToString("N");
        WorkerCapability[] matchingCapabilities = [new("runtime", ".NET", "10.0"), new("tool", "Docker"),
            .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, laterWorkerId, "later worker", "1.0", "test", 1,
            matchingCapabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, laterWorkerId, "1.0", "running", 1, 1,
            matchingCapabilities, [project.Id]));
        var laterRequest = request with { WorkerId = laterWorkerId };
        Assert.False((await store.RequestAssignmentAsync(laterRequest)).HasWork);
        pending = Assert.Single(await store.GetExecutionsAsync());
        Assert.Equal("compatible workers currently at capacity", pending.PendingReason);

        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, laterWorkerId, "1.0", "not-ready", 0, 1,
            matchingCapabilities, []));
        Assert.False((await store.RequestAssignmentAsync(laterRequest)).HasWork);
        Assert.Equal("Queued", Assert.Single(await store.GetExecutionsAsync()).State);

        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, laterWorkerId, "1.0", "running", 0, 1,
            matchingCapabilities, []));
        var assignment = (await store.RequestAssignmentAsync(laterRequest)).Assignment;
        Assert.NotNull(assignment);
        Assert.Equal(execution.Id, assignment.ServerExecutionId);
        Assert.Equal(laterWorkerId, assignment.WorkerId);
        Assert.Equal("Assigned", Assert.Single(await store.GetExecutionsAsync()).State);

        var nextExecution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "50")));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1,
            matchingCapabilities, []));
        var updatedWorkerAssignment = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(updatedWorkerAssignment);
        Assert.Equal(nextExecution.Id, updatedWorkerAssignment.ServerExecutionId);
        Assert.Equal(workerId, updatedWorkerAssignment.WorkerId);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task AssignmentReevaluatesCurrentRevisionWithoutLocalProjects()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "revision-eligibility.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Central", "team/project", "main", "", []));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "49")));
        var workerId = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "empty worker", "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var request = new WorkerAssignmentRequest(workerId, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        var definition = new CentralProjectDefinition("Central", project.Repository, "release", "updated", [new("tool", "docker")]);
        var updated = await store.UpdateProjectAsync(project.Id, definition, project.Revision);
        Assert.NotNull(updated);

        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        Assert.Equal("Queued", Assert.Single(await store.GetExecutionsAsync()).State);
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1,
            [.. capabilities, new("tool", "docker")], []));
        var assignment = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(request)).Assignment);

        Assert.Equal(queued.Id, assignment.ServerExecutionId);
        Assert.Equal(updated.Revision, assignment.Project.Revision);
        Assert.Equal("release", assignment.Project.DefaultBranch);
        Assert.Equal(definition.Requirements, assignment.Project.Requirements);
        Assert.NotNull(assignment.Lease);
    }
}
