using System.Text.Json;

namespace CodexWorker;

public sealed partial class ExecutionHistoryStore
{
    internal async Task ReconcileStandaloneSuccessAsync(ExecutionHistoryEntry source,
        ExecutionCompletion completion, CancellationToken ct)
    {
        // The repository gate is held by the caller. The compare-and-swap and audit
        // commit together; a restart cannot expose a corrected outcome without proof.
        await using var connection = await OpenAsync(ct);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE executions SET state='Completed', recovery_state='standalone-success-reconciled',
                completed_at_utc=COALESCE(completed_at_utc,$now), completion_json=$completion,
                recovery_base_commit=commit_sha, standalone_recovery_diagnostic_json=NULL
            WHERE execution_id=$id AND state=$state AND completion_json=$previous
                AND server_execution_id IS NULL AND assignment_id IS NULL AND ownership_generation IS NULL
                AND integration_recovery_claim IS NULL AND reporting_failure IS NULL
                AND validation_outcome='passed'
                AND NOT EXISTS (SELECT 1 FROM executions other WHERE other.execution_id!=executions.execution_id
                    AND other.repository=executions.repository AND other.issue_number=executions.issue_number
                    AND (other.completed_at_utc IS NULL OR other.attempt_number>=executions.attempt_number
                         OR other.started_at_utc>=executions.started_at_utc OR other.state='Completed'));
            """;
        command.Parameters.AddWithValue("$id", source.ExecutionId.ToString());
        command.Parameters.AddWithValue("$state", source.State);
        command.Parameters.AddWithValue("$previous", source.CompletionJson ?? throw new WorkerInfrastructureException("Completion provenance is missing."));
        command.Parameters.AddWithValue("$completion", JsonSerializer.Serialize(completion));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new WorkerInfrastructureException("Completion ownership changed; reconciliation refused.");
        command.CommandText = """
            INSERT INTO execution_acknowledgements(execution_id,acknowledged_at_utc,proof,pruned)
            VALUES($id,$now,$proof,0) ON CONFLICT(execution_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$proof", JsonSerializer.Serialize(new
        {
            Disposition = "standalone-success-reconciled", OriginalState = source.State,
            OriginalRecoveryState = source.RecoveryState, OriginalRecoveryBaseCommit = source.RecoveryBaseCommit,
            source.ExecutionId, source.Repository,
            source.IssueNumber, source.CommitSha, source.BaseBranch,
            Evidence = "Durable execution-bound validated completion; fresh authoritative remote ancestry; closed/done Issue; no conflicting attempt. Cleanup requires separate ownership/process proof."
        }));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Only a verified, exclusively owned standalone attempt may change recovery disposition.
    /// Original failure/validation diagnostics remain durable, including when the same attempt continues.</summary>
    internal async Task<bool> TryTransitionStandaloneAsync(ExecutionHistoryEntry source, string recovery,
        InterruptedWorkspaceInspection inspection, CancellationToken ct)
    {
        if (recovery is not ("standalone-continuing" or "standalone-retiring" or "standalone-retired"))
            throw new ArgumentException("Unsupported standalone transition.", nameof(recovery));
        if (inspection.Blocker is not null || !inspection.NotIntegrated || !GitRepository.IsCommitId(inspection.Head) ||
            !GitRepository.IsCommitId(inspection.RemoteBase) ||
            recovery == "standalone-retired" && source.RecoveryState != "standalone-retiring" ||
            recovery != "standalone-continuing" && inspection.HasChanges)
            return false;
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE executions SET recovery_state=$recovery,
                state=CASE WHEN $recovery='standalone-continuing' THEN 'Claimed'
                           WHEN $recovery='standalone-retired' THEN 'Superseded' ELSE state END,
                completed_at_utc=CASE WHEN $recovery='standalone-continuing' THEN NULL
                                     ELSE COALESCE(completed_at_utc,$now) END,
                recovery_status=$status,
                validation_outcome=COALESCE($validation,validation_outcome),
                codex_recovery_json=CASE WHEN $recovery='standalone-continuing'
                    THEN COALESCE($codex,codex_recovery_json) ELSE codex_recovery_json END
            WHERE execution_id=$id AND state=$state AND recovery_state IS $previous
                AND (state IN ('Created','Claimed','Preparing','Cancelled','InfrastructureFailure','Validating','Integrating')
                     OR recovery_state IN ('standalone-continuing','standalone-retiring'))
                AND server_execution_id IS NULL AND assignment_id IS NULL AND ownership_generation IS NULL
                AND completion_json IS NULL AND commit_sha IS NULL AND integration_branch IS NULL AND completed_branch IS NULL
                AND integration_recovery_claim IS NULL
                AND NOT EXISTS (SELECT 1 FROM executions other WHERE other.project=executions.project
                    AND other.repository=executions.repository AND other.issue_number=executions.issue_number
                    AND other.execution_id!=executions.execution_id
                    AND (other.completed_at_utc IS NULL OR other.attempt_number>=executions.attempt_number
                         OR other.started_at_utc>=executions.started_at_utc OR other.state='Completed'))
            """;
        command.Parameters.AddWithValue("$id", source.ExecutionId.ToString());
        command.Parameters.AddWithValue("$state", source.State);
        command.Parameters.AddWithValue("$previous", (object?)source.RecoveryState ?? DBNull.Value);
        command.Parameters.AddWithValue("$recovery", recovery);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", JsonSerializer.Serialize(inspection));
        command.Parameters.AddWithValue("$validation", source.ValidationOutcome is "legacy: pending validation"
            ? source.ValidationOutcome : DBNull.Value);
        command.Parameters.AddWithValue("$codex", source.CodexRecovery is null ? DBNull.Value : JsonSerializer.Serialize(source.CodexRecovery));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
