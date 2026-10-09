namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Text.Json;

/// <summary>Durable audit and at-most-once dispatch. Expired dispatches remain uncertain, never replayed.</summary>
public sealed class ExecutionMaintenanceStore(string databasePath, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS execution_maintenance_commands
                (id TEXT PRIMARY KEY, worker TEXT NOT NULL, active INTEGER NOT NULL, body TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_execution_maintenance_active
                ON execution_maintenance_commands(worker) WHERE active=1;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ExecutionMaintenanceCommand> CreateAsync(ExecutionMaintenanceRequest request, string actor, CancellationToken ct = default)
    {
        if (!ExecutionMaintenanceProtocol.Valid(request)) throw new InvalidDataException("Invalid maintenance scope or action.");
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var existing = await ReadAsync(command, request.OperationId, ct);
        if (existing is not null)
        {
            if (existing.Request != request) throw new InvalidOperationException("Operation identity already has a different scope.");
            return Visible(existing);
        }
        var operation = new ExecutionMaintenanceCommand(request, "pending", Now, actor);
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO execution_maintenance_commands VALUES($id,$worker,1,$body)";
        command.Parameters.AddWithValue("$id", request.OperationId);
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(operation));
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new InvalidOperationException("Worker has outstanding maintenance; inspect or finish it before another operation."); }
        await transaction.CommitAsync(ct);
        return operation;
    }

    public async Task<ExecutionMaintenanceCommand?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        var operation = await ReadAsync(command, id, ct);
        return operation is null ? null : Visible(operation);
    }

    public async Task<IReadOnlyList<ExecutionMaintenanceCommand>> ListAsync(string? worker, int limit, int offset, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || offset is < 0 or > 10_000 || worker is not null && !Guid.TryParseExact(worker, "N", out _))
            throw new InvalidDataException("Invalid maintenance inventory bounds or Worker identity.");
        await using var connection = await OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM execution_maintenance_commands WHERE ($worker IS NULL OR worker=$worker) ORDER BY rowid DESC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$worker", (object?)worker ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<ExecutionMaintenanceCommand>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add(Visible(Parse(reader.GetString(0))));
        return items;
    }

    public async Task<ExecutionMaintenanceCommand?> ClaimAsync(string worker, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM execution_maintenance_commands WHERE worker=$worker AND active=1";
        command.Parameters.AddWithValue("$worker", worker);
        var body = await command.ExecuteScalarAsync(ct) as string;
        if (body is null) return null;
        var operation = Parse(body);
        // A quiescent Worker reconnect can close an expired, unconfirmed dispatch without replaying it.
        if (operation.Status == "running" && operation.DeadlineUtc <= Now) return Visible(operation);
        if (operation.Status != "pending") return null;
        operation = operation with { Status = "running", DeadlineUtc = Now.AddSeconds(operation.Request.TimeoutSeconds) };
        await SaveAsync(command, operation, ct);
        await transaction.CommitAsync(ct);
        return operation;
    }

    public async Task<ExecutionMaintenanceCommand?> CancelAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var operation = await ReadAsync(command, id, ct);
        if (operation is null || operation.Status == "cancelled") return operation;
        if (operation.Status != "pending") throw new InvalidOperationException("Only undispatched maintenance can be cancelled; inspect running or uncertain operations.");
        operation = operation with { Status = "cancelled", Report = new("refused", "operator-cancelled-before-dispatch", []), CompletedAtUtc = Now };
        await SaveAsync(command, operation, ct);
        await transaction.CommitAsync(ct);
        return operation;
    }

    public async Task<ExecutionMaintenanceCommand?> ReportAsync(string id, string worker, ExecutionMaintenanceReport report, CancellationToken ct = default)
    {
        if (!ExecutionMaintenanceProtocol.Valid(report)) throw new InvalidDataException("Invalid bounded maintenance report.");
        await using var connection = await OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var operation = await ReadAsync(command, id, ct);
        if (operation is null) return null;
        if (operation.Request.WorkerId != worker) throw new InvalidOperationException("Maintenance belongs to another Worker.");
        if (operation.Report is not null)
        {
            if (JsonSerializer.Serialize(operation.Report) != JsonSerializer.Serialize(report))
                throw new InvalidOperationException("Maintenance already has a different terminal report.");
            return Visible(operation);
        }
        if (operation.Status != "running") throw new InvalidOperationException("Maintenance was not dispatched.");
        if (report.Observations.Count > operation.Request.Limit || operation.Request.Action != "inventory" && report.Observations.Count > 1)
            throw new InvalidDataException("Observation exceeds the requested maintenance bound.");
        if (operation.Request.Action != "inventory" && report.Observations.Any(o => o.ExecutionId != operation.Request.WorkerExecutionId ||
            o.ServerExecutionId != operation.Request.ServerExecutionId || o.AssignmentId != operation.Request.AssignmentId || o.Generation != operation.Request.Generation))
            throw new InvalidDataException("Observation does not match maintenance scope.");
        operation = operation with { Status = report.Outcome, Report = report, CompletedAtUtc = Now };
        await SaveAsync(command, operation, ct);
        await transaction.CommitAsync(ct);
        return operation;
    }

    private ExecutionMaintenanceCommand Visible(ExecutionMaintenanceCommand operation) =>
        operation.Status == "running" && operation.DeadlineUtc <= Now
            ? operation with { Status = "uncertain", Report = new("uncertain", "deadline-expired-inspect-worker", []) } : operation;

    private static ExecutionMaintenanceCommand Parse(string body) => JsonSerializer.Deserialize<ExecutionMaintenanceCommand>(body)
        ?? throw new InvalidDataException("Invalid persisted maintenance command.");
    private static async Task<ExecutionMaintenanceCommand?> ReadAsync(SqliteCommand command, string id, CancellationToken ct)
    {
        command.CommandText = "SELECT body FROM execution_maintenance_commands WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        var body = await command.ExecuteScalarAsync(ct) as string;
        return body is null ? null : Parse(body);
    }
    private static async Task SaveAsync(SqliteCommand command, ExecutionMaintenanceCommand operation, CancellationToken ct)
    {
        command.Parameters.Clear();
        command.CommandText = "UPDATE execution_maintenance_commands SET body=$body,active=$active WHERE id=$id";
        command.Parameters.AddWithValue("$id", operation.Request.OperationId);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(operation));
        // Uncertain partial operations retain the slot until a Worker report provides a final disposition.
        command.Parameters.AddWithValue("$active", operation.Report is null || operation.Status == "uncertain" ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
    }
}
