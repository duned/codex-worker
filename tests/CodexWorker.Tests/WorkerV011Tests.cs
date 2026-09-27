using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerV011Tests
{
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
    }

    [Fact]
    public async Task BlockedRepairStopsLoopAndUsesBlockedWorkflow()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("check", 1, "failure"));
        h.Codex.Repairs.Enqueue(new CodexOutcome("blocked", "Need a decision", [], true, "Which API?"));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Which API?", StringComparison.Ordinal));
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
        Assert.Contains(h.TelegramMessages, message => message.Contains("⚫ CODEX WORKER · DETENIDO", StringComparison.Ordinal));
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
        Assert.Contains(h.TelegramMessages, message => message.Contains("⚫ CODEX WORKER · DETENIDO", StringComparison.Ordinal));
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
        Assert.Contains(h.TelegramMessages, message => message.Contains("🚨 CODEX WORKER · INFRAESTRUCTURA", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("CODEX WORKER · DETENIDO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletingIssueEntersInteractiveIdleAndCancellationCleansSpinner()
    {
        using var h = new Harness(interactive: true);
        h.GitHub.CancelWhenEmpty = false;
        var workerTask = h.Worker.RunAsync(h.Cancellation.Token);

        await WaitForOutputAsync(h.Output, "#17 completed");
        await WaitForOutputAsync(h.Output, "Waiting for work... 00:00");
        h.Cancellation.Cancel();
        await workerTask;

        var output = h.Output.ToString();
        var completed = output.IndexOf("#17 completed", StringComparison.Ordinal);
        var idle = output.IndexOf("Waiting for work...", completed, StringComparison.Ordinal);
        var erased = output.IndexOf("\u001b[2K", idle, StringComparison.Ordinal);
        var stopped = output.IndexOf("■ Worker stopped.", StringComparison.Ordinal);
        Assert.True(completed >= 0 && idle > completed && erased > idle && stopped > erased, output);
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
        using var h = new Harness();
        h.Codex.InitialException = new WorkerInfrastructureException("service authentication failed");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.RunAsync(h.Cancellation.Token));

        Assert.Equal(1, h.GitHub.FindCalls);
        Assert.Contains("ready->working", h.GitHub.Labels);
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Git.Cleanups);
    }

    [Fact]
    public async Task StructuredTaskFailureStillCleansAndReportsFailedIssue()
    {
        using var h = new Harness();
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Implementation could not be completed", [], false, null);

        await h.RunAsync();

        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Validation.Calls);
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

        public Harness(bool interactive = false, bool telegramEnabled = false)
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
                Telegram = new TelegramSettings { Enabled = false },
                Worker = new WorkerSettings { PollingSeconds = 1 }
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
            Worker = new Worker(config, GitHub, Git, Codex, Validation, _telegram, output);
        }

        public IEnumerable<string> TelegramMessages => _telegramHandler?.Messages ?? [];

        public async Task RunAsync() => await Worker.RunAsync(Cancellation.Token);

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

    private sealed class FakeGitHub(List<string> events, CancellationTokenSource cancellation) : IGitHubClient
    {
        private bool _returned;
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
            if (ReturnIssueOnFirstQuery && !_returned) { _returned = true; return Task.FromResult<GitHubIssue?>(Issue); }
            // End the polling loop without waiting; no real GitHub service is involved.
            if (CancelWhenEmpty) cancellation.Cancel();
            return Task.FromResult<GitHubIssue?>(null);
        }

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
        public int Started { get; private set; }
        public int Cleanups { get; private set; }
        public int Integrations { get; private set; }
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartIssueAsync(GitHubIssue issue, CancellationToken ct) { Started++; return Task.CompletedTask; }
        public Task VerifyCodexStateAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DiscardUncommittedIssueChangesAsync(CancellationToken ct) { Cleanups++; return Task.CompletedTask; }
        public Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue, CancellationToken ct)
        { Integrations++; return Task.FromResult(new GitIntegrationResult(true, "integrated")); }
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
            InitialException is null ? Task.FromResult(InitialOutcome) : Task.FromException<CodexOutcome>(InitialException);
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
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : ValidationResult.Success);
        }
    }
}
