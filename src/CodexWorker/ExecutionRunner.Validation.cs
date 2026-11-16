using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed partial class ExecutionRunner
{
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
}
