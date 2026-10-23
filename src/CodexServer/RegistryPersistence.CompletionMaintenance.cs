namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;

public sealed partial class SqliteRegistryStore
{
    internal static bool CompletionMaintenanceEligible(ExecutionRequest execution) =>
        execution.State == "Failed" && execution.RecoveryState == "LeaseExpiredUncertain" && execution.Lease?.State == "Expired" &&
            execution.ValidationResult is null or "passed" ||
        execution.ValidationResult == "passed" && (execution.State == "Failed" && execution.FailureClassification == "InfrastructureFailure" ||
            execution.State == "Completed" && execution.RecoveryState == "CompletionReconciled");

    public async Task RestoreCompletionProvenanceAsync(ExecutionMaintenanceCommand inventory, CancellationToken ct = default)
    {
        if (inventory is not { Status: "succeeded", Request.Action: "inventory", Report: { Outcome: "succeeded" } report } ||
            inventory.Request.Offset != 0 || report.Observations.Count >= inventory.Request.Limit ||
            inventory.Request.Project is not null || inventory.Request.IssueNumber is not null ||
            inventory.Request.Outcome is not null || inventory.Request.Origin is not null) return;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Inventory is authenticated Worker evidence, not completion authority. Restore only
        // absent identity on the exact original assignment; never overwrite a conflicting ID.
        foreach (var group in report.Observations.Where(o => o.ServerExecutionId is not null).GroupBy(o => o.ServerExecutionId))
        {
            if (group.Count() != 1) continue;
            var evidence = group.Single();
            if (report.Observations.Count(o => o.ExecutionId == evidence.ExecutionId) != 1) continue;
            if (evidence.RecoveryState != "managed-completion-quarantined" || evidence.Generation is not > 0 || evidence.AssignmentId is null) continue;
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE execution_requests SET worker_execution_id=$local
                WHERE id=$id AND assigned_worker_id=$worker AND assignment_id=$assignment
                    AND worker_execution_id IS NULL
                    AND (execution_id IS NULL OR lower(replace(execution_id,'-',''))=$normalized)
                    AND state='Failed' AND recovery_state='LeaseExpiredUncertain'
                    AND EXISTS (SELECT 1 FROM execution_leases WHERE execution_id=$id AND worker_id=$worker
                        AND generation=$generation AND state='Expired'
                        AND generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=$id))
                    AND NOT EXISTS (SELECT 1 FROM execution_requests WHERE worker_execution_id=$local)
                """;
            command.Parameters.AddWithValue("$id", evidence.ServerExecutionId);
            command.Parameters.AddWithValue("$worker", inventory.Request.WorkerId);
            command.Parameters.AddWithValue("$assignment", evidence.AssignmentId);
            command.Parameters.AddWithValue("$generation", evidence.Generation);
            command.Parameters.AddWithValue("$local", evidence.ExecutionId.ToString());
            command.Parameters.AddWithValue("$normalized", evidence.ExecutionId.ToString("N"));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
