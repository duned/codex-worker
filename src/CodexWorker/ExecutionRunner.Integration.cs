using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed partial class ExecutionRunner
{
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
        try
        {
            if (CanResolveIntegrationConflict(context)) await VerifyConflictIntentAsync(context, ct);
        }
        catch (GitConflictResolutionIncompleteException ex)
        {
            return await RefuseConflictPreparationAsync(context, source, ex, ct);
        }
        if (isAuthoritative is not null && !await isAuthoritative(execution, ct))
            throw new WorkerInfrastructureException("Fresh Server authorization was unavailable for preserved validation recovery; the workspace remains preserved.");
        await _repositoryGate.WaitAsync(ct);
        try
        {
            try
            {
                if (CanResolveIntegrationConflict(context))
                {
                    await VerifyConflictIntentAsync(context, ct);
                    git.PreservePartialConflictResolution();
                }
                output.Warning($"Reconciling preserved implementation with current {config.Git.BaseBranch} · original execution {source.ExecutionId}");
                if (context.InterruptedExecution) await git.StartInterruptedExecutionAsync(source, ct);
                else if (source.RecoveryState == "codex-interrupted") await git.StartCodexRecoveryAsync(source, ct);
                else await git.StartIntegrationRecoveryAsync(source, ct);
            }
            catch (GitConflictResolutionIncompleteException ex)
            {
                return await RefuseConflictPreparationAsync(context, source, ex, ct);
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
                        try
                        {
                            if (CanResolveIntegrationConflict(context))
                                await VerifyConflictIntentAsync(context, token, resolving: _conflictResolutionAttempts > 0);
                        }
                        catch (GitConflictResolutionIncompleteException ex)
                        {
                            // This callback also runs after local integration or a push.
                            // At that boundary a refusal requires reconciliation, not a safe retry.
                            throw new WorkerInfrastructureException("Integration authority could not be verified; preserve state for reconciliation.", ex);
                        }
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

    private async Task<IssueProcessingResult> RefuseConflictPreparationAsync(ExecutionContext context,
        ExecutionHistoryEntry source, GitConflictResolutionIncompleteException refusal, CancellationToken ct)
    {
        var report = new IssueExecutionReport(source.ImplementationSummary, [], Failure: refusal.Message,
            FailureCategory: "Integration recovery conflict", RecoveryBranch: source.FeatureBranch,
            WorkspacePreserved: true, RetryAvailable: false,
            RecoveryGuidance: "Original workspace was not changed. Review Issue authority before another recovery action.",
            SecretValues: config.Environment.Variables.Values.ToArray());
        await SaveHistoryAsync(CreateEntry(context.Execution, report, null, refusal.Message) with
        {
            RecoveryState = "conflict-resolution-incomplete", RecoveryBaseCommit = source.RecoveryBaseCommit
        }, ct);
        return new IssueProcessingResult(IssueOutcomeKind.IntegrationConflict, report);
    }

    private int _conflictResolutionAttempts;
    private int _firstAttemptConflictResolutions;

    private async Task VerifyConflictIntentAsync(ExecutionContext context, CancellationToken ct, bool resolving = false)
    {
        try
        {
            var execution = context.Execution;
            // A first attempt has its own captured intent; a prior recovery attempt is
            // required only when continuing a preserved conflict, not for parallel integration.
            var source = context.IntegrationRecovery && context.RetryOf is { } previous
                ? previous
                : CreateEntry(execution, null, null, null) with
                {
                    OriginalIssueBody = execution.IssueBody,
                    OriginalIssueBodyFingerprint = ConflictIssueIntent.BodyFingerprint(execution.IssueBody)
                };
            if (execution.ServerExecutionId is not null)
            {
                if (source.ServerExecutionId is null || isAuthoritative is null)
                    throw new GitConflictResolutionIncompleteException("Managed conflict resolution requires an active Server execution lease.");
                await EnsureIntegrationAuthorityAsync(execution, ct);
                // A clean managed recovery integrates already-validated work under its
                // lease. Live Issue proof is additionally required when editing conflicts.
                if (context.IntegrationRecovery && !context.ResolveIntegrationConflict && !resolving) return;
            }
            else if (source.ServerExecutionId is not null || source.AssignmentId is not null || source.OwnershipGeneration is not null)
                throw new GitConflictResolutionIncompleteException("Managed conflict resolution requires Server reconciliation.");
            if (github is null)
                throw new GitConflictResolutionIncompleteException("Conflict resolution requires authoritative Issue verification; workspace preserved.");
            var verified = await StandaloneConflictResolution.VerifyIntentAsync(config, github, source, ct);
            if (verified.ParentProvenance != execution.ParentProvenance)
                throw ConflictIssueIntent.Refuse("issue-parent-proof-changed");
        }
        catch (ConflictIntentException ex)
        {
            // A known intent refusal is a contained conflict. Unknown Git, lease,
            // engine and push failures retain their infrastructure/reconciliation semantics.
            throw new GitConflictResolutionIncompleteException(ex.Message);
        }
    }

    // Engine conflict resolution belongs to integration recovery only: a managed recovery
    // assignment or an explicitly requested standalone resolution. First attempts never use it.
    private static bool CanResolveIntegrationConflict(ExecutionContext context) =>
        context.IntegrationRecovery &&
        (context.Execution.ServerExecutionId is not null || context.ResolveIntegrationConflict);

    private async Task<bool> ResolveConflictAsync(ExecutionContext context, IImplementationEngine engine,
        string details, CancellationToken ct)
    {
        if (!CanResolveIntegrationConflict(context)) return false;
        if (++_conflictResolutionAttempts > config.Validation.MaxFixAttempts)
            throw new GitConflictResolutionIncompleteException("Verified conflict-resolution budget exhausted; workspace preserved.");
        await VerifyConflictIntentAsync(context, ct, resolving: true);
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        git.PreservePartialConflictResolution();
        var result = await engine.ResolveIntegrationConflictAsync(Request(context), details, ct);
        await VerifyConflictIntentAsync(context, ct, resolving: true);
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        if (result.Status != "success")
        {
            var diagnostic = FailureDiagnosticRedactor.Redact(result.Summary, config.Environment.Variables.Values.ToArray());
            if (diagnostic.Length > 1200) diagnostic = diagnostic[..1200];
            throw new GitConflictResolutionIncompleteException("Verified conflict resolution did not complete. Workspace and rebase remain preserved: " + diagnostic);
        }
        return true;
    }

    /// <summary>Resolves a first-attempt rebase conflict against newer base work in the execution worktree.
    /// Returning false aborts the rebase, preserves the implementation and reports a rebase conflict.</summary>
    private async Task<bool> ResolveFirstAttemptConflictAsync(ExecutionContext context, IImplementationEngine engine,
        string details, CancellationToken ct)
    {
        if (++_firstAttemptConflictResolutions > config.Validation.MaxFixAttempts) return false;
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        ImplementationResult result;
        try
        {
            result = await output.RunProgressAsync(TaskLabel(context.Issue,
                    $"Conflict resolution {_firstAttemptConflictResolutions}/{config.Validation.MaxFixAttempts}", context.Execution),
                () => engine.ResolveIntegrationConflictAsync(Request(context), details, ct),
                x => x.Status, x => x.Status == "success", warning: x => x.Status != "success", ct: ct);
        }
        catch (CodexExecutionInfrastructureException ex)
        {
            // The rebase is aborted and verified clean by the caller; the conflict is then
            // reported normally instead of leaving an engine-interrupted rebase in place.
            output.Warning($"Execution {context.Execution.ExecutionId} · conflict resolution did not complete: {FailureDiagnosticRedactor.Redact(ex.Message, config.Environment.Variables.Values.ToArray())}");
            return false;
        }
        await EnsureIntegrationAuthorityAsync(context.Execution, ct);
        return result.Status == "success";
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
        if (CanResolveIntegrationConflict(context)) await VerifyConflictIntentAsync(context, ct, resolving: true);
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
        if (CanResolveIntegrationConflict(context)) await VerifyConflictIntentAsync(context, ct, resolving: true);
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
        // Recheck intent after validation, before Git can fast-forward the shared base.
        if (_conflictResolutionAttempts > 0) await VerifyConflictIntentAsync(context, ct, resolving: true);
        if (integrationRepair is not null)
            repairs[^1] = integrationRepair with { PassedAfterRepair = result.Succeeded,
                ValidationAfterRepair = result.Failure?.ToSummary() };
        await SaveHistoryAsync(CreateEntry(context.Execution, new IssueExecutionReport(null, repairs), null, null)
            with { ValidationOutcome = result.Succeeded ? "passed" : $"failed: {stage.ToLowerInvariant()}" }, ct);
        return result;
    }
}
