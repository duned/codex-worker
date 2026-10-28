namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Text.Json;

/// <summary>One bounded delivery observation per Worker; dispatch is never replayed.</summary>
public sealed class TelegramTestStore(string databasePath, TimeProvider clock)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS telegram_tests(worker TEXT PRIMARY KEY, body TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(ct);
    }

    private enum StoreAction { Get, Create, Claim, Report }
    public Task<TelegramTestOperation?> GetAsync(string worker, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Get, null, null, ct);
    public Task<TelegramTestOperation?> CreateAsync(string worker, string version, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Create, version, null, ct);
    public Task<TelegramTestOperation?> ClaimAsync(string worker, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Claim, null, null, ct);
    public Task<TelegramTestOperation?> ReportAsync(string worker, TelegramTestReport report, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Report, null, report, ct);

    private async Task<TelegramTestOperation?> AccessAsync(string worker, StoreAction action, string? version,
        TelegramTestReport? report, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM telegram_tests WHERE worker=$worker";
        command.Parameters.AddWithValue("$worker", worker);
        var body = await command.ExecuteScalarAsync(ct) as string;
        var operation = body is null ? null : JsonSerializer.Deserialize<TelegramTestOperation>(body);
        var now = clock.GetUtcNow();
        if (operation is { Status: "pending" or "running" } && operation.DeadlineUtc <= now)
            operation = operation with { Status = "failed", Diagnostic = "delivery-unavailable" };
        switch (action)
        {
            case StoreAction.Create:
                if (operation is { Status: "pending" or "running" }) throw new InvalidOperationException("A test is already pending.");
                operation = new(Guid.NewGuid().ToString("N"), version ?? throw new InvalidDataException("Missing version."), "pending", now.AddSeconds(60));
                break;
            case StoreAction.Claim:
                if (operation is not { Status: "pending" }) return null;
                operation = operation with { Status = "running" };
                break;
            case StoreAction.Report:
                if (report is null || !TelegramTestProtocol.ValidDiagnostic(report.Diagnostic) || operation is not { Status: "running" } || operation.Id != report.Id)
                    throw new InvalidOperationException("Test report is stale or invalid.");
                operation = operation with { Status = report.Diagnostic == "accepted" ? "succeeded" : "failed", Diagnostic = report.Diagnostic };
                break;
        }
        if (operation is not null)
        {
            command.CommandText = "INSERT INTO telegram_tests VALUES($worker,$body) ON CONFLICT(worker) DO UPDATE SET body=excluded.body";
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(operation));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return operation;
    }
}
