namespace CodexWorker.Tests;

using CodexServer;
using Microsoft.Data.Sqlite;

public sealed class ExecutionStatisticsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T12:00:00Z"); // Wednesday

    private static ExecutionStatisticsRow Row(string state, DateTimeOffset finished, int attempt = 1, string? retryOf = null,
        string? failure = null, string? integration = null, double queueMinutes = 1, long durationSeconds = 60, decimal? cost = null, string project = "p") =>
        new(project, state, finished.AddMinutes(-queueMinutes - 10), finished.AddMinutes(-10), finished, durationSeconds * 1000,
            failure, integration, attempt, retryOf, cost);

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void WeeksStartOnMondayUtc()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-11-23T00:00:00Z"), ExecutionStatistics.WeekStart(Now));
        Assert.Equal(DateTimeOffset.Parse("2026-11-23T00:00:00Z"), ExecutionStatistics.WeekStart(DateTimeOffset.Parse("2026-11-29T23:59:59Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-11-30T00:00:00Z"), ExecutionStatistics.WeekStart(DateTimeOffset.Parse("2026-11-30T00:00:00+00:00")));
        Assert.Equal(DateTimeOffset.Parse("2026-11-16T00:00:00Z"), ExecutionStatistics.From(Now, 2));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void AWeekCountsOutcomesFirstAttemptSuccessCausesQueueTimeAndCost()
    {
        var thisWeek = DateTimeOffset.Parse("2026-11-24T09:00:00Z");
        var lastWeek = DateTimeOffset.Parse("2026-11-17T09:00:00Z");
        ExecutionStatisticsRow[] rows =
        [
            Row("Completed", thisWeek, queueMinutes: 1, cost: 0.5m),
            Row("Completed", thisWeek, attempt: 2, retryOf: "first", queueMinutes: 3, cost: 0.25m),
            Row("Failed", thisWeek, failure: "IntegrationConflict", integration: "rebase-conflict", queueMinutes: 2),
            Row("Failed", thisWeek, failure: "Failed", queueMinutes: 10),
            Row("Failed", thisWeek, failure: "Failed", attempt: 2, retryOf: "x", queueMinutes: 5),
            Row("Cancelled", thisWeek) with { AssignedAtUtc = null, DurationMilliseconds = null },
            Row("Completed", lastWeek),
            Row("Completed", DateTimeOffset.Parse("2026-10-01T00:00:00Z")), // before the window
            Row("Failed", thisWeek, failure: "Blocked", project: "other")
        ];

        var report = ExecutionStatistics.Build(rows, Now, 2);

        Assert.Equal([DateTimeOffset.Parse("2026-11-16T00:00:00Z"), DateTimeOffset.Parse("2026-11-23T00:00:00Z")], report.WeekStartsUtc);
        Assert.Equal(["other", "p"], report.Projects.Select(project => project.ProjectId));
        var weeks = report.Projects.Single(project => project.ProjectId == "p").Weeks;
        Assert.Equal(2, weeks.Count);
        Assert.Equal(1, weeks[0].Integrated);
        var week = weeks[1];
        Assert.Equal(DateTimeOffset.Parse("2026-11-23T00:00:00Z"), week.WeekStartUtc);
        Assert.Equal((5, 2, 3, 1), (week.Finished, week.Integrated, week.Failed, week.Cancelled));
        // First attempts: one completed, one rebase conflict, one failure.
        Assert.Equal((3, 1), (week.FirstAttempts, week.FirstAttemptsIntegrated));
        Assert.Equal([new FailureCause("Failed", 2), new FailureCause("RebaseConflict", 1)], week.FailureCauses);
        Assert.Equal(180, week.QueueMedianSeconds);
        Assert.Equal(600, week.QueueP90Seconds);
        Assert.Equal(60, week.DurationMedianSeconds);
        Assert.Equal(0.75m, week.CostUsd);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task TheRegistryReadsFinishedExecutionsWithTheirReportedCost()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"statistics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "registry.db");
            var store = new SqliteRegistryStore(path, timeProvider: new FixedClock(Now));
            await store.InitializeAsync();
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO execution_requests (id, project_id, work_type, work_id, work_reference_json, created_at_utc, state, assigned_at_utc, completed_at_utc, duration_ms, failure_classification, attempt_number)
                    VALUES ('a', 'p', 'github-issue', '1', '{}', '2026-11-24T08:00:00.0000000+00:00', 'Completed', '2026-11-24T08:02:00.0000000+00:00', '2026-11-24T09:00:00.0000000+00:00', 3000000, NULL, 1),
                           ('b', 'p', 'github-issue', '2', '{}', '2026-11-24T08:00:00.0000000+00:00', 'Failed', '2026-11-24T08:01:00.0000000+00:00', '2026-11-24T08:30:00.0000000+00:00', 1000000, 'InfrastructureFailure', 1),
                           ('c', 'p', 'github-issue', '3', '{}', '2026-11-24T08:00:00.0000000+00:00', 'Running', '2026-11-24T08:01:00.0000000+00:00', NULL, NULL, NULL, 1),
                           ('d', 'q', 'github-issue', '4', '{}', '2026-11-24T08:00:00.0000000+00:00', 'Completed', NULL, '2026-11-24T08:30:00.0000000+00:00', 10, NULL, 1);
                    INSERT INTO execution_metrics (execution_id, metrics_json) VALUES ('a', '{"usage":{"inputTokens":1,"cachedInputTokens":0,"outputTokens":1,"costUsd":1.25,"runs":1}}');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var report = await store.GetExecutionStatisticsAsync(4, "p");

            var week = Assert.Single(Assert.Single(report.Projects).Weeks);
            Assert.Equal((2, 1, 1), (week.Finished, week.Integrated, week.Failed));
            Assert.Equal(1.25m, week.CostUsd);
            Assert.Equal([new FailureCause("InfrastructureFailure", 1)], week.FailureCauses);
            Assert.Equal(2, (await store.GetExecutionStatisticsAsync(4, null)).Projects.Count);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetExecutionStatisticsAsync(53, null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
