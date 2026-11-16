using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodexWorker;

public enum IssueOutcomeKind { Succeeded, Blocked, Failed, IntegrationConflict, Superseded }
public sealed record IssueProcessingResult(IssueOutcomeKind Kind, IssueExecutionReport Report)
{
    public string Summary => Report.ToMarkdown(Kind);
}

public sealed partial class Worker(WorkerConfiguration config, IGitHubClient github, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, TelegramNotifier telegram, WorkerConsole? output = null, ExecutionHistoryStore? history = null,
    SemaphoreSlim? repositoryGate = null, WorkerServerSettings? serverSettings = null, Action<string>? operationalLog = null, CancellationToken shutdownToken = default,
    WorkerRegistrationClient? registrationClient = null, TimeProvider? timeProvider = null, ImplementationEngineSelector? engines = null,
    Func<string, bool>? engineReady = null, CodexThreadCoordinator? threadCoordinator = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly WorkerRegistrationClient _registration = registrationClient ?? new WorkerRegistrationClient();
    private readonly WorkerConsole _output = output ?? new WorkerConsole();
    private readonly Action<string> _operationalLog = operationalLog ?? (_ => { });
    private readonly SemaphoreSlim _repositoryGate = repositoryGate ?? new SemaphoreSlim(1, 1);
    private readonly CodexThreadCoordinator _threadCoordinator = threadCoordinator ?? new();
    private readonly ConcurrentDictionary<int, byte> _activeIssues = new();
    private IntegrationRecoveryCoordinator? _integrationRecovery;
    private IntegrationRecoveryCoordinator IntegrationRecovery => LazyInitializer.EnsureInitialized(ref _integrationRecovery,
        () => new IntegrationRecoveryCoordinator(config, github, git, telegram, _output, history, _repositoryGate,
            _operationalLog, issueNumber => _activeIssues.ContainsKey(issueNumber)));

    public WorkerConfiguration Configuration => config;

    public async Task PrepareForHostAsync(CancellationToken ct)
    {
        if (!config.Codex.AllowMissingDefaultInstructions && !File.Exists(config.Codex.InstructionsFile))
            throw new WorkerInfrastructureException($"Configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
        await _repositoryGate.WaitAsync(ct);
        try { await git.InitializeAsync(ct); }
        finally { _repositoryGate.Release(); }
        await new CodexRecoveryCoordinator(config, git, history, _repositoryGate, _output, _clock,
            entry => ReportServerAsync(entry, ExecutionState.InfrastructureFailure, ct)).ReconcileAsync(ct);
        await ReconcileIntegrationRecoveryAsync(ct);
    }

    public Task ReconcileIntegrationRecoveryAsync(CancellationToken ct) => IntegrationRecovery.ReconcileAsync(ct);

    /// <summary>Checks this project's queue once and processes at most one claimed Issue.</summary>
    public async Task<IssueProcessingResult?> ProcessOneAsync(CancellationToken ct)
    {
        var execution = await ClaimNextAsync(ct);
        return execution is null ? null : await execution;
    }

    /// <summary>Claims the next eligible Issue and returns its independent execution task, if one was claimed.</summary>
    public async Task<Task<IssueProcessingResult?>?> ClaimNextAsync(CancellationToken ct)
    {
        if (shutdownToken.IsCancellationRequested || ct.IsCancellationRequested) return null;
        var standalone = await ClaimStandaloneInterruptionAsync(ct);
        if (standalone is not null) return standalone;
        var interrupted = await ClaimCodexRecoveryAsync(null, ct);
        if (interrupted is not null) return interrupted;
        foreach (var label in new[] { config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var recoveryExcluded = new HashSet<int>();
            while (true)
            {
                if (shutdownToken.IsCancellationRequested || ct.IsCancellationRequested) return null;
                GitHubIssue? recoveryIssue;
                try { recoveryIssue = await github.FindOldestReadyAsync(label, recoveryExcluded, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
                if (recoveryIssue is null || !recoveryExcluded.Add(recoveryIssue.Number)) break;
                if (recoveryIssue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true)
                {
                    _operationalLog($"Scheduler · {config.Project.Name} · Issue #{recoveryIssue.Number} · explicit ready label takes precedence over integration recovery.");
                    continue;
                }
                var recovery = await ClaimIntegrationRecoveryAsync(recoveryIssue, ct,
                    explicitRecovery: label == config.GitHub.IntegrationRecoveryLabel);
                if (recovery is not null) return recovery;
            }
        }
        var excluded = new HashSet<int>();
        while (true)
        {
            if (shutdownToken.IsCancellationRequested || ct.IsCancellationRequested) return null;
            GitHubIssue? issue;
            try { issue = await github.FindOldestReadyAsync(config.GitHub.ReadyLabel, excluded, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            if (issue is null || !excluded.Add(issue.Number)) return null;
            try
            {
                var selection = CodexProvisioning.IssueExecutionMetadata.Parse(issue.Body);
                if (!(engines ?? new ImplementationEngineSelector(settings: config.Codex)).IsAvailable(selection.Engine) ||
                    engineReady?.Invoke(selection.Engine) == false)
                {
                    _operationalLog($"Scheduler · {config.Project.Name} · Issue #{issue.Number} · engine {selection.Engine} is unavailable on this Worker; leaving Issue ready.");
                    continue;
                }
            }
            catch (InvalidDataException) { /* Existing preparation reporting owns invalid metadata. */ }
            var execution = await ClaimIssueAsync(issue, null, null, null, ct);
            if (execution is not null) return execution;
        }
    }

    /// <summary>Executes a Server assignment through the same claim, history and ExecutionRunner pipeline as standalone work.</summary>
    public async Task<Task<IssueProcessingResult?>?> ClaimAssignedAsync(WorkerAssignmentContract assignment, CancellationToken ct)
    {
        if (assignment.Lease is not { State: "Active", Generation: > 0 } lease ||
            lease.ExpiresAtUtc <= _clock.GetUtcNow() ||
            !string.Equals(lease.ExecutionId, assignment.ServerExecutionId, StringComparison.Ordinal) ||
            !string.Equals(lease.WorkerId, assignment.WorkerId, StringComparison.Ordinal) ||
            lease.ExpiresAtUtc <= lease.AcquiredAtUtc || lease.RenewalIntervalSeconds is < 10 or > 3600 ||
            lease.RenewalIntervalSeconds * 3 >= (lease.ExpiresAtUtc - lease.AcquiredAtUtc).TotalSeconds ||
            string.IsNullOrWhiteSpace(assignment.AssignmentId))
            throw new WorkerInfrastructureException("Server assignment does not contain a valid active execution lease.");
        if (assignment.Work.Type is not ("issue" or "github-issue") ||
            !int.TryParse(assignment.Work.Id, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var issueNumber) || issueNumber <= 0)
            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} does not identify a supported GitHub Issue.");
        var issue = await github.GetIssueAsync(issueNumber, ct)
            ?? throw new WorkerInfrastructureException($"Assigned GitHub Issue #{issueNumber} could not be found.");
        if (assignment.Metadata.TryGetValue("integrationRecoveryExecutionId", out var recoveryId))
        {
            if (!string.IsNullOrEmpty(assignment.Metadata.GetValueOrDefault("explicitIntegrationRecovery")) && history is not null &&
                (await history.ReadAllAsync(ct)).Any(entry => entry.ServerExecutionId == assignment.ServerExecutionId))
                return null; // Durable assignment replay, including terminal/uncertain attempts, never re-runs a resolver.
            if (!Guid.TryParse(recoveryId, out var id) || history is null ||
                !(await history.ReadAllAsync(ct)).Any(entry => entry.ExecutionId == id && entry.IssueNumber == issueNumber &&
                    entry.Project == config.Project.Name && entry.Repository == config.Project.Repository))
                throw new WorkerInfrastructureException("Recovery assignment does not match local preserved execution ownership.");
            return await ClaimIntegrationRecoveryAsync(issue, ct, explicitRecovery: false, assignment: assignment,
                resolveExecutionId: !string.IsNullOrEmpty(assignment.Metadata.GetValueOrDefault("explicitIntegrationRecovery")) ? id : null);
        }
        if (assignment.Metadata.ContainsKey("codexRecoveryExecutionId"))
            return await ClaimCodexRecoveryAsync(assignment, ct);
        CodexProvisioning.IssueExecutionMetadata selection;
        try { selection = CodexProvisioning.IssueExecutionMetadata.Parse(issue.Body); }
        catch (InvalidDataException ex) { throw new IssuePreparationRejectedException(ex.Message); }
        if (CodexProvisioning.IssueExecutionMetadata.ValidationError(assignment.Work.Selection) is { } selectionError)
            throw new IssuePreparationRejectedException(selectionError);
        if (!(engines ?? new ImplementationEngineSelector(settings: config.Codex)).IsAvailable(selection.Engine))
            throw new IssuePreparationRejectedException($"Execution engine '{selection.Engine}' is unavailable on this Worker.");
        if (selection.Engine != assignment.Work.Selection.Engine)
            throw new IssuePreparationRejectedException("Issue execution metadata changed after Server queueing; refresh the execution request before assigning it again.");
        return await ClaimIssueAsync(issue, assignment.ServerExecutionId, assignment.AssignmentId, lease.Generation, ct, assignment.Metadata.ContainsKey("freshRecoveryExecutionId"));
    }

    private async Task<Task<IssueProcessingResult?>?> ClaimIssueAsync(GitHubIssue issue, string? serverExecutionId,
        string? assignmentId, long? ownershipGeneration, CancellationToken ct, bool freshRecovery = false)
    {
        var issueKey = issue.Number;
        if (!_activeIssues.TryAdd(issueKey, 0))
        {
            if (serverExecutionId is not null)
                throw new WorkerInfrastructureException($"Server assigned Issue #{issue.Number} while an execution for that Issue is already active in this Worker.");
            return null;
        }

        try
        {
        var allHistory = history is null ? Array.Empty<ExecutionHistoryEntry>() : (await history.ReadAllAsync(ct)).ToArray();
        var issueHistory = allHistory.Where(e => e.Project == config.Project.Name && e.Repository == config.Project.Repository && e.IssueNumber == issue.Number)
            .OrderByDescending(e => e.AttemptNumber).ThenByDescending(e => e.StartedAtUtc).ToArray();
        if (issueHistory.Any(entry => entry.IntegrationRecoveryClaim is not null))
        {
            _activeIssues.TryRemove(issueKey, out _);
            return null;
        }
        if (issueHistory.Any(entry => entry.RecoveryState == "managed-completion-quarantined"))
        {
            if (serverExecutionId is not null)
                throw new IssuePreparationRejectedException("This Issue has a quarantined integrated managed completion; reconcile the original execution before assigning it again.");
            _activeIssues.TryRemove(issueKey, out _);
            return null;
        }
        var latest = issueHistory.FirstOrDefault();
        if (serverExecutionId is null && issueHistory.Any(HandlesStandaloneRecovery))
        {
            _activeIssues.TryRemove(issueKey, out _);
            return null;
        }
        if (freshRecovery && issueHistory.Any(entry => entry.State == "Completed" || entry.IntegrationBranch is not null ||
            entry.CompletedBranch is not null || entry.RecoveryState is "codex-resuming" or "codex-recovery-inspection-required"))
            throw new WorkerInfrastructureException("Fresh recovery conflicts with local completed, integrated, or uncertain execution history; inspect preserved state.");
        if (!freshRecovery && latest?.RecoveryState is "codex-interrupted" or "codex-resuming" or "codex-recovery-exhausted" or "codex-recovery-inspection-required")
        {
            _activeIssues.TryRemove(issueKey, out _);
            return null;
        }
        // Ready is an explicit request for new implementation after an integration conflict,
        // including older rows that recorded the conflict as a failed task.
        var freshAfterConflict = issue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true &&
            latest is not null && IsTerminalIntegrationConflict(latest);
        // Metadata validation and other explicitly classified pre-workspace failures have no
        // execution state to resume. A new ready label starts a clean attempt even in resume mode.
        var freshAfterPreparationFailure = issue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true &&
            latest?.RecoveryState == "preparation-failed";
        var retryOf = latest?.RecoveryState == "standalone-retired" ? latest : !freshRecovery && !freshAfterConflict && !freshAfterPreparationFailure && latest?.State is "Failed" or "Blocked" ? latest : null;
        var attemptNumber = issueHistory.Length == 0 ? 1 : issueHistory.Max(e => e.AttemptNumber) + 1;
        // Verified no-change cleanup or safe standalone retirement authorizes a clean retry.
        var cleanRetry = retryOf?.RecoveryState is "cleaned-no-changes" or "standalone-retired";
        var retryMode = config.Worker.RetryMode.ToLowerInvariant();
        try { retryMode = CodexProvisioning.IssueExecutionMetadata.Parse(issue.Body).RetryMode ?? retryMode; }
        catch (InvalidDataException) { /* Attempt preparation records and reports invalid metadata before Git mutation. */ }
        var resumed = retryOf is not null && !cleanRetry && retryMode == "resume";
        issue = await PrepareCommentContextAsync(issue, ct);
        var execution = WorkerExecution.Create(config.Project, config.Git, issue, retryOfExecutionId: retryOf?.ExecutionId,
            attemptNumber: attemptNumber, resumed: resumed, serverExecutionId: serverExecutionId, assignmentId: assignmentId,
            ownershipGeneration: ownershipGeneration, codexSettings: config.Codex,
            settingsSource: retryOf?.RecoveryState == "standalone-retired" ? null : retryOf, configuredRetryMode: config.Worker.RetryMode);
        await CreateHistoryAsync(execution, ct);
        if (ct.IsCancellationRequested)
        {
            if (shutdownToken.IsCancellationRequested) await RecordShutdownAsync(execution);
            else await TransitionAsync(execution, ExecutionState.Cancelled, CancellationToken.None);
            _activeIssues.TryRemove(issueKey, out _);
            return null;
        }
        try
        {
            if (execution.MetadataError is null && resumed && (retryOf!.RecoveryState != "recoverable" || string.IsNullOrWhiteSpace(retryOf.RecoveryBaseCommit)))
            {
                await RejectPreparationAsync(execution, issue, config.GitHub.ReadyLabel,
                    $"Previous execution {ExecutionFormatting.Display(retryOf.ExecutionId)} ({retryOf.ExecutionId}) has no verified safe recoverable state; possible work or uncertain state requires operator review. Inspect the preserved state before selecting worker.retryMode: restart.", ct);
                _activeIssues.TryRemove(issueKey, out _);
                return serverExecutionId is null ? null : Task.FromResult<IssueProcessingResult?>(null);
            }
            if (cleanRetry)
                _operationalLog($"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] · verified no-change cleanup starts a clean retry; previous execution {retryOf!.ExecutionId} is preserved.");
            if (freshAfterConflict)
                _operationalLog($"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] · explicit ready after integration conflict starts a fresh execution; previous execution {latest!.ExecutionId} is preserved.");
            if (freshAfterPreparationFailure)
                _operationalLog($"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] · explicit ready after clean preparation failure starts a fresh execution; previous execution {latest!.ExecutionId} is preserved.");
            await _output.StopWaitingAsync();
            await TransitionAsync(execution, ExecutionState.Claimed, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
            foreach (var staleLabel in new[] { config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel, config.GitHub.BlockedLabel, config.GitHub.FailedLabel }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                if (issue.Labels?.Contains(staleLabel, StringComparer.OrdinalIgnoreCase) == true)
                    await github.RemoveLabelAsync(issue.Number, staleLabel, ct);
            _operationalLog($"Scheduler · {config.Project.Name} / #{issue.Number} claimed · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}]");
            DisplayIssueStart(issue, execution);
            await telegram.StartingAsync(config.Project.Name, config.Project.Repository, issue, execution, ct);
            return ProcessClaimedAsync(execution, issue, retryOf, issueKey, ct);
        }
        catch (Exception shutdownError) when (ct.IsCancellationRequested && shutdownToken.IsCancellationRequested && WorkerShutdown.IsCancellation(shutdownError))
        {
            await RecordShutdownAsync(execution, ShutdownRecoveryState(shutdownError));
            throw new WorkerShutdownException(shutdownToken, shutdownError);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, "Cancellation interrupted execution.");
            throw new WorkerInfrastructureException("Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
        }
        catch (Exception ex)
        {
            if (!execution.IsTerminal)
                await RecordInfrastructureFailureAsync(execution, ex.Message,
                    ex is GitHubOperationException { IsMutation: true } ? GitHubOperationException.ReconciliationRequiredState : "uncertain");
            _activeIssues.TryRemove(issueKey, out _);
            if (ex is GitHubOperationException githubFailure)
                throw githubFailure.ForExecution(execution.ExecutionId);
            throw;
        }
        }
        catch
        {
            _activeIssues.TryRemove(issueKey, out _);
            throw;
        }
    }

    private async Task<GitHubIssue> PrepareCommentContextAsync(GitHubIssue issue, CancellationToken ct) =>
        issue with { ParentIssueNumber = issue.ParentIssueCaptured ? issue.ParentIssueNumber : await github.GetParentIssueNumberAsync(issue.Number, ct), ParentIssueCaptured = true,
            CommentContext = await github.GetIssueCommentContextAsync(issue.Number, ct,
            config.Environment.Variables.Values.ToArray()) };

    private static bool IsTerminalIntegrationConflict(ExecutionHistoryEntry entry) =>
        entry.State == "IntegrationConflict" ||
        entry.State is "Failed" or "Blocked" &&
        (entry.RecoveryState == "integration-conflict" ||
         // Legacy GitIntegrationConflictException outcomes were stored as Failed
         // without validation or recovery metadata. Match the worker's diagnostic
         // prefix and Issue identity, not arbitrary mentions in a Codex failure.
         entry.State == "Failed" && entry.RecoveryState is null && entry.CompletedAtUtc is not null &&
         entry.FailureReason?.StartsWith($"Integration conflict for Issue #{entry.IssueNumber};", StringComparison.Ordinal) == true);

    private async Task<IssueProcessingResult?> ProcessClaimedAsync(WorkerExecution execution, GitHubIssue issue, ExecutionHistoryEntry? retryOf,
        int issueKey, CancellationToken ct, bool integrationRecovery = false, bool interruptedExecution = false, bool resolveIntegrationConflict = false)
    {
        var finalQuotaDisplayed = false;
        try
        {
            if (retryOf?.RecoveryState == "codex-interrupted" && !integrationRecovery)
            {
                // The durable recovery claim has already consumed this source attempt. Discovery
                // and reconciliation never enter this flow or repeat its start notification.
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
                if (execution.ServerExecutionId is null && issue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true)
                    foreach (var staleLabel in new[] { config.GitHub.BlockedLabel, config.GitHub.FailedLabel }.Distinct(StringComparer.OrdinalIgnoreCase))
                        if (issue.Labels.Contains(staleLabel, StringComparer.OrdinalIgnoreCase))
                            await github.RemoveLabelAsync(issue.Number, staleLabel, ct);
                await telegram.StartingAsync(config.Project.Name, config.Project.Repository, issue, execution, ct, recovered: true);
            }
            await ReadQuotaAsync(final: false, ct);
            var timer = Stopwatch.StartNew();
            var result = await RunExecutionAsync(new ExecutionContext(execution, issue, retryOf, integrationRecovery, interruptedExecution, resolveIntegrationConflict), ct);
            timer.Stop();
            await TransitionAsync(execution, ExecutionState.Reporting, ct);
            var report = result.Report with { Duration = timer.Elapsed, ExecutionId = execution.ExecutionId,
                AttemptNumber = execution.AttemptNumber, RetryOfExecutionId = execution.RetryOfExecutionId, Resumed = execution.Resumed, RetryMode = execution.RetryMode, EffectiveModel = execution.CodexProfile?.EffectiveModel,
                Engine = execution.CodexProfile?.Engine ?? "codex", EffectiveEffort = execution.CodexProfile?.Effort, QuotaAtEnd = execution.QuotaAtEnd };
            var terminalState = result.Kind switch
            {
                IssueOutcomeKind.Succeeded => ExecutionState.Completed,
                IssueOutcomeKind.Blocked => ExecutionState.Blocked,
                IssueOutcomeKind.Failed => ExecutionState.Failed,
                IssueOutcomeKind.IntegrationConflict => ExecutionState.IntegrationConflict,
                IssueOutcomeKind.Superseded => ExecutionState.Superseded,
                _ => throw new ArgumentOutOfRangeException()
            };
            if (retryOf?.RecoveryState == "codex-interrupted" && history is not null)
                await history.UpdateRecoveryAsync(retryOf.ExecutionId, result.Kind == IssueOutcomeKind.Succeeded ? "codex-recovered" : "codex-recovery-finished", ct);
            var reportedResult = result with { Report = report };
            if (result.Kind == IssueOutcomeKind.Succeeded && history is not null &&
                await history.ReadExecutionAsync(execution.ExecutionId, ct) is { CompletionJson: not null } pending)
                await history.SaveCompletionAsync(execution.ExecutionId,
                    ExecutionCompletion.Read(pending) with { Report = ExecutionCompletion.Sanitize(report, config.Environment.Variables.Values.ToArray()) }, false, ct);
            try
            {
                if (resolveIntegrationConflict && retryOf is not null) await VerifyConflictIntentAsync(retryOf, ct, execution.ParentProvenance);
                report = await ReportResultAsync(issue, reportedResult, ct);
                execution.QuotaAtEnd = report.QuotaAtEnd;
                reportedResult = result with { Report = report };
                finalQuotaDisplayed = true;
            }
            catch (GitHubOperationException)
            {
                // Keep the execution's own result authoritative when reporting it to GitHub
                // fails. The outer handler records the secondary failure and any uncertainty.
                await CompleteHistoryAsync(execution, report, terminalState, CancellationToken.None);
                throw;
            }
            await CompleteHistoryAsync(execution, report, terminalState, CancellationToken.None);
            if (terminalState == ExecutionState.Completed && report.Integration is { HasChanges: false } && history is not null &&
                await history.ReadExecutionAsync(execution.ExecutionId, CancellationToken.None) is { RecoveryState: "standalone-continuing" })
                await history.UpdateRecoveryAsync(execution.ExecutionId, "cleaned-no-changes", CancellationToken.None);
            var finalEntry = history is null ? CreateEntry(execution, report, null, null) :
                (await history.ReadAllAsync(CancellationToken.None)).FirstOrDefault(entry => entry.ExecutionId == execution.ExecutionId)
                ?? CreateEntry(execution, report, null, null);
            await ReportServerAsync(finalEntry, execution.State, CancellationToken.None);
            if (integrationRecovery && history is not null && retryOf is not null &&
                finalEntry.CompletedAtUtc is not null && finalEntry.State == execution.State.ToString())
            {
                await history.FinishIntegrationRecoveryAsync(retryOf.ExecutionId, execution.ExecutionId, null, CancellationToken.None);
                _operationalLog($"Integration recovery {(result.Kind == IssueOutcomeKind.Succeeded ? "completed" : "exhausted")} · Issue #{issue.Number} · execution {execution.ExecutionId} · original execution {retryOf.ExecutionId}");
            }
            return reportedResult;
        }
        catch (Exception shutdownError) when (ct.IsCancellationRequested && shutdownToken.IsCancellationRequested && WorkerShutdown.IsCancellation(shutdownError))
        {
            await RecordShutdownAsync(execution, ShutdownRecoveryState(shutdownError));
            throw new WorkerShutdownException(shutdownToken, shutdownError);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            await ReportInterruptedExecutionAsync(execution, issue, "Cancellation interrupted execution.", ex, suppressGitHubMutations: resolveIntegrationConflict);
            throw new WorkerInfrastructureException("Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
        }
        catch (PreExecutionInfrastructureException ex)
        {
            if (resolveIntegrationConflict)
            {
                await RecordInfrastructureFailureAsync(execution, ex.Message, "preparation-failed");
                throw;
            }
            await RejectPreparationAsync(execution, issue, config.GitHub.WorkingLabel, ex.Message, CancellationToken.None,
                ex.InnerException is ProjectCheckoutDirtyException ? null : "preparation-failed");
            return null;
        }
        catch (Exception ex)
        {
            await ReportInterruptedExecutionAsync(execution, issue,
                ex is CodexExecutionInfrastructureException codexError ? codexError.Summary : ex.Message, ex,
                suppressGitHubMutations: resolveIntegrationConflict);
            if (ex is GitHubOperationException githubFailure)
                throw githubFailure.ForExecution(execution.ExecutionId);
            if (ex is WorkerInfrastructureException && history is not null &&
                await history.ReadExecutionAsync(execution.ExecutionId, CancellationToken.None) is { CompletionJson: not null })
                throw new CompletionReconciliationRequiredException(execution.Project, execution.ExecutionId, issue.Number,
                    $"Integration completion is pending for execution {execution.ExecutionId}; verify the exact commit on the authoritative remote base before resuming reporting.", ex);
            throw;
        }
        finally
        {
            if (!finalQuotaDisplayed)
            {
                execution.QuotaAtEnd ??= await ReadQuotaAsync(final: true, CancellationToken.None);
                _output.Quota(execution, "end", execution.QuotaAtEnd, operationalLog);
            }
            _activeIssues.TryRemove(issueKey, out _);
        }
    }

    private void DisplayIssueStart(GitHubIssue issue, WorkerExecution execution)
    {
        var observation = codex.QuotaReader?.CachedObservation ?? new(_clock.GetUtcNow(), "not cached", []);
        _output.Group(() =>
        {
            _output.IssueStarted(config.Project.Name, issue, execution, observation, operationalLog);
        });
    }

    private async Task<CodexQuotaObservation> ReadQuotaAsync(bool final, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var reader = codex.QuotaReader;
            return reader is null ? new(_clock.GetUtcNow(), "reader unavailable", []) :
                await (final ? reader.ReadFinalAsync(deadline.Token) : reader.ReadAsync(deadline.Token)).WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(_clock.GetUtcNow(), "query failed", []); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _output.Startup(config.Project.Name, config.Project.Repository);
        var safelyIdle = false;
        try
        {
            if (!config.Codex.AllowMissingDefaultInstructions && !File.Exists(config.Codex.InstructionsFile))
                throw new WorkerInfrastructureException($"Configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
            await git.InitializeAsync(ct);
            await ReconcileIntegrationRecoveryAsync(ct);
            safelyIdle = true;
            await _output.RunProgressAsync("Codex preflight", async () =>
            {
                await codex.PreflightAsync(ct);
                return true;
            }, failureDetail: ex => ex.Message, ct: ct);
            await telegram.StartedAsync(config.Project.Name, ct);
            _output.Started();
            while (!ct.IsCancellationRequested)
            {
                _output.Waiting();
                var result = await ProcessOneAsync(ct);
                if (result is null && !ct.IsCancellationRequested) await DelayAsync(ct);
                safelyIdle = true;
            }
            await _output.StopWaitingAsync();
            _output.Shutdown();
            await telegram.StoppedAsync(config.Project.Name, CancellationToken.None);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            await _output.StopWaitingAsync(finalizeLine: true);
            if (safelyIdle)
            {
                _output.Shutdown("Worker stopped.");
                await telegram.StoppedAsync(config.Project.Name, CancellationToken.None);
            }
            else
            {
                var infrastructure = new WorkerInfrastructureException(
                    "Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
                _output.InfrastructureFailure($"Infrastructure failure: {infrastructure.Message}");
                await telegram.CriticalAsync(config.Project.Name, infrastructure.Message, CancellationToken.None);
                throw infrastructure;
            }
        }
        catch (Exception ex)
        {
            await _output.StopWaitingAsync();
            var infrastructure = ex as WorkerInfrastructureException ??
                new WorkerInfrastructureException($"Unexpected worker failure; queue processing stopped: {ex.Message}", ex);
            var reason = infrastructure is CodexExecutionInfrastructureException codexFailure
                ? FailureDiagnosticRedactor.Redact(codexFailure.Summary, config.Environment.Variables.Values.ToArray())
                : infrastructure.Message;
            _output.InfrastructureFailure($"Infrastructure failure: {reason}");
            await telegram.CriticalAsync(config.Project.Name, reason, CancellationToken.None);
            throw infrastructure;
        }
        finally
        {
            await _output.StopWaitingAsync();
        }
    }

    private Task CreateHistoryAsync(WorkerExecution execution, CancellationToken ct) =>
        history is null ? Task.CompletedTask : history.CreateAsync(CreateInitialEntry(execution), ct);

    private ExecutionHistoryEntry CreateInitialEntry(WorkerExecution execution) =>
        CreateEntry(execution, null, null, null) with
        {
            OriginalIssueBody = FailureDiagnosticRedactor.Redact(execution.IssueBody, config.Environment.Variables.Values.ToArray()),
            OriginalIssueBodyFingerprint = ConflictIssueIntent.BodyFingerprint(execution.IssueBody)
        };

    private Task<IssueProcessingResult> RunExecutionAsync(ExecutionContext context, CancellationToken ct)
    {
        // Mutable branch/worktree state belongs to this attempt. Integration still targets its shared repository.
        var executionRepository = git.CreateExecutionRepository();
        var runner = new ExecutionRunner(config, executionRepository, codex, validation, _output, history, _repositoryGate,
            (entry, state, token) => ReportServerAsync(entry, state, token), IsAuthoritativeAsync, shutdownToken, _clock, engines, github, _threadCoordinator);
        return runner.RunAsync(context, ct);
    }

    private async Task<bool> IsAuthoritativeAsync(WorkerExecution execution, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (execution.ConflictRecoveryLease is { } lease)
        {
            if (serverSettings?.Enabled != true ||
                await _registration.RenewExecutionLeaseAsync(serverSettings, lease, ct) is not { } expiry || expiry <= _clock.GetUtcNow())
                return false;
        }
        if (!await github.IsIssueOpenAsync(execution.IssueNumber, ct)) return false;
        if (history is null) return true;
        var sameIssue = (await history.ReadAllAsync(ct)).Where(entry =>
            entry.Project.Equals(execution.Project, StringComparison.OrdinalIgnoreCase) &&
            entry.Repository.Equals(execution.Repository, StringComparison.OrdinalIgnoreCase) &&
            entry.IssueNumber == execution.IssueNumber && entry.ExecutionId != execution.ExecutionId).ToArray();
        return !sameIssue.Any(entry => entry.AttemptNumber > execution.AttemptNumber ||
            entry.State == "Completed" && entry.AttemptNumber >= execution.AttemptNumber);
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        // Compatibility loop remains available to existing workflow tests; production polling is owned by WorkerHost.
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    internal static string QuotaNotification(IssueExecutionReport report) =>
        report.QuotaAtEnd is { Status: "available" } quota && DateTimeOffset.UtcNow - quota.ObservedAtUtc <= TimeSpan.FromMinutes(2)
            ? "\n\nCodex account quota: " + CodexQuotaFormatting.Compact(quota, DateTimeOffset.UtcNow) : "";

    private static string TelegramCompletion(IssueExecutionReport report)
    {
        var details = new List<string>();
        if (QuotaNotification(report) is { Length: > 0 } quota) details.Add(quota.Trim());
        if (report.Integration is not null) details.Add(report.Integration.Summary);
        var integrationRepairs = report.ValidationRepairs.Count(repair => repair.IntegrationRepair);
        if (integrationRepairs > 0) details.Add($"Integration repair succeeded after {integrationRepairs} attempt(s).");
        if (!string.IsNullOrWhiteSpace(report.ImplementationSummary)) details.Add($"{(report.Engine == "ccc" ? "CCC" : "Codex")} summary: {report.ImplementationSummary}");
        return string.Join("\n\n", details);
    }
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 20)] + " … [truncated]";
}
