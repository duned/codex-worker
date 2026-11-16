using System.Text.RegularExpressions;

namespace CodexWorker;

/// <summary>Explicit identity and Issue input for one execution attempt.</summary>
public sealed record ExecutionContext(WorkerExecution Execution, GitHubIssue Issue, ExecutionHistoryEntry? RetryOf = null,
    bool IntegrationRecovery = false, bool InterruptedExecution = false, bool ResolveIntegrationConflict = false);

/// <summary>
/// Runs the repository workspace, implementation engine, validation, repair, and integration lifecycle for one execution.
/// Repository mutations that share Git metadata are serialized per repository while implementation and
/// validation use the independent execution worktree concurrently.
/// </summary>
public sealed partial class ExecutionRunner(WorkerConfiguration config, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, WorkerConsole output, ExecutionHistoryStore? history = null, SemaphoreSlim? repositoryGate = null,
    Func<ExecutionHistoryEntry, ExecutionState, CancellationToken, Task>? reportServer = null,
    Func<WorkerExecution, CancellationToken, Task<bool>>? isAuthoritative = null, CancellationToken shutdownToken = default, TimeProvider? timeProvider = null,
    ImplementationEngineSelector? engines = null, IGitHubClient? github = null)
{
    private ImplementationRequest Request(ExecutionContext context) => new(git.ExecutionDirectory, config.Codex.InstructionsFile,
        // Completion compares the verified live payload; engine prompts retain the redacted original intent.
        context.ResolveIntegrationConflict && context.RetryOf?.OriginalIssueBody is { } body
            ? context.Issue with { Body = body } : context.Issue,
        context.RetryOf, context.Execution.Resumed, context.Execution.AttemptNumber,
        context.Execution.CodexProfile?.EffectiveModel, context.Execution.CodexProfile?.Effort);

    private static bool IsInterruptedValidation(ExecutionContext context) => context.RetryOf is { } source &&
        (source.ValidationOutcome is "pending: validation" or "legacy: pending validation") &&
        source.CodexRecovery?.ImplementationSucceeded == true;

    private readonly SemaphoreSlim _repositoryGate = repositoryGate ?? new SemaphoreSlim(1, 1);
    public async Task<IssueProcessingResult> RunAsync(ExecutionContext context, CancellationToken ct)
    {
        var execution = context.Execution;
        var issue = context.Issue;
        var executionCodex = execution.CodexProfile is { } profile ? codex.WithProfile(profile) : codex;
        executionCodex = executionCodex.WithSessionObserver(async session =>
        {
            execution.CodexThreadId = session;
            execution.CodexSessionOwner = CodexSessionOwnership.Current;
            if (execution.CodexRecovery is { } recovery)
            {
                execution.CodexRecovery = recovery with { SessionId = session };
                if (context.RetryOf?.RecoveryState == "codex-interrupted" && session != recovery.SessionId)
                    output.Warning($"Execution {execution.ExecutionId} · continuing preserved workspace with Codex session {session}.");
            }
            // Record startup durably even if the invocation is subsequently cancelled.
            await SaveHistoryAsync(CreateEntry(execution, null, null, null), CancellationToken.None);
        });
        executionCodex = executionCodex.WithModelObserver(model =>
        {
            execution.RecordCliModel(model);
            output.CodexProfile(execution);
        });
        try
        {
            await TransitionAsync(execution, ExecutionState.Preparing, ct);
            if (execution.MetadataError is { } metadataError)
            {
                return new IssueProcessingResult(IssueOutcomeKind.Blocked,
                    new IssueExecutionReport(null, [], HumanInput: metadataError));
            }
            var identity = execution.CodexProfile?.Engine ?? "codex";
            var engineLabel = identity == "ccc" ? "CCC" : "Codex";
            if (context.RetryOf is { } source && source.Engine != identity)
                return new IssueProcessingResult(IssueOutcomeKind.Blocked,
                    new IssueExecutionReport(null, [], HumanInput: "The preserved execution engine differs from the selected engine."));
            var engine = (engines ?? new ImplementationEngineSelector(settings: config.Codex)).Select(identity, executionCodex);
            if (engine is null)
                return new IssueProcessingResult(IssueOutcomeKind.Blocked,
                    new IssueExecutionReport(null, [], HumanInput: $"Execution engine '{identity}' is unavailable on this Worker."));
            if (context.IntegrationRecovery || context.InterruptedExecution || IsInterruptedValidation(context))
                return await RunIntegrationRecoveryAsync(context, engine, ct);
            await _repositoryGate.WaitAsync(ct);
            try
            {
                try
                {
                    if (context.RetryOf?.RecoveryState == "codex-interrupted")
                        await git.StartCodexRecoveryAsync(context.RetryOf, ct);
                    else
                        await git.StartIssueAsync(execution.ExecutionId, issue, context.RetryOf, execution.Resumed, execution.AttemptNumber, ct);
                }
                catch (Exception ex) when (ex is ProjectCheckoutDirtyException or IssuePreparationRejectedException &&
                    !ct.IsCancellationRequested && !WorkspaceExists())
                {
                    throw new PreExecutionInfrastructureException(execution.Project, issue.Number, execution.ExecutionId,
                        $"Project '{execution.Project}' failed preparing Issue #{issue.Number} before an execution workspace was created: {ex.Message}", ex);
                }
                if (context.RetryOf is { RecoveryState: "recoverable" or "cleanup-pending" or "missing" } previous)
                {
                    if (history is not null) await history.UpdateRecoveryAsync(previous.ExecutionId, "cleanup-pending", ct);
                    try
                    {
                        await git.CleanupRecoveryWorkspaceAsync(previous with { RecoveryState = "cleanup-pending" }, ct);
                        if (history is not null) await history.UpdateRecoveryAsync(previous.ExecutionId,
                            execution.Resumed ? "resumed-cleaned" : "discarded", ct);
                        output.RecoveryCleanupCompleted(previous.ExecutionId);
                    }
                    catch (WorkerInfrastructureException cleanupError)
                    {
                        output.Warning($"Recovery cleanup skipped for execution {ExecutionFormatting.Display(previous.ExecutionId)} ({previous.ExecutionId}): {cleanupError.Message}");
                    }
                }
            }
            finally { _repositoryGate.Release(); }
            execution.CodexRecovery ??= new CodexInterruptionRecovery(execution.ExecutionId, execution.AttemptNumber,
                0, CodexInterruptionRecovery.Fingerprint(config),
                ProcessScope: await InterruptedWorkspaceProcesses.ReadScopeAsync(ct), ImplementationSucceeded: false);
            var workspaceSnapshot = await git.InspectExecutionWorkspaceAsync(ct);
            await SaveHistoryAsync(CreateEntry(execution, null, null, null) with
            {
                RecoveryBaseCommit = workspaceSnapshot?.BaseCommit, RecoveryStatus = workspaceSnapshot?.StatusSummary
            }, ct);
            await TransitionAsync(execution, ExecutionState.Implementing, ct);
            var request = Request(context) with
            {
                CodexSessionDiagnostic = reason => output.Warning($"Execution {execution.ExecutionId} · {reason}.")
            };
            using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdownToken);
            var turnToken = turnCancellation.Token;
            // Every Issue starts its own engine session; sibling Issues never share a conversation.
            var outcome = await output.RunProgressAsync(TaskLabel(issue, $"{engineLabel} working", execution), () =>
                CodexCapacityRetry.RunAsync(async () =>
                {
                    var token = turnToken;
                    token.ThrowIfCancellationRequested();
                    var result = await engine.RunAsync(request, token);
                    if (execution.CodexRecovery is { } recovery)
                        execution.CodexRecovery = recovery with
                        { ImplementationSucceeded = result.Status == "success", RetryAfterUtc = null };
                    await SaveHistoryAsync(CreateEntry(execution,
                        new IssueExecutionReport(result.Summary, []), null, null), token);
                    return result;
                }, async token =>
                {
                    var clock = timeProvider ?? TimeProvider.System;
                    if (execution.CodexRecovery is { } recovery)
                        execution.CodexRecovery = recovery with { RetryAfterUtc = clock.GetUtcNow().AddMinutes(5) };
                    await SaveAndReportAsync(CreateEntry(execution, null, null, CodexFailure.ModelCapacity), execution.State, token);
                    output.Warning($"Execution {execution.ExecutionId} · selected model is at capacity; retrying in five minutes. No fallback model is configured.");
                    await Task.Delay(TimeSpan.FromMinutes(5), clock, token);
                    if (isAuthoritative is not null && !await isAuthoritative(execution, token))
                        throw new OperationCanceledException("Managed execution ownership is no longer authoritative.", token);
                }, turnToken),
                completion: x => x.Status, succeeded: x => x.Status == "success",
                warning: x => x.Status == "blocked", ct: turnToken);
            if (outcome.Status == "failed")
                output.FailureReason(execution.ExecutionId, $"{engineLabel} reported incomplete task", outcome.Summary,
                    config.Environment.Variables.Values.ToArray());
            await git.VerifyCodexStateAsync(ct);
            var implementationSummary = outcome.Summary;
            var repairs = context.RetryOf?.RecoveryState == "codex-interrupted"
                ? context.RetryOf.Repairs.ToList() : new List<ValidationRepairRecord>();
            if (execution.CodexRecovery is { } implementation)
                execution.CodexRecovery = implementation with { ImplementationSucceeded = outcome.Status == "success" };
            await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null), ct);
            if (outcome.Status == "blocked") return await CleanupOutcomeAsync(context, IssueOutcomeKind.Blocked,
                new IssueExecutionReport(implementationSummary, repairs, HumanInput: outcome.Question), ct);
            if (outcome.Status == "failed") return await CleanupOutcomeAsync(context, IssueOutcomeKind.Failed,
                new IssueExecutionReport(implementationSummary, repairs, Failure: outcome.Summary,
                    FailureCategory: $"{engineLabel} reported incomplete task"), ct);

            var validationOutcome = await RunValidationRepairLoopAsync(context, engine, implementationSummary, repairs,
                "Validation", ct);
            if (validationOutcome is not null) return validationOutcome;

            await git.VerifyCodexStateAsync(ct);
            if (repairs.Count > 0) repairs[^1] = repairs[^1] with { PassedAfterRepair = true };
            await TransitionAsync(execution, ExecutionState.Integrating, ct);
            ObserveIntegration(execution, issue, implementationSummary, repairs);
            await _repositoryGate.WaitAsync(ct);
            GitIntegrationResult integration;
            try
            {
                // Managed execution cancellation is also the local signal that lease ownership
                // was lost or could no longer be confirmed. Check after waiting for the shared
                // repository gate, immediately before integration can mutate shared state.
                ct.ThrowIfCancellationRequested();
                if (isAuthoritative is not null && !await isAuthoritative(execution, ct))
                {
                    var staleReport = new IssueExecutionReport(implementationSummary, repairs,
                        Failure: "This execution was superseded because the Issue is closed or another attempt completed.");
                    await SaveHistoryAsync(CreateEntry(execution, staleReport, null, staleReport.Failure) with
                    {
                        RecoveryState = "superseded",
                        RecoveryBaseCommit = null
                    }, ct);
                    return new IssueProcessingResult(IssueOutcomeKind.Superseded, staleReport);
                }
                try
                {
                    integration = await output.RunProgressAsync(TaskLabel(issue, "Integrating", execution), () => git.CommitAndIntegrateAsync(issue,
                        token => ValidateAfterRebaseAsync(context, repairs, token),
                        // A first attempt never asks the engine to resolve a rebase conflict: the conflict is
                        // preserved and reported. Only validation failures on rebased source may be repaired.
                        (_, _) => Task.FromResult(false),
                        (details, token) => RepairIntegrationAsync(context, engine, implementationSummary, repairs, details, token),
                        ct, token => EnsureIntegrationAuthorityAsync(execution, token)),
                        completion: x => x.HasChanges ? "complete" : "no changes", ct: ct);
                }
                catch (GitConflictResolutionIncompleteException ex)
                {
                    // The resolver may have useful partial edits in a detached rebase.
                    // Do not require a clean branch or abort those edits to record a safe failure.
                    var report = new IssueExecutionReport(implementationSummary, repairs,
                        Failure: ex.Message, FailureCategory: "Integration conflict",
                        RecoveryBranch: execution.FeatureBranch, WorkspacePreserved: true, RetryAvailable: false,
                        RecoveryGuidance: "Inspect the retained rebase and original implementation branch before another recovery action.",
                        SecretValues: config.Environment.Variables.Values.ToArray());
                    await SaveHistoryAsync(CreateEntry(execution, report, null, ex.Message) with
                    {
                        RecoveryState = "conflict-resolution-incomplete",
                        RecoveryStatus = "Conflict resolution retained; rebase may be in progress."
                    }, ct);
                    return new IssueProcessingResult(IssueOutcomeKind.IntegrationConflict, report);
                }
                catch (GitIntegrationConflictException ex)
                {
                    if (ex is GitIntegrationArchiveException && history is not null &&
                        await history.ReadExecutionAsync(execution.ExecutionId, ct) is { CompletionJson: not null })
                        throw new WorkerInfrastructureException("Validated base integration succeeded; completed-branch archival is pending reconciliation.", ex);
                    var recovery = await git.PreserveIntegrationConflictAsync(ct);
                    var report = new IssueExecutionReport(implementationSummary, repairs, Failure: ex.Message,
                        FailureCategory: ex is PostRebaseValidationException ? "Post-rebase validation failed" : "Integration conflict", RecoveryBranch: recovery?.Branch,
                        RebaseConflict: ex.RebaseAborted,
                        WorkspacePreserved: recovery is not null, RetryAvailable: recovery is not null,
                        FinalValidationFailure: (ex as PostRebaseValidationException)?.Failure?.Command,
                        FinalValidationDiagnostics: (ex as PostRebaseValidationException)?.Failure?.ToSummary(),
                        FinalValidationExitCode: (ex as PostRebaseValidationException)?.Failure?.ExitCode,
                        SecretValues: config.Environment.Variables.Values.ToArray());
                    await SaveHistoryAsync(CreateEntry(execution, report, null, ex.Message) with
                    {
                        ValidationOutcome = ex is PostRebaseValidationException ? report.PostRebaseValidationOutcome : "passed",
                        CommitSha = recovery?.BaseCommit,
                        IntegrationBranch = execution.BaseBranch,
                        RecoveryState = recovery is null ? "integration-conflict-unavailable" : "integration-conflict",
                        RecoveryBaseCommit = recovery?.BaseCommit,
                        RecoveryStatus = recovery?.StatusSummary,
                        RecoveryExpiresAtUtc = recovery is null ? null : DateTimeOffset.UtcNow.AddDays(config.Worker.RecoveryRetentionDays)
                    }, ct);
                    return new IssueProcessingResult(IssueOutcomeKind.IntegrationConflict,
                        report);
                }
            }
            finally { _repositoryGate.Release(); }
            return new IssueProcessingResult(IssueOutcomeKind.Succeeded,
                new IssueExecutionReport(implementationSummary, repairs, Integration: integration));
        }
        catch (Exception shutdownError) when (shutdownToken.IsCancellationRequested && WorkerShutdown.IsCancellation(shutdownError))
        {
            var recoveryState = GitHubOperationException.Find(shutdownError) is { IsMutation: true }
                ? GitHubOperationException.ReconciliationRequiredState : "uncertain";
            if (execution.State is ExecutionState.Implementing or ExecutionState.Repairing &&
                execution.CodexRecovery is { ImplementationSucceeded: not true } && recoveryState != GitHubOperationException.ReconciliationRequiredState)
                recoveryState = "codex-interrupted";
            if (execution.ServerExecutionId is not null && execution.State == ExecutionState.Validating &&
                execution.CodexRecovery is { ImplementationSucceeded: true } && recoveryState != GitHubOperationException.ReconciliationRequiredState &&
                history is not null && await history.ReadExecutionAsync(execution.ExecutionId, CancellationToken.None) is
                    { ValidationOutcome: "pending: validation" })
                recoveryState = "codex-interrupted";
            await RecordInfrastructureFailureAsync(execution, "Execution interrupted by Worker shutdown", recoveryState, shutdown: true);
            throw new WorkerShutdownException(shutdownToken, shutdownError);
        }
        catch (Exception ex)
        {
            if (ex is CodexExecutionInfrastructureException codexFailure)
                output.FailureReason(execution.ExecutionId, codexFailure.Category,
                    "Codex execution did not complete; see execution history for details.");
            else if (ex is OperationCanceledException && ct.IsCancellationRequested)
                output.FailureReason(execution.ExecutionId, "Cancellation/interruption", "Execution was cancelled or interrupted.");
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, ex is CodexExecutionInfrastructureException failure ? failure.Summary : ex.Message,
                ex switch
                {
                    CodexExecutionInfrastructureException { Recoverable: true } when execution.CodexRecovery is not null &&
                        execution.State is ExecutionState.Implementing or ExecutionState.Repairing =>
                        execution.CodexRecovery.ResumeCount >= CodexInterruptionRecovery.MaximumResumes ? "codex-recovery-exhausted" : "codex-interrupted",
                    PreExecutionInfrastructureException { InnerException: ProjectCheckoutDirtyException } => null,
                    PreExecutionInfrastructureException => "preparation-failed",
                    _ => "uncertain"
                });
            throw;
        }
    }

    private async Task<IssueProcessingResult> CleanupOutcomeAsync(ExecutionContext context, IssueOutcomeKind kind,
        IssueExecutionReport report, CancellationToken ct)
    {
        GitRecoveryInfo? recovery = null;
        if (context.Execution.MetadataError is null)
        {
            await _repositoryGate.WaitAsync(ct);
            try
            {
                if (kind is IssueOutcomeKind.Failed or IssueOutcomeKind.Blocked)
                    recovery = await git.PreserveFailedIssueChangesAsync(ct);
                else
                    await git.DiscardUncommittedIssueChangesAsync(ct);
            }
            finally { _repositoryGate.Release(); }
        }
        var completedReport = report with
        {
            RecoveryBranch = recovery?.Branch,
            WorkspacePreserved = recovery is not null,
            RetryAvailable = recovery is not null,
            RecoveryGuidance = recovery is null && context.Execution.MetadataError is null &&
                kind is IssueOutcomeKind.Failed or IssueOutcomeKind.Blocked
                ? "No useful file changes remained; the workspace was safely cleaned. Make the Issue ready again to start a new clean attempt, including when worker.retryMode is resume."
                : null,
            SecretValues = config.Environment.Variables.Values.ToArray()
        };
        var historyFailure = completedReport.FinalValidationDiagnostics is null ? null :
            $"{completedReport.Failure}\n{completedReport.FinalValidationDiagnostics}";
        await SaveHistoryAsync(CreateEntry(context.Execution, completedReport, null, historyFailure) with
        {
            RecoveryState = context.Execution.MetadataError is not null ? "preparation-failed" :
                recovery is not null ? "recoverable" : kind is IssueOutcomeKind.Failed or IssueOutcomeKind.Blocked ? "cleaned-no-changes" : null,
            RecoveryBaseCommit = recovery?.BaseCommit,
            RecoveryStatus = recovery?.StatusSummary,
            RecoveryExpiresAtUtc = recovery is null ? null : DateTimeOffset.UtcNow.AddDays(config.Worker.RecoveryRetentionDays)
        }, ct);
        return new IssueProcessingResult(kind, completedReport);
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
        if (reportServer is not null) await reportServer(entry, state, ct);
    }

    private async Task RecordInfrastructureFailureAsync(WorkerExecution execution, string reason, string? recoveryState = "uncertain", bool shutdown = false)
    {
        GitRecoveryInfo? recoverySnapshot = null;
        if (recoveryState is "codex-interrupted" or "codex-recovery-exhausted")
        {
            // Verify ownership again after the child stopped, with a bounded process timeout.
            // Corrupt Git state remains preserved but must never be automatically resumed.
            try
            {
                recoverySnapshot = await git.InspectExecutionWorkspaceAsync(CancellationToken.None);
                if (recoverySnapshot is null) recoveryState = "uncertain";
            }
            catch (WorkerInfrastructureException verificationError)
            {
                recoveryState = "uncertain";
                reason += " Recovery verification failed: " + verificationError.Message;
            }
            if (execution.CodexRecovery is { } recovery)
                execution.CodexRecovery = recovery with { RetryAfterUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().AddMinutes(5) };
        }
        if (!execution.IsTerminal) execution.TransitionTo(shutdown ? ExecutionState.Cancelled : ExecutionState.InfrastructureFailure);
        if (recoveryState == "codex-recovery-exhausted")
            reason += " Automatic recovery budget exhausted; inspect the preserved workspace and execution history. Reapplying the ready label does not reset the budget.";
        reason = FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray());
        output.Warning($"Execution {execution.ExecutionId} · {(shutdown ? "Worker shutdown" : "infrastructure failure")} · {reason}");
        var workspace = git.ExecutionDirectory;
        var checkout = config.Project.Directory;
        if (!string.IsNullOrWhiteSpace(workspace) && !string.IsNullOrWhiteSpace(checkout) &&
            !PathEquals(workspace, checkout) && Directory.Exists(workspace))
            reason += $" Preserved execution workspace: {Path.GetFullPath(workspace)}";
        try { await SaveHistoryAsync(CreateEntry(execution, null, null, reason) with { RecoveryState = recoveryState, RecoveryBaseCommit = recoverySnapshot?.BaseCommit, RecoveryStatus = recoverySnapshot?.StatusSummary }, CancellationToken.None); }
        catch (WorkerInfrastructureException) when (!shutdown) { /* Preserve the original failure; the existing row remains incomplete. */ }
    }

    private static bool PathEquals(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private bool WorkspaceExists()
    {
        var workspace = git.ExecutionDirectory;
        var checkout = config.Project.Directory;
        return !string.IsNullOrWhiteSpace(workspace) && !string.IsNullOrWhiteSpace(checkout) &&
            !PathEquals(workspace, checkout) && Directory.Exists(workspace);
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
            AttemptNumber: execution.AttemptNumber, Resumed: execution.Resumed, RetryMode: execution.RetryMode,
            ServerExecutionId: execution.ServerExecutionId, EffectiveModel: execution.CodexProfile?.EffectiveModel, EffectiveEffort: execution.CodexProfile?.Effort,
            Engine: execution.CodexProfile?.Engine ?? "codex", ModelSelectedByCli: execution.CodexProfile is { Model: null },
            AssignmentId: execution.AssignmentId,
            OwnershipGeneration: execution.OwnershipGeneration, CodexRecovery: execution.CodexRecovery,
            CodexThreadId: execution.CodexThreadId, CodexSessionOwner: execution.CodexSessionOwner,
            ParentIssueNumber: execution.ParentIssueNumber, ParentIssueCaptured: execution.ParentIssueCaptured, ParentProvenance: execution.ParentProvenance);

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string TaskLabel(GitHubIssue issue, string stage, WorkerExecution execution) =>
        $"{ExecutionFormatting.OperationalIdentity(issue, execution.ExecutionId)} · {stage}";
}
