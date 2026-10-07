using Microsoft.Data.Sqlite;

namespace CodexWorker.Tests;

public sealed class ExecutionHistoryStoreTests
{
    [Fact]
    public async Task CodexRecoveryClaimAndConsumedBudgetSurviveRestartAndCannotBeDoubleClaimed()
    {
        using var database = new TemporaryDatabase();
        var now = DateTimeOffset.UtcNow;
        var source = Entry(Guid.NewGuid(), now) with
        {
            State = "InfrastructureFailure", CompletedAtUtc = now, RecoveryState = "codex-interrupted",
            CodexRecovery = new(Guid.NewGuid(), 1, 2, "fingerprint", Guid.NewGuid().ToString())
        };
        using (var store = new ExecutionHistoryStore(database.Path)) await store.CreateAsync(source);
        using var reopened = new ExecutionHistoryStore(database.Path);
        var persisted = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal(source.CodexRecovery, persisted.CodexRecovery);
        var attempt = Entry(Guid.NewGuid(), now.AddSeconds(1)) with
        {
            AttemptNumber = 2, RetryOfExecutionId = source.ExecutionId,
            CodexRecovery = source.CodexRecovery with { ResumeCount = 3 }
        };
        Assert.True(await reopened.TryClaimCodexRecoveryAsync(persisted, attempt, CancellationToken.None));
        Assert.False(await reopened.TryClaimCodexRecoveryAsync(persisted, attempt with { ExecutionId = Guid.NewGuid() }, CancellationToken.None));
        using var again = new ExecutionHistoryStore(database.Path);
        var consumed = (await again.ReadAllAsync()).Single(entry => entry.ExecutionId == attempt.ExecutionId);
        Assert.Equal(3, consumed.CodexRecovery?.ResumeCount);
        await again.UpdateAsync(consumed with { State = "InfrastructureFailure", CompletedAtUtc = now, RecoveryState = "codex-interrupted" });
        Assert.False(await again.TryClaimCodexRecoveryAsync(consumed,
            attempt with { ExecutionId = Guid.NewGuid(), AttemptNumber = 3 }, CancellationToken.None));
    }

    [Fact]
    public async Task CreationAndUpdatesSurviveStoreRecreationWithLifecycleDetails()
    {
        using var database = new TemporaryDatabase();
        var executionId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var initial = Entry(executionId, started) with { EffectiveModel = "task-model", EffectiveEffort = "low" };
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
            RecoveryStatus = "2 changed path(s); 1 staged path(s). Workspace retained for recovery.",
            ReportingFailure = "GitHub comment failed; remote Issue state is uncertain."
        };
        using (var store = new ExecutionHistoryStore(database.Path))
            await store.UpdateAsync(completed);

