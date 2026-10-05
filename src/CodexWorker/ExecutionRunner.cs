using System.Text.RegularExpressions;

namespace CodexWorker;

/// <summary>Explicit identity and Issue input for one execution attempt.</summary>
public sealed record ExecutionContext(WorkerExecution Execution, GitHubIssue Issue, ExecutionHistoryEntry? RetryOf = null,
    bool IntegrationRecovery = false);

/// <summary>
/// Runs the repository workspace, Codex, validation, repair, and integration lifecycle for one execution.
/// Repository mutations that share Git metadata are serialized per repository while implementation and
/// validation use the independent execution worktree concurrently.
/// </summary>
public sealed class ExecutionRunner(WorkerConfiguration config, IGitRepository git, ICodexExecutor codex,
    IValidationRunner validation, WorkerConsole output, ExecutionHistoryStore? history = null, SemaphoreSlim? repositoryGate = null,
    Func<ExecutionHistoryEntry, ExecutionState, CancellationToken, Task>? reportServer = null,
    Func<WorkerExecution, CancellationToken, Task<bool>>? isAuthoritative = null, CancellationToken shutdownToken = default)
{
    private readonly SemaphoreSlim _repositoryGate = repositoryGate ?? new SemaphoreSlim(1, 1);
    public async Task<IssueProcessingResult> RunAsync(ExecutionContext context, CancellationToken ct)
    {
        var execution = context.Execution;
        var issue = context.Issue;
        var executionCodex = execution.CodexProfile is { } profile ? codex.WithProfile(profile) : codex;
        try
        {
            await TransitionAsync(execution, ExecutionState.Preparing, ct);
            if (execution.MetadataError is { } metadataError)
            {
                return new IssueProcessingResult(IssueOutcomeKind.Blocked,
                    new IssueExecutionReport(null, [], HumanInput: metadataError));
            }
            if (context.IntegrationRecovery)
                return await RunIntegrationRecoveryAsync(context, executionCodex, ct);
            await _repositoryGate.WaitAsync(ct);
            try
            {
                try { await git.StartIssueAsync(execution.ExecutionId, issue, context.RetryOf, execution.Resumed, execution.AttemptNumber, ct); }
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
            await TransitionAsync(execution, ExecutionState.Implementing, ct);
            var outcome = await output.RunProgressAsync(TaskLabel(issue, "Codex working", execution), () =>
                executionCodex.RunAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, issue, context.RetryOf,
                    execution.Resumed, execution.AttemptNumber, ct),
                completion: x => x.Status, succeeded: x => x.Status == "success",
                warning: x => x.Status == "blocked", ct: ct);
            if (outcome.Status == "failed")
                output.FailureReason(execution.ExecutionId, "Codex reported incomplete task", outcome.Summary,
                    config.Environment.Variables.Values.ToArray());
            await git.VerifyCodexStateAsync(ct);
            var implementationSummary = outcome.Summary;
            var repairs = new List<ValidationRepairRecord>();
            await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null), ct);
            if (outcome.Status == "blocked") return await CleanupOutcomeAsync(context, IssueOutcomeKind.Blocked,
                new IssueExecutionReport(implementationSummary, repairs, HumanInput: outcome.Question), ct);
            if (outcome.Status == "failed") return await CleanupOutcomeAsync(context, IssueOutcomeKind.Failed,
                new IssueExecutionReport(implementationSummary, repairs, Failure: outcome.Summary,
                    FailureCategory: "Codex reported incomplete task"), ct);

            var repairAttempts = 0;
            while (true)
            {
                await TransitionAsync(execution, ExecutionState.Validating, ct);
                var validationResult = await output.RunProgressAsync(TaskLabel(issue, "Validation", execution), () =>
                    validation.RunAsync(config.Validation.Commands, git.ExecutionDirectory, ct),
                    x => x.Succeeded ? "passed" : $"command {x.Failure!.CommandNumber} failed",
                    x => x.Succeeded, ct: ct);
                if (validationResult.Succeeded)
                {
                    // Durably record the validation handoff before verification, cancellation,
                    // authority checks, or integration can interrupt it.
                    await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
                        with { ValidationOutcome = "passed" }, CancellationToken.None);
                    break;
                }

                await git.VerifyCodexStateAsync(ct);
                var failure = validationResult.Failure!;
                var failureSummary = failure.ToSummary();
                if (repairs.Count > 0)
                    repairs[^1] = repairs[^1] with { ValidationAfterRepair = failureSummary };
                if (repairAttempts >= config.Validation.MaxFixAttempts)
                    return await CleanupOutcomeAsync(context, IssueOutcomeKind.Failed,
                        new IssueExecutionReport(implementationSummary, repairs, FinalValidationFailure: failure.Command,
                            Failure: $"Validation failed after {repairAttempts} repair attempt(s).",
                            FinalValidationDiagnostics: failureSummary, FinalValidationExitCode: failure.ExitCode), ct);

                repairAttempts++;
                await TransitionAsync(execution, ExecutionState.Repairing, ct);
                outcome = await output.RunProgressAsync(TaskLabel(issue, $"Repair {repairAttempts}/{config.Validation.MaxFixAttempts}", execution), () =>
                    executionCodex.RepairAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, issue,
                        failure, repairAttempts, config.Validation.MaxFixAttempts, ct),
                    completion: x => x.Status, succeeded: x => x.Status == "success",
                    warning: x => x.Status == "blocked", ct: ct);
                await git.VerifyCodexStateAsync(ct);
                if (outcome.Status is "blocked" or "failed")
                {
                    if (outcome.Status == "failed")
                        output.FailureReason(execution.ExecutionId, "Codex repair reported incomplete task", outcome.Summary,
                            config.Environment.Variables.Values.ToArray());
                    repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                        outcome.Summary, false, failureSummary));
                    return await CleanupOutcomeAsync(context,
                        outcome.Status == "blocked" ? IssueOutcomeKind.Blocked : IssueOutcomeKind.Failed,
                        new IssueExecutionReport(implementationSummary, repairs,
                            HumanInput: outcome.Status == "blocked" ? outcome.Question : null,
                            Failure: outcome.Status == "failed" ? outcome.Summary : null,
                            FailureCategory: outcome.Status == "failed" ? "Codex repair reported incomplete task" : null), ct);
                }
                repairs.Add(new ValidationRepairRecord(failure.Command, repairAttempts, config.Validation.MaxFixAttempts,
                    outcome.Summary, false, failureSummary));
                await SaveHistoryAsync(CreateEntry(execution, new IssueExecutionReport(implementationSummary, repairs), null, null), ct);
            }

            await git.VerifyCodexStateAsync(ct);
            if (repairs.Count > 0) repairs[^1] = repairs[^1] with { PassedAfterRepair = true };
            await TransitionAsync(execution, ExecutionState.Integrating, ct);
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
                        async (details, token) =>
                        {
                            var resolution = await output.RunProgressAsync(TaskLabel(issue, "Resolving integration conflict", execution),
                                () => executionCodex.ResolveIntegrationConflictAsync(git.ExecutionDirectory, config.Codex.InstructionsFile,
                                    issue, details, token), x => x.Status, x => x.Status == "success", ct: token);
                            return resolution.Status == "success";
                        }, (details, token) => RepairIntegrationAsync(context, executionCodex, implementationSummary, repairs, details, token),
                        ct, token => EnsureIntegrationAuthorityAsync(execution, token)),
                        completion: x => x.HasChanges ? "complete" : "no changes", ct: ct);
                }
                catch (GitIntegrationConflictException ex)
                {
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
        catch (Exception shutdownError) when (ct.IsCancellationRequested && shutdownToken.IsCancellationRequested && WorkerShutdown.IsCancellation(shutdownError))
        {
            var recoveryState = GitHubOperationException.Find(shutdownError) is { IsMutation: true }
                ? GitHubOperationException.ReconciliationRequiredState : "uncertain";
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
                    PreExecutionInfrastructureException { InnerException: ProjectCheckoutDirtyException } => null,
                    PreExecutionInfrastructureException => "preparation-failed",
                    _ => "uncertain"
                });
            throw;
        }
    }

    private async Task<IssueProcessingResult> RunIntegrationRecoveryAsync(ExecutionContext context, ICodexExecutor executionCodex, CancellationToken ct)
    {
        var execution = context.Execution;
        var issue = context.Issue;
        var source = context.RetryOf ?? throw new WorkerInfrastructureException("Integration recovery has no source execution history.");
        await _repositoryGate.WaitAsync(ct);
        try
        {
            try
            {
                output.Warning($"Reconciling preserved implementation with current {config.Git.BaseBranch} · original execution {source.ExecutionId}");
                await git.StartIntegrationRecoveryAsync(source, ct);
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
        var repairs = new List<ValidationRepairRecord>();
        await TransitionAsync(execution, ExecutionState.Integrating, ct);
        await _repositoryGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (isAuthoritative is not null && !await isAuthoritative(execution, ct))
                return new IssueProcessingResult(IssueOutcomeKind.Superseded,
                    new IssueExecutionReport($"Integration recovery from execution {source.ExecutionId} was superseded because the Issue is closed or a later attempt completed.", []));
            var integration = await output.RunProgressAsync(TaskLabel(issue, "Integration recovery", execution), () =>
                git.CommitAndIntegrateAsync(issue,
                    token => ValidateAfterRebaseAsync(context, repairs, token),
                    async (details, token) =>
                    {
                        var resolved = await output.RunProgressAsync(TaskLabel(issue, "Resolving integration conflict", execution),
                            () => executionCodex.ResolveIntegrationConflictAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, issue, details, token),
                            x => x.Status, x => x.Status == "success", ct: token);
                        return resolved.Status == "success";
                    }, (details, token) => RepairIntegrationAsync(context, executionCodex, source.ImplementationSummary, repairs, details, token),
                    ct, token => EnsureIntegrationAuthorityAsync(execution, token)), x => x.HasChanges ? "integrated" : "no changes", ct: ct);
            var report = new IssueExecutionReport($"Integration recovery from execution {source.ExecutionId} completed without rerunning implementation.\n\n{source.ImplementationSummary}", repairs, Integration: integration);
            if (history is not null) await history.UpdateRecoveryAsync(source.ExecutionId, "integration-recovered", ct);
            return new IssueProcessingResult(IssueOutcomeKind.Succeeded, report);
        }
        catch (GitIntegrationConflictException ex)
        {
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

    private async Task EnsureIntegrationAuthorityAsync(WorkerExecution execution, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (isAuthoritative is not null && !await isAuthoritative(execution, ct))
            throw new WorkerInfrastructureException("Integration authority was lost; the execution workspace is preserved for reconciliation.");
    }

    private async Task<IntegrationRepairResult> RepairIntegrationAsync(ExecutionContext context, ICodexExecutor executionCodex,
        string? implementationSummary, List<ValidationRepairRecord> repairs, IntegrationRepairContext details, CancellationToken ct)
    {
        var attempt = repairs.Count(repair => repair.IntegrationRepair) + 1;
        if (attempt > config.Validation.MaxFixAttempts) return new IntegrationRepairResult(false, false);
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        var outcome = await output.RunProgressAsync(TaskLabel(context.Issue,
            $"Integration repair {attempt}/{config.Validation.MaxFixAttempts}", context.Execution), () =>
            executionCodex.RepairIntegrationAsync(git.ExecutionDirectory, config.Codex.InstructionsFile, context.Issue,
                details, implementationSummary, config.Validation.Commands, attempt, config.Validation.MaxFixAttempts, ct),
            x => x.Status, x => x.Status == "success", warning: x => x.Status == "blocked", ct: ct);
        var summary = FailureDiagnosticRedactor.Redact(outcome.Summary +
            (outcome.Question is null ? "" : $"\n{outcome.Question}"), config.Environment.Variables.Values.ToArray());
        repairs.Add(new ValidationRepairRecord(details.Failure.Command, attempt, config.Validation.MaxFixAttempts,
            summary, false, details.Failure.ToSummary(), IntegrationRepair: true));
        await SaveHistoryAsync(CreateEntry(context.Execution, new IssueExecutionReport(implementationSummary, repairs), null, null)
            with { ValidationOutcome = $"integration repair {attempt}/{config.Validation.MaxFixAttempts}" }, ct);
        return new IntegrationRepairResult(true, outcome.Status == "success");
    }

    private async Task<ValidationResult> ValidateAfterRebaseAsync(ExecutionContext context,
        List<ValidationRepairRecord> repairs, CancellationToken ct)
    {
        var integrationRepair = repairs.LastOrDefault(repair => repair.IntegrationRepair);
        var stage = integrationRepair is null ? (context.IntegrationRecovery ? "Validation after integration recovery" : "Validation after rebase") : "Validation after integration repair";
        var result = await output.RunProgressAsync(TaskLabel(context.Issue, stage, context.Execution),
            () => validation.RunAsync(config.Validation.Commands, git.ExecutionDirectory, ct),
            x => x.Succeeded ? (integrationRepair is null ? "passed" : "integration repair succeeded") : $"command {x.Failure!.CommandNumber} failed", x => x.Succeeded, ct: ct);
        if (integrationRepair is not null)
            repairs[^1] = integrationRepair with { PassedAfterRepair = result.Succeeded,
                ValidationAfterRepair = result.Failure?.ToSummary() };
        if (!result.Succeeded)
            output.FailureReason(context.Execution.ExecutionId, $"{stage} failed",
                result.Failure!.ToSummary(), config.Environment.Variables.Values.ToArray());
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
        if (!execution.IsTerminal) execution.TransitionTo(shutdown ? ExecutionState.Cancelled : ExecutionState.InfrastructureFailure);
        reason = FailureDiagnosticRedactor.Redact(reason, config.Environment.Variables.Values.ToArray());
        output.Warning($"Execution {execution.ExecutionId} · {(shutdown ? "Worker shutdown" : "infrastructure failure")} · {reason}");
        var workspace = git.ExecutionDirectory;
        var checkout = config.Project.Directory;
        if (!string.IsNullOrWhiteSpace(workspace) && !string.IsNullOrWhiteSpace(checkout) &&
            !PathEquals(workspace, checkout) && Directory.Exists(workspace))
            reason += $" Preserved execution workspace: {Path.GetFullPath(workspace)}";
        try { await SaveHistoryAsync(CreateEntry(execution, null, null, reason) with { RecoveryState = recoveryState }, CancellationToken.None); }
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
            AttemptNumber: execution.AttemptNumber, Resumed: execution.Resumed,
            ServerExecutionId: execution.ServerExecutionId, EffectiveModel: execution.CodexProfile?.Model, EffectiveEffort: execution.CodexProfile?.Effort,
            AssignmentId: execution.AssignmentId,
            OwnershipGeneration: execution.OwnershipGeneration);

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string TaskLabel(GitHubIssue issue, string stage, WorkerExecution execution) =>
        $"{ExecutionFormatting.OperationalIdentity(issue, execution.ExecutionId)} · {stage}";
}
