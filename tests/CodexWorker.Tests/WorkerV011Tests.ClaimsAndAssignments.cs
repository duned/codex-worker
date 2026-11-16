using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptionImmediatelyAfterValidationIsPersistedReportedAndStopsQueue(bool cancel)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReadyIssueCount = 2;
        h.GitHub.CancelWhenEmpty = false;
        h.Git.VerifyAfterValidation = async ct =>
        {
            Assert.Equal("passed", Assert.Single(await history.ReadAllAsync()).ValidationOutcome);
            // The claimed Issue still occupies worker capacity during the handoff.
            Assert.Null(await h.Worker.ClaimNextAsync(ct));
            Assert.Equal(1, h.Git.Started);
            if (cancel)
            {
                h.Cancellation.Cancel();
                throw new OperationCanceledException(ct);
            }
            throw new WorkerInfrastructureException("verification interrupted after validation");
        };

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.RunAsync());

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.Equal("passed", entry.ValidationOutcome);
        Assert.Equal("uncertain", entry.RecoveryState);
        Assert.NotNull(entry.CompletedAtUtc);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Contains(entry.ExecutionId.ToString(), Assert.Single(h.GitHub.Comments));
        Assert.Contains(h.OperationalMessages, message => message.Contains(entry.ExecutionId.ToString(), StringComparison.Ordinal));
        Assert.Equal(1, h.Git.Started);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Fact]
    public async Task ValidatedExecutionKeepsActiveClaimUntilTerminalReportingFinishes()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.ReadyIssueCount = 3;
        h.GitHub.CancelWhenEmpty = false;
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.GitHub.CommentAction = () =>
        {
            reportStarted.TrySetResult();
            return releaseReport.Task;
        };

        var execution = await h.Worker.ClaimNextAsync(h.Cancellation.Token);

        Assert.NotNull(execution);
        await reportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        Assert.Equal(1, h.Git.Integrations);
        var active = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Reporting", active.State);
        Assert.Equal("passed", active.ValidationOutcome);
        Assert.Null(active.CompletedAtUtc);
        Assert.Null(await h.Worker.ClaimNextAsync(h.Cancellation.Token));
        Assert.Equal(1, h.Git.Started);

        releaseReport.TrySetResult();
        Assert.Equal(IssueOutcomeKind.Succeeded, (await execution)!.Kind);
        Assert.Equal("Completed", Assert.Single(await history.ReadAllAsync()).State);
        h.GitHub.CommentAction = null;
        h.GitHub.ReadyIssueCount++;
        var next = await h.Worker.ClaimNextAsync(h.Cancellation.Token);
        Assert.NotNull(next);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await next)!.Kind);
        Assert.Equal(2, h.Git.Started);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task LaterAttemptSupersedesValidatedExecutionWithExplicitReport()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var laterId = Guid.NewGuid();
        h.Git.VerifyAfterValidation = async _ =>
        {
            var current = Assert.Single(await history.ReadAllAsync());
            await history.CreateAsync(current with
            {
                ExecutionId = laterId, AttemptNumber = 2, State = "Completed",
                CompletedAtUtc = DateTimeOffset.UtcNow
            });
        };

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Superseded, result!.Kind);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains("### Execution superseded", Assert.Single(h.GitHub.Comments));
        var entry = (await history.ReadAllAsync()).Single(row => row.ExecutionId != laterId);
        Assert.Equal("Superseded", entry.State);
        Assert.Equal("passed", entry.ValidationOutcome);
        Assert.Equal("superseded", entry.RecoveryState);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task IncompatibleAssignmentCanRetryCleanlyInResumeModeOnceRequirementsAreRestored()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.GitHub.IssueLabels = ["ready"];
        var now = DateTimeOffset.UtcNow;
        var project = new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now);
        var assignment = new WorkerAssignmentContract("assignment", "server-execution", project,
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-execution", "worker-id", 1, now, now.AddMinutes(5), "Active"));
        var host = new WorkerHost(new GlobalWorkerConfiguration(), []);
        await host.RejectIncompatibleAssignmentAsync(assignment, h.Worker.Configuration, history,
            "Required runtime is missing.", CancellationToken.None);
        Assert.Empty(h.GitHub.Labels);
        Assert.Equal(0, h.Git.Started);

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, result?.Kind);
        var entries = await history.ReadAllAsync();
        var retry = entries.Single(entry => entry.State == "Completed");
        Assert.Equal(2, retry.AttemptNumber);
        Assert.False(retry.Resumed);
        Assert.Null(retry.RetryOfExecutionId);
        Assert.Equal("preparation-failed", entries.Single(entry => entry.State == "Blocked").RecoveryState);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task AssignedUnrecoverableResumeReturnsCompletedRejectionTaskInsteadOfFailingClaim()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        var now = DateTimeOffset.UtcNow;
        await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "Test Project", "owner/repo", 17, "Example task",
            "feature/17-example-task", "main", now.AddMinutes(-5), now,
            "Failed", 1000, "Partial work", null, 0, [], null, null, null, "failed"));
        var assignment = new WorkerAssignmentContract("assignment-rejected", "server-rejected",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-rejected", "worker-id", 1, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);

        Assert.NotNull(execution);
        Assert.Null(await execution!);
        Assert.Equal(0, h.Git.Started);
        Assert.Contains("ready->blocked", h.GitHub.Labels);
        var rejected = (await history.ReadAllAsync()).Single(entry => entry.ServerExecutionId == "server-rejected");
        Assert.Equal("InfrastructureFailure", rejected.State);
        Assert.Equal("assignment-rejected", rejected.AssignmentId);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ServerAssignmentCreatesLinkedExecutionAndUsesNormalRunnerAndGitHubLifecycle()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("assignment-456", "server-request-123",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-request-123", "worker-id", 1, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        var result = await execution;

        Assert.NotNull(result);
        Assert.Equal(1, h.Git.Started);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.Contains("working->done", h.GitHub.Labels);
        var persisted = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("server-request-123", persisted.ServerExecutionId);
        Assert.Equal("assignment-456", persisted.AssignmentId);
        Assert.Equal("Completed", persisted.State);
        Assert.Equal(1, persisted.OwnershipGeneration);
        Assert.DoesNotContain(h.OperationalMessages, message => message.StartsWith("Managed ·", StringComparison.Ordinal));
        Assert.Contains(h.OperationalMessages, message => message.StartsWith("Scheduler · Test Project / #17 claimed", StringComparison.Ordinal));
        var lines = h.Output.ToString().Split(Environment.NewLine);
        var started = Array.FindIndex(lines, line => line.StartsWith("▶ Issue ·", StringComparison.Ordinal));
        Assert.True(started >= 0);
        Assert.Equal("↳ Codex · model unknown (CLI model unavailable) · effort medium", lines[started + 1]);
        Assert.Null(persisted.EffectiveModel);
        Assert.True(persisted.ModelSelectedByCli);
        Assert.Equal("medium", persisted.EffectiveEffort);
        Assert.Contains(lines, line => line.Contains("Codex working OK", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Validation OK", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Integrating OK", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("completed ·", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ManagedPreExecutionFailureLogsCorrelatedRedactedReasonWithoutRunningCodex()
    {
        using var h = new Harness();
        h.Worker.Configuration.Environment.Variables = new Dictionary<string, string> { ["TEST_SECRET"] = "private-test-value" };
        h.Git.StartFailure = new ProjectCheckoutDirtyException("Checkout is dirty: private-test-value token=unsafe-value");
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("assignment-failed", "server-failed",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-failed", "worker-id", 3, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        Assert.Null(await execution);

        Assert.Null(h.Codex.InitialDirectory);
        Assert.Contains(h.OperationalMessages, message => message.Contains("preparation rejected", StringComparison.Ordinal) &&
            message.Contains("assignment assignment-failed · Server execution server-failed · lease generation 3", StringComparison.Ordinal) &&
            message.Contains("Checkout is dirty", StringComparison.Ordinal));
        Assert.DoesNotContain(h.OperationalMessages, message => message.Contains(" · stage ", StringComparison.Ordinal));
        Assert.DoesNotContain("Managed ·", h.Output.ToString(), StringComparison.Ordinal);
        Assert.All(h.OperationalMessages, message =>
        {
            Assert.DoesNotContain("private-test-value", message, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-value", message, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("private-test-value", h.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-value", h.Output.ToString(), StringComparison.Ordinal);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("codex")]
    [InlineData("ccc")]
    public async Task AssignmentCancellationBeforeIntegrationStopsAtTheSharedRepositoryBoundary(string engine)
    {
        if (engine == "ccc" && !OperatingSystem.IsLinux()) return;
        using var cli = new FakeClaudeCli();
        using var h = new Harness(engine: cli.CreateEngine());
        h.GitHub.Issue = h.GitHub.Issue with { Body = $"## Worker\nengine: {engine}" };
        h.Validation.CancelOnCall = 1;
        h.Validation.OnRun = h.Cancellation.Cancel;
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("assignment-fence", "server-fence",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17") { Selection = new(engine) }, "worker-id", new Dictionary<string, string>(),
            new ServerExecutionLeaseContract("server-fence", "worker-id", 4, now, now.AddMinutes(5), "Active"));

        var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => execution!);
        Assert.Equal(0, h.Git.Integrations);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("codex")]
    [InlineData("ccc")]
    public async Task QueuedServerWorkRunsThroughWorkerPipelineAndReportsTerminalState(string engine)
    {
        if (engine == "ccc" && !OperatingSystem.IsLinux()) return;
        using var cli = new FakeClaudeCli();
        using var directory = new TempHistoryDatabase();
        var store = new SqliteRegistryStore(Path.Combine(Path.GetDirectoryName(directory.Path)!, "server.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Test Project", "owner/repo", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        CodexServer.WorkerCapability[] capabilities = [new("tool", "git"),
            .. WorkerAuthenticationRequirements.ForRepository(project.Repository).Select(requirement =>
                new CodexServer.WorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope)),
            new("agent-provider", engine)];
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "test worker", "test", "test", 1, capabilities));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "test", "running", 0, 1, capabilities, []));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id,
            new WorkReference("github-issue", "17") { Selection = new(engine, engine == "ccc" ? "sonnet" : null, "low") }));
        var assignmentResponse = await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }));
        var assignment = Assert.IsType<WorkAssignment>(assignmentResponse.Assignment);

        using var history = new ExecutionHistoryStore(Path.Combine(Path.GetDirectoryName(directory.Path)!, "worker.db"));
        using var h = new Harness(history: history, engine: cli.CreateEngine());
        h.GitHub.Issue = h.GitHub.Issue with { Body = $"## Worker\nengine: {engine}\neffort: low" };
        h.Validation.Results.Enqueue(Failure("check", 1, "repair required"));
        h.Codex.Repairs.Enqueue(Success("repaired"));
        h.Validation.Results.Enqueue(ValidationResult.Success);
        var workerAssignment = new WorkerAssignmentContract(assignment.AssignmentId, assignment.ServerExecutionId,
            new ServerProjectContract(project.Id, project.Name, project.Repository, project.DefaultBranch,
                project.Description, project.Requirements.Select(requirement => new ServerProjectRequirementContract(
                    requirement.Type, requirement.Name, requirement.Version)).ToArray(), project.Revision,
                project.CreatedAtUtc, project.UpdatedAtUtc),
            new ServerWorkReferenceContract(assignment.Work.Type, assignment.Work.Id, assignment.Work.Url) { Selection = assignment.Work.Selection }, workerId,
            assignment.Metadata, new ServerExecutionLeaseContract(assignment.Lease!.ExecutionId, assignment.Lease.WorkerId,
                assignment.Lease.Generation, assignment.Lease.AcquiredAtUtc, assignment.Lease.ExpiresAtUtc, assignment.Lease.State));

        var execution = await h.Worker.ClaimAssignedAsync(workerAssignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        Assert.NotNull(await execution);
        var workerEntry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(queued.Id, workerEntry.ServerExecutionId);
        Assert.Equal(assignment.AssignmentId, workerEntry.AssignmentId);
        Assert.Equal("Completed", workerEntry.State);
        Assert.Equal(engine, workerEntry.Engine);
        Assert.Equal(2, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(engine == "ccc" ? 2 : 0, cli.Invocations);
        if (engine == "ccc") Assert.Empty(h.Codex.Issues);

        var reported = await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(workerId,
            assignment.AssignmentId, workerEntry.ExecutionId.ToString(), "Completed", StartedAtUtc: workerEntry.StartedAtUtc,
            CompletedAtUtc: workerEntry.CompletedAtUtc, DurationMilliseconds: workerEntry.DurationMilliseconds,
            ValidationResult: workerEntry.ValidationOutcome, IntegrationResult: "passed", Recoverable: false,
            Summary: workerEntry.ImplementationSummary, Generation: assignment.Lease.Generation));
        Assert.NotNull(reported);
        Assert.Equal("Completed", reported.State);
        Assert.Equal(workerEntry.ExecutionId.ToString(), reported.WorkerExecutionId);
        Assert.Equal(workerId, reported.AssignedWorkerId);
        Assert.False(reported.Recoverable);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task SchedulerDispatchesExplicitExecutionAndReceivesSafeTerminalOutcome()
    {
        using var h = new Harness();
        h.Codex.InitialOutcome = new CodexOutcome("blocked", "Needs a decision", [], true, "Which API?");

        var result = await h.ProcessOneAsync();

        Assert.NotNull(result);
        Assert.Equal(IssueOutcomeKind.Blocked, result.Kind);
        Assert.NotNull(result.Report.ExecutionId);
        Assert.Equal(result.Report.ExecutionId, h.Git.LastExecutionId);
        Assert.Equal(1, h.Git.Started);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.Contains("working->blocked", h.GitHub.Labels);
    }
}
