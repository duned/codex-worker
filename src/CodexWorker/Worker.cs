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
    SemaphoreSlim? repositoryGate = null, WorkerServerSettings? serverSettings = null, Action<string>? operationalLog = null)
{
    private readonly WorkerConsole _output = output ?? new WorkerConsole();
    private readonly Action<string> _operationalLog = operationalLog ?? (_ => { });
    private readonly SemaphoreSlim _repositoryGate = repositoryGate ?? new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<int, byte> _activeIssues = new();

    public WorkerConfiguration Configuration => config;

    public async Task PrepareForHostAsync(CancellationToken ct)
    {
        if (!File.Exists(config.Codex.InstructionsFile))
            throw new WorkerInfrastructureException($"Configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
        await git.InitializeAsync(ct);
    }

    /// <summary>Checks this project's queue once and processes at most one claimed Issue.</summary>
    public async Task<IssueProcessingResult?> ProcessOneAsync(CancellationToken ct)
    {
        var execution = await ClaimNextAsync(ct);
        return execution is null ? null : await execution;
    }

    /// <summary>Claims the next eligible Issue and returns its independent execution task, if one was claimed.</summary>
    public async Task<Task<IssueProcessingResult?>?> ClaimNextAsync(CancellationToken ct)
    {
        var recoveryExcluded = new HashSet<int>();
        while (true)
        {
            GitHubIssue? recoveryIssue;
            try { recoveryIssue = await github.FindOldestReadyAsync(config.GitHub.IntegrationRecoveryLabel, recoveryExcluded, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            if (recoveryIssue is null || !recoveryExcluded.Add(recoveryIssue.Number)) break;
            if (recoveryIssue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true)
            {
                var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{recoveryIssue.Number} · explicit ready label takes precedence over integration recovery.";
                _operationalLog(diagnostic);
                continue;
            }
            var recovery = await ClaimIntegrationRecoveryAsync(recoveryIssue, ct);
            if (recovery is not null) return recovery;
        }
        var excluded = new HashSet<int>();
        while (true)
        {
            GitHubIssue? issue;
            try { issue = await github.FindOldestReadyAsync(config.GitHub.ReadyLabel, excluded, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            if (issue is null || !excluded.Add(issue.Number)) return null;
            var execution = await ClaimIssueAsync(issue, null, null, null, ct);
            if (execution is not null) return execution;
        }
    }

    private async Task<Task<IssueProcessingResult?>?> ClaimIntegrationRecoveryAsync(GitHubIssue issue, CancellationToken ct)
    {
        var issueKey = issue.Number;
        if (!_activeIssues.TryAdd(issueKey, 0)) return null;
        try
        {
            IReadOnlyList<ExecutionHistoryEntry> allHistory = history is null ? [] : await history.ReadAllAsync(ct);
            ExecutionHistoryEntry[] prior = allHistory.Where(entry =>
                entry.Project == config.Project.Name && entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number &&
                entry.State == "IntegrationConflict" && entry.RecoveryState is (null or "integration-conflict"))
                .OrderByDescending(entry => entry.StartedAtUtc).ToArray();
            var source = prior.FirstOrDefault();
            if (source is null)
            {
                const string missingHistoryReason = "no recoverable integration-conflict execution history";
                var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · integration recovery rejected · {missingHistoryReason}.";
                _operationalLog(diagnostic);
                _output.Warning(diagnostic);
                await telegram.IntegrationRecoveryRejectedAsync(config.Project.Name, config.Project.Repository, issue,
                    missingHistoryReason, ct);
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel, ct);
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            while (source.RetryOfExecutionId is { } parentId)
            {
                var parent = allHistory.FirstOrDefault(entry => entry.ExecutionId == parentId);
                if (parent is null || parent.State != "IntegrationConflict" || parent.RecoveryState is not (null or "integration-conflict")) break;
                source = parent;
            }
            var rejectionReason = await git.ValidateIntegrationRecoveryAsync(source, ct);
            if (rejectionReason is not null)
            {
                var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution {source.ExecutionId} · integration recovery rejected · {FailureDiagnosticRedactor.Redact(rejectionReason, config.Environment.Variables.Values.ToArray())}.";
                _operationalLog(diagnostic);
                _output.Warning(diagnostic);
                await telegram.IntegrationRecoveryRejectedAsync(config.Project.Name, config.Project.Repository, issue, rejectionReason, ct);
                await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel, ct);
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            source = source with { RecoveryBaseCommit = source.RecoveryBaseCommit ?? source.CommitSha };
            var execution = WorkerExecution.Create(config.Project, config.Git, issue,
                retryOfExecutionId: source.ExecutionId, attemptNumber: allHistory.Where(entry =>
                    entry.Project == config.Project.Name && entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number)
                    .Select(entry => entry.AttemptNumber).DefaultIfEmpty(0).Max() + 1,
                featureBranchOverride: source.FeatureBranch);
            await CreateHistoryAsync(execution, ct);
            await _output.StopWaitingAsync();
            await TransitionAsync(execution, ExecutionState.Claimed, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationConflictLabel, config.GitHub.IntegrationRecoveryLabel, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.WorkingLabel, ct);
            _output.IssueStarted(config.Project.Name, issue, execution);
            return ProcessClaimedAsync(execution, issue, source, issueKey, ct, integrationRecovery: true);
        }
        catch
        {
            _activeIssues.TryRemove(issueKey, out _);
            throw;
        }
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
        var latest = issueHistory.FirstOrDefault();
        // Ready is an explicit request for new implementation after an integration conflict,
        // including older rows that recorded the conflict as a failed task.
        var freshAfterConflict = issue.Labels?.Contains(config.GitHub.ReadyLabel, StringComparer.OrdinalIgnoreCase) == true &&
            latest is not null && IsTerminalIntegrationConflict(latest);
        var retryOf = !freshAfterConflict && latest?.State is "Failed" or "Blocked" ? latest : null;
        var attemptNumber = issueHistory.Length == 0 ? 1 : issueHistory.Max(e => e.AttemptNumber) + 1;
        var resumed = retryOf is not null && config.Worker.RetryMode.Equals("resume", StringComparison.OrdinalIgnoreCase);
        var execution = WorkerExecution.Create(config.Project, config.Git, issue, retryOfExecutionId: retryOf?.ExecutionId,
            attemptNumber: attemptNumber, resumed: resumed, serverExecutionId: serverExecutionId, assignmentId: assignmentId,
            ownershipGeneration: ownershipGeneration);
        await CreateHistoryAsync(execution, ct);
        if (ct.IsCancellationRequested)
        {
            await TransitionAsync(execution, ExecutionState.Cancelled, CancellationToken.None);
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
            await _output.StopWaitingAsync();
            await TransitionAsync(execution, ExecutionState.Claimed, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.ReadyLabel, config.GitHub.WorkingLabel, ct);
            foreach (var staleLabel in new[] { config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                if (issue.Labels?.Contains(staleLabel, StringComparer.OrdinalIgnoreCase) == true)
                    await github.RemoveLabelAsync(issue.Number, staleLabel, ct);
            _operationalLog($"Scheduler · #{issue.Number} claimed · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}]");
            _output.IssueStarted(config.Project.Name, issue, execution);
            await telegram.StartingAsync(config.Project.Name, config.Project.Repository, issue, execution, ct);
            return ProcessClaimedAsync(execution, issue, retryOf, issueKey, ct);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, "Cancellation interrupted execution.");
            throw new WorkerInfrastructureException("Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
        }
        catch (Exception ex)
        {
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, ex.Message);
            _activeIssues.TryRemove(issueKey, out _);
            throw;
        }
        }
        catch
        {
            _activeIssues.TryRemove(issueKey, out _);
            throw;
        }
    }

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
                AttemptNumber = execution.AttemptNumber, RetryOfExecutionId = execution.RetryOfExecutionId, Resumed = execution.Resumed };
            await ReportResultAsync(issue, result with { Report = report }, ct);
            await CompleteHistoryAsync(execution, report, result.Kind switch
            {
                IssueOutcomeKind.Succeeded => ExecutionState.Completed,
                IssueOutcomeKind.Blocked => ExecutionState.Blocked,
                IssueOutcomeKind.Failed => ExecutionState.Failed,
                IssueOutcomeKind.IntegrationConflict => ExecutionState.IntegrationConflict,
                IssueOutcomeKind.Superseded => ExecutionState.Superseded,
                _ => throw new ArgumentOutOfRangeException()
            }, CancellationToken.None);
            var finalEntry = history is null ? CreateEntry(execution, report, null, null) :
                (await history.ReadAllAsync(CancellationToken.None)).FirstOrDefault(entry => entry.ExecutionId == execution.ExecutionId)
                ?? CreateEntry(execution, report, null, null);
            await ReportServerAsync(finalEntry, execution.State, CancellationToken.None);
            return result with { Report = report };
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            await ReportInterruptedExecutionAsync(execution, issue, "Cancellation interrupted execution.");
            throw new WorkerInfrastructureException("Cancellation interrupted an operation while Issue or repository state may be uncertain; inspect before restarting.", ex);
        }
        catch (PreExecutionInfrastructureException ex)
        {
            await RejectPreparationAsync(execution, issue, config.GitHub.WorkingLabel, ex.Message, CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            await ReportInterruptedExecutionAsync(execution, issue, ex.Message);
            throw;
        }
        finally
        {
            _activeIssues.TryRemove(issueKey, out _);
        }
    }

    private async Task ReportInterruptedExecutionAsync(WorkerExecution execution, GitHubIssue issue, string reason)
    {
        var safeReason = Limit(FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray()), 1200);
        if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, safeReason);
        var diagnostic = $"Execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · Issue #{issue.Number} · infrastructure failure · {safeReason}";
        _operationalLog(diagnostic);
        _output.Warning(diagnostic);
        // Do not release the scheduler as a safe task failure. Remote reporting failure also
        // propagates so the host stops with its capacity reservation intact.
        await github.ReplaceLabelAsync(issue.Number, config.GitHub.WorkingLabel, config.GitHub.BlockedLabel, CancellationToken.None);
        await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
            $"### Execution interrupted\n\nExecution `{execution.ExecutionId}` stopped because of an infrastructure failure.\n\n{safeReason}\n\n" +
            "### Recovery\n\n- Inspect execution history and the preserved workspace before restarting.\n- Reconcile Git and GitHub state before requesting another attempt.\n", CancellationToken.None);
        if (history is not null)
        {
            var entry = (await history.ReadAllAsync(CancellationToken.None)).Single(row => row.ExecutionId == execution.ExecutionId);
            await ReportServerAsync(entry, execution.State, CancellationToken.None);
        }
    }

    private async Task RejectPreparationAsync(WorkerExecution execution, GitHubIssue issue, string claimedLabel,
        string reason, CancellationToken ct)
    {
        var safeReason = Limit(FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray()), 1200);
        await RecordInfrastructureFailureAsync(execution, safeReason);
        var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution [{ExecutionFormatting.ShortId(execution.ExecutionId)}] ({execution.ExecutionId}) · preparation rejected · {safeReason}";
        _operationalLog(diagnostic);
        _output.Warning(diagnostic);
        // Remove eligibility before reporting so this Issue cannot repeatedly consume capacity.
        // A failed GitHub update remains infrastructure failure: its remote state is uncertain.
        await github.ReplaceLabelAsync(issue.Number, claimedLabel, config.GitHub.BlockedLabel, ct);
        await telegram.PreparationRejectedAsync(config.Project.Name, config.Project.Repository, issue,
            execution.ExecutionId, safeReason, CancellationToken.None);
        await github.CommentAsync(issue.Number, IssueFormatting.ReportHeading(issue) +
            $"### Preparation rejected\n\nExecution `{execution.ExecutionId}` could not start.\n\n{safeReason}\n\n### Recovery\n\n" +
            $"- Inspect the previous execution and preserved workspace.\n- Correct the preparation problem or select `worker.retryMode: restart`.\n- Apply `{config.GitHub.ReadyLabel}` for a new attempt, or `{config.GitHub.IntegrationRecoveryLabel}` to recover a preserved integration conflict.\n", ct);
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
        history is null ? Task.CompletedTask : history.CreateAsync(CreateEntry(execution, null, null, null), ct);

    private Task<IssueProcessingResult> RunExecutionAsync(ExecutionContext context, CancellationToken ct)
    {
        // Mutable branch/worktree state belongs to this attempt. Integration still targets its shared repository.
        var executionRepository = git.CreateExecutionRepository();
        var runner = new ExecutionRunner(config, executionRepository, codex, validation, _output, history, _repositoryGate,
            (entry, state, token) => ReportServerAsync(entry, state, token), IsAuthoritativeAsync);
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
        if (serverSettings is null || !serverSettings.Enabled || entry.ServerExecutionId is null) return;
        var stateName = state == ExecutionState.Completed ? "Completed" : state is ExecutionState.Failed or ExecutionState.Blocked or ExecutionState.Superseded or ExecutionState.InfrastructureFailure or ExecutionState.Cancelled ? "Failed" : "Running";
        if (serverSettings is { Enabled: true } && entry.ServerExecutionId is not null && entry.OwnershipGeneration is null)
            throw new WorkerInfrastructureException("Managed execution is missing its ownership generation.");
        await new WorkerRegistrationClient().ReportExecutionAsync(serverSettings, entry, stateName,
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
    }

    private async Task CompleteHistoryAsync(WorkerExecution execution, IssueExecutionReport report, ExecutionState state,
        CancellationToken ct)
    {
        execution.TransitionTo(state);
        try { await SaveHistoryAsync(CreateEntry(execution, report, null, null), ct); }
        catch (WorkerInfrastructureException ex) { _output.Warning($"Execution completed, but its final history details could not be saved: {ex.Message}"); }
    }

    private async Task RecordInfrastructureFailureAsync(WorkerExecution execution, string reason)
    {
        if (!execution.IsTerminal) execution.TransitionTo(ExecutionState.InfrastructureFailure);
        try
        {
            var entry = CreateEntry(execution, null, null, reason);
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
            report?.FailureCategory == "Post-rebase validation failed" ? "failed: post-rebase validation (2 attempts)" :
            report?.Integration is not null || report?.FailureCategory == "Integration conflict" ? "passed" : report?.FinalValidationFailure is not null ? $"failed: {report.FinalValidationFailure}" :
                report is { ValidationRepairs.Count: > 0 } ? "failed or interrupted" : null,
            report?.ValidationRepairs.Count ?? 0, report?.ValidationRepairs ?? [],
            report?.Integration?.CommitSha ?? Extract(report?.Integration?.Summary, "Committed as `([^`]+)`"),
            report?.Integration?.IntegrationBranch ?? Extract(report?.Integration?.Summary, "Merged into `([^`]+)`"),
            report?.Integration is { HasChanges: true } integration ? integration.CompletedBranch : null,
            failure ?? report?.Failure ?? report?.HumanInput, RetryOfExecutionId: execution.RetryOfExecutionId,
            AttemptNumber: execution.AttemptNumber, Resumed: execution.Resumed,
            ServerExecutionId: execution.ServerExecutionId, AssignmentId: execution.AssignmentId,
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
                    result.Report.ExecutionId!.Value, result.Report.HumanInput ?? "Human input is required.", ct);
                _output.IssueBlocked(issue, result.Report.Duration, result.Report.ExecutionId!.Value);
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
                    result.Report.ExecutionId!.Value, "Implementation is complete; integration recovery is required.", ct);
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
        if (!string.IsNullOrWhiteSpace(report.ImplementationSummary)) details.Add($"Codex summary: {report.ImplementationSummary}");
        return string.Join("\n\n", details);
    }
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(length - 20)] + " … [truncated]";
}
