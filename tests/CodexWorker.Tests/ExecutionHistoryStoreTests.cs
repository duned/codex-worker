using Microsoft.Data.Sqlite;

namespace CodexWorker.Tests;

public sealed class ExecutionHistoryStoreTests
{
    [Fact]
    public async Task CreationAndUpdatesSurviveStoreRecreationWithLifecycleDetails()
    {
        using var database = new TemporaryDatabase();
        var executionId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var initial = Entry(executionId, started);
        using (var store = new ExecutionHistoryStore(database.Path))
            await store.CreateAsync(initial);

        var repair = new ValidationRepairRecord("dotnet test", 1, 2, "Fixed the failing assertion", true);
        var completed = initial with
        {
            State = "Completed", CompletedAtUtc = started.AddMinutes(3), DurationMilliseconds = 180_000,
            ImplementationSummary = "Implemented the requested change.", ValidationOutcome = "passed",
            RepairCount = 1, Repairs = [repair], CommitSha = "0123456789abcdef0123456789abcdef01234567",
            IntegrationBranch = "main", CompletedBranch = "done/feature/8-example",
            RecoveryState = "recoverable", RecoveryBaseCommit = "base-sha",
            RecoveryStatus = "2 changed path(s); 1 staged path(s). Workspace retained for recovery."
        };
        using (var store = new ExecutionHistoryStore(database.Path))
            await store.UpdateAsync(completed);

        using var reopened = new ExecutionHistoryStore(database.Path);
        var actual = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal(executionId, actual.ExecutionId);
        Assert.Equal("Completed", actual.State);
        Assert.Equal(started.AddMinutes(3), actual.CompletedAtUtc);
        Assert.Equal((long?)180_000, actual.DurationMilliseconds);
        Assert.Equal(repair, Assert.Single(actual.Repairs));
        Assert.Equal(completed.CommitSha, actual.CommitSha);
        Assert.Equal("main", actual.IntegrationBranch);
        Assert.Equal("done/feature/8-example", actual.CompletedBranch);
        Assert.Equal("recoverable", actual.RecoveryState);
        Assert.Equal("base-sha", actual.RecoveryBaseCommit);
        Assert.Contains("Workspace retained", actual.RecoveryStatus);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => reopened.UpdateAsync(actual with { State = "Failed" }));
    }

    [Theory]
    [InlineData("Blocked", "Human input required")]
    [InlineData("Failed", "Validation failed")]
    [InlineData("InfrastructureFailure", "Git state uncertain")]
    public async Task TerminalOutcomesAndConciseReasonAreStored(string state, string reason)
    {
        using var database = new TemporaryDatabase();
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow);
        using (var store = new ExecutionHistoryStore(database.Path))
        {
            await store.CreateAsync(entry);
            await store.UpdateAsync(entry with { State = state, CompletedAtUtc = DateTimeOffset.UtcNow, FailureReason = reason });
        }

        using var reopened = new ExecutionHistoryStore(database.Path);
        var actual = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal(state, actual.State);
        Assert.Equal(reason, actual.FailureReason);
        Assert.NotNull(actual.CompletedAtUtc);
    }

    [Fact]
    public async Task CreatedExecutionWithoutTerminalStateRemainsIncompleteAfterRestart()
    {
        using var database = new TemporaryDatabase();
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow);
        using (var store = new ExecutionHistoryStore(database.Path)) await store.CreateAsync(entry);

        using var reopened = new ExecutionHistoryStore(database.Path);
        var interrupted = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("Created", interrupted.State);
        Assert.Null(interrupted.CompletedAtUtc);
    }

    [Fact]
    public async Task RetryAttemptIsPersistedAsSeparateLinkedHistoryRow()
    {
        using var database = new TemporaryDatabase();
        var first = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "Failed", CompletedAtUtc = DateTimeOffset.UtcNow, FailureReason = "Validation failed",
            RecoveryState = "recoverable", RecoveryBaseCommit = "base", AttemptNumber = 1
        };
        using (var store = new ExecutionHistoryStore(database.Path)) await store.CreateAsync(first);
        ExecutionHistoryEntry second;
        using (var reopenedBeforeRetry = new ExecutionHistoryStore(database.Path))
        {
            var persistedFailure = Assert.Single(await reopenedBeforeRetry.ReadAllAsync());
            Assert.Equal(first.ExecutionId, persistedFailure.ExecutionId);
            second = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1)) with
            {
                State = "Failed", CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(2), RecoveryState = "recoverable",
                RecoveryBaseCommit = "base-2", RetryOfExecutionId = persistedFailure.ExecutionId, AttemptNumber = 2, Resumed = true
            };
            await reopenedBeforeRetry.CreateAsync(second);
            await reopenedBeforeRetry.CreateAsync(Entry(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(3)) with
            {
                RetryOfExecutionId = second.ExecutionId, AttemptNumber = 3, Resumed = true
            });
        }
        using var reopened = new ExecutionHistoryStore(database.Path);
        var entries = await reopened.ReadAllAsync();
        Assert.Equal(3, entries.Count);
        Assert.Equal("Failed", entries.Single(e => e.ExecutionId == first.ExecutionId).State);
        var retry = entries.Single(e => e.ExecutionId == second.ExecutionId);
        Assert.NotEqual(first.ExecutionId, retry.ExecutionId);
        Assert.Equal(first.ExecutionId, retry.RetryOfExecutionId);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.True(retry.Resumed);
        Assert.Equal(second.ExecutionId, entries.Single(e => e.AttemptNumber == 3).RetryOfExecutionId);
    }

    [Fact]
    public async Task FreshDatabaseInitializesSchemaAndDoesNotPersistEnvironmentValues()
    {
        using var database = new TemporaryDatabase();
        using var store = new ExecutionHistoryStore(database.Path);
        Environment.SetEnvironmentVariable("CODEX_WORKER_HISTORY_TEST_SECRET", "TEST_SECRET_SENTINEL");
        try
        {
            await store.CreateAsync(Entry(Guid.NewGuid(), DateTimeOffset.UtcNow));
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM executions";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            var raw = await File.ReadAllTextAsync(database.Path);
            Assert.DoesNotContain("TEST_SECRET_SENTINEL", raw, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_WORKER_HISTORY_TEST_SECRET", null); }
    }

    private static ExecutionHistoryEntry Entry(Guid id, DateTimeOffset started) => new(
        id, "sample", "owner/repo", 8, "Persist execution history", "feature/8-history", "main", started,
        null, "Created", null, null, null, 0, [], null, null, null, null);

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(_directory, "history.db");
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
