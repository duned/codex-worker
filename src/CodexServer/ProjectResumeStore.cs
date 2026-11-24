namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using System.Text.Json;

/// <summary>One operator request per Worker to lift a project pause held in that Worker's memory; dispatch is never replayed.</summary>
public sealed class ProjectResumeStore(string databasePath, TimeProvider clock)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS project_resumes(worker TEXT PRIMARY KEY, body TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(ct);
    }

    private enum StoreAction { Get, Create, Claim, Report }
    public Task<ProjectResumeOperation?> GetAsync(string worker, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Get, null, null, ct);
    public Task<ProjectResumeOperation?> CreateAsync(string worker, string projectId, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Create, projectId, null, ct);
    public Task<ProjectResumeOperation?> ClaimAsync(string worker, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Claim, null, null, ct);
    public Task<ProjectResumeOperation?> ReportAsync(string worker, ProjectResumeReport report, CancellationToken ct) =>
        AccessAsync(worker, StoreAction.Report, null, report, ct);

    private async Task<ProjectResumeOperation?> AccessAsync(string worker, StoreAction action, string? projectId,
        ProjectResumeReport? report, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT body FROM project_resumes WHERE worker=$worker";
        command.Parameters.AddWithValue("$worker", worker);
        var body = await command.ExecuteScalarAsync(ct) as string;
        var operation = body is null ? null : JsonSerializer.Deserialize<ProjectResumeOperation>(body);
        var now = clock.GetUtcNow();
        if (operation is { Status: "pending" or "running" } && operation.DeadlineUtc <= now)
            operation = operation with { Status = "failed", Diagnostic = "delivery-unavailable" };
        switch (action)
        {
            case StoreAction.Create:
                if (!ProjectResumeProtocol.ValidProjectId(projectId)) throw new InvalidDataException("Invalid project.");
                if (operation is { Status: "pending" or "running" }) throw new InvalidOperationException("A resume request is already pending.");
                operation = new(Guid.NewGuid().ToString("N"), projectId, "pending", now.AddSeconds(ProjectResumeProtocol.TimeoutSeconds));
                break;
            case StoreAction.Claim:
                if (operation is not { Status: "pending" }) return null;
                operation = operation with { Status = "running" };
                break;
            case StoreAction.Report:
                if (report is null || !ProjectResumeProtocol.ValidDiagnostic(report.Diagnostic) || operation is not { Status: "running" } || operation.Id != report.Id)
                    throw new InvalidOperationException("Resume report is stale or invalid.");
                operation = operation with { Status = report.Diagnostic == "resumed" ? "succeeded" : "failed", Diagnostic = report.Diagnostic };
                break;
        }
        if (operation is not null)
        {
            command.CommandText = "INSERT INTO project_resumes VALUES($worker,$body) ON CONFLICT(worker) DO UPDATE SET body=excluded.body";
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(operation));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return operation;
    }
}