        using var reopened = new ExecutionHistoryStore(database.Path);
        var actual = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal(executionId, actual.ExecutionId);
        Assert.Equal("task-model", actual.EffectiveModel);
        Assert.Equal("low", actual.EffectiveEffort);
        Assert.Equal("Completed", actual.State);
        Assert.Equal(started.AddMinutes(3), actual.CompletedAtUtc);
        Assert.Equal((long?)180_000, actual.DurationMilliseconds);
        Assert.Equal("Implemented the requested change.", actual.ImplementationSummary);
        Assert.Equal(repair, Assert.Single(actual.Repairs));
        Assert.Equal(completed.CommitSha, actual.CommitSha);
        Assert.Equal("main", actual.IntegrationBranch);
        Assert.Equal("done/feature/8-example", actual.CompletedBranch);
        Assert.Equal("recoverable", actual.RecoveryState);
        Assert.Equal("base-sha", actual.RecoveryBaseCommit);
        Assert.Contains("Workspace retained", actual.RecoveryStatus);
        Assert.Contains("remote Issue state is uncertain", actual.ReportingFailure);
        await reopened.UpdateReportingFailureAsync(executionId, "Secondary interruption report failed.");
        Assert.Equal("Secondary interruption report failed.", Assert.Single(await reopened.ReadAllAsync()).ReportingFailure);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => reopened.UpdateAsync(actual with { State = "Failed" }));
    }

    [Fact]
    public async Task VersionSevenDatabaseMigratesReportingFailureWithoutChangingExistingOutcomes()
    {
        using var database = new TemporaryDatabase();
        var original = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "InfrastructureFailure", CompletedAtUtc = DateTimeOffset.UtcNow,
            FailureReason = "Primary execution failure"
        };
        using (var store = new ExecutionHistoryStore(database.Path)) await store.CreateAsync(original);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE executions DROP COLUMN codex_recovery_json; ALTER TABLE executions DROP COLUMN model_selected_by_cli; ALTER TABLE executions DROP COLUMN reporting_failure; ALTER TABLE executions DROP COLUMN integration_recovery_attempt_base; ALTER TABLE executions DROP COLUMN integration_recovery_claim; ALTER TABLE executions DROP COLUMN original_issue_body; PRAGMA user_version = 7;";
            await command.ExecuteNonQueryAsync();
        }

        using var migrated = new ExecutionHistoryStore(database.Path);
        var entry = Assert.Single(await migrated.ReadAllAsync());
        Assert.Equal("Primary execution failure", entry.FailureReason);
        Assert.Null(entry.ReportingFailure);
    }

    [Fact]
    public async Task VersionSixDatabaseMigratesWithoutInventingHistoricalSettings()
    {
        using var database = new TemporaryDatabase();
        var legacy = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow);
        using (var store = new ExecutionHistoryStore(database.Path))
            await store.CreateAsync(legacy);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE executions DROP COLUMN codex_recovery_json; ALTER TABLE executions DROP COLUMN effective_model; ALTER TABLE executions DROP COLUMN effective_effort; ALTER TABLE executions DROP COLUMN model_selected_by_cli; ALTER TABLE executions DROP COLUMN reporting_failure; ALTER TABLE executions DROP COLUMN integration_recovery_attempt_base; ALTER TABLE executions DROP COLUMN integration_recovery_claim; ALTER TABLE executions DROP COLUMN original_issue_body; PRAGMA user_version = 6;";
            await command.ExecuteNonQueryAsync();
        }
        using var migrated = new ExecutionHistoryStore(database.Path);
        var entry = Assert.Single(await migrated.ReadAllAsync());
        Assert.Equal(legacy.ExecutionId, entry.ExecutionId);
        Assert.Null(entry.EffectiveModel);
        Assert.Null(entry.EffectiveEffort);
        await migrated.UpdateAsync(entry with { EffectiveModel = "resolved-model", EffectiveEffort = "high" });
        Assert.Equal("high", Assert.Single(await migrated.ReadAllAsync()).EffectiveEffort);
    }

    [Fact]
    public async Task VersionNineMigrationRetainsCliSelectionWithoutInventingAModel()
    {
        using var database = new TemporaryDatabase();
        using (var store = new ExecutionHistoryStore(database.Path))
        {
            await store.CreateAsync(Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with { EffectiveEffort = "low" });
            await store.CreateAsync(Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with { EffectiveModel = "explicit-model", EffectiveEffort = "high" });
        }
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE executions DROP COLUMN codex_recovery_json; ALTER TABLE executions DROP COLUMN model_selected_by_cli; PRAGMA user_version = 9;";
            await command.ExecuteNonQueryAsync();
        }
        using var migrated = new ExecutionHistoryStore(database.Path);
        var entries = await migrated.ReadAllAsync();
        Assert.True(Assert.Single(entries, entry => entry.EffectiveModel is null).ModelSelectedByCli);
        Assert.False(Assert.Single(entries, entry => entry.EffectiveModel == "explicit-model").ModelSelectedByCli);
    }

    [Fact]
    public async Task UpdatesResolveCliModelWithoutChangingEffort()
    {
        using var database = new TemporaryDatabase();
        using var store = new ExecutionHistoryStore(database.Path);
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with { EffectiveEffort = "low", ModelSelectedByCli = true };
        await store.CreateAsync(entry);
        await store.UpdateAsync(entry with { EffectiveModel = "changed", EffectiveEffort = "high" });
        var actual = Assert.Single(await store.ReadAllAsync());
        Assert.Equal("changed", actual.EffectiveModel);
        Assert.True(actual.ModelSelectedByCli);
        Assert.Equal("low", actual.EffectiveEffort);
        await store.UpdateAsync(entry with { EffectiveModel = "another" });
        Assert.Equal("another", Assert.Single(await store.ReadAllAsync()).EffectiveModel);
        using var reopened = new ExecutionHistoryStore(database.Path);
        Assert.Equal("another", Assert.Single(await reopened.ReadAllAsync()).EffectiveModel);
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
    public async Task RecoveryExpiryIsStoredAndRecoveryCleanupDoesNotChangeExecutionHistory()
    {
        using var database = new TemporaryDatabase();
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "Failed", CompletedAtUtc = DateTimeOffset.UtcNow, RecoveryState = "recoverable",
            RecoveryBaseCommit = "base", RecoveryExpiresAtUtc = expires
        };
        using (var store = new ExecutionHistoryStore(database.Path))
        {
            await store.CreateAsync(entry);
            await store.UpdateRecoveryAsync(entry.ExecutionId, "expired-cleaned");
        }

        using var reopened = new ExecutionHistoryStore(database.Path);
        var actual = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("Failed", actual.State);
        Assert.NotNull(actual.CompletedAtUtc);
        Assert.Equal("expired-cleaned", actual.RecoveryState);
        Assert.Equal(expires, actual.RecoveryExpiresAtUtc);
    }

    [Fact]
    public void RecoveryRetentionUsesPersistedExpiryOrAControllableFallbackClock()
    {
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var entry = Entry(Guid.NewGuid(), started) with { CompletedAtUtc = started.AddHours(2) };
        var retention = TimeSpan.FromDays(7);
        var expiry = RecoveryRetentionPolicy.ExpiresAt(entry, retention);

        Assert.Equal(started.AddHours(2).AddDays(7), expiry);
        Assert.False(RecoveryRetentionPolicy.IsExpired(entry, retention, expiry.AddTicks(-1)));
        Assert.True(RecoveryRetentionPolicy.IsExpired(entry, retention, expiry));
        Assert.Equal(started.AddDays(20), RecoveryRetentionPolicy.ExpiresAt(entry with
        {
            RecoveryExpiresAtUtc = started.AddDays(20)
        }, retention));
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
            Assert.Equal(11L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM executions";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            var raw = await File.ReadAllTextAsync(database.Path);
            Assert.DoesNotContain("TEST_SECRET_SENTINEL", raw, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("CODEX_WORKER_HISTORY_TEST_SECRET", null); }
    }

    [Fact]
    public async Task ServerAssignmentRelationshipSurvivesHistoryReopen()
    {
        using var database = new TemporaryDatabase();
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            ServerExecutionId = "server-request-123", AssignmentId = "assignment-456"
        };
        using (var store = new ExecutionHistoryStore(database.Path)) await store.CreateAsync(entry);

        using var reopened = new ExecutionHistoryStore(database.Path);
        var actual = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("server-request-123", actual.ServerExecutionId);
        Assert.Equal("assignment-456", actual.AssignmentId);
    }

    [Fact]
    public async Task IntegrationRecoveryClaimsAreAtomicAndBaseBudgetSurvivesRestart()
    {
        using var database = new TemporaryDatabase();
        using var firstStore = new ExecutionHistoryStore(database.Path);
        using var secondStore = new ExecutionHistoryStore(database.Path);
        var source = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "IntegrationConflict", CompletedAtUtc = DateTimeOffset.UtcNow,
            RecoveryState = "integration-conflict", RecoveryBaseCommit = "implementation"
        };
        await firstStore.CreateAsync(source);
        var first = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with { RetryOfExecutionId = source.ExecutionId, AttemptNumber = 2 };
        var duplicate = first with { ExecutionId = Guid.NewGuid() };
        var claims = await Task.WhenAll(firstStore.TryClaimIntegrationRecoveryAsync(source, first, "base-A", false),
            secondStore.TryClaimIntegrationRecoveryAsync(source, duplicate, "base-A", false));
        Assert.Single(claims, claimed => claimed);
        var rows = await firstStore.ReadAllAsync();
        Assert.Equal(2, rows.Count);
        var claimedId = rows.Single(row => row.ExecutionId == source.ExecutionId).IntegrationRecoveryClaim;
        Assert.NotNull(claimedId);
        // A different attempt cannot release the claim.
        await secondStore.FinishIntegrationRecoveryAsync(source.ExecutionId, Guid.NewGuid(), null);
        Assert.Equal(claimedId, (await firstStore.ReadAllAsync()).Single(row => row.ExecutionId == source.ExecutionId).IntegrationRecoveryClaim);
        var claimed = rows.Single(row => row.ExecutionId == claimedId);
        await firstStore.UpdateAsync(claimed with { State = "IntegrationConflict", CompletedAtUtc = DateTimeOffset.UtcNow });
        await firstStore.FinishIntegrationRecoveryAsync(source.ExecutionId, claimed.ExecutionId, "base-B");
        using var reopened = new ExecutionHistoryStore(database.Path);
        var next = first with { ExecutionId = Guid.NewGuid(), AttemptNumber = 3 };
        Assert.False(await reopened.TryClaimIntegrationRecoveryAsync(source, next, "base-B", false));
        Assert.True(await reopened.TryClaimIntegrationRecoveryAsync(source, next, "base-C", false));
        Assert.Equal(3, (await reopened.ReadAllAsync()).Count);
    }

    [Fact]
    public async Task FailedRecoveryHistoryInsertRollsBackClaimAndDoesNotConsumeBaseBudget()
    {
        using var database = new TemporaryDatabase();
        using var store = new ExecutionHistoryStore(database.Path);
        var source = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        { State = "IntegrationConflict", CompletedAtUtc = DateTimeOffset.UtcNow, RecoveryState = "integration-conflict" };
        await store.CreateAsync(source);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => store.TryClaimIntegrationRecoveryAsync(source, source, "base-A", false));
        var unchanged = Assert.Single(await store.ReadAllAsync());
        Assert.Null(unchanged.IntegrationRecoveryClaim);
        Assert.Null(unchanged.IntegrationRecoveryAttemptBase);
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
