using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodexWorker;

public sealed partial class Worker
{
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

    private async Task ReportServerAsync(ExecutionHistoryEntry entry, ExecutionState state, CancellationToken ct, bool rebaseConflict = false)
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
            }) : null, entry.OwnershipGeneration ?? 0, ct, rebaseConflict: rebaseConflict && state == ExecutionState.IntegrationConflict);
        if (terminal && history is not null)
            await history.SaveServerReportDispositionAsync(entry.ExecutionId, "acknowledged", ct);
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
            AttemptNumber: execution.AttemptNumber, Resumed: execution.Resumed, RetryMode: execution.RetryMode,
            ServerExecutionId: execution.ServerExecutionId, EffectiveModel: execution.CodexProfile?.EffectiveModel, EffectiveEffort: execution.CodexProfile?.Effort,
            Engine: execution.CodexProfile?.Engine ?? "codex", ModelSelectedByCli: execution.CodexProfile is { Model: null },
            AssignmentId: execution.AssignmentId,
            OwnershipGeneration: execution.OwnershipGeneration, CodexRecovery: execution.CodexRecovery,
            CodexThreadId: execution.CodexThreadId, CodexSessionOwner: execution.CodexSessionOwner,
            ParentIssueNumber: execution.ParentIssueNumber, ParentIssueCaptured: execution.ParentIssueCaptured, ParentProvenance: execution.ParentProvenance,
            Metrics: execution.MetricsWith(report?.Integration?.Changes));

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }
}
