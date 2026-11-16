using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    private sealed class CompletionReportHandler : HttpMessageHandler
    {
        internal List<WorkerExecutionReportContract> Reports { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var report = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<WorkerExecutionReportContract>(
                request.Content ?? throw new InvalidOperationException("Missing report"), cancellationToken);
            Reports.Add(report ?? throw new InvalidOperationException("Empty report"));
            return new(System.Net.HttpStatusCode.OK);
        }
    }

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
        public TelegramNotifier Telegram => _telegram;
        public StringWriter Output { get; } = new();
        public StringWriter ErrorOutput { get; } = new();
        public List<string> OperationalMessages { get; } = [];
        private readonly TelegramNotifier _telegram;
        private readonly HttpClient? _telegramClient;
        private readonly StubTelegramHandler? _telegramHandler;

        public Harness(bool interactive = false, bool telegramEnabled = false, ExecutionHistoryStore? history = null, bool gracefulShutdown = false, TimeProvider? timeProvider = null, IImplementationEngine? engine = null)
        {
            Directory.CreateDirectory(_directory);
            Git.ExecutionDirectory = Path.Combine(_directory, "worktree");
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
            Worker = new Worker(config, GitHub, Git, Codex, Validation, _telegram, output, history,
                operationalLog: OperationalMessages.Add, shutdownToken: gracefulShutdown ? Cancellation.Token : default, timeProvider: timeProvider,
                engines: new ImplementationEngineSelector(engine is null ? [] : [engine]));
        }

        public IEnumerable<string> TelegramMessages => _telegramHandler?.Messages ?? [];
        public bool? TelegramFailure { set => _telegramHandler!.Failure = value; }

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
            public bool? Failure { get; set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(body);
                Messages.Add(document.RootElement.GetProperty("text").GetString()!);
                if (Failure == true) throw new HttpRequestException("Simulated Telegram delivery failure");
                return new HttpResponseMessage(Failure == false ? System.Net.HttpStatusCode.BadGateway : System.Net.HttpStatusCode.OK);
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
        public bool CompletionMode { get; set; }
        public bool Closed { get; set; }
        public int CloseCalls { get; private set; }
        public IReadOnlyList<string> CompletionLabels { get; set; } = ["working"];
        public string? CompletionFailurePhase { get; set; }
        private void FailCompletion(string phase)
        {
            if (CompletionFailurePhase == phase)
                throw new GitHubOperationException(phase, Issue.Number, true, GitHubFailureKind.TransientProvider,
                    GitHubRemoteState.Uncertain, "Simulated lost reporting response");
        }
        public Exception? StateReadFailure { get; set; }
        public Task<GitHubIssueState> ReadIssueStateAsync(int issueNumber, CancellationToken ct) =>
            StateReadFailure is null ? Task.FromResult(new GitHubIssueState(!Closed, CompletionLabels))
                : Task.FromException<GitHubIssueState>(StateReadFailure);
        public async Task EnsureSuccessCommentAsync(int issueNumber, Guid executionId, string comment, CancellationToken ct, bool allowCreate = true)
        {
            FailCompletion("comment-before");
            if (!Comments.Contains(comment)) await CommentAsync(issueNumber, comment, ct);
            FailCompletion("comment-after");
        }
        public string CommentContext { get; set; } = "";
        public Exception? CommentFetchFailure { get; set; }
        public int CommentFetches { get; private set; }
        internal string? ParentProvenance { get; set; }
        public Task<string?> GetParentProvenanceAsync(int issueNumber, DateTimeOffset startedAt, int? currentParent, CancellationToken ct) => Task.FromResult(ParentProvenance);
        public Task<int?> GetParentIssueNumberAsync(int issueNumber, CancellationToken ct) => Task.FromResult(Issue.ParentIssueNumber);
        public Task<string> GetIssueCommentContextAsync(int issueNumber, CancellationToken cancellationToken,
            IReadOnlyList<string>? secretValues = null)
        {
            CommentFetches++;
            return CommentFetchFailure is null ? Task.FromResult(CommentContext) : Task.FromException<string>(CommentFetchFailure);
        }
        public IReadOnlyList<int> Blockers { get; set; } = [];
        public Task<IReadOnlyList<int>?> GetBlockerIssueNumbersAsync(int issueNumber, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>?>(Blockers);
        private int _returned;
        public int ReadyIssueCount { get; set; } = 1;
        public bool ReturnIssueOnFirstQuery { get; set; } = true;
        public bool CancelWhenEmpty { get; set; } = true;
        public bool CancelDuringQuery { get; set; }
        public bool CancelDuringClaim { get; set; }
        public bool ReturnDistinctIssues { get; set; }
        public bool ReturnRecoveryIssueOnFirstQuery { get; set; }
        public bool ReturnConflictIssue { get; set; }
        public int? ExcludeReadyIssue { get; set; }
        public string ReadyLabel { get; set; } = "ready";
        public string RecoveryLabel { get; set; } = "codex-integration-recovery";
        public IReadOnlyList<string> IssueLabels { get; set; } = [];
        public List<string> RemovedLabels { get; } = [];
        public GitHubIssue Issue { get; set; } = new(17, "Example task", "Implement this request", DateTimeOffset.UtcNow);
        public int FindCalls { get; private set; }
        public List<string> Labels { get; } = [];
        public List<string> Comments { get; } = [];
        public Func<Task>? CommentAction { get; set; }
        public GitHubOperationException? InterruptionReportingFailure { get; set; }
        public GitHubOperationException? ResultReportingFailure { get; set; }

        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken)
            => FindOldestReadyAsync(label, new HashSet<int>(), cancellationToken);

        public Task<GitHubIssue?> FindOldestReadyAsync(string label, IReadOnlySet<int> excludedIssueNumbers,
            CancellationToken cancellationToken)
        {
            FindCalls++;
            events.Add("find");
            if (label == RecoveryLabel && ReturnRecoveryIssueOnFirstQuery)
            {
                ReturnRecoveryIssueOnFirstQuery = false;
                return Task.FromResult<GitHubIssue?>(Issue with { Labels = IssueLabels });
            }
            if (label == "codex-integration-conflict" && ReturnConflictIssue && !excludedIssueNumbers.Contains(Issue.Number))
                return Task.FromResult<GitHubIssue?>(Issue with { Labels = [label] });
            if (label != ReadyLabel) return Task.FromResult<GitHubIssue?>(null);
            if (CancelDuringQuery)
            {
                cancellation.Cancel();
                return Task.FromException<GitHubIssue?>(new OperationCanceledException(cancellation.Token));
            }
            if (ReturnIssueOnFirstQuery)
            {
                while (_returned < ReadyIssueCount)
                {
                    var number = Issue.Number + (ReturnDistinctIssues ? _returned : 0);
                    _returned++;
                    if (!excludedIssueNumbers.Contains(number) && number != ExcludeReadyIssue)
                        return Task.FromResult<GitHubIssue?>(Issue with { Number = number, Labels = IssueLabels });
                }
            }
            // End the polling loop without waiting; no real GitHub service is involved.
            if (CancelWhenEmpty) cancellation.Cancel();
            return Task.FromResult<GitHubIssue?>(null);
        }

        public Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubIssue?>(issueNumber == Issue.Number ? Issue : null);

        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        {
            if (CompletionMode && add == "done") FailCompletion("label-before");
            Labels.Add($"{remove}->{add}");
            if (CompletionMode)
            {
                CompletionLabels = CompletionLabels.Where(label => label != remove).Append(add).ToArray();
                if (add == "done") FailCompletion("label-after");
            }
            if (InterruptionReportingFailure is not null && remove == "working" && add == "blocked")
                return Task.FromException(InterruptionReportingFailure);
            if (ResultReportingFailure is not null && remove == "working" && add == "done")
                return Task.FromException(ResultReportingFailure);
            if (CancelDuringClaim && remove == "ready")
            {
                cancellation.Cancel();
                return Task.FromException(new WorkerInfrastructureException("GitHub operation was cancelled; remote Issue state may be uncertain.", new OperationCanceledException(ct)));
            }
            return Task.CompletedTask;
        }
        public Task RemoveLabelAsync(int issueNumber, string label, CancellationToken ct)
        { RemovedLabels.Add(label); return Task.CompletedTask; }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct)
        { Comments.Add(comment); return CommentAction?.Invoke() ?? Task.CompletedTask; }
        public Task CloseAsync(int issueNumber, CancellationToken ct)
        {
            if (CompletionMode) FailCompletion("close-before");
            Closed = true;
            CloseCalls++;
            if (CompletionMode) FailCompletion("close-after");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGit : IGitRepository
    {
        public bool CompletionMode { get; set; }
        public bool RemoteContainsIntegration { get; set; }
        public string? CompletionFailurePhase { get; set; }
        private Func<GitIntegrationResult, bool, CancellationToken, Task>? _integrationObserver;
        public IGitRepository WithIntegrationObserver(Func<GitIntegrationResult, bool, CancellationToken, Task> observer)
        { _integrationObserver = observer; return this; }
        public Task<bool> VerifyRemoteIntegrationAsync(ExecutionHistoryEntry entry, CancellationToken ct) => Task.FromResult(RemoteContainsIntegration);
        public Task FinishIntegratedExecutionAsync(ExecutionHistoryEntry entry, CancellationToken ct)
        {
            if (CompletionFailurePhase is "cleanup-before" or "cleanup-after")
                throw new WorkerInfrastructureException("Cleanup interrupted");
            return Task.CompletedTask;
        }
        public string ExecutionDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "execution-worktree");
        public int Started { get; private set; }
        public int Cleanups { get; private set; }
        public int Integrations { get; private set; }
        public int RecoveryStarted { get; private set; }
        public GitRecoveryInfo? Recovery { get; set; }
        public Guid? LastExecutionId { get; private set; }
        public bool LastResume { get; private set; }
        public ExecutionHistoryEntry? LastRetryOf { get; private set; }
        public int LastAttemptNumber { get; private set; }
        public GitIntegrationConflictException? IntegrationFailure { get; set; }
        public Exception? IntegrationException { get; set; }
        public Action? BeforeIntegration { get; set; }
        public WorkerInfrastructureException? StartFailure { get; set; }
        public int? FailIssueNumber { get; set; }
        public GitRecoveryInfo? Snapshot { get; set; }
        public int CodexResumes { get; private set; }
        public Task<GitRecoveryInfo?> InspectExecutionWorkspaceAsync(CancellationToken ct) => Task.FromResult(Snapshot);
        public Task<string?> ValidateCodexRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<InterruptedWorkspaceInspection> InspectInterruptedExecutionAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
            Task.FromResult(source.RecoveryState == "codex-interrupted"
                ? new InterruptedWorkspaceInspection(Head: source.RecoveryBaseCommit, RemoteBase: source.RecoveryBaseCommit, HasChanges: true, NotIntegrated: true)
                : new InterruptedWorkspaceInspection("inspection-unsupported"));
        public Task StartCodexRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
        { CodexResumes++; return Task.CompletedTask; }
        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, CancellationToken ct) { Started++; LastExecutionId = executionId; return Task.CompletedTask; }
        public Task StartIssueAsync(Guid executionId, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resume, int attemptNumber, CancellationToken ct)
        { Started++; LastExecutionId = executionId; LastResume = resume; LastRetryOf = retryOf; LastAttemptNumber = attemptNumber;
            var failure = FailIssueNumber is null || issue.Number == FailIssueNumber ? StartFailure : null;
            return failure is null ? Task.CompletedTask : Task.FromException(failure); }
        public string? IntegrationBase { get; set; } = "current-main";
        public string? RecoveryRejection { get; set; }
        public Func<Task>? IntegrationAction { get; set; }
        public Task<string?> GetIntegrationBaseAsync(CancellationToken ct) => Task.FromResult(IntegrationBase);
        public Task<GitRecoveryInfo?> PreserveIntegrationConflictAsync(CancellationToken ct) => Task.FromResult(Recovery);
        public void PreservePartialConflictResolution() { }
        public Task StartIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct)
        { RecoveryStarted++; LastRetryOf = source; return Task.CompletedTask; }
        public Task<string?> ValidateIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
            Task.FromResult(RecoveryRejection);
        private int _verifications;
        public Func<CancellationToken, Task>? VerifyAfterValidation { get; set; }
        public Task VerifyCodexStateAsync(CancellationToken ct) =>
            ++_verifications == 2 && VerifyAfterValidation is not null ? VerifyAfterValidation(ct) : Task.CompletedTask;
        public Task DiscardUncommittedIssueChangesAsync(CancellationToken ct) { Cleanups++; return Task.CompletedTask; }
        public Task<GitRecoveryInfo?> PreserveFailedIssueChangesAsync(CancellationToken ct)
        {
            if (Recovery is null) Cleanups++;
            return Task.FromResult(Recovery);
        }
        public string? ProcessBlocker { get; set; }
        public Task<string?> InspectExecutionProcessesAsync(ExecutionHistoryEntry source, CancellationToken ct) => Task.FromResult(ProcessBlocker);
        public bool RequestConflictResolution { get; set; }
        public bool CheckConflictPolicy { get; set; }
        public int ConflictPolicyChecks { get; private set; }
        public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
            Func<CancellationToken, Task<ValidationResult>> validateAfterRebase,
            Func<string, CancellationToken, Task<bool>> resolveConflict,
            Func<IntegrationRepairContext, CancellationToken, Task<IntegrationRepairResult>> repairIntegration,
            CancellationToken ct, Func<CancellationToken, Task>? ensureAuthority = null)
        {
            if (RequestConflictResolution)
            {
                if (ensureAuthority is not null) await ensureAuthority(ct);
                if (await resolveConflict("confirmed rebase conflict", ct))
                {
                    var validated = await validateAfterRebase(ct);
                    if (!validated.Succeeded) throw new PostRebaseValidationException("validation failed", validated.Failure);
                    if (ensureAuthority is not null) await ensureAuthority(ct);
                }
                else
                {
                    throw new GitIntegrationConflictException("Integration conflict remained unresolved; the preserved implementation was not integrated.");
                }
            }
            if (CheckConflictPolicy)
            {
                ConflictPolicyChecks++;
                Assert.False(await resolveConflict("confirmed rebase conflict", ct));
            }
            return await CommitAndIntegrateAsync(issue, validateAfterRebase, ct);
        }
        public async Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
            Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct)
        {
            if (IntegrationAction is not null) await IntegrationAction();
            Integrations++;
            BeforeIntegration?.Invoke();
            var prepared = new GitIntegrationResult(true,
                "Committed as `0123456789ab`. Merged into `main`. Preserved on origin as `completed/17`.",
                "0123456789abcdef0123456789abcdef01234567", "main", "completed/17",
                RecoveryStarted > 0 ? LastRetryOf?.ExecutionId : LastExecutionId,
                RecoveryStarted > 0 ? LastRetryOf?.AttemptNumber : LastAttemptNumber);
            if (CompletionMode && _integrationObserver is not null)
                await _integrationObserver(prepared, false, ct);
            if (IntegrationException is not null) throw IntegrationException;
            if (CompletionMode && _integrationObserver is not null)
                await _integrationObserver(prepared, true, ct);
            if (IntegrationFailure is not null) throw IntegrationFailure;
            if (CompletionMode) return prepared;
            return new GitIntegrationResult(true,
            "Committed as `0123456789ab`. Merged into `main`. Preserved on origin as `completed/17`.",
            "0123456789abcdef0123456789abcdef01234567", "main", "completed/17");
        }
    }

    private sealed class FakeCodex(List<string> events) : ICodexExecutor
    {
        public int ConflictResolutions { get; private set; }
        public string? ConflictDirectory { get; private set; }
        public Func<CodexOutcome>? ConflictAction { get; set; }
        public string? ConflictIssueBody { get; private set; }
        public Task<CodexOutcome> ResolveIntegrationConflictAsync(string projectDirectory, string instructionsFile,
            GitHubIssue issue, string conflictDetails, CancellationToken ct)
        {
            ConflictResolutions++;
            ConflictIssueBody = issue.Body;
            ConflictDirectory = projectDirectory;
            return Task.FromResult(ConflictAction?.Invoke() ?? Success("resolved"));
        }
        public ICodexQuotaReader? QuotaReader { get; set; }
        public List<GitHubIssue> Issues { get; } = [];
        public List<ImplementationRequest> Requests { get; } = [];
        public Task<CodexOutcome> RunAsync(ImplementationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return RunAsync(request.ProjectDirectory, request.InstructionsFile, request.Issue, request.RetryOf,
                request.Resumed, request.AttemptNumber, ct);
        }
        private Action<string?>? _modelObserver;
        public string? CliModel { get; set; }
        public string? ThreadId { get; set; }
        private Func<string, Task>? _sessionObserver;
        public ICodexExecutor WithSessionObserver(Func<string, Task> observer)
        {
            _sessionObserver = observer;
            return this;
        }
        public ICodexExecutor WithModelObserver(Action<string?> observer)
        {
            _modelObserver = observer;
            return this;
        }
        public List<CodexExecutionProfile> Profiles { get; } = [];
        public ICodexExecutor WithProfile(CodexExecutionProfile profile)
        {
            Profiles.Add(profile);
            return this;
        }
        public WorkerInfrastructureException? PreflightException { get; set; }
        public Exception? InitialException { get; set; }
        public CodexOutcome InitialOutcome { get; set; } = Success("implemented");
        public Queue<CodexOutcome> Repairs { get; } = new();
        public List<int> RepairAttempts { get; } = [];
        public List<ValidationFailure> RepairFailures { get; } = [];
        public bool BlockRuns { get; set; }
        public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRuns { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreflightAsync(CancellationToken ct)
        {
            events.Add("preflight");
            return PreflightException is null ? Task.CompletedTask : Task.FromException(PreflightException);
        }
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct) =>
            RunAsync(projectDirectory, instructionsFile, issue, null, false, 1, ct);
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct)
        {
            Issues.Add(issue);
            return RunCoreAsync(projectDirectory, ct);
        }
        private async Task<CodexOutcome> RunCoreAsync(string projectDirectory, CancellationToken ct)
        {
            if (ThreadId is not null && _sessionObserver is not null) await _sessionObserver(ThreadId);
            if (CliModel is not null) _modelObserver?.Invoke(CliModel);
            InitialDirectory = projectDirectory;
            RunStarted.TrySetResult();
            if (BlockRuns)
            {
                BlockedRunStarted.TrySetResult();
                await ReleaseRuns.Task.WaitAsync(ct);
            }
            if (InitialException is not null) throw InitialException;
            return InitialOutcome;
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
        public int? CancelOnCall { get; set; }
        public Action? OnRun { get; set; }
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            Calls++;
            LastDirectory = directory;
            if (Calls == CancelOnCall) OnRun?.Invoke();
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : ValidationResult.Success);
        }
    }
}
