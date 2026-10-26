namespace CodexServer;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed partial class SqliteRegistryStore
{
    // Display-only projection. Expired or invalidated snapshots never authorize execution decisions.
    private async Task<IReadOnlyList<ExecutionRequest>> DecorateIssueTitlesAsync(
        IReadOnlyList<ExecutionRequest> executions, CancellationToken cancellationToken)
    {
        if (executions.Count == 0) return executions;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='github_issue_reads'";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            return executions;

        var projects = (await GetProjectsAsync(cancellationToken)).ToDictionary(project => project.Id);
        var keys = executions.Where(execution => execution.WorkReference.Type is "issue" or "github-issue")
            .Where(execution => projects.ContainsKey(execution.ProjectId) &&
                int.TryParse(execution.WorkReference.Id, out var number) && number > 0)
            .Select(execution => new { Repository = projects[execution.ProjectId].Repository.ToLowerInvariant(),
                Number = int.Parse(execution.WorkReference.Id, CultureInfo.InvariantCulture) }).Distinct().ToArray();
        if (keys.Length == 0) return executions;
        await using var command = connection.CreateCommand();
        // A single JSON parameter keeps the batch bounded by the response, without SQLite parameter limits.
        command.CommandText = """
            SELECT cached.repository, cached.number, json_extract(cached.snapshot, '$.Issue.Title')
            FROM github_issue_reads cached
            JOIN json_each($keys) requested
              ON cached.repository=json_extract(requested.value, '$.Repository')
              AND cached.number=json_extract(requested.value, '$.Number')
            WHERE cached.snapshot IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(keys));
        var titles = new Dictionary<(string Repository, int Number), string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                if (!reader.IsDBNull(2) && !string.IsNullOrWhiteSpace(reader.GetString(2)))
                    titles[(reader.GetString(0), reader.GetInt32(1))] = reader.GetString(2);
        return executions.Select(execution => projects.TryGetValue(execution.ProjectId, out var project) &&
            execution.WorkReference.Type is "issue" or "github-issue" &&
            int.TryParse(execution.WorkReference.Id, out var number) &&
            titles.TryGetValue((project.Repository.ToLowerInvariant(), number), out var title)
                ? execution with { WorkReference = execution.WorkReference with { Title = title } }
                : execution).ToArray();
    }
}
