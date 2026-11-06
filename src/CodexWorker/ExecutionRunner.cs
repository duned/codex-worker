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
public sealed class ExecutionRunner(WorkerConfiguration config, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, WorkerConsole output, ExecutionHistoryStore? history = null, SemaphoreSlim? repositoryGate = null,
    Func<ExecutionHistoryEntry, ExecutionState, CancellationToken, Task>? reportServer = null,
    Func<WorkerExecution, CancellationToken, Task<bool>>? isAuthoritative = null, CancellationToken shutdownToken = default, TimeProvider? timeProvider = null,
    ImplementationEngineSelector? engines = null, IGitHubClient? github = null, CodexThreadCoordinator? threadCoordinator = null)
{
    private ImplementationRequest Request(ExecutionContext context) => new(git.ExecutionDirectory, config.Codex.InstructionsFile,
        context.Issue, context.RetryOf, context.Execution.Resumed, context.Execution.AttemptNumber,
        context.Execution.CodexProfile?.EffectiveModel, context.Execution.CodexProfile?.Effort);

    private static bool IsInterruptedValidation(ExecutionContext context) => context.RetryOf is { } source &&
        (source.ValidationOutcome is "pending: validation" or "legacy: pending validation") &&
        source.CodexRecovery?.ImplementationSucceeded == true;

    private readonly CodexThreadCoordinator _threadCoordinator = threadCoordinator ?? new();
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
            Task<(string? Thread, string Reason)> SelectThreadAsync(CancellationToken token) =>
                BlockerCodexSession.SelectAsync(request, execution.Project, execution.Repository,
                    identity, history, github, CodexSessionOwnership.Current,
                    new LegacyCodexSessionStore(CodexProvisioning.CodexServiceEnvironment.Home).ContainsAsync, token);
            var inherited = await SelectThreadAsync(turnToken);
            if (inherited.Thread is null)
                output.Warning($"Execution {execution.ExecutionId} · Codex thread inheritance skipped: {inherited.Reason}.");
            var outcome = await output.RunProgressAsync(TaskLabel(issue, $"{engineLabel} working", execution), () =>
                CodexCapacityRetry.RunAsync(() => _threadCoordinator.RunAsync(inherited.Thread, async token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (execution.IsTerminal || (isAuthoritative is not null && !await isAuthoritative(execution, token)))
                        throw new OperationCanceledException("Execution is no longer authoritative.", token);
                    var current = await SelectThreadAsync(token);
                    return Guid.TryParse(current.Thread, out var currentId) &&
                        Guid.TryParse(inherited.Thread, out var selectedId) && currentId == selectedId;
                }, async (thread, token) =>
                {
                    if (inherited.Thread is not null && thread is null)
                        output.Warning($"Execution {execution.ExecutionId} · inherited Codex context is no longer valid; starting a fresh session.");
                    var result = await engine.RunAsync(request with { InheritedCodexThreadId = thread }, token);
                    if (execution.CodexRecovery is { } recovery)
                        execution.CodexRecovery = recovery with
                        { ImplementationSucceeded = result.Status == "success", RetryAfterUtc = null };
                    // Capture completion before another child may advance this thread.
                    await SaveHistoryAsync(CreateEntry(execution,
                        new IssueExecutionReport(result.Summary, []), null, null), token);
                    return result;
                }, turnToken), async token =>
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
                        // Conflicts require inspection; only validation failures on successfully rebased
                        // source may enter the bounded integration-repair loop.
                        (_, _) => Task.FromResult(false), (details, token) => RepairIntegrationAsync(context, engine, implementationSummary, repairs, details, token),
                        ct, token => EnsureIntegrationAuthorityAsync(execution, token)),
                        completion: x => x.HasChanges ? "complete" : "no changes", ct: ct);
                }
                catch (GitIntegrationConflictException ex)
                {
                    if (ex is GitIntegrationArchiveException && history is not null &&
                        await history.ReadExecutionAsync(execution.ExecutionId, ct) is { CompletionJson: not null })
                        throw new WorkerInfrastructureException("Validated base integration succeeded; completed-branch archival is pending reconciliation.", ex);
                    var recovery = await git.PreserveIntegrationConflictAsync(ct);
                    var report = new IssueExecutionReport(implementationSummary, repairs, Failure: ex.Message,
                        FailureCategory: ex is PostRebaseValidationException ? "Post-rebase validation failed" : "Integration conflict", RecoveryBranch: recovery?.Branch,
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
            if (!execution.IsTerminal) await RecordInfrastructureFailureAsync(execution, ex.Message,
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

    private void ObserveIntegration(WorkerExecution execution, GitHubIssue issue, string? summary,
        IReadOnlyList<ValidationRepairRecord> repairs)
    {
        if (history is null) return;
        git.WithIntegrationObserver(async (integration, confirmed, token) =>
        {
            var report = new IssueExecutionReport(summary, repairs, Integration: integration, ExecutionId: execution.ExecutionId,
                Duration: DateTimeOffset.UtcNow - execution.StartedAtUtc, AttemptNumber: execution.AttemptNumber,
                RetryOfExecutionId: execution.RetryOfExecutionId, Resumed: execution.Resumed,
                EffectiveModel: execution.CodexProfile?.EffectiveModel, EffectiveEffort: execution.CodexProfile?.Effort, Engine: execution.CodexProfile?.Engine ?? "codex");
            report = ExecutionCompletion.Sanitize(report, config.Environment.Variables.Values.ToArray());
            await SaveHistoryAsync(CreateEntry(execution, report, null, null) with { ValidationOutcome = "passed" }, token);
            await history.SaveCompletionAsync(execution.ExecutionId,
                new ExecutionCompletion(report, ExecutionCompletion.IssueIntent(issue), ExecutionCompletion.Policy(config), confirmed), false, token);
        });
    }

    private async Task<IssueProcessingResult> RunIntegrationRecoveryAsync(ExecutionContext context, IImplementationEngine engine, CancellationToken ct)
    {
        var execution = context.Execution;
        var issue = context.Issue;
        var source = context.RetryOf ?? throw new WorkerInfrastructureException("Integration recovery has no source execution history.");
        if (context.ResolveIntegrationConflict) await VerifyConflictIntentAsync(context, ct);
        if (isAuthoritative is not null && !await isAuthoritative(execution, ct))
            throw new WorkerInfrastructureException("Fresh Server authorization was unavailable for preserved validation recovery; the workspace remains preserved.");
        await _repositoryGate.WaitAsync(ct);
        try
        {
            try
            {
                if (context.ResolveIntegrationConflict)
                {
                    await VerifyConflictIntentAsync(context, ct);
                    git.PreservePartialConflictResolution();
                }
                output.Warning($"Reconciling preserved implementation with current {config.Git.BaseBranch} · original execution {source.ExecutionId}");
                if (context.InterruptedExecution) await git.StartInterruptedExecutionAsync(source, ct);
                else if (source.RecoveryState == "codex-interrupted") await git.StartCodexRecoveryAsync(source, ct);
                else await git.StartIntegrationRecoveryAsync(source, ct);
            }
            catch (IssuePreparationRejectedException ex) when (!ct.IsCancellationRequested)
            {
                return new IssueProcessingResult(IssueOutcomeKind.IntegrationConflict,
                    new IssueExecutionReport($"Integration recovery from execution {source.ExecutionId} requires inspection.", [],
                        Failure: ex.Message, FailureCategory: "Integration recovery conflict", RecoveryBranch: source.FeatureBranch,
                        WorkspacePreserved: false, SecretValues: config.Environment.Variables.Values.ToArray()));
            }
            catch (ProjectCheckoutDirtyException ex) when (!ct.IsCancellationRequested)
            {
                throw new PreExecutionInfrastructureException(execution.Project, issue.Number, execution.ExecutionId,
                    $"Integration recovery preparation was rejected; the preserved execution was not changed: {ex.Message}", ex);
            }
        }
        finally { _repositoryGate.Release(); }
        var repairs = context.InterruptedExecution || IsInterruptedValidation(context)
            ? source.Repairs.ToList() : new List<ValidationRepairRecord>();
        if (IsInterruptedValidation(context))
        {
            var implementationSummary = source.ImplementationSummary ?? "Original implementation completed; continuing its preserved validation.";
            var validationOutcome = await RunValidationRepairLoopAsync(context, engine, implementationSummary, repairs,
                "Resumed validation", ct);
            if (validationOutcome is not null) return validationOutcome;
        }
        await TransitionAsync(execution, ExecutionState.Integrating, ct);
        if (repairs.Count > 0 && !repairs[^1].IntegrationRepair)
            repairs[^1] = repairs[^1] with { PassedAfterRepair = true };
        await _repositoryGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (isAuthoritative is not null && !await isAuthoritative(execution, ct))
                return new IssueProcessingResult(IssueOutcomeKind.Superseded,
                    new IssueExecutionReport($"Integration recovery from execution {source.ExecutionId} was superseded because the Issue is closed or a later attempt completed.", []));
            ObserveIntegration(execution, issue, source.ImplementationSummary, repairs);
            var integration = await output.RunProgressAsync(TaskLabel(issue, "Integration recovery", execution), () =>
                git.CommitAndIntegrateAsync(issue,
                    token => ValidateAfterRebaseAsync(context, repairs, token),
                    (details, token) => ResolveConflictAsync(context, engine, details, token), (details, token) => RepairIntegrationAsync(context, engine, source.ImplementationSummary, repairs, details, token),
                    ct, async token =>
                    {
                        if (context.ResolveIntegrationConflict) await VerifyConflictIntentAsync(context, token);
                        await EnsureIntegrationAuthorityAsync(execution, token);
                    }), x => x.HasChanges ? "integrated" : "no changes", ct: ct);
            var report = new IssueExecutionReport($"Integration recovery from execution {source.ExecutionId} completed without rerunning implementation.\n\n{source.ImplementationSummary}", repairs, Integration: integration);
            if (history is not null && !context.InterruptedExecution) await history.UpdateRecoveryAsync(source.ExecutionId, "integration-recovered", ct);
            return new IssueProcessingResult(IssueOutcomeKind.Succeeded, report);
        }
        catch (GitConflictResolutionIncompleteException ex)
        {
            // A structured engine failure is a task outcome. Keep its partial edits and in-progress
            // rebase for inspection rather than aborting away useful resolution work.
            var report = new IssueExecutionReport(source.ImplementationSummary, repairs,
                Failure: ex.Message, FailureCategory: "Integration recovery conflict", RecoveryBranch: source.FeatureBranch,
                WorkspacePreserved: true, RetryAvailable: false,
                RecoveryGuidance: "Inspect the retained rebase and linked recovery attempt before another recovery action.");
            await SaveHistoryAsync(CreateEntry(execution, report, null, ex.Message) with
            {
                RecoveryState = "conflict-resolution-incomplete", RecoveryBaseCommit = source.RecoveryBaseCommit,
                RecoveryStatus = "Partial conflict resolution retained; rebase may be in progress."
            }, ct);
            return new IssueProcessingResult(IssueOutcomeKind.IntegrationConflict, report);
        }
        catch (GitIntegrationConflictException ex)
        {
            if (ex is GitIntegrationArchiveException && history is not null &&
                await history.ReadExecutionAsync(execution.ExecutionId, ct) is { CompletionJson: not null })
                throw new WorkerInfrastructureException("Validated base integration succeeded; completed-branch archival is pending reconciliation.", ex);
            var recovery = await git.PreserveIntegrationConflictAsync(ct);
            if (history is not null && recovery is not null)
                await history.UpdateIntegrationRecoverySnapshotAsync(source.ExecutionId, recovery.BaseCommit,
                    recovery.StatusSummary, DateTimeOffset.UtcNow.AddDays(config.Worker.RecoveryRetentionDays), ct, recovery.IntegrationBase);
            var report = new IssueExecutionReport($"Integration recovery from execution {source.ExecutionId} did not complete.", repairs,
                Failure: ex.Message, FailureCategory: ex is PostRebaseValidationException ? "Post-rebase validation failed" : "Integration recovery conflict", RecoveryBranch: recovery?.Branch,
                WorkspacePreserved: recovery is not null, RetryAvailable: recovery is not null,
                FinalValidationFailure: (ex as PostRebaseValidationException)?.Failure?.Command,
                FinalValidationDiagnostics: (ex as PostRebaseValidationException)?.Failure?.ToSummary(),
                FinalValidationExitCode: (ex as PostRebaseValidationException)?.Failure?.ExitCode,
                SecretValues: config.Environment.Variables.Values.ToArray());
            await SaveHistoryAsync(CreateEntry(execution, report, null, ex.Message) with
            {
                ValidationOutcome = ex is PostRebaseValidationException ? report.PostRebaseValidationOutcome : "not run: integration recovery conflict",
                CommitSha = recovery?.BaseCommit,
                IntegrationBranch = config.Git.BaseBranch,
                RecoveryState = recovery is null ? "integration-conflict-unavailable" : "integration-conflict",
                RecoveryBaseCommit = recovery?.BaseCommit,
                RecoveryStatus = recovery?.StatusSummary,
                RecoveryExpiresAtUtc = recovery is null ? null : DateTimeOffset.UtcNow.AddDays(config.Worker.RecoveryRetentionDays)
            }, ct);
            return new IssueProcessingResult(IssueOutcomeKind.IntegrationConflict, report);
        }
        finally { _repositoryGate.Release(); }
    }

    private int _conflictResolutionAttempts;

    private async Task VerifyConflictIntentAsync(ExecutionContext context, CancellationToken ct)
    {
        if (github is null || context.RetryOf is null || context.Execution.ServerExecutionId is not null ||
            context.RetryOf.ServerExecutionId is not null || context.RetryOf.AssignmentId is not null || context.RetryOf.OwnershipGeneration is not null)
            throw new WorkerInfrastructureException("Conflict resolution requires authoritative Issue verification.");
        await StandaloneConflictResolution.VerifyIntentAsync(config, github, context.RetryOf, ct);
    }

    private async Task<bool> ResolveConflictAsync(ExecutionContext context, IImplementationEngine engine,
        string details, CancellationToken ct)
    {
        // A resolver runs only inside a separately verified standalone recovery claim.
        if (!context.ResolveIntegrationConflict || !context.IntegrationRecovery) return false;
        if (++_conflictResolutionAttempts > config.Validation.MaxFixAttempts)
            throw new GitConflictResolutionIncompleteException("Verified conflict-resolution budget exhausted; workspace preserved.");
        await VerifyConflictIntentAsync(context, ct);
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        var result = await engine.ResolveIntegrationConflictAsync(Request(context), details, ct);
        await VerifyConflictIntentAsync(context, ct);
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        if (result.Status != "success")
        {
            var diagnostic = FailureDiagnosticRedactor.Redact(result.Summary, config.Environment.Variables.Values.ToArray());
            if (diagnostic.Length > 1200) diagnostic = diagnostic[..1200];
            throw new GitConflictResolutionIncompleteException("Verified conflict resolution did not complete. Workspace and rebase remain preserved: " + diagnostic);
        }
        return true;
    }

    private async Task EnsureIntegrationAuthorityAsync(WorkerExecution execution, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isAuthoritative is not null && !await isAuthoritative(execution, ct))
            throw new WorkerInfrastructureException("Integration authority was lost; the execution workspace is preserved for reconciliation.");
    }

    private async Task<IntegrationRepairResult> RepairIntegrationAsync(ExecutionContext context, IImplementationEngine engine,
        string? implementationSummary, List<ValidationRepairRecord> repairs, IntegrationRepairContext details, CancellationToken ct)
    {
        var attempt = repairs.Count(repair => repair.IntegrationRepair) + 1;
        if (attempt > config.Validation.MaxFixAttempts) return new IntegrationRepairResult(false, false);
        if (context.ResolveIntegrationConflict) await VerifyConflictIntentAsync(context, ct);
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        if (context.Execution.CodexRecovery is { } repairing)
            context.Execution.CodexRecovery = repairing with { ImplementationSucceeded = false };
        await SaveHistoryAsync(CreateEntry(context.Execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
            with { ValidationOutcome = "pending: integration repair" }, ct);
        var outcome = await output.RunProgressAsync(TaskLabel(context.Issue,
            $"Integration repair {attempt}/{config.Validation.MaxFixAttempts}", context.Execution), () =>
            engine.RepairIntegrationAsync(Request(context),
                details, implementationSummary, config.Validation.Commands, attempt, config.Validation.MaxFixAttempts, ct),
            x => x.Status, x => x.Status == "success", warning: x => x.Status == "blocked", ct: ct);
        if (context.ResolveIntegrationConflict) await VerifyConflictIntentAsync(context, ct);
        if (context.Execution.CodexRecovery is { } repaired)
            context.Execution.CodexRecovery = repaired with { ImplementationSucceeded = outcome.Status == "success" };
        var summary = FailureDiagnosticRedactor.Redact(outcome.Summary +
            (outcome.Question is null ? "" : $"\n{outcome.Question}"), config.Environment.Variables.Values.ToArray());
        repairs.Add(new ValidationRepairRecord(details.Failure.Command, attempt, config.Validation.MaxFixAttempts,
            summary, false, details.Failure.ToSummary(), IntegrationRepair: true));
        await SaveHistoryAsync(CreateEntry(context.Execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
            with { ValidationOutcome = $"integration repair {attempt}/{config.Validation.MaxFixAttempts}" }, ct);
        return new IntegrationRepairResult(true, outcome.Status == "success");
    }

    private async Task<ValidationResult> RunValidationAsync(ExecutionContext context, string step, int repairAttempt, CancellationToken ct)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await validation.RunAsync(config.Validation.Commands, git.ExecutionDirectory, ct);
            foreach (var report in result.Reports ?? [])
                output.ValidationSelection(context.Execution.ExecutionId, step, report);
            if (result.Failure is { } failure)
                output.ValidationFailure(context.Issue.Number, context.Execution.ExecutionId, step, repairAttempt,
                    config.Validation.Commands.Count, failure, elapsed.Elapsed, config.Environment.Variables.Values.ToArray());
            return result;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // Preserve cancellation and infrastructure/startup semantics; observability must not add retries.
            output.ValidationFailure(context.Issue.Number, context.Execution.ExecutionId, step, repairAttempt,
                config.Validation.Commands.Count, null, elapsed.Elapsed, config.Environment.Variables.Values.ToArray(),
                ex is OperationCanceledException ? "cancelled" : "environment/startup error", ex.Message);
            throw;
        }
    }

    private async Task<IssueProcessingResult?> RunValidationRepairLoopAsync(ExecutionContext context, IImplementationEngine engine,
        string? implementationSummary, List<ValidationRepairRecord> repairs, string stage, CancellationToken ct)
    {
        var execution = context.Execution;
        var issue = context.Issue;
        var engineLabel = execution.CodexProfile?.Engine == "ccc" ? "CCC" : "Codex";
        var repairAttempts = repairs.Where(repair => !repair.IntegrationRepair).Select(repair => repair.Attempt).DefaultIfEmpty(0).Max();
        while (true)
        {
            await TransitionAsync(execution, ExecutionState.Validating, ct);
            await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
                with { ValidationOutcome = "pending: validation" }, ct);
            var validationResult = await output.RunProgressAsync(TaskLabel(issue, stage, execution),
                () => RunValidationAsync(context, stage, repairAttempts, ct),
                result => result.Succeeded ? "passed" : $"command {result.Failure!.CommandNumber} failed",
                result => result.Succeeded, ct: ct);
            if (validationResult.Succeeded)
            {
                // Record the handoff before verification, authority checks, or integration can interrupt it.
                await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
                    with { ValidationOutcome = "passed" }, CancellationToken.None);
                return null;
            }

            await git.VerifyCodexStateAsync(ct);
            var failure = validationResult.Failure!;
            var failureSummary = failure.ToSummary();
            if (repairs.Count > 0) repairs[^1] = repairs[^1] with { ValidationAfterRepair = failureSummary };
            if (repairAttempts >= config.Validation.MaxFixAttempts)
                return await CleanupOutcomeAsync(context, IssueOutcomeKind.Failed,
                    new IssueExecutionReport(implementationSummary, repairs, FinalValidationFailure: failure.Command,
                        Failure: $"Validation failed after {repairAttempts} repair attempt(s).",
                        FinalValidationDiagnostics: failureSummary, FinalValidationExitCode: failure.ExitCode), ct);

            repairAttempts++;
            repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                "Repair invocation interrupted before a structured result was available.", false, failureSummary));
            if (execution.CodexRecovery is { } interruptedRepair)
                execution.CodexRecovery = interruptedRepair with { ImplementationSucceeded = false };
            await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
                with { ValidationOutcome = $"pending: repair {repairAttempts}" }, ct);
            await TransitionAsync(execution, ExecutionState.Repairing, ct);
            var repair = await output.RunProgressAsync(TaskLabel(issue, $"Repair {repairAttempts}/{config.Validation.MaxFixAttempts}", execution),
                () => engine.RepairAsync(Request(context), failure, repairAttempts, config.Validation.MaxFixAttempts, ct),
                result => result.Status, result => result.Status == "success", warning: result => result.Status == "blocked", ct: ct);
            await git.VerifyCodexStateAsync(ct);
            repairs[^1] = new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                repair.Summary, false, failureSummary);
            if (repair.Status is "blocked" or "failed")
            {
                if (repair.Status == "failed")
                    output.FailureReason(execution.ExecutionId, $"{engineLabel} repair reported incomplete task", repair.Summary,
                        config.Environment.Variables.Values.ToArray());
                return await CleanupOutcomeAsync(context, repair.Status == "blocked" ? IssueOutcomeKind.Blocked : IssueOutcomeKind.Failed,
                    new IssueExecutionReport(implementationSummary, repairs,
                        HumanInput: repair.Status == "blocked" ? repair.Question : null,
                        Failure: repair.Status == "failed" ? repair.Summary : null,
                        FailureCategory: repair.Status == "failed" ? $"{engineLabel} repair reported incomplete task" : null), ct);
            }

            if (execution.CodexRecovery is { } repaired)
                execution.CodexRecovery = repaired with { ImplementationSucceeded = true };
            await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
                with { ValidationOutcome = "pending: validation" }, ct);
        }
    }

    private async Task<ValidationResult> ValidateAfterRebaseAsync(ExecutionContext context,
        List<ValidationRepairRecord> repairs, CancellationToken ct)
    {
        var integrationRepair = repairs.LastOrDefault(repair => repair.IntegrationRepair);
        var stage = integrationRepair is null ? (context.IntegrationRecovery ? "Validation after integration recovery" : "Validation after rebase") : "Validation after integration repair";
        // Initial validation does not certify the rebased source. Persist this distinction
        // before invoking a command that may be interrupted by shutdown.
        await SaveHistoryAsync(CreateEntry(context.Execution, new IssueExecutionReport(null, repairs), null, null)
            with { ValidationOutcome = $"pending: {stage.ToLowerInvariant()}" }, ct);
        var result = await output.RunProgressAsync(TaskLabel(context.Issue, stage, context.Execution),
            () => RunValidationAsync(context, stage, integrationRepair?.Attempt ?? 0, ct),
            x => x.Succeeded ? (integrationRepair is null ? "passed" : "integration repair succeeded") : $"command {x.Failure!.CommandNumber} failed", x => x.Succeeded, ct: ct);
        if (integrationRepair is not null)
            repairs[^1] = integrationRepair with { PassedAfterRepair = result.Succeeded,
                ValidationAfterRepair = result.Failure?.ToSummary() };
        await SaveHistoryAsync(CreateEntry(context.Execution, new IssueExecutionReport(null, repairs), null, null)
            with { ValidationOutcome = result.Succeeded ? "passed" : $"failed: {stage.ToLowerInvariant()}" }, ct);
        return result;
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
            ParentIssueNumber: execution.ParentIssueNumber);

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string TaskLabel(GitHubIssue issue, string stage, WorkerExecution execution) =>
        $"{ExecutionFormatting.OperationalIdentity(issue, execution.ExecutionId)} · {stage}";
}
