namespace CodexWorker;

using CodexProvisioning;
using System.Text.Json;

public sealed partial class Worker
{
    // Invoked only inside the drained host maintenance reservation. The repository gate
    // protects fresh proof and completion checkpoints; this never integrates or validates.
    internal async Task<ExecutionMaintenanceReport> ReconcileManagedCompletionAsync(ExecutionMaintenanceCommand command,
        Func<CancellationToken, Task<bool>> authorize, CancellationToken ct)
    {
        if (!ExecutionMaintenanceProtocol.Valid(command.Request) || command.Status != "running" ||
            command.DeadlineUtc is null || command.DeadlineUtc <= _clock.GetUtcNow())
            return new("refused", "maintenance-not-current", []);
        if (history is null) return new("refused", "worker-record-missing", []);
        await _repositoryGate.WaitAsync(ct);
        try
        {
            var request = command.Request;
            var entry = await history.ReadExecutionAsync(request.WorkerExecutionId ?? Guid.Empty, ct);
            ExecutionMaintenanceReport Refuse(string reason) => new("refused", reason, []);
            if (entry is null) return Refuse("worker-record-missing");
            if (request.Action != "reconcile-completion" || entry.ServerExecutionId != request.ServerExecutionId ||
                entry.AssignmentId != request.AssignmentId || entry.OwnershipGeneration != request.Generation ||
                entry.ServerExecutionId is null || entry.Project != config.Project.Name || entry.Repository != config.Project.Repository)
                return Refuse("worker-identity-mismatch");
            if (request.Apply && (serverSettings is null || !serverSettings.Enabled)) return Refuse("server-authority-required");
            if (!await authorize(ct)) return Refuse("server-authority-rejected");
            if (entry.RecoveryState is not ("managed-completion-quarantined" or "completion-reconciled") ||
                entry.ValidationOutcome != "passed" || entry.CompletedAtUtc is null ||
                entry.State is not ("InfrastructureFailure" or "Completed"))
                return Refuse("quarantined-completion-required");
            ExecutionCompletion completion;
            try { completion = ExecutionCompletion.Read(entry); }
            catch (WorkerInfrastructureException) { return Refuse("completion-provenance-missing"); }
            if (completion.Report.FinalValidationFailure is not null || completion.Report.Failure is not null ||
                completion.Report.AttemptNumber != entry.AttemptNumber || completion.Report.RetryOfExecutionId != entry.RetryOfExecutionId)
                return Refuse("completion-provenance-missing");
            if (completion.PolicyFingerprint != ExecutionCompletion.Policy(config) || entry.BaseBranch != config.Git.BaseBranch ||
                !config.Git.AutoMerge) return Refuse("completion-policy-changed");
            var others = await history.ReadAllAsync(ct);
            if (others.Any(other => other.ExecutionId != entry.ExecutionId &&
                other.ServerExecutionId == entry.ServerExecutionId && other.AssignmentId == entry.AssignmentId &&
                other.OwnershipGeneration == entry.OwnershipGeneration))
                return Refuse("ambiguous-completion-ownership");
            if (others.Any(other => other.ExecutionId != entry.ExecutionId && other.Project == entry.Project &&
                other.Repository == entry.Repository && other.IssueNumber == entry.IssueNumber &&
                (other.CompletedAtUtc is null || other.AttemptNumber >= entry.AttemptNumber || other.State == "Completed")))
                return Refuse("issue-execution-ownership-changed");
            var integration = completion.Report.Integration;
            var owner = integration?.ResourceExecutionId == entry.ExecutionId ? entry :
                integration?.ResourceExecutionId == entry.RetryOfExecutionId ? others.SingleOrDefault(e => e.ExecutionId == entry.RetryOfExecutionId) : null;
            if (owner is null || integration?.ResourceAttemptNumber != owner.AttemptNumber || owner.Project != entry.Project ||
                owner.Repository != entry.Repository || owner.IssueNumber != entry.IssueNumber || owner.FeatureBranch != entry.FeatureBranch)
                return Refuse("completion-provenance-missing");
            if (!await git.VerifyRemoteIntegrationAsync(entry, ct)) return Refuse("validated-commit-missing-on-remote-base");
            var issue = await github.GetCompletionIssueAsync(entry.IssueNumber, ct);
            if (issue is null || issue.Number != entry.IssueNumber) return Refuse("completion-issue-unavailable");
            var state = await github.ReadIssueStateAsync(entry.IssueNumber, ct);
            if (!CompletionIssueStateCompatible(state, completion)) return Refuse("completion-issue-state-conflict");
            if (state.Labels.Count > 100 || state.Labels.Any(label => label.Length > 100)) return Refuse("completion-issue-state-conflict");
            var fingerprint = ExecutionCompletion.IssueIntent(issue);
            var labels = state.Labels.Order(StringComparer.Ordinal).ToArray();
            var token = ExecutionCompletion.Fingerprint(JsonSerializer.Serialize(new
            {
                entry.ExecutionId, entry.ServerExecutionId, entry.AssignmentId, entry.OwnershipGeneration,
                entry.CommitSha, entry.BaseBranch, completion.PolicyFingerprint, fingerprint, state.IsOpen, labels
            }));
            var proof = new CompletionReconciliationProof(token, entry.CommitSha ?? "", entry.BaseBranch, "passed",
                state.IsOpen, labels, fingerprint != completion.IssueFingerprint, completion.IssueFingerprint, fingerprint, _clock.GetUtcNow());
            ExecutionMaintenanceObservation Observation() => new(entry.ExecutionId, entry.ServerExecutionId, entry.AssignmentId,
                entry.OwnershipGeneration, entry.State, entry.RecoveryState ?? "none", entry.ReportingFailure is null ? "none" : "pending",
                entry.Project, entry.IssueNumber);
            if (!request.Apply) return new("succeeded", "completion-reconciliation-preview", [Observation()], proof);
            if (request.ExpectedProof != token) return Refuse("completion-preview-changed");
            if (!await authorize(ct)) return Refuse("server-authority-rejected");
            // Retain the freshly verified proof before the first side effect, even if
            // the process stops before the final maintenance receipt reaches Server.
            completion = completion with { ReconciliationProof = proof, ReconciliationOperationId = request.OperationId };
            await history.SaveCompletionAsync(entry.ExecutionId, completion, false, ct);
            if (!completion.Finished)
            {
                completion = completion with { RemoteConfirmed = true };
                await history.SaveCompletionAsync(entry.ExecutionId, completion, false, ct);
                await ReportSuccessAsync(issue, completion.Report, completion, ct, repositoryGateHeld: true,
                    approvedIssueFingerprint: fingerprint, authorize: authorize);
                entry = await history.ReadExecutionAsync(entry.ExecutionId, ct) ?? throw new WorkerInfrastructureException("Completion history disappeared.");
                completion = ExecutionCompletion.Read(entry);
            }
            // Correct the original terminal result through the normal report endpoint with
            // explicit maintenance authority, never a fabricated execution lease.
            if (serverSettings is null) return Refuse("server-authority-required");
            await _registration.ReportExecutionAsync(serverSettings, entry with { State = "Completed" }, "Completed", null,
                request.Generation ?? 0, ct, request.OperationId);
            await history.SaveCompletionAsync(entry.ExecutionId, completion, true, ct);
            await history.UpdateReportingFailureAsync(entry.ExecutionId, null, ct);
            await history.RecordMaintenanceReportDispositionAsync(entry.ExecutionId, "acknowledged", ct);
            await FinishCompletionLineageAsync(entry, ct);
            entry = entry with { State = "Completed", RecoveryState = "completion-reconciled", ReportingFailure = null };
            return new("succeeded", "completion-reconciled", [Observation()], proof);
        }
        finally { _repositoryGate.Release(); }
    }
}
