using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodexWorker;

public enum IssueOutcomeKind { Succeeded, Blocked, Failed, IntegrationConflict, Superseded }
public sealed record IssueProcessingResult(IssueOutcomeKind Kind, IssueExecutionReport Report)
{
    public string Summary => Report.ToMarkdown(Kind);
}

public sealed class Worker(WorkerConfiguration config, IGitHubClient github, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, TelegramNotifier telegram, WorkerConsole? output = null, ExecutionHistoryStore? history = null,
    SemaphoreSlim? repositoryGate = null, WorkerServerSettings? serverSettings = null, Action<string>? operationalLog = null, CancellationToken shutdownToken = default,
    WorkerRegistrationClient? registrationClient = null)
{
    private readonly WorkerRegistrationClient _registration = registrationClient ?? new WorkerRegistrationClient();
    private readonly WorkerConsole _output = output ?? new WorkerConsole();
    private readonly Action<string> _operationalLog = operationalLog ?? (_ => { });
    private readonly SemaphoreSlim _repositoryGate = repositoryGate ?? new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<int, byte> _activeIssues = new();
    private IntegrationRecoveryCoordinator? _integrationRecovery;
    private IntegrationRecoveryCoordinator IntegrationRecovery => LazyInitializer.EnsureInitialized(ref _integrationRecovery,
        () => new IntegrationRecoveryCoordinator(config, github, git, telegram, _output, history, _repositoryGate,
            _operationalLog, issueNumber => _activeIssues.ContainsKey(issueNumber)));

    public WorkerConfiguration Configuration => config;

    public async Task PrepareForHostAsync(CancellationToken ct)
    {
        if (!File.Exists(config.Codex.InstructionsFile))
            throw new WorkerInfrastructureException($"Configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
        await _repositoryGate.WaitAsync(ct);
        try { await git.InitializeAsync(ct); }
        finally { _repositoryGate.Release(); }
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
            var execution = await ClaimIssueAsync(issue, null, null, null, ct);
            if (execution is not null) return execution;
        }
    }

    private async Task<Task<IssueProcessingResult?>?> ClaimIntegrationRecoveryAsync(GitHubIssue issue, CancellationToken ct, bool explicitRecovery = true,
        WorkerAssignmentContract? assignment = null)
    {
        var issueKey = issue.Number;
        if (!_activeIssues.TryAdd(issueKey, 0)) return null;
        WorkerExecution? claimedExecution = null;
        try
        {
            IReadOnlyList<ExecutionHistoryEntry> allHistory = history is null ? [] : await history.ReadAllAsync(ct);
            ExecutionHistoryEntry[] prior = allHistory.Where(entry =>
                entry.Project == config.Project.Name && entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number &&
                entry.State == "IntegrationConflict" && entry.RecoveryState is (null or "integration-conflict"))
                .OrderByDescending(entry => entry.StartedAtUtc).ToArray();
            var source = prior.FirstOrDefault();
            // Recovery attempts use their original worktree, not a workspace named for the new history row.
            // A later implementation or completion supersedes older conflicts.
            var latest = allHistory.Where(entry => entry.Project == config.Project.Name &&
                entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number)
                .OrderByDescending(entry => entry.AttemptNumber).ThenByDescending(entry => entry.StartedAtUtc).FirstOrDefault();
            if (source is not null && latest is not null && latest.ExecutionId != source.ExecutionId &&
                latest.FeatureBranch != source.FeatureBranch) source = null;
            if (source is null)
            {
                const string missingHistoryReason = "no recoverable integration-conflict execution history";
                if (!explicitRecovery)
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, null, missingHistoryReason, ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · integration recovery rejected · {missingHistoryReason}.";
                _operationalLog(diagnostic);
                _output.Warning(diagnostic);
                await telegram.IntegrationRecoveryRejectedAsync(config.Project.Name, config.Project.Repository, issue,
                    missingHistoryReason, ct);
                if (explicitRecovery) await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel, ct);
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            var lineage = new HashSet<Guid> { source.ExecutionId };
            while (source.RetryOfExecutionId is { } parentId)
            {
                var parent = allHistory.FirstOrDefault(entry => entry.ExecutionId == parentId);
                if (parent is null || parent.Project != source.Project || parent.Repository != source.Repository ||
                    parent.IssueNumber != source.IssueNumber || !lineage.Add(parent.ExecutionId))
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, source.ExecutionId,
                        "preserved recovery lineage is missing, cyclic or inconsistent", ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                // A normal implementation retry owns its own branch/worktree. Only integration
                // recovery attempts share their parent's preserved branch and workspace.
                if (parent.FeatureBranch != source.FeatureBranch) break;
                if (parent.State != "IntegrationConflict" || parent.RecoveryState is not (null or "integration-conflict"))
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, source.ExecutionId,
                        "the preserved workspace's original execution is no longer recoverable", ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                source = parent;
            }
            if (source.IntegrationRecoveryClaim is not null)
            {
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            if (assignment is not null && (assignment.Metadata["integrationRecoveryExecutionId"] != source.ExecutionId.ToString() ||
                !assignment.Metadata.TryGetValue("originalServerExecutionId", out var originalServerId) || originalServerId != source.ServerExecutionId))
                throw new WorkerInfrastructureException("Recovery assignment source differs from the preserved implementation.");
            issue = issue with { Title = source.IssueTitle, Body = source.OriginalIssueBody ?? issue.Body };
            string? integrationBase;
            string? rejectionReason;
            await _repositoryGate.WaitAsync(ct);
            try
            {
                rejectionReason = await IntegrationRecovery.ValidateSourceAsync(source, ct);
                integrationBase = rejectionReason is null ? await git.GetIntegrationBaseAsync(ct) : null;
            }
            finally { _repositoryGate.Release(); }
            if (!explicitRecovery && source.IntegrationRecoveryAttemptBase == integrationBase && integrationBase is not null)
            {
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            if (!explicitRecovery && integrationBase is null && rejectionReason is null)
                rejectionReason = "the authoritative integration base could not be identified";
            if (rejectionReason is not null)
            {
                if (!explicitRecovery)
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, source.ExecutionId, rejectionReason, ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution {source.ExecutionId} · integration recovery rejected · {FailureDiagnosticRedactor.Redact(rejectionReason, config.Environment.Variables.Values.ToArray())}.";
                _operationalLog(diagnostic);
                _output.Warning(diagnostic);
                await telegram.IntegrationRecoveryRejectedAsync(config.Project.Name, config.Project.Repository, issue, rejectionReason, ct);
                if (explicitRecovery) await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel, ct);
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            _operationalLog($"Integration recovery discovered · {config.Project.Name} · Issue #{issue.Number} · original execution {source.ExecutionId}");
            source = source with { RecoveryBaseCommit = source.RecoveryBaseCommit ?? source.CommitSha };
            issue = await PrepareCommentContextAsync(issue, ct);
            var execution = WorkerExecution.Create(config.Project, config.Git, issue,
                retryOfExecutionId: source.ExecutionId, attemptNumber: allHistory.Where(entry =>
                    entry.Project == config.Project.Name && entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number)
                    .Select(entry => entry.AttemptNumber).DefaultIfEmpty(0).Max() + 1,
                featureBranchOverride: source.FeatureBranch, codexSettings: config.Codex, settingsSource: source,
                resumed: true, serverExecutionId: assignment?.ServerExecutionId, assignmentId: assignment?.AssignmentId,
                ownershipGeneration: assignment?.Lease?.Generation);
            if (history is null || !await history.TryClaimIntegrationRecoveryAsync(source,
                    CreateInitialEntry(execution), integrationBase ?? "explicit", explicitRecovery, ct))
            {
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            claimedExecution = execution;
            await _output.StopWaitingAsync();
            await TransitionAsync(execution, ExecutionState.Claimed, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationConflictLabel, config.GitHub.IntegrationRecoveryLabel, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.WorkingLabel, ct);
            _operationalLog($"Integration recovery claimed · Issue #{issue.Number} · execution {execution.ExecutionId} · original execution {source.ExecutionId}");
            _output.IssueStarted(config.Project.Name, issue, execution);
            await telegram.StartingAsync(config.Project.Name, config.Project.Repository, issue, execution, ct);
            return ProcessClaimedAsync(execution, issue, source, issueKey, ct, integrationRecovery: true);
        }
        catch (Exception shutdownError) when (ct.IsCancellationRequested && shutdownToken.IsCancellationRequested && WorkerShutdown.IsCancellation(shutdownError))
        {
            _activeIssues.TryRemove(issueKey, out _);
            if (claimedExecution is not null) await RecordShutdownAsync(claimedExecution, ShutdownRecoveryState(shutdownError));
            throw new WorkerShutdownException(shutdownToken, shutdownError);
        }
        catch (Exception ex)
        {
            _activeIssues.TryRemove(issueKey, out _);
            if (claimedExecution is not null && !claimedExecution.IsTerminal)
                await RecordInfrastructureFailureAsync(claimedExecution, ex.Message,
                    GitHubOperationException.Find(ex) is { IsMutation: true } ? GitHubOperationException.ReconciliationRequiredState : "uncertain");
            if (claimedExecution is not null && ex is GitHubOperationException githubFailure)
                throw githubFailure.ForExecution(claimedExecution.ExecutionId);
            throw;
        }
    }

    public Task<IReadOnlyList<CodexProvisioning.IntegrationRecoveryCandidate>> DiscoverManagedIntegrationRecoveriesAsync(
        string projectId, CancellationToken ct) => IntegrationRecovery.DiscoverManagedAsync(projectId, ct);

    public async Task ReportIntegrationRecoveryRejectionAsync(Guid sourceId, string reason, CancellationToken ct)
    {
        if (history is null) return;
        var source = (await history.ReadAllAsync(ct)).FirstOrDefault(entry => entry.ExecutionId == sourceId &&
            entry.Project == config.Project.Name && entry.Repository == config.Project.Repository);
        if (source is not null) await IntegrationRecovery.ReportRejectedAsync(source.IssueNumber, source.ExecutionId, reason, ct);
    }

    /// <summary>Executes a Server assignment through the same claim, history and ExecutionRunner pipeline as standalone work.</summary>
    public async Task<Task<IssueProcessingResult?>?> ClaimAssignedAsync(WorkerAssignmentContract assignment, CancellationToken ct)
    {
        if (assignment.Lease is not { State: "Active", Generation: > 0 } lease ||
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
            if (!Guid.TryParse(recoveryId, out var id) || history is null ||
                !(await history.ReadAllAsync(ct)).Any(entry => entry.ExecutionId == id && entry.IssueNumber == issueNumber &&
                    entry.Project == config.Project.Name && entry.Repository == config.Project.Repository))
                throw new WorkerInfrastructureException("Recovery assignment does not match local preserved execution ownership.");
            return await ClaimIntegrationRecoveryAsync(issue, ct, explicitRecovery: false, assignment: assignment);
        }
        return await ClaimIssueAsync(issue, assignment.ServerExecutionId, assignment.AssignmentId, lease.Generation, ct);
    }

    private async Task<Task<IssueProcessingResult?>?> ClaimIssueAsync(GitHubIssue issue, string? serverExecutionId,
        string? assignmentId, long? ownershipGeneration, CancellationToken ct)
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
        var latest = issueHistory.FirstOrDefault();
        // Ready is an explicit request for new implementation after an integration conflict,
        // including older rows that recorded the conflict as a failed task.
        var freshAfterConflict = issue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true &&
            latest is not null && IsTerminalIntegrationConflict(latest);
        // Metadata validation and other explicitly classified pre-workspace failures have no
        // execution state to resume. A new ready label starts a clean attempt even in resume mode.
        var freshAfterPreparationFailure = issue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true &&
            latest?.RecoveryState == "preparation-failed";
        var retryOf = !freshAfterConflict && !freshAfterPreparationFailure && latest?.State is "Failed" or "Blocked" ? latest : null;
        var attemptNumber = issueHistory.Length == 0 ? 1 : issueHistory.Max(e => e.AttemptNumber) + 1;
        var resumed = retryOf is not null && config.Worker.RetryMode.Equals("resume", StringComparison.OrdinalIgnoreCase);
        issue = await PrepareCommentContextAsync(issue, ct);
        var execution = WorkerExecution.Create(config.Project, config.Git, issue, retryOfExecutionId: retryOf?.ExecutionId,
            attemptNumber: attemptNumber, resumed: resumed, serverExecutionId: serverExecutionId, assignmentId: assignmentId,
            ownershipGeneration: ownershipGeneration, codexSettings: config.Codex, settingsSource: retryOf);
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
            if (resumed && (retryOf!.RecoveryState != "recoverable" || string.IsNullOrWhiteSpace(retryOf.RecoveryBaseCommit)))
            {
                await RejectPreparationAsync(execution, issue, config.GitHub.ReadyLabel,
                    $"Previous execution {ExecutionFormatting.Display(retryOf.ExecutionId)} ({retryOf.ExecutionId}) has no safe recoverable state. Change worker.retryMode to restart or inspect the recovery workspace.", ct);
                _activeIssues.TryRemove(issueKey, out _);
                return serverExecutionId is null ? null : Task.FromResult<IssueProcessingResult?>(null);
            }
            if (freshAfterConflict)
                _operationalLog($"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] · explicit ready after integration conflict starts a fresh execution; previous execution {latest!.ExecutionId} is preserved.");
            if (freshAfterPreparationFailure)
                _operationalLog($"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] · explicit ready after clean preparation failure starts a fresh execution; previous execution {latest!.ExecutionId} is preserved.");
            await _output.StopWaitingAsync();
            await TransitionAsync(execution, ExecutionState.Claimed, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
            foreach (var staleLabel in new[] { config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                if (issue.Labels?.Contains(staleLabel, StringComparer.OrdinalIgnoreCase) == true)
                    await github.RemoveLabelAsync(issue.Number, staleLabel, ct);
            _operationalLog($"Scheduler · {config.Project.Name} / #{issue.Number} claimed · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}]");
            _output.IssueStarted(config.Project.Name, issue, execution);
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
        issue with { CommentContext = await github.GetIssueCommentContextAsync(issue.Number, ct,
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
        int issueKey, CancellationToken ct, bool integrationRecovery = false)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            var result = await RunExecutionAsync(new ExecutionContext(execution, issue, retryOf, integrationRecovery), ct);
            timer.Stop();
            await TransitionAsync(execution, ExecutionState.Reporting, ct);
            var report = result.Report with { Duration = timer.Elapsed, ExecutionId = execution.ExecutionId,
                AttemptNumber = execution.AttemptNumber, RetryOfExecutionId = execution.RetryOfExecutionId, Resumed = execution.Resumed, EffectiveModel = execution.CodexProfile?.EffectiveModel,
                EffectiveEffort = execution.CodexProfile?.Effort };
            var terminalState = result.Kind switch
            {
                IssueOutcomeKind.Succeeded => ExecutionState.Completed,
                IssueOutcomeKind.Blocked => ExecutionState.Blocked,
                IssueOutcomeKind.Failed => ExecutionState.Failed,
                IssueOutcomeKind.IntegrationConflict => ExecutionState.IntegrationConflict,
                IssueOutcomeKind.Superseded => ExecutionState.Superseded,
                _ => throw new ArgumentOutOfRangeException()
            };
            var reportedResult = result with { Report = report };
            try
            {
                await ReportResultAsync(issue, reportedResult, ct);
            }
            catch (GitHubOperationException)
            {
                // Keep the execution's own result authoritative when reporting it to GitHub
                // fails. The outer handler records the secondary failure and any uncertainty.
                await CompleteHistoryAsync(execution, report, terminalState, CancellationToken.None);
                throw;
            }
            await CompleteHistoryAsync(execution, report, terminalState, CancellationToken.None);
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
            await ReportInterruptedExecutionAsync(execution, issue, "Cancellation interrupted execution.", ex);
            throw new WorkerInfrastructureException("Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
        }
        catch (PreExecutionInfrastructureException ex)
        {
            await RejectPreparationAsync(execution, issue, config.GitHub.WorkingLabel, ex.Message, CancellationToken.None,
                ex.InnerException is ProjectCheckoutDirtyException ? null : "preparation-failed");
            return null;
        }
        catch (Exception ex)
        {
            await ReportInterruptedExecutionAsync(execution, issue, ex.Message, ex);
            if (ex is GitHubOperationException githubFailure)
                throw githubFailure.ForExecution(execution.ExecutionId);
            throw;
        }
        finally
        {
            _activeIssues.TryRemove(issueKey, out _);
        }
    }

    private async Task ReportInterruptedExecutionAsync(WorkerExecution execution, GitHubIssue issue, string reason,
        Exception? primaryFailure = null)
    {
        var safeReason = Limit(FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray()), 1200);
        var primaryGitHubFailure = primaryFailure is null ? null : GitHubOperationException.Find(primaryFailure);
        var recoveryState = primaryGitHubFailure is { IsMutation: true }
            ? GitHubOperationException.ReconciliationRequiredState : "uncertain";
        if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, safeReason, recoveryState);
        var diagnostic = primaryGitHubFailure is not null && execution.IsTerminal &&
            execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled)
            ? $"Execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · Issue #{issue.Number} · outcome {execution.State} · GitHub reporting failure · {safeReason}"
            : $"Execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · Issue #{issue.Number} · infrastructure failure · {safeReason}";
        if (execution.ServerExecutionId is not null)
            diagnostic = ManagedExecutionLog.Execution(CreateEntry(execution, null, null, null),
                primaryGitHubFailure is not null && execution.IsTerminal &&
                    execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled)
                    ? $"outcome {execution.State} · GitHub reporting failure · {safeReason}"
                    : $"infrastructure failure · {safeReason}",
                config.Environment.Variables.Values.ToArray());
        _operationalLog(diagnostic);
        if (execution.ServerExecutionId is null) _output.Warning(diagnostic);
        if (primaryGitHubFailure is not null)
        {
            if (history is not null)
            {
                if (execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled))
                {
                    var reportingFailure = Limit(FailureDiagnosticRedactor.Redact(primaryGitHubFailure.Message,
                        config.Environment.Variables.Values.ToArray()), 1600);
                    await history.UpdateReportingFailureAsync(execution.ExecutionId, reportingFailure, CancellationToken.None);
                }
                if (primaryGitHubFailure.IsMutation)
                    await history.UpdateRecoveryAsync(execution.ExecutionId, GitHubOperationException.ReconciliationRequiredState, CancellationToken.None);
            }
            _operationalLog($"Execution {execution.ExecutionId} · primary GitHub failure during {primaryGitHubFailure.Operation}; no further GitHub mutation will be attempted · remote Issue state {(primaryGitHubFailure.RemoteStateUncertain ? "uncertain" : primaryGitHubFailure.RemoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "read only")}.");
            return;
        }
        if (execution.IsTerminal && execution.State is not (ExecutionState.InfrastructureFailure or ExecutionState.Cancelled))
        {
            _operationalLog($"Execution {execution.ExecutionId} · outcome {execution.State} remains authoritative; secondary reporting failed with {safeReason} · no further GitHub mutation will be attempted.");
            return;
        }
        try
        {
            // These reporting mutations are attempted once. A failed mutation is retained for
            // reconciliation and must never be replayed as an automatic retry.
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, CancellationToken.None);
            await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
                $"### Execution interrupted\n\nExecution `{execution.ExecutionId}` stopped because of an infrastructure failure.\n\n{safeReason}\n\n" +
                (execution.CodexProfile is { } profile ? $"Codex model: `{profile.EffectiveModel ?? "unknown (CLI model unavailable)"}` · effort: `{profile.Effort}`.\n\n" : "") +
                "### Recovery\n\n- Inspect execution history and the preserved workspace before restarting.\n- Reconcile Git and GitHub state before requesting another attempt.\n", CancellationToken.None);
            if (history is not null)
            {
                var entry = (await history.ReadAllAsync(CancellationToken.None)).Single(row => row.ExecutionId == execution.ExecutionId);
                await ReportServerAsync(entry, execution.State, CancellationToken.None);
            }
        }
        catch (GitHubOperationException reportingFailure)
        {
            var secondaryMessage = Limit(FailureDiagnosticRedactor.Redact(reportingFailure.Message,
                config.Environment.Variables.Values.ToArray()), 1600);
            var correlated = reportingFailure.DuringReporting(execution.ExecutionId, safeReason);
            var followUp = reportingFailure.IsMutation && reportingFailure.RemoteStateUncertain
                ? "manual reconciliation is required"
                : "project scheduling will pause until GitHub reporting recovers";
            _operationalLog($"Execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · primary failure · {safeReason} · secondary GitHub reporting failure · {secondaryMessage} · remote Issue state {(reportingFailure.RemoteStateUncertain ? "uncertain" : reportingFailure.RemoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "read only")} · {followUp}.");
            _output.Warning($"Execution {execution.ExecutionId} · secondary GitHub reporting failure; primary failure remains authoritative. {secondaryMessage}");
            if (history is not null)
            {
                await history.UpdateReportingFailureAsync(execution.ExecutionId, secondaryMessage, CancellationToken.None);
                if (reportingFailure.IsMutation)
                    await history.UpdateRecoveryAsync(execution.ExecutionId, GitHubOperationException.ReconciliationRequiredState, CancellationToken.None);
            }
            throw correlated;
        }
    }

    private async Task RejectPreparationAsync(WorkerExecution execution, GitHubIssue issue, string claimedLabel,
        string reason, CancellationToken ct, string? recoveryState = "uncertain")
    {
        var safeReason = Limit(FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray()), 1200);
        await RecordInfrastructureFailureAsync(execution, safeReason, recoveryState);
        var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · preparation rejected · {safeReason}";
        if (execution.ServerExecutionId is not null)
            diagnostic = ManagedExecutionLog.Execution(CreateEntry(execution, null, null, null), $"preparation rejected · {safeReason}",
                config.Environment.Variables.Values.ToArray());
        _operationalLog(diagnostic);
        if (execution.ServerExecutionId is null) _output.Warning(diagnostic);
        // Remove eligibility before reporting so this Issue cannot repeatedly consume capacity.
        // A failed GitHub update remains infrastructure failure: its remote state is uncertain.
        try
        {
            await github.ReplaceLabelAsync(issue.Number, claimedLabel, config.GitHub.BlockedLabel, ct);
            await telegram.PreparationRejectedAsync(config.Project.Name, config.Project.Repository, issue,
                execution.ExecutionId, safeReason, CancellationToken.None);
            await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
                $"### Preparation rejected\n\nExecution `{execution.ExecutionId}` could not start.\n\n{safeReason}\n\n### Recovery\n\n" +
                $"- Inspect the previous execution and preserved workspace.\n- Correct the preparation problem or select `worker.retryMode: restart`.\n- Apply `{config.GitHub.ReadyLabel}` for a new attempt, or `{config.GitHub.IntegrationRecoveryLabel}` to recover a preserved integration conflict.\n", ct);
        }
        catch (GitHubOperationException reportingFailure)
        {
            var secondary = Limit(FailureDiagnosticRedactor.Redact(reportingFailure.Message,
                config.Environment.Variables.Values.ToArray()), 1600);
            _operationalLog($"Execution {execution.ExecutionId} · primary preparation failure · {safeReason} · secondary GitHub reporting failure · {secondary} · remote Issue state {(reportingFailure.RemoteStateUncertain ? "uncertain" : "known unchanged")}.");
            if (history is not null)
            {
                await history.UpdateReportingFailureAsync(execution.ExecutionId, secondary, CancellationToken.None);
                if (reportingFailure.IsMutation)
                    await history.UpdateRecoveryAsync(execution.ExecutionId, GitHubOperationException.ReconciliationRequiredState, CancellationToken.None);
            }
            throw reportingFailure.DuringReporting(execution.ExecutionId, safeReason);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _output.Startup(config.Project.Name, config.Project.Repository);
        var safelyIdle = false;
        try
        {
            if (!File.Exists(config.Codex.InstructionsFile))
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
            _output.InfrastructureFailure($"Infrastructure failure: {infrastructure.Message}");
            await telegram.CriticalAsync(config.Project.Name, infrastructure.Message, CancellationToken.None);
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
            OriginalIssueBody = FailureDiagnosticRedactor.Redact(execution.IssueBody, config.Environment.Variables.Values.ToArray())
        };

    private Task<IssueProcessingResult> RunExecutionAsync(ExecutionContext context, CancellationToken ct)
    {
        // Mutable branch/worktree state belongs to this attempt. Integration still targets its shared repository.
        var executionRepository = git.CreateExecutionRepository();
        var runner = new ExecutionRunner(config, executionRepository, codex, validation, _output, history, _repositoryGate,
            (entry, state, token) => ReportServerAsync(entry, state, token), IsAuthoritativeAsync, shutdownToken);
        return runner.RunAsync(context, ct);
    }

    private async Task<bool> IsAuthoritativeAsync(WorkerExecution execution, CancellationToken ct)
    {
        if (!await github.IsIssueOpenAsync(execution.IssueNumber, ct)) return false;
        if (history is null) return true;
        var sameIssue = (await history.ReadAllAsync(ct)).Where(entry =>
            entry.Project.Equals(execution.Project, StringComparison.OrdinalIgnoreCase) &&
            entry.Repository.Equals(execution.Repository, StringComparison.OrdinalIgnoreCase) &&
            entry.IssueNumber == execution.IssueNumber && entry.ExecutionId != execution.ExecutionId).ToArray();
        return !sameIssue.Any(entry => entry.AttemptNumber > execution.AttemptNumber ||
            entry.State == "Completed" && entry.AttemptNumber >= execution.AttemptNumber);
    }

    private Task TransitionAsync(WorkerExecution execution, ExecutionState state, CancellationToken ct)
    {
        execution.TransitionTo(state);
        var entry = CreateEntry(execution, null, null, null);
        return SaveAndReportAsync(entry, state, ct);
    }

    private async Task SaveAndReportAsync(ExecutionHistoryEntry entry, ExecutionState state, CancellationToken ct)
    {
        if (history is not null) await history.UpdateAsync(entry, ct);
        await ReportServerAsync(entry, state, ct);
    }

    private async Task ReportServerAsync(ExecutionHistoryEntry entry, ExecutionState state, CancellationToken ct)
    {
        if (entry.ServerExecutionId is null) return;
        var terminal = state is ExecutionState.Completed or ExecutionState.Failed or ExecutionState.Blocked or
            ExecutionState.IntegrationConflict or ExecutionState.Superseded or ExecutionState.InfrastructureFailure or ExecutionState.Cancelled;
        if (serverSettings is null || !serverSettings.Enabled) return;
        var stateName = state == ExecutionState.Completed ? "Completed" : state is ExecutionState.Failed or ExecutionState.Blocked or ExecutionState.IntegrationConflict or ExecutionState.Superseded or ExecutionState.InfrastructureFailure or ExecutionState.Cancelled ? "Failed" : "Running";
        if (entry.OwnershipGeneration is null)
        {
            _operationalLog(ManagedExecutionLog.Execution(entry,
                "infrastructure failure · Managed execution is missing its ownership generation.", config.Environment.Variables.Values.ToArray()));
            throw new WorkerInfrastructureException("Managed execution is missing its ownership generation.");
        }
        await _registration.ReportExecutionAsync(serverSettings, entry, stateName,
            stateName == "Running" ? (state switch
            {
                ExecutionState.Claimed => "Claiming",
                ExecutionState.Preparing => "Preparing",
                ExecutionState.Implementing => "Codex",
                ExecutionState.Validating or ExecutionState.Repairing => "Validation",
                ExecutionState.Integrating => "Integration",
                ExecutionState.Reporting => "Reporting",
                _ => state.ToString()
            }) : null, entry.OwnershipGeneration ?? 0, ct);
        if (terminal)
            _operationalLog(ManagedExecutionLog.ReportCompleted(entry,
                state.ToString(), config.Environment.Variables.Values.ToArray()));
    }

    private async Task CompleteHistoryAsync(WorkerExecution execution, IssueExecutionReport report, ExecutionState state,
        CancellationToken ct)
    {
        execution.TransitionTo(state);
        var entry = CreateEntry(execution, report, null, null);
        if (execution.MetadataError is not null) entry = entry with { RecoveryState = "preparation-failed" };
        try { await SaveHistoryAsync(entry, ct); }
        catch (WorkerInfrastructureException ex) { _output.Warning($"Execution completed, but its final history details could not be saved: {ex.Message}"); }
    }

    private async Task RecordShutdownAsync(WorkerExecution execution, string recoveryState = "uncertain")
    {
        if (execution.IsTerminal) return; // ExecutionRunner already recorded the workspace interruption.
        execution.TransitionTo(ExecutionState.Cancelled);
        const string reason = "Execution interrupted by Worker shutdown";
        _output.Warning($"Execution {execution.ExecutionId} · {reason}");
        await SaveHistoryAsync(CreateEntry(execution, null, null, reason) with { RecoveryState = recoveryState }, CancellationToken.None);
        // Keep remote ownership and labels unchanged. Restart/lease reconciliation must inspect
        // uncertain Git/GitHub state before any new attempt; shutdown must not perform remote mutations.
    }

    private static string ShutdownRecoveryState(Exception error) =>
        GitHubOperationException.Find(error) is { IsMutation: true }
            ? GitHubOperationException.ReconciliationRequiredState : "uncertain";

    private async Task RecordInfrastructureFailureAsync(WorkerExecution execution, string reason, string? recoveryState = "uncertain")
    {
        if (!execution.IsTerminal) execution.TransitionTo(ExecutionState.InfrastructureFailure);
        try
        {
            var entry = CreateEntry(execution, null, null, reason) with { RecoveryState = recoveryState };
            await SaveHistoryAsync(entry, CancellationToken.None);
            await ReportServerAsync(entry, execution.State, CancellationToken.None);
        }
        catch (WorkerInfrastructureException) { /* Preserve the original infrastructure failure. The existing row remains incomplete. */ }
    }

    private Task SaveHistoryAsync(ExecutionHistoryEntry entry, CancellationToken ct) =>
        history is null ? Task.CompletedTask : history.UpdateAsync(entry, ct);

    private static ExecutionHistoryEntry CreateEntry(WorkerExecution execution, IssueExecutionReport? report, TimeSpan? duration, string? failure) =>
        new(execution.ExecutionId, execution.Project, execution.Repository, execution.IssueNumber, execution.IssueTitle,
            execution.FeatureBranch, execution.BaseBranch, execution.StartedAtUtc,
            execution.IsTerminal ? DateTimeOffset.UtcNow : null, execution.State.ToString(),
            execution.IsTerminal ? (long)(duration ?? (DateTimeOffset.UtcNow - execution.StartedAtUtc)).TotalMilliseconds : null,
            report?.ImplementationSummary,
            report?.FailureCategory == "Post-rebase validation failed" ? report.PostRebaseValidationOutcome :
            report?.Integration is not null || report?.FailureCategory == "Integration conflict" ? "passed" : report?.FinalValidationFailure is not null ? $"failed: {report.FinalValidationFailure}" :
                report is { ValidationRepairs.Count: > 0 } ? "failed or interrupted" : null,
            report?.ValidationRepairs.Count ?? 0, report?.ValidationRepairs ?? [],
            report?.Integration?.CommitSha ?? Extract(report?.Integration?.Summary, "Committed as `([^`]+)`"),
            report?.Integration?.IntegrationBranch ?? Extract(report?.Integration?.Summary, "Merged into `([^`]+)`"),
            report?.Integration is { HasChanges: true } integration ? integration.CompletedBranch : null,
            failure ?? report?.Failure ?? report?.HumanInput, RetryOfExecutionId: execution.RetryOfExecutionId,
            AttemptNumber: execution.AttemptNumber, Resumed: execution.Resumed,
            ServerExecutionId: execution.ServerExecutionId, EffectiveModel: execution.CodexProfile?.EffectiveModel, EffectiveEffort: execution.CodexProfile?.Effort,
            ModelSelectedByCli: execution.CodexProfile is { Model: null },
            AssignmentId: execution.AssignmentId,
            OwnershipGeneration: execution.OwnershipGeneration);

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task ReportResultAsync(GitHubIssue issue, IssueProcessingResult result, CancellationToken ct)
    {
        // Any failed GitHub operation is infrastructure failure. Stop and leave the partial state for a human to reconcile.
        switch (result.Kind)
        {
            case IssueOutcomeKind.Succeeded:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.DoneLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + result.Summary, ct);
                await github.CloseAsync(issue.Number, ct);
                await telegram.SuccessAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, TelegramCompletion(result.Report), ct);
                _output.IssueCompleted(issue, result.Report.Duration, result.Report.ExecutionId!.Value,
                    result.Report.AttemptNumber, result.Report.RetryOfExecutionId);
                break;
            case IssueOutcomeKind.Blocked:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + result.Summary, ct);
                await telegram.BlockedAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, FailureDiagnosticRedactor.Redact(
                        result.Report.HumanInput ?? "A required prerequisite is unavailable.", result.Report.SecretValues), ct);
                _output.IssueBlocked(issue, result.Report.Duration, result.Report.ExecutionId!.Value,
                    FailureDiagnosticRedactor.Redact(result.Report.HumanInput ?? "A required prerequisite is unavailable.", result.Report.SecretValues));
                break;
            case IssueOutcomeKind.Failed:
                var message = result.Summary;
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.FailedLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + message, ct);
                await telegram.FailedAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, "See the Issue report for validation diagnostics and recovery details.", ct);
                var recoveryDetails = $"execution {ExecutionFormatting.Display(result.Report.ExecutionId!.Value)}" +
                    (result.Report.RecoveryBranch is null ? " · workspace not preserved · retry/resume unavailable" :
                        $" · workspace preserved on {result.Report.RecoveryBranch} · retry/resume {(result.Report.RetryAvailable ? "available" : "unavailable")}");
                _output.IssueFailed(issue, result.Report.Duration, result.Report.ExecutionId!.Value, recoveryDetails);
                break;
            case IssueOutcomeKind.IntegrationConflict:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.IntegrationConflictLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) + result.Summary, ct);
                await telegram.FailedAsync(config.Project.Name, config.Project.Repository, issue, result.Report.Duration,
                    result.Report.ExecutionId!.Value, FailureDiagnosticRedactor.Redact($"Integration recovery is required after {result.Report.ValidationRepairs.Count(repair => repair.IntegrationRepair)} integration repair attempt(s). " +
                        (result.Report.FinalValidationDiagnostics ?? result.Report.Failure ?? "See the Issue report."), result.Report.SecretValues), ct);
                var conflictDetails = $"execution {ExecutionFormatting.Display(result.Report.ExecutionId!.Value)}" +
                    (result.Report.WorkspacePreserved ? $" · implementation workspace preserved on {result.Report.RecoveryBranch} · integration recovery available" :
                        " · implementation workspace preservation could not be verified");
                _output.IssueFailed(issue, result.Report.Duration, result.Report.ExecutionId!.Value, conflictDetails);
                break;
            case IssueOutcomeKind.Superseded:
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, ct);
                await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
                    $"### Execution superseded\n\nExecution `{result.Report.ExecutionId}` did not integrate.\n\n{result.Report.Failure}\n\n" +
                    "### Recovery\n\n- Inspect the later execution or closed Issue before requesting another attempt.\n", ct);
                _operationalLog($"Execution {result.Report.ExecutionId} · Issue #{issue.Number} · superseded; integration skipped and workspace retained.");
                _output.Warning($"Execution {result.Report.ExecutionId} · Issue #{issue.Number} · superseded; integration skipped and workspace retained.");
                break;
        }
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        // Compatibility loop remains available to existing workflow tests; production polling is owned by WorkerHost.
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    }

    private static string TelegramCompletion(IssueExecutionReport report)
    {
        var details = new List<string>();
        if (report.Integration is not null) details.Add(report.Integration.Summary);
        var integrationRepairs = report.ValidationRepairs.Count(repair => repair.IntegrationRepair);
        if (integrationRepairs > 0) details.Add($"Integration repair succeeded after {integrationRepairs} attempt(s).");
        if (!string.IsNullOrWhiteSpace(report.ImplementationSummary)) details.Add($"Codex summary: {report.ImplementationSummary}");
        return string.Join("\n\n", details);
    }
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 20)] + " … [truncated]";
}
