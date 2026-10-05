namespace CodexWorker.Tests;

using CodexServer;
using Microsoft.Extensions.Logging;
using WorkerCapability = CodexServer.WorkerCapability;

public sealed class ServerOperationalDiagnosticsTests
{
    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public async Task QueueAssignmentAndReportsCarryCorrelationWithoutPayloadsOrPollNoise(string outcome)
    {
        using var fixture = new Fixture();
        var (project, worker) = await fixture.InitializeAsync();
        var store = fixture.Store;
        var execution = await store.EnqueueExecutionAsync(new(project.Id, new("issue", "001")));
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => store.EnqueueExecutionAsync(new(project.Id, new("github-issue", "1"))));
        var assignment = Assert.IsType<WorkAssignment>((await store.RequestAssignmentAsync(Request(project.Id, worker))).Assignment);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        var beforeRenewal = fixture.Logger.Events.Count;
        Assert.NotNull(await store.RenewExecutionLeaseAsync(execution.Id, new(worker, lease.Generation)));
        Assert.Equal(beforeRenewal, fixture.Logger.Events.Count);
        var report = new WorkerExecutionReport(worker, assignment.AssignmentId, "local-run", "Running", "Codex",
            Summary: "secret=private-summary", ValidationResult: "raw process output", Generation: lease.Generation);
        await store.ReportExecutionAsync(execution.Id, report);
        await store.ReportExecutionAsync(execution.Id, report);
        await Assert.ThrowsAsync<ExecutionRequestOwnershipException>(() => store.ReportExecutionAsync(execution.Id, report with { Generation = lease.Generation + 1 }));
        var completed = report with { State = outcome, FailureClassification = "secret=private-classification" };
        await store.ReportExecutionAsync(execution.Id, completed);
        await store.ReportExecutionAsync(execution.Id, completed);
        await store.RequestAssignmentAsync(Request(project.Id, worker));
        var count = fixture.Logger.Events.Count;
        await store.ExpireLeasesAsync();
        await store.ExpireLeasesAsync();
        Assert.Equal(count, fixture.Logger.Events.Count);

