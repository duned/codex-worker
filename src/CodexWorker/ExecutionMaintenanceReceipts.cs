namespace CodexWorker;

using CodexProvisioning;
using System.Text.Json;

internal sealed record ExecutionMaintenanceReceipt(ExecutionMaintenanceCommand Command, ExecutionMaintenanceReport? Report = null);

public sealed partial class ExecutionHistoryStore
{
    internal async Task<ExecutionMaintenanceReceipt?> ReadMaintenanceReceiptAsync(string endpoint, string id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM execution_maintenance_receipts WHERE endpoint=$endpoint AND id=$id";
        command.Parameters.AddWithValue("$endpoint", endpoint);
        command.Parameters.AddWithValue("$id", id);
        var body = await command.ExecuteScalarAsync(ct) as string;
        return body is null ? null : JsonSerializer.Deserialize<ExecutionMaintenanceReceipt>(body)
            ?? throw new WorkerInfrastructureException("Maintenance receipt is invalid; preserve local state.");
    }
    internal async Task RecordMaintenanceReportDispositionAsync(Guid id, string disposition, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO execution_server_report_dispositions VALUES($id,$disposition) ON CONFLICT(execution_id) DO UPDATE SET disposition=excluded.disposition";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$disposition", disposition);
        await command.ExecuteNonQueryAsync(ct);
    }
    internal async Task<ExecutionMaintenanceReceipt?> ReadPendingMaintenanceAsync(string endpoint, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM execution_maintenance_receipts WHERE endpoint=$endpoint AND acknowledged=0 ORDER BY rowid LIMIT 1";
        command.Parameters.AddWithValue("$endpoint", endpoint);
        var body = await command.ExecuteScalarAsync(ct) as string;
        return body is null ? null : JsonSerializer.Deserialize<ExecutionMaintenanceReceipt>(body)
            ?? throw new WorkerInfrastructureException("Maintenance receipt is invalid; preserve local state.");
    }

    internal async Task SaveMaintenanceReceiptAsync(string endpoint, ExecutionMaintenanceReceipt receipt, bool acknowledged, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO execution_maintenance_receipts VALUES($id,$endpoint,$acknowledged,$body) ON CONFLICT(id) DO UPDATE SET acknowledged=excluded.acknowledged,body=excluded.body WHERE endpoint=excluded.endpoint";
        command.Parameters.AddWithValue("$id", receipt.Command.Request.OperationId);
        command.Parameters.AddWithValue("$endpoint", endpoint);
        command.Parameters.AddWithValue("$acknowledged", acknowledged ? 1 : 0);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(receipt));
        await command.ExecuteNonQueryAsync(ct);
    }
}
