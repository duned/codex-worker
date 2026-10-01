namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Text.Json;

/// <summary>Durable, at-most-once dispatch. Running commands are never replayed after reconnect.</summary>
public sealed class ProvisioningCommandStore(string databasePath, TimeProvider? timeProvider = null)
{
    private DateTimeOffset UtcNow => (timeProvider ?? TimeProvider.System).GetUtcNow();

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(token);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provisioning_commands (
                id TEXT PRIMARY KEY, node TEXT NOT NULL, capability TEXT NOT NULL, active INTEGER NOT NULL, body TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_provisioning_commands_active
                ON provisioning_commands(node, capability) WHERE active=1;
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<ProvisioningCommand> CreateAsync(ProvisioningCommandRequest request, CancellationToken token = default)
    {
        if (!ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request))
            throw new InvalidDataException("Unsupported or invalid provisioning action.");
        var operation = new ProvisioningCommand(Guid.NewGuid().ToString("N"), request, UtcNow,
            ProvisioningCommandStatus.Pending, ProvisioningDiagnostic.Queued);
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO provisioning_commands(id,node,capability,active,body) VALUES($id,$node,$capability,1,$body);";
        command.Parameters.AddWithValue("$id", operation.Id);
        command.Parameters.AddWithValue("$node", request.NodeId);
        command.Parameters.AddWithValue("$capability", request.CapabilityId);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(operation));
        try { await command.ExecuteNonQueryAsync(token); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new InvalidOperationException("This node capability already has an active operation."); }
        return operation;
    }

    public async Task<IReadOnlyList<ProvisioningCommand>> ListAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM provisioning_commands ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync(token);
        var operations = new List<ProvisioningCommand>();
        while (await reader.ReadAsync(token)) operations.Add(JsonSerializer.Deserialize<ProvisioningCommand>(reader.GetString(0))!);
        return operations;
    }

    // SQLite's immediate transaction serializes claims and all state transitions across service instances.
    public async Task<ProvisioningCommand?> ClaimAsync(string node, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM provisioning_commands WHERE node=$node AND active=1 ORDER BY rowid;";
        command.Parameters.AddWithValue("$node", node);
        ProvisioningCommand? pending = null;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var item = JsonSerializer.Deserialize<ProvisioningCommand>(reader.GetString(0))!;
                if (item.Status == ProvisioningCommandStatus.Pending) { pending = item; break; }
            }
        }
        if (pending is null) return null;
        var now = UtcNow;
        var running = pending with { Status = ProvisioningCommandStatus.Running, Diagnostic = ProvisioningDiagnostic.Executing,
            StartedAtUtc = now, DeadlineUtc = now.AddSeconds(pending.Request.TimeoutSeconds) };
        await SaveAsync(command, running, token);
        await transaction.CommitAsync(token);
        return running;
    }

    public async Task<ProvisioningCommand?> ReportAsync(string id, string node, ProvisioningCommandReport report,
        CancellationToken token = default) => await ChangeAsync(id, operation =>
    {
        if (!ProvisioningCommandProtocol.ValidReport(report)) throw new InvalidDataException("Invalid operation report.");
        if (report.PublicIdentity is not null && operation.Request.Action is not
            (ProvisioningCommandAction.GenerateSshKey or ProvisioningCommandAction.InspectSshKey))
            throw new InvalidDataException("Unexpected public identity.");
        if (report.Status == ProvisioningCommandStatus.Succeeded && report.PublicIdentity is null &&
            operation.Request.Action is ProvisioningCommandAction.GenerateSshKey or ProvisioningCommandAction.InspectSshKey)
            throw new InvalidDataException("Public identity is required for this action.");
        if (operation.Request.NodeId != node) throw new InvalidOperationException("Operation is not owned by this node.");
        if (ProvisioningCommandProtocol.Terminal(operation.Status))
        {
            if (operation.Status == report.Status && operation.Diagnostic == report.Diagnostic && operation.PublicIdentity == report.PublicIdentity) return operation;
            throw new InvalidOperationException("Operation is already terminal.");
        }
        if (operation.Status != ProvisioningCommandStatus.Running) throw new InvalidOperationException("Operation has not been dispatched.");
        return operation with { Status = report.Status, Diagnostic = report.Diagnostic,
            PublicIdentity = report.PublicIdentity, CompletedAtUtc = ProvisioningCommandProtocol.Terminal(report.Status) ? UtcNow : null };
    }, token);

    public async Task<ProvisioningCommand?> CancelAsync(string id, CancellationToken token = default) => await ChangeAsync(id, operation =>
    {
        if (operation.Status != ProvisioningCommandStatus.Pending) throw new InvalidOperationException("Only queued operations can be cancelled; running operations use their deadline.");
        return operation with { Status = ProvisioningCommandStatus.Cancelled, Diagnostic = ProvisioningDiagnostic.Cancelled, CompletedAtUtc = UtcNow };
    }, token);

    public async Task<ProvisioningCommand?> ReconcileAsync(string id, CancellationToken token = default) => await ChangeAsync(id, operation =>
    {
        if (operation.Status != ProvisioningCommandStatus.Running || operation.DeadlineUtc >= UtcNow)
            throw new InvalidOperationException("Only dispatched operations past their deadline can be reconciled after verifying the node is quiescent.");
        return operation with { Status = ProvisioningCommandStatus.Failed, Diagnostic = ProvisioningDiagnostic.Interrupted, CompletedAtUtc = UtcNow };
    }, token);

    private async Task<ProvisioningCommand?> ChangeAsync(string id, Func<ProvisioningCommand, ProvisioningCommand> change, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM provisioning_commands WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        var body = await command.ExecuteScalarAsync(token) as string;
        if (body is null) return null;
        var updated = change(JsonSerializer.Deserialize<ProvisioningCommand>(body)!);
        await SaveAsync(command, updated, token);
        await transaction.CommitAsync(token);
        return updated;
    }

    private static async Task SaveAsync(SqliteCommand command, ProvisioningCommand operation, CancellationToken token)
    {
        command.Parameters.Clear();
        command.CommandText = "UPDATE provisioning_commands SET active=$active,body=$body WHERE id=$id;";
        command.Parameters.AddWithValue("$id", operation.Id);
        command.Parameters.AddWithValue("$active", ProvisioningCommandProtocol.Terminal(operation.Status) ? 0 : 1);
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(operation));
        await command.ExecuteNonQueryAsync(token);
    }
}