        var acquired = Assert.Single(fixture.Logger.Events, e => e.Reason == "lease-acquired");
        Assert.Equal(project.Id, acquired.Fields["ProjectId"]);
        Assert.Equal("https://github.com/team/project/issues/1", acquired.Fields["WorkReference"]);
        Assert.Equal(execution.Id, acquired.Fields["ExecutionId"]);
        Assert.Equal(worker, acquired.Fields["WorkerId"]);
        Assert.Equal(assignment.AssignmentId, acquired.Fields["AssignmentId"]);
        Assert.Equal(lease.Generation, acquired.Fields["LeaseGeneration"]);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "duplicate-active-work" && e.Level == LogLevel.Information);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "ownership-or-stage-rejected" && e.Fields["ProjectId"] as string == project.Id);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "outcome-" + outcome.ToLowerInvariant() && e.Level == LogLevel.Information && e.Fields["WorkerExecutionId"] as string == "local-run");
        Assert.Equal(new[] { LogLevel.Information, LogLevel.Debug }, fixture.Logger.Events.Where(e => e.Reason == "stage-Codex").Select(e => e.Level));
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "duplicate-accepted" && e.Level == LogLevel.Debug);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "no-eligible-work" && e.Level == LogLevel.Debug);
        Assert.All(fixture.Logger.Events, e =>
        {
            Assert.Null(e.Exception);
            Assert.DoesNotContain("private-summary", e.Message);
            Assert.DoesNotContain("raw process output", e.Message);
            Assert.DoesNotContain("private-classification", e.Message);
        });
    }

    [Theory]
    [InlineData("Codex", "expired-requeued")]
    [InlineData("Integration", "expired-uncertain")]
    public async Task LeaseExpiryIsLoggedOnceAfterCommitWithRecoveryDecision(string stage, string reason)
    {
        using var fixture = new Fixture();
        var (project, worker) = await fixture.InitializeAsync();
        var execution = await fixture.Store.EnqueueExecutionAsync(new(project.Id, new("issue", "2")));
        var assignment = Assert.IsType<WorkAssignment>((await fixture.Store.RequestAssignmentAsync(Request(project.Id, worker))).Assignment);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        await fixture.Store.ReportExecutionAsync(execution.Id, new(worker, assignment.AssignmentId, "local-run", "Running", stage, Generation: lease.Generation));
        fixture.Clock.Advance(TimeSpan.FromSeconds(121));
        await fixture.Store.ExpireLeasesAsync();
        await fixture.Store.ExpireLeasesAsync();
        Assert.Null(await fixture.Store.RenewExecutionLeaseAsync(execution.Id, new(worker, lease.Generation)));
        var expired = Assert.Single(fixture.Logger.Events, e => e.Reason == reason);
        Assert.Equal(LogLevel.Warning, expired.Level);
        Assert.Equal(execution.Id, expired.Fields["ExecutionId"]);
        Assert.Equal(worker, expired.Fields["WorkerId"]);
        Assert.Equal(lease.Generation, expired.Fields["LeaseGeneration"]);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "ownership-or-lease-lost" && e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task CancellationIdleAndEligibilityAreOperationalDecisions()
    {
        using var fixture = new Fixture();
        var (project, worker) = await fixture.InitializeAsync();
        await fixture.Store.RequestAssignmentAsync(Request(project.Id, worker));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.RequestAssignmentAsync(Request(project.Id, worker), cancellation.Token));
        var execution = await fixture.Store.EnqueueExecutionAsync(new(project.Id, new("issue", "3")));
        var blocked = new ManagedEligibilityUpdate("blocked", ["secret=private-reason"], fixture.Clock.GetUtcNow());
        await fixture.Store.UpdateManagedEligibilityAsync(execution.Id, blocked);
        await fixture.Store.UpdateManagedEligibilityAsync(execution.Id, blocked);
        Assert.False((await fixture.Store.RequestAssignmentAsync(Request(project.Id, worker))).HasWork);
        await fixture.Store.CancelQueuedExecutionAsync(execution.Id);
        Assert.Single(fixture.Logger.Events, e => e.Reason == "eligibility-blocked");
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "operator-cancelled" && e.Level == LogLevel.Information);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "request-cancelled" && e.Level == LogLevel.Debug);
        Assert.DoesNotContain(fixture.Logger.Events, e => e.Level >= LogLevel.Warning || e.Message.Contains("private-reason", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedInputsAndInfrastructureExceptionsCannotLeakAuthentication()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.EnqueueExecutionAsync(new("token=private-project", new("issue", "1", "https://user:private-password@github.com/team/project/issues/1?token=private-query"))));
        await Assert.ThrowsAsync<IOException>(() => ServerOperationalDiagnostics.RunAsync<int>(fixture.Logger, "enqueue-check",
            () => Task.FromException<int>(new IOException("Bearer private-auth", new Exception("raw environment secret=private-inner"))), CancellationToken.None));
        ServerOperationalDiagnostics.Write(fixture.Logger, LogLevel.Information, "test", "invalid-request",
            workerId: "ghp_private-token", workerExecutionId: new string('x', 201));
        Assert.All(fixture.Logger.Events, e =>
        {
            Assert.DoesNotContain("private-", e.Message);
            Assert.Null(e.Exception);
            Assert.Equal(2201, e.EventId.Id);
        });
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "invalid-request" && e.Level == LogLevel.Information);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "infrastructure-failure" && e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task LiveIneligibleIssueRejectsEnqueueAndReleasesAssignmentWithoutLoggingBody()
    {
        using var fixture = new Fixture();
        var (project, worker) = await fixture.InitializeAsync();
        var logger = new CaptureLogger<ServerGitHubAdministrationService>();
        var service = new ServerGitHubAdministrationService(fixture.Store, new BlockedIssueReads(), logger: logger);
        await Assert.ThrowsAsync<ManagedIssueIneligibleException>(() => service.EnqueueIssueAsync(project.Id,
            new("issue", "1", "https://github.com/team/project/issues/1")));
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
        var rejected = Assert.Single(logger.Events);
        Assert.Equal("issue-ineligible", rejected.Reason);
        Assert.Equal(LogLevel.Information, rejected.Level);
        Assert.Equal(project.Id, rejected.Fields["ProjectId"]);
        Assert.Equal("https://github.com/team/project/issues/1", rejected.Fields["WorkReference"]);
        Assert.DoesNotContain("private-body", rejected.Message);
        var queued = await fixture.Store.EnqueueExecutionAsync(new(project.Id, new("issue", "1")));
        Assert.False((await service.RequestAssignmentAsync(Request(project.Id, worker))).HasWork);
        var execution = Assert.IsType<ExecutionRequest>(await fixture.Store.GetExecutionAsync(queued.Id));
        Assert.Equal("blocked", execution.ManagedEligibilityState);
        Assert.Equal("Released", execution.Lease?.State);
        Assert.Contains(fixture.Logger.Events, e => e.Reason == "eligibility-blocked" && e.Fields["ExecutionId"] as string == queued.Id);
    }

    private sealed class BlockedIssueReads : IServerGitHubReadService
    {
        public Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<GitHubIssueRelationships?> GetIssueRelationshipsAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedGitHubIssue?>(new(issueNumber, "Title", "secret=private-body", "open",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, $"https://github.com/{project.Repository}/issues/{issueNumber}",
                [], [], false, ["Blocked by policy."]));
    }

    [Fact]
    public async Task ProjectDefinitionAndLifecycleChangesUseFixedReasonCodes()
    {
        using var fixture = new Fixture();
        var (project, _) = await fixture.InitializeAsync();
        var updated = Assert.IsType<CentralProject>(await fixture.Store.UpdateProjectAsync(project.Id,
            new("Project", "team/project", "main", "secret=private-description", []), project.Revision));
        var disabled = Assert.IsType<CentralProject>(await fixture.Store.UpdateProjectLifecycleAsync(project.Id, false, updated.Revision));
        var enabled = Assert.IsType<CentralProject>(await fixture.Store.UpdateProjectLifecycleAsync(project.Id, true, disabled.Revision));
        Assert.True(await fixture.Store.RemoveProjectAsync(project.Id, enabled.Revision));
        Assert.Equal(new[] { "created", "definition-updated", "disabled", "enabled", "deleted" },
            fixture.Logger.Events.Select(e => e.Reason));
        Assert.All(fixture.Logger.Events, e =>
        {
            Assert.Equal(project.Id, e.Fields["ProjectId"]);
            Assert.Equal(LogLevel.Information, e.Level);
            Assert.DoesNotContain("private-description", e.Message);
        });
    }

    private static WorkerAssignmentRequest Request(string project, string worker) => new(worker, true, 1,
        new Dictionary<string, int> { [project] = 1 });

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "server-diagnostics-" + Guid.NewGuid().ToString("N"));
        public CaptureLogger<SqliteRegistryStore> Logger { get; } = new();
        public Clock Clock { get; } = new();
        public SqliteRegistryStore Store { get; }
        public Fixture() => Store = new(Path.Combine(_directory, "registry.db"), timeProvider: Clock, leaseDurationSeconds: 120, logger: Logger);
        public async Task<(CentralProject Project, string Worker)> InitializeAsync()
        {
            await Store.InitializeAsync();
            var project = await Store.CreateProjectAsync(new("Project", "team/project", "main", "", []));
            var worker = Guid.NewGuid().ToString("N");
            WorkerCapability[] capabilities = [.. WorkerAuthenticationRequirements.ForRepository(project.Repository).Select(requirement =>
                new WorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope)), new("agent-provider", "codex")];
            await Store.RegisterWorkerAsync(new(1, worker, "worker", "1", "test", 1, capabilities));
            await Store.HeartbeatWorkerAsync(new(1, worker, "1", "running", 0, 1, capabilities, []));
            return (project, worker);
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed record CapturedEvent(LogLevel Level, EventId EventId, string Message,
        IReadOnlyDictionary<string, object?> Fields, Exception? Exception)
    {
        public string? Reason => Fields.GetValueOrDefault("ReasonCode") as string;
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<CapturedEvent> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Events.Add(new(logLevel, eventId, formatter(state, exception),
                (state is IEnumerable<KeyValuePair<string, object?>> fields ? fields : throw new InvalidOperationException("Structured fields are required.")).ToDictionary(item => item.Key, item => item.Value), exception));
    }
}
