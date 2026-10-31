using System.Text.Json;

namespace CodexWorker;

public sealed partial class ExecutionHistoryStore
{
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
        command.Parameters.AddWithValue("$codex", source.CodexRecovery is null ? DBNull.Value : JsonSerializer.Serialize(source.CodexRecovery));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
