using Microsoft.Data.Sqlite;

namespace CodexWorker.Tests;

public sealed class ExecutionHistoryStoreTests
{
    [Theory]
    [InlineData(404, true)]
    [InlineData(409, true)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(0, false)]
    public async Task HistoricalServerReportRejectionsAreDurableOnlyForDefinitiveResponses(int status, bool terminal)
    {
        using var database = new TemporaryDatabase();
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            ServerExecutionId = "server-original", AssignmentId = "assignment-original", OwnershipGeneration = 3
        };
        var calls = 0;
        var warnings = new List<string>();
        Task Report()
        {
            calls++;
            throw new HttpRequestException("Unavailable", null, status == 0 ? null : (System.Net.HttpStatusCode)status);
        }
        using (var store = new ExecutionHistoryStore(database.Path))
        {
            await store.CreateAsync(entry);
            await WorkerHost.DeliverHistoricalServerResultAsync(store, entry, Report, warnings.Add, CancellationToken.None);
        }
        using var reopened = new ExecutionHistoryStore(database.Path);
        await WorkerHost.DeliverHistoricalServerResultAsync(reopened, entry, Report, warnings.Add, CancellationToken.None);
        Assert.Equal(terminal ? 1 : 2, calls);
        Assert.Equal(terminal, await reopened.ReadServerReportDispositionAsync(entry.ExecutionId) is not null);
        Assert.All(warnings, warning => Assert.Contains("Server execution server-original", warning, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("acknowledged")]
    [InlineData("manual-reconciliation-required: HTTP 404")]
    [InlineData("manual-reconciliation-required: HTTP 409")]
    public async Task ServerReportDispositionSurvivesRestartAndDuplicateRecording(string disposition)
    {
        using var database = new TemporaryDatabase();
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow);
        using (var store = new ExecutionHistoryStore(database.Path))
        {
            await store.CreateAsync(entry);
            Assert.Null(await store.ReadServerReportDispositionAsync(entry.ExecutionId));
            await store.SaveServerReportDispositionAsync(entry.ExecutionId, disposition);
            await store.SaveServerReportDispositionAsync(entry.ExecutionId, "duplicate");
        }
        using var reopened = new ExecutionHistoryStore(database.Path);
        Assert.Equal(disposition, await reopened.ReadServerReportDispositionAsync(entry.ExecutionId));
        Assert.Equal(entry.ExecutionId, (await reopened.ReadExecutionAsync(entry.ExecutionId))?.ExecutionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualCompletionRequiresVerifiedValidatedIntegration(bool verified)
    {
        var config = new WorkerConfiguration();
        config.GitHub = new GitHubSettings { DoneLabel = "done", ReadyLabel = "ready", WorkingLabel = "working", FailedLabel = "failed", BlockedLabel = "blocked" };
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "InfrastructureFailure", CompletedAtUtc = DateTimeOffset.UtcNow,
            ValidationOutcome = "passed", CommitSha = new string('a', 40), IntegrationBranch = "main"
        };
        var remote = new GitHubIssueState(false, ["done"]);
        var calls = 0;
        Task<bool> Verify(ExecutionHistoryEntry candidate, CancellationToken ct)
        {
            Assert.Equal(entry, candidate);
            Assert.Equal(CancellationToken.None, ct);
            calls++;
            return Task.FromResult(verified);
        }
        if (verified)
            Assert.Contains(entry.CommitSha, await ExecutionAdministrationCli.VerifyResolutionAsync(entry, config, remote, Verify, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => ExecutionAdministrationCli.VerifyResolutionAsync(entry, config, remote, Verify, CancellationToken.None));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecutionAdministrationCli.VerifyResolutionAsync(
            entry with { CommitSha = null, ValidationOutcome = null }, config, remote, Verify, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionListingShowsLegacyAndModernTerminalRecordsWithoutPrivateEvidence(bool modern)
    {
        using var database = new TemporaryDatabase();
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "Completed", CompletedAtUtc = DateTimeOffset.UtcNow,
            RecoveryState = "uncertain", CompletionJson = modern ? "private-completion-content" : null,
            OriginalIssueBody = "private-prompt", FailureReason = "/private/path"
        };
        using (var store = new ExecutionHistoryStore(database.Path)) await store.CreateAsync(entry);
        using var writer = new StringWriter();
        var result = await ExecutionAdministrationCli.RunAsync(new("executions", null, ["show", entry.ExecutionId.ToString()]),
            new WorkerConsole(), CancellationToken.None, writer, database.Path);
        Assert.Equal(ProcessExitCodes.Success, result);
        Assert.Contains(entry.ExecutionId.ToString(), writer.ToString());
        Assert.Contains("Completed", writer.ToString());
        Assert.Contains("uncertain", writer.ToString());
        Assert.DoesNotContain("private", writer.ToString());
    }

    [Fact]
    public async Task OperatorCheckoutLockRejectsConcurrentWorkerOrAdministration()
    {
        using var database = new TemporaryDatabase();
        var directory = System.IO.Path.GetDirectoryName(database.Path) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(directory);
        var runner = new ProcessRunner();
        var initialized = await runner.RunAsync("git", ["init", "--initial-branch=main"], directory,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(0, initialized.ExitCode);
        using var worker = new GitRepository(runner, directory, "owner/repo", new GitSettings(), new WorkerSettings());
        using var administrator = new GitRepository(runner, directory, "owner/repo", new GitSettings(), new WorkerSettings());
        await worker.AcquireWorkerLockAsync(CancellationToken.None);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => administrator.AcquireWorkerLockAsync(CancellationToken.None));
        worker.Dispose();
        await administrator.AcquireWorkerLockAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OperatorAcknowledgmentRetainsProvenanceAndAuditAcrossRestart()
    {
        using var database = new TemporaryDatabase();
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "Completed", CompletedAtUtc = DateTimeOffset.UtcNow,
            CommitSha = new string('a', 40), ValidationOutcome = "passed",
            OriginalIssueBody = "retained prompt", RecoveryState = "uncertain"
        };
        string? audit;
        using (var store = new ExecutionHistoryStore(database.Path))
        {
            await store.CreateAsync(entry);
            await store.AcknowledgeAsync(entry, "verified remote integration", false, CancellationToken.None);
            audit = await store.ReadAcknowledgementAsync(entry.ExecutionId);
            await store.AcknowledgeAsync(entry, "second call", false, CancellationToken.None);
            Assert.Equal(audit, await store.ReadAcknowledgementAsync(entry.ExecutionId));
        }
        using var restarted = new ExecutionHistoryStore(database.Path);
        var acknowledged = Assert.Single(await restarted.ReadAllAsync());
        Assert.Equal("operator-acknowledged", acknowledged.RecoveryState);
        Assert.False(WorkerHost.NeedsCompletionReconciliation(acknowledged));
        Assert.True(WorkerHost.NeedsCompletionReconciliation(entry));
        Assert.Equal(entry.CommitSha, acknowledged.CommitSha);
        Assert.Equal(entry.State, acknowledged.State);
        Assert.Equal(entry.OriginalIssueBody, acknowledged.OriginalIssueBody);
        Assert.Equal(audit, await restarted.ReadAcknowledgementAsync(entry.ExecutionId));
        await restarted.AcknowledgeAsync(acknowledged, "third call", true, CancellationToken.None);
        Assert.Null((await restarted.ReadExecutionAsync(entry.ExecutionId))?.OriginalIssueBody);
        Assert.Contains("verified remote integration", await restarted.ReadAcknowledgementAsync(entry.ExecutionId));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("managed")]
    [InlineData("claim")]
    [InlineData("session")]
    public async Task OperatorAcknowledgmentRejectsUnsafePruning(string kind)
    {
        using var database = new TemporaryDatabase();
        using var store = new ExecutionHistoryStore(database.Path);
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "Completed", CompletedAtUtc = kind == "active" ? null : DateTimeOffset.UtcNow,
            ServerExecutionId = kind == "managed" ? "server-id" : null,
            IntegrationRecoveryClaim = kind == "claim" ? Guid.NewGuid() : null,
            CodexRecovery = kind == "session" ? new(Guid.NewGuid(), 1, 0, "fingerprint", "session") : null
        };
        await store.CreateAsync(entry);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => store.AcknowledgeAsync(entry, "proof", true, CancellationToken.None));
        Assert.Null(await store.ReadAcknowledgementAsync(entry.ExecutionId));
        Assert.Equal(entry.RecoveryState, (await store.ReadExecutionAsync(entry.ExecutionId))?.RecoveryState);
    }

    [Fact]
    public void OperatorProofRequiresClosedDoneAndNoConflictingLabels()
    {
        var labels = new GitHubSettings { DoneLabel = "done", ReadyLabel = "ready", WorkingLabel = "working", FailedLabel = "failed", BlockedLabel = "blocked" };
        Assert.True(ExecutionAdministrationCli.IsResolved(new(false, [labels.DoneLabel]), labels));
        Assert.False(ExecutionAdministrationCli.IsResolved(new(true, [labels.DoneLabel]), labels));
        Assert.False(ExecutionAdministrationCli.IsResolved(new(false, []), labels));
        Assert.False(ExecutionAdministrationCli.IsResolved(new(false, [labels.DoneLabel, labels.WorkingLabel]), labels));
        var entry = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with { State = "Completed", CompletedAtUtc = DateTimeOffset.UtcNow };
        Assert.Null(ExecutionAdministrationCli.Ineligible(entry, [entry]));
        Assert.NotNull(ExecutionAdministrationCli.Ineligible(entry, [entry, entry with { ExecutionId = Guid.NewGuid(), CompletedAtUtc = null }]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReconstructionIsAtomicSurvivesRestartAndRejectsNewerOwnership(bool competing)
    {
        using var database = new TemporaryDatabase();
        var source = LegacyCodexSessionTests.Source();
        var candidate = source with
        {
            RecoveryBaseCommit = new string('a', 40), RecoveryState = "codex-interrupted",
            CodexRecovery = new(source.ExecutionId, source.AttemptNumber, 0, "fingerprint", Guid.NewGuid().ToString())
        };
        using (var store = new ExecutionHistoryStore(database.Path))
        {
            await store.CreateAsync(source);
            if (competing) await store.CreateAsync(source with { ExecutionId = Guid.NewGuid(), AttemptNumber = 2, State = "Implementing", CompletedAtUtc = null });
            Assert.Equal(!competing, await store.TryReconstructCodexRecoveryAsync(candidate, CancellationToken.None));
        }
        using var restarted = new ExecutionHistoryStore(database.Path);
        var persisted = (await restarted.ReadAllAsync()).Single(row => row.ExecutionId == source.ExecutionId);
        Assert.Equal(source.State, persisted.State);
        Assert.Equal(source.CompletedAtUtc, persisted.CompletedAtUtc);
        Assert.Equal(competing ? "uncertain" : "codex-interrupted", persisted.RecoveryState);
        Assert.Equal(competing ? null : candidate.CodexRecovery, persisted.CodexRecovery);
        Assert.False(await restarted.TryReconstructCodexRecoveryAsync(candidate, CancellationToken.None));
        if (competing) return;
        var attempt = source with
        {
            ExecutionId = Guid.NewGuid(), AttemptNumber = 2, RetryOfExecutionId = source.ExecutionId,
            CompletedAtUtc = null, State = "Created", CodexRecovery = candidate.CodexRecovery with { ResumeCount = 1 }
        };
        Assert.True(await restarted.TryClaimCodexRecoveryAsync(persisted, attempt, CancellationToken.None));
        Assert.False(await restarted.TryClaimCodexRecoveryAsync(persisted, attempt with { ExecutionId = Guid.NewGuid() }, CancellationToken.None));
        using var nextRestart = new ExecutionHistoryStore(database.Path);
        Assert.Equal("codex-resuming", (await nextRestart.ReadExecutionAsync(source.ExecutionId))?.RecoveryState);
        Assert.Equal(attempt.CodexRecovery, (await nextRestart.ReadExecutionAsync(attempt.ExecutionId))?.CodexRecovery);
    }

    [Fact]
    public async Task VersionElevenMigrationPreservesOutcomeWithoutInventingIntegrationCompletionProof()
    {
        using var database = new TemporaryDatabase();
        var original = Entry(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            State = "InfrastructureFailure", CompletedAtUtc = DateTimeOffset.UtcNow,
            ValidationOutcome = "passed", RecoveryState = GitHubOperationException.ReconciliationRequiredState
        };
        using (var store = new ExecutionHistoryStore(database.Path)) await store.CreateAsync(original);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE execution_archives; DROP TABLE execution_server_report_dispositions; DROP TABLE execution_acknowledgements; ALTER TABLE executions DROP COLUMN completion_json; PRAGMA user_version = 11;";
            await command.ExecuteNonQueryAsync();
        }
        using var migrated = new ExecutionHistoryStore(database.Path);
        Assert.Equivalent(original, Assert.Single(await migrated.ReadAllAsync()));
        Assert.Null((await migrated.ReadExecutionAsync(original.ExecutionId))?.CompletionJson);
    }

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
            command.CommandText = "DROP TABLE execution_archives; DROP TABLE execution_server_report_dispositions; DROP TABLE execution_acknowledgements; ALTER TABLE executions DROP COLUMN completion_json; ALTER TABLE executions DROP COLUMN codex_recovery_json; ALTER TABLE executions DROP COLUMN model_selected_by_cli; ALTER TABLE executions DROP COLUMN reporting_failure; ALTER TABLE executions DROP COLUMN integration_recovery_attempt_base; ALTER TABLE executions DROP COLUMN integration_recovery_claim; ALTER TABLE executions DROP COLUMN original_issue_body; PRAGMA user_version = 7;";
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
            command.CommandText = "DROP TABLE execution_archives; DROP TABLE execution_server_report_dispositions; DROP TABLE execution_acknowledgements; ALTER TABLE executions DROP COLUMN completion_json; ALTER TABLE executions DROP COLUMN codex_recovery_json; ALTER TABLE executions DROP COLUMN effective_model; ALTER TABLE executions DROP COLUMN effective_effort; ALTER TABLE executions DROP COLUMN model_selected_by_cli; ALTER TABLE executions DROP COLUMN reporting_failure; ALTER TABLE executions DROP COLUMN integration_recovery_attempt_base; ALTER TABLE executions DROP COLUMN integration_recovery_claim; ALTER TABLE executions DROP COLUMN original_issue_body; PRAGMA user_version = 6;";
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
            command.CommandText = "DROP TABLE execution_archives; DROP TABLE execution_server_report_dispositions; DROP TABLE execution_acknowledgements; ALTER TABLE executions DROP COLUMN completion_json; ALTER TABLE executions DROP COLUMN codex_recovery_json; ALTER TABLE executions DROP COLUMN model_selected_by_cli; PRAGMA user_version = 9;";
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
            Assert.Equal(15L, (long)(await command.ExecuteScalarAsync())!);
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
