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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitManagedConflictRearmsExhaustedBaseOnceWithDurableIdempotencyAndOriginalWorkerLease(bool reconcileParent)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-11-10T16:47:10Z"));
        var path = Path.Combine(temporary.Path, "explicit-conflict.db");
        var store = new SqliteRegistryStore(path, timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery", "team/recovery", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, owner, "worker", "1.0", "test", 2, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, owner, "1.0", "running", 0, 2, capabilities, []));
        var original = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17")));
        var poll = new WorkerAssignmentRequest(owner, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var first = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(poll)).Assignment);
        var local = Guid.NewGuid().ToString();
        await store.ReportExecutionAsync(original.Id, new WorkerExecutionReport(owner, first.AssignmentId, local,
            "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: first.Lease?.Generation ?? 0));
        var candidate = new CodexProvisioning.IntegrationRecoveryCandidate(project.Id, original.Id, local,
            new string('a', 40), PreservedCommit: new string('b', 40));
        poll = poll with { IntegrationRecoveries = [candidate] };
        // A known intent refusal cannot silently authorize automatic recovery either.
        var refusedCandidate = candidate with { RefusalCode = "issue-body-changed",
            RefusalReason = "issue-body-changed: Review the original intent before recovery." };
        Assert.False((await store.RequestAssignmentAsync(poll with { IntegrationRecoveries = [refusedCandidate] })).HasWork);
        Assert.Equal(refusedCandidate.RefusalCode, (await store.InspectManagedConflictAsync(original.Id)).ReasonCode);
        var automatic = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(poll)).Assignment);
        await store.ReportExecutionAsync(automatic.ServerExecutionId, new WorkerExecutionReport(owner, automatic.AssignmentId,
            Guid.NewGuid().ToString(), "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: automatic.Lease?.Generation ?? 0));
        candidate = candidate with { AutomaticRetryExhausted = true };
        if (reconcileParent)
        {
            var proof = new CodexProvisioning.IssueParentProvenance(project.Repository, 17, "original-issue-node",
                clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddSeconds(5), 32,
                [new("native-added-event", clock.GetUtcNow().AddSeconds(-51), true, 32)]);
            var provenance = OperatingSystem.IsLinux()
                ? await IssueParentProvenanceTests.QueryThroughManagedProcessAsync(project.Repository, 17, proof.ExecutionStartedAtUtc)
                : System.Text.Json.JsonSerializer.Serialize(proof);
            candidate = candidate with { ParentProvenance = provenance };
            // Exercise the Worker response contract, rather than injecting only a proof object.
            candidate = Assert.IsType<CodexProvisioning.IntegrationRecoveryCandidate>(
                System.Text.Json.JsonSerializer.Deserialize<CodexProvisioning.IntegrationRecoveryCandidate>(
                    System.Text.Json.JsonSerializer.Serialize(candidate)));
            var late = proof with { Events = [new("late-parent", clock.GetUtcNow().AddSeconds(1), true, 32)] };
            var incomplete = candidate with { ParentProvenance = System.Text.Json.JsonSerializer.Serialize(late) };
            Assert.False((await store.RequestAssignmentAsync(poll with { IntegrationRecoveries = [incomplete] })).HasWork);
            Assert.Equal("issue-parent-history-incomplete", (await store.InspectManagedConflictAsync(original.Id)).ReasonCode);
        }
        poll = poll with { IntegrationRecoveries = [candidate] };
        Assert.False((await store.RequestAssignmentAsync(poll)).HasWork);
        var refusal = candidate with { RefusalCode = "issue-parent-changed",
            RefusalReason = "issue-parent-changed: Review the parent and retained history before recovery." };
        Assert.False((await store.RequestAssignmentAsync(poll with { IntegrationRecoveries = [refusal] })).HasWork);
        var refused = await store.InspectManagedConflictAsync(original.Id);
        Assert.False(refused.Eligible);
        Assert.Equal(refusal.RefusalCode, refused.ReasonCode);
        Assert.StartsWith(refusal.RefusalReason, refused.Reason, StringComparison.Ordinal);
        Assert.Equal(refusal, refused.Candidate);
        Assert.False((await store.RequestAssignmentAsync(poll)).HasWork);
        var preview = await store.InspectManagedConflictAsync(original.Id);
        Assert.True(preview.Eligible, preview.Reason);
        Assert.Equal("1.0", preview.WorkerVersion);
        Assert.Equal(clock.GetUtcNow(), preview.ObservedAtUtc);
        Assert.Equal(candidate.ParentProvenance, preview.Candidate?.ParentProvenance);
        // A newer heartbeat must not lend its build identity to an older observation.
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, owner, "1.1", "running", 0, 2, capabilities, []));
        Assert.Equal("recovery-observation-stale", (await store.InspectManagedConflictAsync(original.Id)).ReasonCode);
        Assert.False((await store.RequestAssignmentAsync(poll with { IntegrationRecoveries = [] })).HasWork);
        Assert.Equal("recovery-observation-missing", (await store.InspectManagedConflictAsync(original.Id)).ReasonCode);
        Assert.False((await store.RequestAssignmentAsync(poll)).HasWork);
        Assert.True((await store.InspectManagedConflictAsync(original.Id)).Eligible);
        var request = new ManagedConflictRetry(Guid.NewGuid().ToString("N"), owner, candidate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RetryManagedConflictAsync(original.Id,
            request with { Candidate = candidate with { PreservedCommit = new string('c', 40) } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RetryManagedConflictAsync(original.Id,
            request with { WorkerId = Guid.NewGuid().ToString("N") }));
        var duplicates = await Task.WhenAll(store.RetryManagedConflictAsync(original.Id, request), store.RetryManagedConflictAsync(original.Id, request));
        var retry = duplicates[0];
        Assert.Equal(retry.Id, duplicates[1].Id);
        Assert.Equal(3, retry.AttemptNumber);
        Assert.Equal(original.Id, retry.RetryOfExecutionId);
        Assert.Equal("Queued", retry.State);
        Assert.Equal(retry.Id, (await store.RetryManagedConflictAsync(original.Id, request)).Id);
        Assert.False((await store.InspectManagedConflictAsync(original.Id)).Eligible);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RetryManagedConflictAsync(original.Id,
            request with { OperationId = Guid.NewGuid().ToString("N") }));
        if (!reconcileParent)
        {
            // Durable requests created before parent provenance existed must retain idempotency.
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT metadata_json FROM execution_metadata WHERE execution_id=$id;";
            command.Parameters.AddWithValue("$id", retry.Id);
            var metadataJson = Assert.IsType<string>(await command.ExecuteScalarAsync());
            var metadata = Assert.IsType<Dictionary<string, string>>(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson));
            metadata["explicitRecoveryCandidate"] = metadata["explicitRecoveryCandidate"].Replace(
                ",\"ParentProvenance\":null", "", StringComparison.Ordinal);
            command.CommandText = "UPDATE execution_metadata SET metadata_json=$metadata WHERE execution_id=$id;";
            command.Parameters.AddWithValue("$metadata", System.Text.Json.JsonSerializer.Serialize(metadata));
            await command.ExecuteNonQueryAsync();
        }
        var restarted = new SqliteRegistryStore(path, timeProvider: clock);
        await restarted.InitializeAsync();
        Assert.Equal(retry.Id, (await restarted.RetryManagedConflictAsync(original.Id, request)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RetryManagedConflictAsync(original.Id,
            request with { WorkerId = Guid.NewGuid().ToString("N") }));
        var assigned = Assert.IsType<WorkAssignment>((await restarted.RequestAssignmentAsync(poll)).Assignment);
        Assert.Equal(retry.Id, assigned.ServerExecutionId);
        Assert.Equal(owner, assigned.WorkerId);
        Assert.NotEqual(first.AssignmentId, assigned.AssignmentId);
        Assert.NotNull(assigned.Lease);
        Assert.Equal("Active", assigned.Lease.State);
        Assert.Equal(request.OperationId, assigned.Metadata["explicitIntegrationRecovery"]);
        Assert.Equal(candidate.PreservedCommit, assigned.Metadata["preservedCommit"]);
        Assert.Equal(candidate.ParentProvenance, assigned.Metadata.GetValueOrDefault("parentProvenance"));
        await restarted.ReportExecutionAsync(retry.Id, new WorkerExecutionReport(owner, assigned.AssignmentId,
            Guid.NewGuid().ToString(), "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: assigned.Lease.Generation));
        // Explicit retry never erases the automatic fingerprint.
        Assert.False((await restarted.RequestAssignmentAsync(poll)).HasWork);
        Assert.True((await restarted.InspectManagedConflictAsync(original.Id)).Eligible);
        clock.Advance(TimeSpan.FromMinutes(2));
        await restarted.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, owner, "1.1", "running", 0, 2, capabilities, []));
        Assert.Equal("recovery-observation-stale", (await restarted.InspectManagedConflictAsync(original.Id)).ReasonCode);
        Assert.Equal(retry.Id, (await restarted.RetryManagedConflictAsync(original.Id, request)).Id);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodexRecoveryRequiresOriginalNodeAndFreshLeaseAndSurvivesExpiry(bool expired)
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-15T00:00:00Z"));
        var store = new SqliteRegistryStore(Path.Combine(temporary.Path, "codex-recovery.db"), timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery project", "team/recovery", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        foreach (var workerId in new[] { owner, other })
        {
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 2, capabilities));
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 2, capabilities, []));
        }
        var queued = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17")));
        var request = new WorkerAssignmentRequest(owner, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var initial = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(request)).Assignment);
        var localId = Guid.NewGuid().ToString();
        if (expired)
        {
            await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(owner, initial.AssignmentId, localId,
                "Running", Stage: "Codex", Generation: initial.Lease!.Generation));
            clock.Advance(TimeSpan.FromMinutes(16));
            await store.GetExecutionsAsync();
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, owner, "1.0", "running", 0, 2, capabilities, []));
        }
        else
        {
            await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(owner, initial.AssignmentId, localId,
                "Failed", FailureClassification: "CodexInterruption", Recoverable: true, Generation: initial.Lease!.Generation));
            var rejected = await store.RequestAssignmentAsync(request with
            {
                WorkerId = other, IntegrationRecoveries = [new(project.Id, queued.Id, localId, new string('a', 40), "Codex")]
            });
            Assert.False(rejected.HasWork);
        }
        request = request with { IntegrationRecoveries = [new(project.Id, queued.Id, localId, new string('a', 40), "Codex")] };
        var recovery = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(request)).Assignment);
        Assert.Equal(localId, recovery.Metadata["codexRecoveryExecutionId"]);
        Assert.Equal(queued.Id, recovery.Metadata["originalServerExecutionId"]);
        Assert.NotEqual(initial.ServerExecutionId, recovery.ServerExecutionId);
        Assert.NotNull(recovery.Lease);
        Assert.Equal("Active", recovery.Lease.State);
        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        // Even a changed base cannot re-arm the same interrupted attempt.
        await store.ReportExecutionAsync(recovery.ServerExecutionId, new WorkerExecutionReport(owner, recovery.AssignmentId,
            Guid.NewGuid().ToString(), "Failed", FailureClassification: "CodexInterruption", Recoverable: true, Generation: recovery.Lease.Generation));
        Assert.False((await store.RequestAssignmentAsync(request with
        { IntegrationRecoveries = [new(project.Id, queued.Id, localId, new string('b', 40), "Codex")] })).HasWork);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RecoveryAssignmentsAreNodeBoundIdempotentAndExhaustedBaseDoesNotRearmAfterRestart()
    {
        using var temporary = new TemporaryDirectory();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-15T00:00:00Z"));
        var path = Path.Combine(temporary.Path, "integration-recovery.db");
        var store = new SqliteRegistryStore(path, timeProvider: clock);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery project", "team/recovery", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var competitor = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        foreach (var workerId in new[] { owner, competitor })
        {
            await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "worker", "1.0", "test", 2, capabilities));
            await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "1.0", "running", 0, 2, capabilities, []));
        }
        var queued = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17")));
        var request = new WorkerAssignmentRequest(owner, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var initial = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(initial);
        var localId = Guid.NewGuid().ToString();
        await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(owner, initial.AssignmentId, localId,
            "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: initial.Lease!.Generation));
        request = request with { IntegrationRecoveries = [new(project.Id, queued.Id, localId, new string('a', 40))] };
        var rejected = await store.RequestAssignmentAsync(request with { WorkerId = competitor });
        Assert.False(rejected.HasWork);
        Assert.Contains("local execution identity", rejected.IntegrationRecoveryRejections?[localId]);
        var recovery = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(recovery);
        Assert.Equal(localId, recovery.Metadata["integrationRecoveryExecutionId"]);
        Assert.False((await store.RequestAssignmentAsync(request)).HasWork);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17"))));
        await store.ReportExecutionAsync(recovery.ServerExecutionId, new WorkerExecutionReport(owner, recovery.AssignmentId,
            Guid.NewGuid().ToString(), "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: recovery.Lease!.Generation));
        var restarted = new SqliteRegistryStore(path, timeProvider: clock);
        await restarted.InitializeAsync();
        Assert.False((await restarted.RequestAssignmentAsync(request)).HasWork);
        var changed = request with { IntegrationRecoveries = [new(project.Id, queued.Id, localId, new string('b', 40))] };
        var rearmed = (await restarted.RequestAssignmentAsync(changed)).Assignment;
        Assert.NotNull(rearmed);
        Assert.NotEqual(recovery.ServerExecutionId, rearmed.ServerExecutionId);
        Assert.Equal(3, (await restarted.GetExecutionAsync(rearmed.ServerExecutionId))?.AttemptNumber);
        // A restart in recovery cannot enqueue a fresh implementation after lease expiry.
        await restarted.ReportExecutionAsync(rearmed.ServerExecutionId, new WorkerExecutionReport(owner, rearmed.AssignmentId,
            Guid.NewGuid().ToString(), "Running", Stage: "Preparing", Generation: rearmed.Lease!.Generation));
        clock.Advance(TimeSpan.FromMinutes(16));
        var rows = await restarted.GetExecutionsAsync();
        Assert.DoesNotContain(rows, entry => entry.State == "Queued");
        Assert.Equal("LeaseExpiredUncertain", rows.Single(entry => entry.Id == rearmed.ServerExecutionId).RecoveryState);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrCorruptRecoveryAssignmentMetadataPreservesReservationAndSchedulesOtherIssues(bool missing)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "corrupt-recovery.db");
        var store = new SqliteRegistryStore(path);
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Recovery project", "team/recovery", "main", "", []));
        var owner = Guid.NewGuid().ToString("N");
        var capabilities = AuthenticationCapabilities(project.Repository);
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, owner, "worker", "1.0", "test", 2, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, owner, "1.0", "running", 0, 2, capabilities, []));
        var original = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "17")));
        var request = new WorkerAssignmentRequest(owner, true, 2, new Dictionary<string, int> { [project.Id] = 2 });
        var initial = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(initial);
        var localId = Guid.NewGuid().ToString();
        await store.ReportExecutionAsync(original.Id, new WorkerExecutionReport(owner, initial.AssignmentId, localId,
            "Failed", FailureClassification: "IntegrationConflict", Recoverable: true, Generation: initial.Lease!.Generation));
        request = request with { IntegrationRecoveries = [new(project.Id, original.Id, localId, new string('a', 40))] };
        var recovery = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.NotNull(recovery);
        await store.RejectManagedAssignmentAsync(recovery.ServerExecutionId, recovery.AssignmentId, owner,
            new("unavailable", ["Simulated read interruption"], DateTimeOffset.UtcNow));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = missing ? "DELETE FROM execution_metadata WHERE execution_id=$id" :
                "UPDATE execution_metadata SET metadata_json='{invalid' WHERE execution_id=$id";
            command.Parameters.AddWithValue("$id", recovery.ServerExecutionId);
            await command.ExecuteNonQueryAsync();
        }
        var other = await store.EnqueueExecutionAsync(new(project.Id, new WorkReference("issue", "18")));
        var next = (await store.RequestAssignmentAsync(request)).Assignment;
        Assert.Equal(other.Id, next?.ServerExecutionId);
        var retained = await store.GetExecutionAsync(recovery.ServerExecutionId);
        Assert.NotNull(retained);
        Assert.Equal("Queued", retained.State);
        Assert.Equal("blocked", retained.ManagedEligibilityState);
        Assert.Contains("ownership metadata", Assert.Single(retained.ManagedEligibilityReasons!));
    }
}
