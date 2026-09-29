using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed class WorkerV011Tests
{
    [Fact]
    public async Task ServerAssignmentCreatesLinkedExecutionAndUsesNormalRunnerAndGitHubLifecycle()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var now = DateTimeOffset.UtcNow;
        var assignment = new WorkerAssignmentContract("assignment-456", "server-request-123",
            new ServerProjectContract("test-project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
            new ServerWorkReferenceContract("github-issue", "17"), "worker-id", new Dictionary<string, string>());

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
    }

    [Fact]
    public async Task QueuedServerWorkRunsThroughWorkerPipelineAndReportsTerminalState()
    {
        using var directory = new TempHistoryDatabase();
        var store = new SqliteRegistryStore(Path.Combine(Path.GetDirectoryName(directory.Path)!, "server.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync(new CentralProjectDefinition("Test Project", "owner/repo", "main", "", []));
        var workerId = Guid.NewGuid().ToString("N");
        await store.RegisterWorkerAsync(new WorkerRegistrationRequest(1, workerId, "test worker", "test", "test", 1, ["git"]));
        await store.HeartbeatWorkerAsync(new WorkerHeartbeatRequest(1, workerId, "test", "running", 0, 1, ["git"], []));
        var queued = await store.EnqueueExecutionAsync(new EnqueueExecutionRequest(project.Id,
            new WorkReference("github-issue", "17")));
        var assignmentResponse = await store.RequestAssignmentAsync(new WorkerAssignmentRequest(workerId, true, 1,
            new Dictionary<string, int> { [project.Id] = 1 }));
        var assignment = Assert.IsType<WorkAssignment>(assignmentResponse.Assignment);

        using var history = new ExecutionHistoryStore(Path.Combine(Path.GetDirectoryName(directory.Path)!, "worker.db"));
        using var h = new Harness(history: history);
        var workerAssignment = new WorkerAssignmentContract(assignment.AssignmentId, assignment.ServerExecutionId,
            new ServerProjectContract(project.Id, project.Name, project.Repository, project.DefaultBranch,
                project.Description, project.Requirements, project.Revision, project.CreatedAtUtc, project.UpdatedAtUtc),
            new ServerWorkReferenceContract(assignment.Work.Type, assignment.Work.Id, assignment.Work.Url), workerId,
            assignment.Metadata);

        var execution = await h.Worker.ClaimAssignedAsync(workerAssignment, h.Cancellation.Token);
        Assert.NotNull(execution);
        Assert.NotNull(await execution);
        var workerEntry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(queued.Id, workerEntry.ServerExecutionId);
        Assert.Equal(assignment.AssignmentId, workerEntry.AssignmentId);
        Assert.Equal("Completed", workerEntry.State);

        var reported = await store.ReportExecutionAsync(queued.Id, new WorkerExecutionReport(workerId,
            assignment.AssignmentId, workerEntry.ExecutionId.ToString(), "Completed", StartedAtUtc: workerEntry.StartedAtUtc,
            CompletedAtUtc: workerEntry.CompletedAtUtc, DurationMilliseconds: workerEntry.DurationMilliseconds,
            ValidationResult: workerEntry.ValidationOutcome, IntegrationResult: "passed", Recoverable: false,
            Summary: workerEntry.ImplementationSummary));
        Assert.NotNull(reported);
        Assert.Equal("Completed", reported.State);
        Assert.Equal(workerEntry.ExecutionId.ToString(), reported.WorkerExecutionId);
        Assert.Equal(workerId, reported.AssignedWorkerId);
        Assert.False(reported.Recoverable);
    }

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

    [Fact]
    public async Task InitialValidationSuccessDoesNotInvokeRepair()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Empty(h.Codex.RepairAttempts);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal("preflight", h.Events[0]);
        Assert.Equal("find", h.Events[1]);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("## Implementation", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("## Validation\n\nValidation passed successfully.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulIssuePersistsWorkerLifecycleAndIntegrationFacts()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Validation.Results.Enqueue(Failure("authoritative-check", 1, "failed once"));
            h.Validation.Results.Enqueue(ValidationResult.Success);
            h.Codex.Repairs.Enqueue(Success("fixed check"));
            await h.RunAsync();
        }

        using var reopened = new ExecutionHistoryStore(database);
        var execution = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("Completed", execution.State);
        Assert.Equal("implemented", execution.ImplementationSummary);
        Assert.Equal("passed", execution.ValidationOutcome);
        Assert.Equal(1, execution.RepairCount);
        Assert.True(Assert.Single(execution.Repairs).PassedAfterRepair);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", execution.CommitSha);
        Assert.Equal("main", execution.IntegrationBranch);
        Assert.Equal("completed/17", execution.CompletedBranch);
        File.Delete(database);
    }

    [Fact]
    public async Task CodexAndValidationUseTheExecutionWorktreeDirectory()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Equal(h.Git.ExecutionDirectory, h.Codex.InitialDirectory);
        Assert.Equal(h.Git.ExecutionDirectory, h.Validation.LastDirectory);
        Assert.NotEqual(h.Worker.Configuration.Project.Directory, h.Codex.InitialDirectory);
    }

    [Fact]
    public async Task InitialValidationFailureThenSuccessfulRepairIsRevalidatedAndIntegrated()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("dotnet test", 1, "restore error"));
        h.Validation.Results.Enqueue(ValidationResult.Success);
        h.Codex.Repairs.Enqueue(Success("fixed restore issue"));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(2, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.Contains("working->done", h.GitHub.Labels);
        var comment = Assert.Single(h.GitHub.Comments);
        Assert.StartsWith("# Example task #17\n\n", comment);
        Assert.Contains("implemented", comment);
        Assert.Contains("fixed restore issue", comment);
        Assert.Contains("Initial validation failed: `dotnet test`.", comment);
        Assert.Contains("Validation passed after repair 1/2.", comment);
        Assert.DoesNotContain("stdout", comment);
    }

    [Fact]
    public async Task SupportsMultipleBoundedRepairAttempts()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("check 1", 1, "first"));
        h.Validation.Results.Enqueue(Failure("check 1", 1, "second"));
        h.Validation.Results.Enqueue(ValidationResult.Success);
        h.Codex.Repairs.Enqueue(Success("repair one"));
        h.Codex.Repairs.Enqueue(Success("repair two"));

        await h.RunAsync();

        Assert.Equal(new[] { 1, 2 }, h.Codex.RepairAttempts);
        Assert.Equal(3, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        var comment = Assert.Single(h.GitHub.Comments);
        Assert.True(comment.IndexOf("repair one", StringComparison.Ordinal) < comment.IndexOf("repair two", StringComparison.Ordinal));
        Assert.Contains("implemented", comment);
    }

    [Fact]
    public async Task ExhaustedRepairAttemptsFailTaskWithoutIntegratingEvenWhenRepairsReportSuccess()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "initial failure"));
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "repair one still fails"));
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "repair two still fails"));
        h.Codex.Repairs.Enqueue(Success("repair one says ready"));
        h.Codex.Repairs.Enqueue(Success("repair two says ready"));

        await h.RunAsync();

        Assert.Equal(new[] { 1, 2 }, h.Codex.RepairAttempts);
        Assert.Equal(3, h.Validation.Calls);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("authoritative check", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("## Implementation attempt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BlockedRepairStopsLoopAndUsesBlockedWorkflow()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Validation.Results.Enqueue(Failure("check", 1, "failure"));
        h.Codex.Repairs.Enqueue(new CodexOutcome("blocked", "Need a decision", [], true, "Which API?"));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Which API?", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("## Work performed", StringComparison.Ordinal));
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Blocked", execution.State);
        Assert.Equal(1, execution.RepairCount);
        Assert.Contains("Which API?", execution.FailureReason);
        File.Delete(database);
    }

    [Fact]
    public async Task FailedRepairStopsLoopAndUsesFailedWorkflow()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("check", 1, "failure"));
        h.Codex.Repairs.Enqueue(new CodexOutcome("failed", "Could not repair", [], false, null));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
    }

    [Fact]
    public async Task PreflightRunsBeforeQueueAndSuccessfulPreflightAllowsClaim()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Equal("preflight", h.Events[0]);
        Assert.Equal("find", h.Events[1]);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.Contains("Codex preflight...", h.Output.ToString());
        Assert.Contains("Codex preflight OK ·", h.Output.ToString());
    }

    [Fact]
    public async Task CancellationWhileSafelyIdleUsesConciseShutdownPresentation()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.ReturnIssueOnFirstQuery = false;

        await h.RunAsync();

        Assert.Contains("○ Waiting for work...", h.Output.ToString());
        Assert.Contains("■ Worker stopped.", h.Output.ToString());
        Assert.DoesNotContain("Infrastructure failure", h.Output.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"⚫ CW {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("INFRAESTRUCTURA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringReadyIssueQueryIsGracefulAndDoesNotThrowInfrastructureFailure()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.CancelDuringQuery = true;

        var exception = await Record.ExceptionAsync(() => h.RunAsync());

        Assert.Null(exception);
        Assert.Contains("■ Worker stopped.", h.Output.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"⚫ CW {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("INFRAESTRUCTURA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringIssueClaimRetainsInfrastructureClassification()
    {
        using var h = new Harness(telegramEnabled: true);
        h.GitHub.CancelDuringClaim = true;

        var exception = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.RunAsync());

        Assert.Contains("may be uncertain", exception.Message);
        Assert.Contains("Infrastructure failure", h.ErrorOutput.ToString());
        Assert.Contains(h.TelegramMessages, message => message.Contains($"🚨 CW {ApplicationVersion.Display} · INFRAESTRUCTURA", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains($"CW {ApplicationVersion.Display} · DETENIDO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletingIssueEntersInteractiveIdleAndCancellationCleansSpinner()
    {
        using var h = new Harness(interactive: true);
        h.GitHub.CancelWhenEmpty = false;
        var workerTask = h.Worker.RunAsync(h.Cancellation.Token);

        await WaitForOutputAsync(h.Output, "Issue · Example task #17 · completed");
        await WaitForOutputAsync(h.Output, "Waiting for work... 00:00");
        h.Cancellation.Cancel();
        await workerTask;

        var output = h.Output.ToString();
        var completed = output.IndexOf("Issue · Example task #17 · completed", StringComparison.Ordinal);
        var idle = output.IndexOf("Waiting for work...", completed, StringComparison.Ordinal);
        var stopped = output.IndexOf("■ Worker stopped.", StringComparison.Ordinal);
        Assert.True(completed >= 0 && idle > completed && stopped > idle, output);
        Assert.Contains("\n■ Worker stopped.", output[idle..]);
        Assert.DoesNotContain("\r\u001b[2K", output[idle..stopped]);
    }

    private static async Task WaitForOutputAsync(StringWriter output, string value)
    {
        for (var i = 0; i < 50; i++)
        {
            if (output.ToString().Contains(value, StringComparison.Ordinal)) return;
            await Task.Delay(100);
        }
        Assert.Contains(value, output.ToString());
    }

    [Fact]
    public async Task FailedPreflightStopsBeforeIssueQueryOrClaim()
    {
        using var h = new Harness();
        h.Codex.PreflightException = new WorkerInfrastructureException("401 unauthorized");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.RunAsync(h.Cancellation.Token));

        Assert.Equal(0, h.GitHub.FindCalls);
        Assert.Empty(h.GitHub.Labels);
        Assert.Equal(0, h.Git.Started);
    }

    [Fact]
    public async Task RuntimeCodexInfrastructureFailureStopsWithoutMarkingIssueFailed()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Codex.InitialException = new WorkerInfrastructureException("service authentication failed");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.RunAsync(h.Cancellation.Token));

        Assert.Equal(1, h.GitHub.FindCalls);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Git.Cleanups);
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", execution.State);
        Assert.Contains("service authentication failed", execution.FailureReason);
        File.Delete(database);
    }

    [Fact]
    public async Task StructuredTaskFailureStillCleansAndReportsFailedIssue()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Implementation could not be completed", [], false, null);

        await h.RunAsync();

        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Validation.Calls);
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Failed", execution.State);
        Assert.Contains("Implementation could not be completed", execution.FailureReason);
        File.Delete(database);
    }

    [Fact]
    public async Task StructuredFailurePersistsRecoverableWorkspaceMetadata()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "2 changed path(s); 0 staged path(s). Workspace retained for recovery.");
            h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial implementation remains", [], false, null);

            await h.RunAsync();

            Assert.Equal(0, h.Git.Cleanups);
            Assert.Equal(0, h.Git.Integrations);
        }

        using var reopened = new ExecutionHistoryStore(database);
        var execution = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("Failed", execution.State);
        Assert.Equal("recoverable", execution.RecoveryState);
        Assert.Equal("base-sha", execution.RecoveryBaseCommit);
        Assert.Contains("2 changed path(s)", execution.RecoveryStatus);
        File.Delete(database);
    }

    [Fact]
    public async Task BlockedExecutionPreservesUsefulWorkspaceForAnExplicitRetry()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "1 changed path(s); 0 staged path(s). Workspace retained for recovery.");
            h.Codex.InitialOutcome = new CodexOutcome("blocked", "Need a product decision", [], false, "Choose a policy.");

            var result = await h.ProcessOneAsync();

            Assert.Equal(IssueOutcomeKind.Blocked, result!.Kind);
            Assert.Equal(0, h.Git.Cleanups);
            var execution = Assert.Single(await history.ReadAllAsync());
            Assert.Equal("Blocked", execution.State);
            Assert.Equal("recoverable", execution.RecoveryState);
        }

        using var reopened = new ExecutionHistoryStore(database);
        Assert.Equal("Blocked", Assert.Single(await reopened.ReadAllAsync()).State);
        File.Delete(database);
    }

    [Fact]
    public async Task ResumedRetryKeepsFailedAttemptAndRunsFreshValidationBeforeIntegration()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Worker.Configuration.Worker.RetryMode = "resume";
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "2 changed path(s); 0 staged path(s). Workspace retained for recovery.");
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial implementation remains", [], false, null);

        var failed = await h.ProcessOneAsync();
        Assert.Equal(IssueOutcomeKind.Failed, failed!.Kind);
        var first = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Failed", first.State);
        h.GitHub.ReadyIssueCount = 2;
        h.Codex.InitialOutcome = Success("Completed the full task");

        var completed = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, completed!.Kind);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        var entries = await history.ReadAllAsync();
        Assert.Equal(2, entries.Count);
        var retry = entries.Single(entry => entry.AttemptNumber == 2);
        Assert.NotEqual(first.ExecutionId, retry.ExecutionId);
        Assert.Equal(first.ExecutionId, retry.RetryOfExecutionId);
        Assert.True(retry.Resumed);
        Assert.Equal("Failed", entries.Single(entry => entry.ExecutionId == first.ExecutionId).State);
        Assert.Equal("Completed", retry.State);
        Assert.True(h.Git.LastResume);
        File.Delete(database);
    }

    private static ValidationResult Failure(string command, int exitCode, string stderr) =>
        new(new ValidationFailure(1, command, exitCode, "useful stdout", stderr, false));

    private static CodexOutcome Success(string summary) => new("success", summary, [], false, null);

    private sealed class Harness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"codex-worker-test-{Guid.NewGuid():N}");
        public CancellationTokenSource Cancellation { get; } = new();
        public List<string> Events { get; } = [];
        public FakeGitHub GitHub { get; }
        public FakeGit Git { get; } = new();
        public FakeCodex Codex { get; }
        public FakeValidation Validation { get; } = new();
        public Worker Worker { get; }
        public StringWriter Output { get; } = new();
        public StringWriter ErrorOutput { get; } = new();
        private readonly TelegramNotifier _telegram;
        private readonly HttpClient? _telegramClient;
        private readonly StubTelegramHandler? _telegramHandler;

        public Harness(bool interactive = false, bool telegramEnabled = false, ExecutionHistoryStore? history = null)
        {
            Directory.CreateDirectory(_directory);
            var instructions = Path.Combine(_directory, "AGENTS.md");
            File.WriteAllText(instructions, "test instructions");
            var config = new WorkerConfiguration
            {
                Project = new ProjectSettings { Name = "Test Project", Repository = "owner/repo", Directory = _directory },
                Git = new GitSettings(),
                GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" },
                Codex = new CodexSettings { InstructionsFile = instructions },
                Validation = new ValidationSettings { Commands = ["authoritative-check"], MaxFixAttempts = 2 },
                Worker = new WorkerSettings()
            };
            GitHub = new FakeGitHub(Events, Cancellation);
            Codex = new FakeCodex(Events);
            var output = new WorkerConsole(Output, interactive, ErrorOutput);
            if (telegramEnabled)
            {
                _telegramHandler = new StubTelegramHandler();
                _telegramClient = new HttpClient(_telegramHandler);
                _telegram = new TelegramNotifier(true, "fake-token", "fake-chat", _telegramClient, output);
            }
            else _telegram = new TelegramNotifier(false, output);
            Worker = new Worker(config, GitHub, Git, Codex, Validation, _telegram, output, history);
        }

        public IEnumerable<string> TelegramMessages => _telegramHandler?.Messages ?? [];

        public async Task RunAsync() => await Worker.RunAsync(Cancellation.Token);
        public Task<IssueProcessingResult?> ProcessOneAsync() => Worker.ProcessOneAsync(Cancellation.Token);

        public void Dispose()
        {
            Cancellation.Dispose();
            _telegram.Dispose();
            _telegramClient?.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }

        private sealed class StubTelegramHandler : HttpMessageHandler
        {
            public List<string> Messages { get; } = [];
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(body);
                Messages.Add(document.RootElement.GetProperty("text").GetString()!);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
        }
    }

    private sealed class TempHistoryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-worker-assignment-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(_directory, "history.db");
        public TempHistoryDatabase() => Directory.CreateDirectory(_directory);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private sealed class FakeGitHub(List<string> events, CancellationTokenSource cancellation) : IGitHubClient
    {
        private int _returned;
        public int ReadyIssueCount { get; set; } = 1;
        public bool ReturnIssueOnFirstQuery { get; set; } = true;
        public bool CancelWhenEmpty { get; set; } = true;
        public bool CancelDuringQuery { get; set; }
        public bool CancelDuringClaim { get; set; }
        public GitHubIssue Issue { get; } = new(17, "Example task", "Implement this request", DateTimeOffset.UtcNow);
        public int FindCalls { get; private set; }
        public List<string> Labels { get; } = [];
        public List<string> Comments { get; } = [];

        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken)
        {
            FindCalls++;
            events.Add("find");
            if (CancelDuringQuery)
            {
                cancellation.Cancel();
                return Task.FromException<GitHubIssue?>(new OperationCanceledException(cancellation.Token));
            }
            if (ReturnIssueOnFirstQuery && _returned < ReadyIssueCount) { _returned++; return Task.FromResult<GitHubIssue?>(Issue); }
            // End the polling loop without waiting; no real GitHub service is involved.
            if (CancelWhenEmpty) cancellation.Cancel();
            return Task.FromResult<GitHubIssue?>(null);
        }

        public Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubIssue?>(issueNumber == Issue.Number ? Issue : null);

        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        {
            Labels.Add($"{remove}->{add}");
            if (CancelDuringClaim && remove == "ready")
            {
                cancellation.Cancel();
                return Task.FromException(new WorkerInfrastructureException("GitHub operation was cancelled; remote Issue state may be uncertain.", new OperationCanceledException(ct)));
            }
            return Task.CompletedTask;
        }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct)
        { Comments.Add(comment); return Task.CompletedTask; }
        public Task CloseAsync(int issueNumber, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGit : IGitRepository
    {
        public string ExecutionDirectory { get; } = Path.Combine(Path.GetTempPath(), "execution-worktree");
        public int Started { get; private set; }
        public int Cleanups { get; private set; }
        public int Integrations { get; private set; }
        public GitRecoveryInfo? Recovery { get; set; }
        public Guid? LastExecutionId { get; private set; }
        public bool LastResume { get; private set; }
        public int LastAttemptNumber { get; private set; }
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, CancellationToken ct) { Started++; LastExecutionId = executionId; return Task.CompletedTask; }
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resume, int attemptNumber, CancellationToken ct)
        { Started++; LastExecutionId = executionId; LastResume = resume; LastAttemptNumber = attemptNumber; return Task.CompletedTask; }
        public Task VerifyCodexStateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DiscardUncommittedIssueChangesAsync(CancellationToken ct) { Cleanups++; return Task.CompletedTask; }
        public Task<GitRecoveryInfo?> PreserveFailedIssueChangesAsync(CancellationToken ct)
        {
            if (Recovery is null) Cleanups++;
            return Task.FromResult(Recovery);
        }
        public Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
            Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct)
        { Integrations++; return Task.FromResult(new GitIntegrationResult(true,
            "Committed as `0123456789ab`. Merged into `main`. Preserved on origin as `completed/17`.",
            "0123456789abcdef0123456789abcdef01234567", "main", "completed/17")); }
    }

    private sealed class FakeCodex(List<string> events) : ICodexExecutor
    {
        public WorkerInfrastructureException? PreflightException { get; set; }
        public Exception? InitialException { get; set; }
        public CodexOutcome InitialOutcome { get; set; } = Success("implemented");
        public Queue<CodexOutcome> Repairs { get; } = new();
        public List<int> RepairAttempts { get; } = [];
        public List<ValidationFailure> RepairFailures { get; } = [];
        public Task PreflightAsync(CancellationToken ct)
        {
            events.Add("preflight");
            return PreflightException is null ? Task.CompletedTask : Task.FromException(PreflightException);
        }
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct) =>
            RunCoreAsync(projectDirectory);
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct) => RunCoreAsync(projectDirectory);
        private Task<CodexOutcome> RunCoreAsync(string projectDirectory)
        {
            InitialDirectory = projectDirectory;
            return InitialException is null ? Task.FromResult(InitialOutcome) : Task.FromException<CodexOutcome>(InitialException);
        }
        public string? InitialDirectory { get; private set; }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct)
        {
            RepairAttempts.Add(attempt);
            RepairFailures.Add(failure);
            return Task.FromResult(Repairs.Dequeue());
        }
    }

    private sealed class FakeValidation : IValidationRunner
    {
        public Queue<ValidationResult> Results { get; } = new();
        public int Calls { get; private set; }
        public string? LastDirectory { get; private set; }
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            Calls++;
            LastDirectory = directory;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : ValidationResult.Success);
        }
    }
}
