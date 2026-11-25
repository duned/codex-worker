namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;

public sealed record FailureCause(string Cause, int Count);

/// <summary>Finished executions of one project in one UTC week (Monday to Sunday), by the time they finished.</summary>
/// <param name="FirstAttempts">Executions that were an Issue's first attempt (not a retry).</param>
/// <param name="FirstAttemptsIntegrated">First attempts that completed: success without a retry.</param>
/// <param name="QueueMedianSeconds">From queued to assigned to a Worker; null when none was assigned.</param>
public sealed record WeeklyExecutionStatistics(DateTimeOffset WeekStartUtc, int Finished, int Integrated, int Failed, int Cancelled,
    int FirstAttempts, int FirstAttemptsIntegrated, double? QueueMedianSeconds, double? QueueP90Seconds, double? DurationMedianSeconds,
    decimal? CostUsd, IReadOnlyList<FailureCause> FailureCauses);
public sealed record ProjectExecutionStatistics(string ProjectId, IReadOnlyList<WeeklyExecutionStatistics> Weeks);
public sealed record ExecutionStatisticsReport(DateTimeOffset FromUtc, DateTimeOffset ToUtc, IReadOnlyList<DateTimeOffset> WeekStartsUtc,
    IReadOnlyList<ProjectExecutionStatistics> Projects);

/// <summary>One finished execution request, as read for statistics.</summary>
public sealed record ExecutionStatisticsRow(string ProjectId, string State, DateTimeOffset CreatedAtUtc, DateTimeOffset? AssignedAtUtc,
    DateTimeOffset FinishedAtUtc, long? DurationMilliseconds, string? FailureClassification, string? IntegrationResult,
    int AttemptNumber, string? RetryOfExecutionId, decimal? CostUsd);

/// <summary>Weekly history per project, computed on the Server from every finished execution (archived ones included).</summary>
public static class ExecutionStatistics
{
    public const int MaximumWeeks = 52;
    public const int TopFailureCauses = 3;

    public static DateTimeOffset WeekStart(DateTimeOffset value)
    {
        var day = value.UtcDateTime.Date;
        return new DateTimeOffset(day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), TimeSpan.Zero);
    }

    /// <summary>The first week included when the report covers <paramref name="weeks"/> weeks ending with the current one.</summary>
    public static DateTimeOffset From(DateTimeOffset now, int weeks) => WeekStart(now).AddDays(-7 * (weeks - 1));

    public static ExecutionStatisticsReport Build(IEnumerable<ExecutionStatisticsRow> rows, DateTimeOffset now, int weeks)
    {
        var from = From(now, weeks);
        var starts = Enumerable.Range(0, weeks).Select(index => from.AddDays(7 * index)).ToArray();
        var projects = rows.Where(row => row.FinishedAtUtc >= from)
            .GroupBy(row => row.ProjectId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(project => new ProjectExecutionStatistics(project.Key, project.GroupBy(row => WeekStart(row.FinishedAtUtc))
                .OrderBy(week => week.Key).Select(week => Week(week.Key, week.ToArray())).ToArray()))
            .ToArray();
        return new(from, now, starts, projects);
    }

    private static WeeklyExecutionStatistics Week(DateTimeOffset start, IReadOnlyList<ExecutionStatisticsRow> rows)
    {
        var first = rows.Where(row => row.AttemptNumber <= 1 && row.RetryOfExecutionId is null && row.State != "Cancelled").ToArray();
        var queue = rows.Where(row => row.AssignedAtUtc is { } assigned && assigned >= row.CreatedAtUtc)
            .Select(row => (row.AssignedAtUtc!.Value - row.CreatedAtUtc).TotalSeconds).Order().ToArray();
        var durations = rows.Where(row => row.State != "Cancelled" && row.DurationMilliseconds is > 0)
            .Select(row => row.DurationMilliseconds!.Value / 1000d).Order().ToArray();
        var causes = rows.Where(row => row.State == "Failed").GroupBy(Cause, StringComparer.Ordinal)
            .Select(group => new FailureCause(group.Key, group.Count()))
            .OrderByDescending(cause => cause.Count).ThenBy(cause => cause.Cause, StringComparer.Ordinal).Take(TopFailureCauses).ToArray();
        var costs = rows.Where(row => row.CostUsd is not null).Select(row => row.CostUsd!.Value).ToArray();
        return new(start, rows.Count(row => row.State != "Cancelled"), rows.Count(row => row.State == "Completed"),
            rows.Count(row => row.State == "Failed"), rows.Count(row => row.State == "Cancelled"),
            first.Length, first.Count(row => row.State == "Completed"), Percentile(queue, 0.5), Percentile(queue, 0.9),
            Percentile(durations, 0.5), costs.Length == 0 ? null : costs.Sum(), causes);
    }

    /// <summary>A failed execution's cause: a rebase conflict, otherwise the classification the Worker reported.</summary>
    private static string Cause(ExecutionStatisticsRow row) =>
        row.IntegrationResult == ManagedIntegrationResults.RebaseConflict ? "RebaseConflict" :
        string.IsNullOrWhiteSpace(row.FailureClassification) ? "Unknown" : row.FailureClassification;

    /// <summary>Nearest-rank percentile of sorted values; null when there are none.</summary>
    internal static double? Percentile(IReadOnlyList<double> sorted, double fraction) =>
        sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];
}

public sealed partial class SqliteRegistryStore
{
    public async Task<ExecutionStatisticsReport> GetExecutionStatisticsAsync(int weeks, string? projectId, CancellationToken cancellationToken = default)
    {
        if (weeks is < 1 or > ExecutionStatistics.MaximumWeeks) throw new ArgumentOutOfRangeException(nameof(weeks));
        var now = _timeProvider.GetUtcNow();
        var from = ExecutionStatistics.From(now, weeks);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Finished requests only; a cancelled queued request has no assignment and finishes when it is cancelled.
        command.CommandText = """
            SELECT r.project_id, r.state, r.created_at_utc, r.assigned_at_utc, COALESCE(r.completed_at_utc, r.created_at_utc),
                   r.duration_ms, r.failure_classification, r.integration_result, r.attempt_number, r.retry_of_execution_id,
                   json_extract(m.metrics_json, '$.usage.costUsd')
            FROM execution_requests r LEFT JOIN execution_metrics m ON m.execution_id = r.id
            WHERE r.state IN ('Completed', 'Failed', 'Cancelled') AND COALESCE(r.completed_at_utc, r.created_at_utc) >= $from
              AND ($project IS NULL OR r.project_id = $project);
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O"));
        command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
        var rows = new List<ExecutionStatisticsRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(new(reader.GetString(0), reader.GetString(1), Time(reader.GetString(2)), reader.IsDBNull(3) ? null : Time(reader.GetString(3)),
                Time(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetDecimal(10)));
        }
        return ExecutionStatistics.Build(rows, now, weeks);
    }
}
