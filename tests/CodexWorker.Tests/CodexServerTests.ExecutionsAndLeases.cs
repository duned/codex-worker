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
    [Fact]
    public async Task ExecutionQueuePersistsFifoWorkerReportsAndAllowsLaterAttemptsAcrossProjects()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "queue.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var projectA = await store.CreateProjectAsync(new CentralProjectDefinition("Alpha", "team/alpha", "main", "", []));
        var projectB = await store.CreateProjectAsync(new CentralProjectDefinition("Beta", "team/beta", "main", "", []));
        var work = new WorkReference("github-issue", "42", "https://github.com/team/alpha/issues/42");
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, work));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, new WorkReference("github-issue", "43")));
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, work)));
        var sameIssueOtherProject = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectB.Id, new WorkReference("issue", "42")));
        Assert.Equal(new[] { first.Id, second.Id, sameIssueOtherProject.Id }, (await store.GetExecutionsAsync()).Select(x => x.Id));

        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(projectA.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "queue worker", "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var assigned = (await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [projectA.Id] = 1 }))).Assignment;
        Assert.NotNull(assigned);
        Assert.Equal(new WorkReference("github-issue", "42", "https://github.com/team/alpha/issues/42"), assigned.Work);
        var assignment = await store.GetExecutionAsync(first.Id);
        Assert.NotNull(assignment);
        Assert.Equal(assigned.AssignmentId, assignment.AssignmentId);
        Assert.Equal(first.Id, assigned.ServerExecutionId);
        var leased = assignment;
        Assert.Equal("Assigned", leased!.State);
        Assert.Equal(workerId, leased.AssignedWorkerId);
        Assert.NotNull(leased.AssignedAtUtc);
        var lease = Assert.IsType<ExecutionLease>(leased.Lease);
        Assert.Equal(new ExecutionLease(first.Id, workerId, 1, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(15), "Active"), lease);
        var started = DateTimeOffset.Parse("2026-02-01T00:00:03Z");
        var running = await store.ReportExecutionAsync(first.Id, new WorkerExecutionReport(workerId, assigned.AssignmentId, "run-a", "Running", "Implementing", started, Generation: lease.Generation));
        Assert.Equal("Running", running!.State);
        Assert.Equal("run-a", running.ExecutionId);
        var finalReport = new WorkerExecutionReport(workerId, assigned.AssignmentId, "run-a", "Completed", null,
            started, started.AddMinutes(2), 120000, "passed", "integrated", null, false, "Implemented Issue #42.", lease.Generation);
        var completed = await store.ReportExecutionAsync(first.Id, finalReport);
        var duplicate = await store.ReportExecutionAsync(first.Id, finalReport);
        Assert.Equal("Completed", completed!.State);
        Assert.Equivalent(completed, duplicate);
        Assert.Equal("Implementing", completed.CurrentStage);
        Assert.Equal("run-a", completed.WorkerExecutionId);
        Assert.Equal("passed", completed.ValidationResult);
        Assert.Equal("integrated", completed.IntegrationResult);
        Assert.Equal("Implemented Issue #42.", completed.CompletionSummary);
        Assert.Equal("Released", completed.Lease!.State);
        Assert.Equal(1L, completed.Lease.Generation);
        var retry = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(projectA.Id, work));
        Assert.NotEqual(first.Id, retry.Id);
        // A later request for the same work is its next attempt; other work and projects keep their own count.
        Assert.Equal(1, first.AttemptNumber);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.Equal(1, second.AttemptNumber);
        Assert.Equal(1, sameIssueOtherProject.AttemptNumber);

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        var persisted = await restarted.GetExecutionsAsync();
        Assert.Equal(new[] { "Completed", "Queued", "Queued", "Queued" }, persisted.Select(x => x.State));
        Assert.Equal(new[] { projectA.Id, projectA.Id, projectB.Id, projectA.Id }, persisted.Select(x => x.ProjectId));
        Assert.Equal(first.Id, persisted[0].Id);
        Assert.Equal(work, persisted[0].WorkReference);
        Assert.Equal("Completed", persisted[0].State);
        Assert.Equal(120000, persisted[0].DurationMilliseconds);
        Assert.Equal(started.AddMinutes(2), persisted[0].CompletedAtUtc);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ExecutionStageTransitionsPersistServerTimestampsWithoutResettingRepeatedReports()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-10-23T12:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "stage-timings.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Stage timing", "team/stage-timing", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "stage timing worker", "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var execution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "31")));
        var assignment = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }))).Assignment);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        var startedAt = clock.GetUtcNow();

        async Task ReportStageAsync(string stage)
        {
            await store.ReportExecutionAsync(execution.Id,
                new WorkerExecutionReport(workerId, assignment.AssignmentId, "timed-run", "Running", stage, startedAt, Generation: lease.Generation));
        }

        await ReportStageAsync("Claiming");
        clock.Advance(TimeSpan.FromSeconds(42));
        await ReportStageAsync("Preparing");
        clock.Advance(TimeSpan.FromSeconds(75));
        await ReportStageAsync("Codex");
        clock.Advance(TimeSpan.FromSeconds(1));
        await ReportStageAsync("Codex");
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(14));
        await ReportStageAsync("Validation");
        clock.Advance(TimeSpan.FromSeconds(9));
        await ReportStageAsync("Integration");
        clock.Advance(TimeSpan.FromSeconds(59));
        await ReportStageAsync("Reporting");

        var projected = await store.GetExecutionAsync(execution.Id);
        var stageTimes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DateTimeOffset>>(projected!.StageStartedAtUtc);
        Assert.Equal(6, stageTimes.Count);
        Assert.Equal(startedAt, stageTimes["claimed"]);
        Assert.Equal(startedAt.AddSeconds(42), stageTimes["preparing"]);
        Assert.Equal(startedAt.AddSeconds(117), stageTimes["codex"]);
        Assert.Equal(startedAt.AddSeconds(252), stageTimes["validation"]);
        Assert.Equal(startedAt.AddSeconds(261), stageTimes["integration"]);
        Assert.Equal(startedAt.AddSeconds(320), stageTimes["reporting"]);

        var restarted = new SqliteRegistryStore(Path.Combine(temporary.Path, "stage-timings.db"));
        await restarted.InitializeAsync();
        var persisted = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DateTimeOffset>>(
            (await restarted.GetExecutionAsync(execution.Id))!.StageStartedAtUtc);
        foreach (var (stage, timestamp) in stageTimes) Assert.Equal(timestamp, persisted[stage]);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ManagedWorkReferencesAreCanonicalPositiveGitHubIssuesAndRemainUniqueAcrossAliasesAndRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "canonical-issues.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Canonical", "Team/Compiler", "main", "", []));

        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id,
            new WorkReference("issue", "00042", "https://github.com/team/compiler/issues/42")));

        Assert.Equal(new WorkReference("github-issue", "42", "https://github.com/Team/Compiler/issues/42"), first.WorkReference);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("GITHUB-ISSUE", "42"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("pull-request", "42"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "0"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "42abc"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "43", "https://github.com/other/repo/issues/43"))));

        var concurrentRequests = new[]
        {
            new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "99")),
            new EnqueueExecutionRequest(project.Id, new WorkReference("github-issue", "099"))
        };
        var results = await Task.WhenAll(concurrentRequests.Select(async request =>
        {
            try
            {
                await store.EnqueueExecutionAsync(request);
                return true;
            }
            catch (ExecutionRequestConflictException) { return false; }
        }));
        Assert.Single(results, created => created);
        Assert.Single(results, created => !created);

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        var persisted = Assert.Single(await restarted.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, WorkType: "issue", WorkId: "42", Limit: 10)));
        Assert.Equal(first.WorkReference, persisted.WorkReference);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => restarted.EnqueueExecutionAsync(new EnqueueExecutionRequest(
            project.Id, new WorkReference("issue", "42"))));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ArchiveHidesOnlyFinishedExecutionsFromListsWithoutChangingLifecycle()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "execution-archive.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Archive", "team/archive", "main", "", []));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var finished = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        await store.CancelQueuedExecutionAsync(finished.Id);
        var failed = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3")));
        var uncertain = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "4")));
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
        {
            // Terminal states normally come from Worker reports and lease expiry; seed them directly.
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE execution_requests SET state='Failed' WHERE id=$failed; UPDATE execution_requests SET state='Failed', recovery_state='LeaseExpiredUncertain' WHERE id=$uncertain;";
            command.Parameters.AddWithValue("$failed", failed.Id);
            command.Parameters.AddWithValue("$uncertain", uncertain.Id);
            await command.ExecuteNonQueryAsync();
        }
        var before = (await store.GetResourceStatusAsync()).Freshness!.Executions;

        var result = await store.SetExecutionsArchivedAsync([finished.Id, failed.Id, queued.Id, uncertain.Id, "missing"], archived: true);
        Assert.Equal([finished.Id, failed.Id], result.Changed);
        Assert.Equal(new[] { new ExecutionArchiveRefusal(queued.Id, "not-finished"), new ExecutionArchiveRefusal(uncertain.Id, "not-finished"),
            new ExecutionArchiveRefusal("missing", "not-found") }, result.Refused);
        Assert.True((await store.GetResourceStatusAsync()).Freshness!.Executions > before);
        Assert.Equal(new ExecutionArchiveRefusal(finished.Id, "already-archived"),
            Assert.Single((await store.SetExecutionsArchivedAsync([finished.Id], archived: true)).Refused));

        // Archive is presentation only: state and detail reads are unchanged.
        var archived = await store.GetExecutionAsync(finished.Id);
        Assert.Equal("Cancelled", archived!.State);
        Assert.NotNull(archived.ArchivedAtUtc);
        Assert.Equal([uncertain.Id, queued.Id], (await store.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, Archived: "exclude"))).Select(item => item.Id));
        Assert.Equal([failed.Id, finished.Id], (await store.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, Archived: "only"))).Select(item => item.Id));
        Assert.Equal(4, (await store.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id))).Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListExecutionsAsync(new ExecutionQuery(Archived: "all")));

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        Assert.NotNull((await restarted.GetExecutionAsync(finished.Id))!.ArchivedAtUtc);
        Assert.Equal([finished.Id], (await restarted.SetExecutionsArchivedAsync([finished.Id], archived: false)).Changed);
        Assert.Null((await restarted.GetExecutionAsync(finished.Id))!.ArchivedAtUtc);
        Assert.Equal(new ExecutionArchiveRefusal(finished.Id, "not-archived"),
            Assert.Single((await restarted.SetExecutionsArchivedAsync([finished.Id], archived: false)).Refused));
        Assert.NotNull(ExecutionArchiveValidation.Error(new ExecutionArchiveRequest([], true)));
        Assert.NotNull(ExecutionArchiveValidation.Error(new ExecutionArchiveRequest(Enumerable.Range(0, 101).Select(i => $"id-{i}").ToArray(), true)));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExecutionAdministrationFiltersDetailsCancelsOnlyQueuedWorkAndPersistsAcrossRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "execution-administration.db");
        var store = new SqliteRegistryStore(database);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Administration", "team/admin", "main", "", []));
        var assignedRequest = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var cancelled = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var filtered = await store.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, WorkType: "issue", WorkId: "0002", Limit: 1));
        Assert.Equal(assignedRequest.Id, Assert.Single(filtered).Id);
        Assert.Equal(assignedRequest.Id, (await store.GetExecutionAsync(assignedRequest.Id))!.Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListExecutionsAsync(new ExecutionQuery(Limit: 101)));

        var workerId = Guid.NewGuid().ToString("N");
        ServerWorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "admin worker", "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 1, capabilities, []));
        var assignment = (await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }))).Assignment!;
        Assert.Equal(assignedRequest.Id, assignment.ServerExecutionId);
        await Assert.ThrowsAsync<ExecutionRequestCancellationException>(() => store.CancelQueuedExecutionAsync(assignedRequest.Id));
        var cancelledRequest = await store.CancelQueuedExecutionAsync(cancelled.Id);
        Assert.Equal("Cancelled", cancelledRequest!.State);
        Assert.Null(cancelledRequest.Lease);
        await Assert.ThrowsAsync<ExecutionRequestTransitionException>(() => store.TransitionExecutionAsync(assignedRequest.Id,
            new ExecutionStateTransition("Completed")));

        var restarted = new SqliteRegistryStore(database);
        await restarted.InitializeAsync();
        Assert.Equal("Cancelled", (await restarted.GetExecutionAsync(cancelled.Id))!.State);
        Assert.Equal("Assigned", (await restarted.GetExecutionAsync(assignedRequest.Id))!.State);
        Assert.Equal(2, (await restarted.ListExecutionsAsync(new ExecutionQuery(ProjectId: project.Id, Limit: 100))).Count);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task AssignmentLeaseIsExclusiveDurableAndReleasedOnTerminalReport()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "lease-race.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Lease", "team/lease", "main", "", []));
        var firstWorker = Guid.NewGuid().ToString("N");
        var secondWorker = Guid.NewGuid().ToString("N");
        foreach (var worker in new[] { firstWorker, secondWorker })
        {
            WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, worker, worker, "1.0", "test", 1, capabilities));
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, worker, "1.0", "running", 0, 1, capabilities, []));
        }
        var execution = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        WorkerAssignmentRequest Request(string worker) => new(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        var attempts = await Task.WhenAll(store.RequestAssignmentAsync(Request(firstWorker)), store.RequestAssignmentAsync(Request(secondWorker)));
        var acquired = Assert.Single(attempts, x => x.HasWork).Assignment!;
        Assert.Equal(execution.Id, acquired.ServerExecutionId);
        Assert.Equal(1L, acquired.Lease!.Generation);
        Assert.Equal(clock.GetUtcNow(), acquired.Lease.AcquiredAtUtc);
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), acquired.Lease.ExpiresAtUtc);
        Assert.False((await store.RequestAssignmentAsync(Request(firstWorker))).HasWork);

        var restarted = new SqliteRegistryStore(database, timeProvider: clock);
        await restarted.InitializeAsync();
        var owned = Assert.Single(await restarted.GetExecutionsAsync());
        Assert.Equal(acquired.WorkerId, owned.Lease!.WorkerId);
        Assert.Equal(1L, owned.Lease.Generation);
        Assert.Equal("Active", owned.Lease.State);

        var staleReport = new WorkerExecutionReport("stale-worker", acquired.AssignmentId, "stale-run", "Completed",
            CompletedAtUtc: clock.GetUtcNow(), Generation: acquired.Lease!.Generation);
        await Assert.ThrowsAsync<ExecutionRequestOwnershipException>(() => restarted.ReportExecutionAsync(execution.Id, staleReport));
        var unchanged = await restarted.GetExecutionsAsync();
        Assert.Equal("Assigned", Assert.Single(unchanged).State);
        Assert.Equal("Active", Assert.Single(unchanged).Lease!.State);
        await Assert.ThrowsAsync<ExecutionRequestOwnershipException>(() => restarted.ReportExecutionAsync(execution.Id, new WorkerExecutionReport(
            acquired.WorkerId, acquired.AssignmentId, "old-run", "Running", "Codex", Generation: acquired.Lease.Generation + 1)));

        var report = new WorkerExecutionReport(acquired.WorkerId, acquired.AssignmentId, "lease-run", "Completed",
            CompletedAtUtc: clock.GetUtcNow(), Generation: acquired.Lease!.Generation);
        var completed = await restarted.ReportExecutionAsync(execution.Id, report);
        Assert.Equal("Released", completed!.Lease!.State);
        Assert.Equal(acquired.WorkerId, completed.Lease.WorkerId);
        Assert.Equal(1L, completed.Lease.Generation);
        Assert.Equal(1L, await LeaseCountAsync(database, execution.Id));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(CodexProvisioning.ManagedIntegrationResults.RebaseConflict, true)]
    [InlineData(null, false)]
    public async Task FirstAttemptRebaseConflictQueuesExactlyOneFreshAttempt(string? integrationResult, bool requeued)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "rebase-conflict.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Conflict", "team/conflict", "main", "", []));
        var worker = Guid.NewGuid().ToString("N");
        WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, worker, worker, "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, worker, "1.0", "running", 0, 1, capabilities, []));
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "7")));
        var request = new WorkerAssignmentRequest(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 });

        async Task<ExecutionRequest> FailWithConflictAsync(string executionId)
        {
            var assignment = (await store.RequestAssignmentAsync(request)).Assignment!;
            Assert.Equal(executionId, assignment.ServerExecutionId);
            return (await store.ReportExecutionAsync(executionId, new WorkerExecutionReport(worker, assignment.AssignmentId,
                Guid.NewGuid().ToString(), "Failed", CompletedAtUtc: clock.GetUtcNow(), IntegrationResult: integrationResult,
                FailureClassification: "IntegrationConflict", Recoverable: true, Generation: assignment.Lease!.Generation)))!;
        }

        var failed = await FailWithConflictAsync(first.Id);
        var queued = (await store.GetExecutionsAsync()).Where(item => item.State == "Queued").ToArray();
        if (!requeued)
        {
            Assert.Null(failed.RecoveryState);
            Assert.Empty(queued);
            return;
        }
        Assert.Equal("RebaseConflictRequeued", failed.RecoveryState);
        var retry = Assert.Single(queued);
        Assert.Equal(first.Id, retry.RetryOfExecutionId);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.Equal(first.WorkReference, retry.WorkReference);
        Assert.Equal("FreshWorkspaceRequired", retry.WorkspaceRecovery);

        // The second attempt conflicts too: it stays failed for review and nothing else is queued.
        var second = await FailWithConflictAsync(retry.Id);
        Assert.Null(second.RecoveryState);
        Assert.DoesNotContain(await store.GetExecutionsAsync(), item => item.State == "Queued");
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task PrioritizedQueuedRequestsAreAssignedFirst()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "priority.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Priority", "team/priority", "main", "", []));
        var worker = Guid.NewGuid().ToString("N");
        WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, worker, worker, "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, worker, "1.0", "running", 0, 1, capabilities, []));
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var second = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var third = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3")));

        var prioritized = await store.PrioritizeQueuedExecutionAsync(third.Id);
        Assert.Equal(clock.GetUtcNow(), prioritized?.PrioritizedAtUtc);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.PrioritizeQueuedExecutionAsync(second.Id);
        Assert.Null(await store.PrioritizeQueuedExecutionAsync("missing"));

        var request = new WorkerAssignmentRequest(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        var assignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        Assert.Equal(second.Id, assignment.ServerExecutionId);
        await Assert.ThrowsAsync<ExecutionRequestPriorityException>(() => store.PrioritizeQueuedExecutionAsync(second.Id));
        Assert.Null((await store.GetExecutionAsync(first.Id))?.PrioritizedAtUtc);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("PreparationFailed", true)]
    [InlineData("Blocked", false)]
    public async Task PreparationRefusalQueuesExactlyOneAutomaticAttempt(string classification, bool requeued)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "preparation.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Prepare", "team/prepare", "main", "", []));
        var worker = Guid.NewGuid().ToString("N");
        WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, worker, worker, "1.0", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, worker, "1.0", "running", 0, 1, capabilities, []));
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "9")));
        var request = new WorkerAssignmentRequest(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 });

        async Task<ExecutionRequest> RefuseAsync(string executionId)
        {
            var assignment = (await store.RequestAssignmentAsync(request)).Assignment!;
            Assert.Equal(executionId, assignment.ServerExecutionId);
            return (await store.ReportExecutionAsync(executionId, new WorkerExecutionReport(worker, assignment.AssignmentId,
                Guid.NewGuid().ToString(), "Failed", CompletedAtUtc: clock.GetUtcNow(), FailureClassification: classification,
                Generation: assignment.Lease!.Generation)))!;
        }

        var failed = await RefuseAsync(first.Id);
        var queued = (await store.GetExecutionsAsync()).Where(item => item.State == "Queued").ToArray();
        if (!requeued)
        {
            Assert.Null(failed.RecoveryState);
            Assert.Empty(queued);
            return;
        }
        Assert.Equal("PreparationRequeued", failed.RecoveryState);
        var retry = Assert.Single(queued);
        Assert.Equal((first.Id, 2, first.WorkReference), (retry.RetryOfExecutionId, retry.AttemptNumber, retry.WorkReference));

        // A second refusal in the same chain stays failed for the operator.
        var second = await RefuseAsync(retry.Id);
        Assert.Null(second.RecoveryState);
        Assert.DoesNotContain(await store.GetExecutionsAsync(), item => item.State == "Queued");
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task LeaseRenewalIsGenerationAndOwnerBoundAndExpiryCreatesSafeRetry()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "lease-renewal.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-04-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 120, leaseRenewalIntervalSeconds: 20);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Renewal", "team/renewal", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        foreach (var worker in new[] { owner, other })
        {
            WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, worker, worker, "1.0", "test", 2, capabilities));
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, worker, "1.0", "running", 0, 2, capabilities, []));
        }
        var first = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var second = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3")));
        WorkerAssignmentRequest Request(string worker) => new(worker, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var firstAssignment = (await store.RequestAssignmentAsync(Request(owner))).Assignment!;
        var secondAssignment = (await store.RequestAssignmentAsync(Request(owner))).Assignment!;
        Assert.Equal(3, (await store.GetExecutionsAsync()).Count); // both leases and the queued request persist across reads
        var lease = firstAssignment.Lease!;
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), lease.ExpiresAtUtc);
        Assert.Null(await store.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(other, lease.Generation)));
        Assert.Null(await store.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, lease.Generation + 1)));
        clock.Advance(TimeSpan.FromSeconds(30)); // a missed scheduled renewal still leaves ample safety margin
        var renewed = await store.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, lease.Generation));
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), renewed!.ExpiresAtUtc);

        var restarted = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 120, leaseRenewalIntervalSeconds: 20);
        await restarted.InitializeAsync();
        var afterRestart = await restarted.GetExecutionsAsync();
        Assert.Equal(renewed.ExpiresAtUtc, afterRestart.Single(x => x.Id == first.Id).Lease!.ExpiresAtUtc);
        Assert.Equal("Active", afterRestart.Single(x => x.Id == second.Id).Lease!.State);

        clock.Advance(TimeSpan.FromSeconds(91));
        var expired = await restarted.GetExecutionsAsync();
        Assert.Equal("Expired", expired.Single(x => x.Id == second.Id).Lease!.State);
        var expiredAttempt = expired.Single(x => x.Id == second.Id);
        Assert.Equal("Failed", expiredAttempt.State);
        Assert.Equal("LeaseExpiredRequeued", expiredAttempt.RecoveryState);
        var retry = Assert.Single(expired, x => x.RetryOfExecutionId == second.Id);
        Assert.Equal("Queued", retry.State);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.Null(await restarted.RenewExecutionLeaseAsync(second.Id,
            new ExecutionLeaseRenewal(owner, secondAssignment.Lease!.Generation)));
        Assert.Null(await restarted.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, 0)));
        await restarted.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, other, "1.0", "running", 0, 2,
            [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)], []));
        var laterAssignment = (await restarted.RequestAssignmentAsync(Request(other))).Assignment;
        Assert.Equal(queued.Id, laterAssignment!.ServerExecutionId);
        Assert.Equal("Failed", (await restarted.GetExecutionsAsync()).Single(x => x.Id == second.Id).State);

        var final = await restarted.ReportExecutionAsync(first.Id, new WorkerExecutionReport(owner,
            firstAssignment.AssignmentId, "run-renew-1", "Completed", CompletedAtUtc: clock.GetUtcNow(), Generation: firstAssignment.Lease!.Generation));
        Assert.Equal("Released", final!.Lease!.State);
        Assert.Null(await restarted.RenewExecutionLeaseAsync(first.Id, new ExecutionLeaseRenewal(owner, lease.Generation)));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExpiredLeaseReconciliationCreatesLinkedAttemptOnlyBeforeIntegrationAndIsIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "reconciliation.db");
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-05-01T00:00:00Z"));
        var store = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 10, leaseRenewalIntervalSeconds: 2);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery", "team/recovery", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        WorkerCapability[] capabilities = [new("tool", "git"), .. AuthenticationCapabilities(project.Repository)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 6, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 6, capabilities, []));
        var safe = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "1")));
        var implementing = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "2")));
        var validating = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "3")));
        var claiming = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "4")));
        var uncertain = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "5")));
        var reporting = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "6")));
        WorkerAssignmentRequest request = new(workerId, true, 6, new Dictionary<string, int> { [project.Id] = 6 });
        var safeAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var implementingAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var validatingAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var claimingAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var uncertainAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        var reportingAssignment = (await store.RequestAssignmentAsync(request)).Assignment!;
        await store.ReportExecutionAsync(implementing.Id, new WorkerExecutionReport(workerId, implementingAssignment.AssignmentId,
            "worker-implementation", "Running", Stage: "Codex", StartedAtUtc: clock.GetUtcNow(), Generation: implementingAssignment.Lease!.Generation));
        await store.ReportExecutionAsync(validating.Id, new WorkerExecutionReport(workerId, validatingAssignment.AssignmentId,
            "worker-validation", "Running", Stage: "Validation", StartedAtUtc: clock.GetUtcNow(), Generation: validatingAssignment.Lease!.Generation));
        await store.ReportExecutionAsync(uncertain.Id, new WorkerExecutionReport(workerId, uncertainAssignment.AssignmentId,
            "worker-execution", "Running", Stage: "Integration", StartedAtUtc: clock.GetUtcNow(), Generation: uncertainAssignment.Lease!.Generation));
        await store.ReportExecutionAsync(claiming.Id, new WorkerExecutionReport(workerId, claimingAssignment.AssignmentId,
            "worker-claiming", "Running", Stage: "Claiming", StartedAtUtc: clock.GetUtcNow(), Generation: claimingAssignment.Lease!.Generation));
        await store.ReportExecutionAsync(reporting.Id, new WorkerExecutionReport(workerId, reportingAssignment.AssignmentId,
            "worker-reporting", "Running", Stage: "Reporting", StartedAtUtc: clock.GetUtcNow(), Generation: reportingAssignment.Lease!.Generation));

        clock.Advance(TimeSpan.FromSeconds(11));
        var afterRestart = new SqliteRegistryStore(database, timeProvider: clock, leaseDurationSeconds: 10, leaseRenewalIntervalSeconds: 2);
        await afterRestart.InitializeAsync(); // reconciliation also runs after a Server restart
        var firstRead = await afterRestart.GetExecutionsAsync();
        var safeOriginal = firstRead.Single(x => x.Id == safe.Id);
        var safeRetry = Assert.Single(firstRead, x => x.RetryOfExecutionId == safe.Id);
        Assert.Equal("LeaseExpiredRequeued", safeOriginal.RecoveryState);
        Assert.Equal("Failed", safeOriginal.State);
        Assert.Equal("Queued", safeRetry.State);
        Assert.Equal(2, safeRetry.AttemptNumber);
        Assert.Equal(safe.Id, safeRetry.RetryOfExecutionId);
        Assert.Equal(workerId, safeOriginal.Lease!.WorkerId);
        Assert.Equal("PreviousWorkerLocalStateUnknown", safeOriginal.WorkspaceRecovery);
        Assert.Equal("FreshWorkspaceRequired", safeRetry.WorkspaceRecovery);
        Assert.Equal("LeaseExpiredRequeued", firstRead.Single(x => x.Id == implementing.Id).RecoveryState);
        Assert.Single(firstRead, x => x.RetryOfExecutionId == implementing.Id);
        var validatingOriginal = firstRead.Single(x => x.Id == validating.Id);
        Assert.Equal("LeaseExpiredRequeued", validatingOriginal.RecoveryState);
        Assert.Single(firstRead, x => x.RetryOfExecutionId == validating.Id);

        var uncertainResult = firstRead.Single(x => x.Id == uncertain.Id);
        Assert.Equal("LeaseExpiredUncertain", uncertainResult.RecoveryState);
        Assert.Contains("Integration", uncertainResult.RecoveryReason);
        Assert.DoesNotContain(firstRead, x => x.RetryOfExecutionId == uncertain.Id);
        // Claiming precedes any implementation or integration, so a new attempt is safe; its claim re-verifies the Issue.
        Assert.Equal("LeaseExpiredRequeued", firstRead.Single(x => x.Id == claiming.Id).RecoveryState);
        Assert.Single(firstRead, x => x.RetryOfExecutionId == claiming.Id);
        Assert.Equal("LeaseExpiredUncertain", firstRead.Single(x => x.Id == reporting.Id).RecoveryState);
        Assert.DoesNotContain(firstRead, x => x.RetryOfExecutionId == reporting.Id);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => afterRestart.EnqueueExecutionAsync(
            new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "5"))));

        var verifiedRetry = await afterRestart.ReconcileUncertainExecutionAsync(uncertain.Id,
            new ExecutionReconciliationRequest("NotIntegrated", "Checked authoritative base branch and confirmed the execution commit is absent."));
        Assert.NotNull(verifiedRetry);
        Assert.Equal("Failed", verifiedRetry.Execution.State);
        Assert.Equal("OperatorRetryQueued", verifiedRetry.Execution.RecoveryState);
        Assert.Equal(uncertain.Id, verifiedRetry.Retry!.RetryOfExecutionId);
        Assert.Equal(2, verifiedRetry.Retry.AttemptNumber);
        Assert.Equal("Queued", verifiedRetry.Retry.State);
        await Assert.ThrowsAsync<ExecutionRequestReconciliationException>(() => afterRestart.ReconcileUncertainExecutionAsync(uncertain.Id,
            new ExecutionReconciliationRequest("NotIntegrated", "Duplicate operator request.")));

        var verifiedIntegrated = await afterRestart.ReconcileUncertainExecutionAsync(reporting.Id,
            new ExecutionReconciliationRequest("Integrated", "Confirmed the commit on the protected base branch.", new string('a', 40)));
        Assert.NotNull(verifiedIntegrated);
        Assert.Null(verifiedIntegrated.Retry);
        Assert.Equal("Failed", verifiedIntegrated.Execution.State);
        Assert.Equal("OperatorVerifiedIntegrated", verifiedIntegrated.Execution.RecoveryState);
        Assert.Contains(new string('a', 40), Assert.IsType<string>(verifiedIntegrated.Execution.IntegrationResult));
        await Assert.ThrowsAsync<ExecutionRequestOwnershipException>(() => afterRestart.ReportExecutionAsync(uncertain.Id,
            new WorkerExecutionReport(workerId, uncertainAssignment.AssignmentId, "late-worker-report", "Completed",
                CompletedAtUtc: clock.GetUtcNow(), Generation: uncertainAssignment.Lease!.Generation)));

        var secondRead = await afterRestart.GetExecutionsAsync();
        Assert.Equal(11, secondRead.Count);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == safe.Id);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == implementing.Id);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == validating.Id);
        Assert.Single(secondRead, x => x.RetryOfExecutionId == uncertain.Id);
        Assert.Single(secondRead, x => x.Id == uncertain.Id && x.RecoveryState == "OperatorRetryQueued");
        Assert.Single(secondRead, x => x.RetryOfExecutionId == claiming.Id);
        Assert.Single(secondRead, x => x.Id == reporting.Id && x.RecoveryState == "OperatorVerifiedIntegrated");
    }

    private static async Task<long> LeaseCountAsync(string database, string executionId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM execution_leases WHERE execution_id=$id;";
        command.Parameters.AddWithValue("$id", executionId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Fact]
    public async Task WorkerAssignmentRequestsRespectEligibilityCapacityAndRetainOwnershipAfterRestart()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "assignments.db");
        var url = $"http://127.0.0.1:{ReservePort()}";
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", "assignment-test-token");
        var workerA = Guid.NewGuid().ToString("N");
        var workerB = Guid.NewGuid().ToString("N");
        var firstAssignmentId = "";
        var alphaId = "";
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, database), githubReadService: new AlwaysEligibleServerGitHubReadService());
            await app.StartAsync();
            var store = app.Services.GetRequiredService<IRegistryStore>();
            var alphaDefinition = new CentralProjectDefinition("Alpha", "team/alpha", "main", "", [], MaxParallelTasks: 1);
            var alpha = await store.CreateProjectAsync(alphaDefinition);
            var beta = await store.CreateProjectAsync(new CentralProjectDefinition("Beta", "team/beta", "main", "", []));
            WorkerCapability[] Capabilities(string id) => id == workerA
                ? [new("tool", "git"), .. AuthenticationCapabilities("team/alpha"), .. AuthenticationCapabilities("team/beta")]
                : [new("tool", "git"), .. AuthenticationCapabilities("team/alpha")];
            alphaId = alpha.Id;
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(alpha.Id, new WorkReference("issue", "1")));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(alpha.Id, new WorkReference("issue", "2")));
            await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(beta.Id, new WorkReference("issue", "3")));
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-assignment-token-" + workerB);
                foreach (var id in new[] { workerA, workerB })
                {
                    var registration = new WorkerRegistrationRequest(1, id, id, "1.0", "test", 2, Capabilities(id));
                    var bootstrap = await store.CreateWorkerBootstrapTokenAsync(TimeSpan.FromMinutes(1));
                    Assert.True(await store.BootstrapWorkerAsync(bootstrap, registration, "individual-assignment-token-" + id));
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-assignment-token-" + id);
                    Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/workers/{id}", registration)).StatusCode);
                }
                async Task Heartbeat(string id, string state)
                {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-assignment-token-" + id);
                    Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/v1/workers/{id}/heartbeat",
                        new WorkerHeartbeatRequest(1, id, "1.0", state, 0, 2, Capabilities(id), []))).StatusCode);
                }
                await Heartbeat(workerA, "running");
                await Heartbeat(workerB, "draining");

                async Task<HttpResponseMessage> Request(string id, bool enabled, int capacity, Dictionary<string, int> projectCapacities)
                {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-assignment-token-" + id);
                    return await client.PostAsJsonAsync($"/api/v1/workers/{id}/assignments/request",
                        new WorkerAssignmentRequest(id, enabled, capacity, projectCapacities));
                }

                using var disabled = await Request(workerA, false, 2, new() { [alpha.Id] = 2 });
                Assert.False((await disabled.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);
                using var draining = await Request(workerB, true, 2, new() { [alpha.Id] = 2 });
                Assert.False((await draining.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);
                using var zeroCapacity = await Request(workerA, true, 0, new() { [alpha.Id] = 2 });
                Assert.False((await zeroCapacity.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);

                using var firstResponse = await Request(workerA, true, 2, new() { [alpha.Id] = 1, [beta.Id] = 1 });
                Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
                var first = (await firstResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!;
                Assert.True(first.HasWork);
                Assert.Equal(workerA, first.Assignment!.WorkerId);
                Assert.Equal(alpha.Id, first.Assignment.Project.Id);
                Assert.Equal(workerA, first.Assignment.Lease!.WorkerId);
                Assert.Equal(first.Assignment.ServerExecutionId, first.Assignment.Lease.ExecutionId);
                Assert.Equal(1L, first.Assignment.Lease.Generation);
                Assert.NotEqual(first.Assignment.AssignmentId, first.Assignment.ServerExecutionId);
                firstAssignmentId = first.Assignment.AssignmentId;

                // The Server-owned project cap skips Alpha while other projects remain eligible.
                using var secondResponse = await Request(workerA, true, 2, new() { [alpha.Id] = 1, [beta.Id] = 1 });
                var second = (await secondResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!;
                Assert.True(second.HasWork);
                Assert.Equal(beta.Id, second.Assignment!.Project.Id);
                var report = new WorkerExecutionReport(workerA, second.Assignment.AssignmentId, "worker-run-2", "Running", "Validation",
                    DateTimeOffset.UtcNow, Generation: second.Assignment.Lease!.Generation);
                using var lifecycle = await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/executions/{second.Assignment.ServerExecutionId}/report", report);
                Assert.Equal(HttpStatusCode.OK, lifecycle.StatusCode);
                report = report with { State = "Failed", Stage = null, CompletedAtUtc = DateTimeOffset.UtcNow,
                    FailureClassification = "TaskFailure", Recoverable = true, Summary = "Validation could not be repaired." };
                using var final = await client.PostAsJsonAsync($"/api/v1/workers/{workerA}/executions/{second.Assignment.ServerExecutionId}/report", report);
                Assert.Equal(HttpStatusCode.OK, final.StatusCode);
                var reported = await final.Content.ReadFromJsonAsync<ExecutionRequest>();
                Assert.Equal("Failed", reported!.State);
                Assert.True(reported.Recoverable);
                Assert.Equal("TaskFailure", reported.FailureClassification);
                await store.UpdateProjectAsync(alpha.Id, alphaDefinition with { MaxParallelTasks = null }, alpha.Revision);
                await Heartbeat(workerB, "running");
                using var otherWorkerResponse = await Request(workerB, true, 2, new() { [alpha.Id] = 1 });
                var otherWorkerAssignment = (await otherWorkerResponse.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!;
                Assert.True(otherWorkerAssignment.HasWork);
                Assert.Equal(workerB, otherWorkerAssignment.Assignment!.WorkerId);
                Assert.Equal(alpha.Id, otherWorkerAssignment.Assignment.Project.Id);

                // The uncertain-delivery case is equivalent to dropping the response: server ownership is already durable.
                var owned = Assert.Single(await store.GetExecutionsAsync(), x => x.AssignmentId == first.Assignment.AssignmentId);
                Assert.Equal("Assigned", owned.State);
                Assert.Equal(workerA, owned.AssignedWorkerId);
                Assert.Equal(first.Assignment.ServerExecutionId, owned.Id);
                await app.StopAsync();
                await app.DisposeAsync();
            }

            await using var restarted = await ServerApplication.BuildAsync(Args(url, database));
            await restarted.StartAsync();
            var restartedStore = restarted.Services.GetRequiredService<IRegistryStore>();
            var retained = Assert.Single(await restartedStore.GetExecutionsAsync(), x => x.AssignmentId == firstAssignmentId);
            Assert.Equal("Assigned", retained.State);
            Assert.Equal(workerA, retained.AssignedWorkerId);
            using (var client = new HttpClient { BaseAddress = new Uri(url) })
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "individual-assignment-token-" + workerB);
                using var noWork = await client.PostAsJsonAsync($"/api/v1/workers/{workerB}/assignments/request",
                    new WorkerAssignmentRequest(workerB, true, 2, new Dictionary<string, int> { [alphaId] = 2 }));
                Assert.False((await noWork.Content.ReadFromJsonAsync<WorkAssignmentResponse>())!.HasWork);
            }
            await restarted.StopAsync();
            await restarted.DisposeAsync();

        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", prior); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Fact]
    public async Task ExecutionQueueApiRequiresManagementTokenAndReturnsStructuredRequests()
    {
        using var temporary = new TemporaryDirectory();
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "execution-test-token");
        var url = $"http://127.0.0.1:{ReservePort()}";
        try
        {
            await using var app = await ServerApplication.BuildAsync(Args(url, Path.Combine(temporary.Path, "server.db")),
                githubReadService: new AlwaysEligibleServerGitHubReadService());
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var projectDefinition = new CentralProjectDefinition("Queue project", "team/queue", "main", "", [new("tool", "docker")]);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "execution-test-token");
            var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", projectDefinition);
            var project = (await projectResponse.Content.ReadFromJsonAsync<CentralProject>())!;
            client.DefaultRequestHeaders.Authorization = null;
            var request = new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "7", "https://github.com/team/queue/issues/7"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/executions", request)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "execution-test-token");
            var created = await client.PostAsJsonAsync("/api/v1/executions", request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var representation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.Equal(project.Id, representation.RootElement.GetProperty("projectId").GetString());
            Assert.Equal("Queued", representation.RootElement.GetProperty("state").GetString());
            Assert.Equal("github-issue", representation.RootElement.GetProperty("workReference").GetProperty("type").GetString());
            Assert.Equal("7", representation.RootElement.GetProperty("workReference").GetProperty("id").GetString());
            Assert.True(representation.RootElement.TryGetProperty("createdAtUtc", out _));
            var executionId = representation.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/executions", request)).StatusCode);
            var list = await client.GetFromJsonAsync<ExecutionRequest[]>("/api/v1/executions");
            var queued = Assert.Single(list!);
            Assert.Equal("Queued", queued.State);
            Assert.Equal("no compatible worker", queued.PendingReason);
            Assert.Contains("requires Docker; capability unavailable", queued.MissingRequirements!);
            Assert.Equal(executionId, (await client.GetFromJsonAsync<ExecutionRequest>($"/api/v1/executions/{executionId}"))!.Id);
            Assert.Single((await client.GetFromJsonAsync<ExecutionRequest[]>($"/api/v1/executions?projectId={project.Id}&state=Queued&limit=1"))!);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/executions?limit=101")).StatusCode);
            Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync($"/api/v1/executions/{executionId}/state",
                new ExecutionStateTransition("Completed"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/executions",
                new EnqueueExecutionRequest(project.Id, new WorkReference("pull-request", "8")))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/executions",
                new EnqueueExecutionRequest(project.Id, new WorkReference("issue", "8", "https://github.com/other/repo/issues/8")))).StatusCode);
            using var cancelled = await client.PostAsync($"/api/v1/executions/{executionId}/cancel", content: null);
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            Assert.Equal("Cancelled", (await cancelled.Content.ReadFromJsonAsync<ExecutionRequest>())!.State);
            using var archive = await client.PostAsJsonAsync("/api/v1/executions/archive", new ExecutionArchiveRequest([executionId], true));
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
            Assert.Equal([executionId], (await archive.Content.ReadFromJsonAsync<ExecutionArchiveResult>())!.Changed);
            Assert.DoesNotContain((await client.GetFromJsonAsync<ExecutionRequest[]>($"/api/v1/executions?projectId={project.Id}"))!, item => item.Id == executionId);
            Assert.Equal(executionId, Assert.Single((await client.GetFromJsonAsync<ExecutionRequest[]>($"/api/v1/executions?projectId={project.Id}&archived=only"))!).Id);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/executions?archived=all")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/executions/archive", new ExecutionArchiveRequest([], true))).StatusCode);
            using var dashboardResponse = await client.GetAsync("/executions");
            Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
            Assert.Equal("text/html", dashboardResponse.Content.Headers.ContentType?.MediaType);
            var dashboard = await dashboardResponse.Content.ReadAsStringAsync();
            Assert.Contains("id=\"dashboard\"", dashboard);
            Assert.Contains("/dashboard-assets/preview/", dashboard);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", prior); }
    }
}
