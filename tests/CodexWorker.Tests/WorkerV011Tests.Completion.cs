using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatedIntegrationWithLostPushResponseResumesCompletionWithoutImplementation(bool remoteReceivedPush)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = remoteReceivedPush;
        h.Git.IntegrationException = new WorkerInfrastructureException("Push response uncertain");
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("passed", pending.ValidationOutcome);
        Assert.Equal("main", pending.IntegrationBranch);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", pending.CommitSha);
        Assert.False(ExecutionCompletion.Read(pending).RemoteConfirmed);
        Assert.DoesNotContain("working->blocked", h.GitHub.Labels);
        Assert.Empty(h.GitHub.Comments);
        if (!remoteReceivedPush)
        {
            // Terminal Issue state must not bypass proof for a modern completion.
            h.GitHub.Closed = true;
            h.GitHub.CompletionLabels = ["done"];
            var missing = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.ResumeCompletionAsync(pending, CancellationToken.None));
            Assert.Contains("missing the exact", missing.Message, StringComparison.Ordinal);
            Assert.Empty(h.GitHub.Comments);
            h.GitHub.Closed = false;
            h.GitHub.CompletionLabels = ["working"];
            // Operator retries only the exact push. Reconciliation never publishes the base.
            h.Git.RemoteContainsIntegration = true;
        }
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var completed = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Completed", completed.State);
        Assert.True(ExecutionCompletion.Read(completed).Finished);
        Assert.True(h.GitHub.Closed);
        Assert.Single(h.GitHub.Comments);
        Assert.Contains("working->done", h.GitHub.Labels);
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA COMPLETADA", StringComparison.Ordinal));
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Single(h.GitHub.Comments);
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(1, h.Git.Started);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("label-before")]
    [InlineData("label-after")]
    [InlineData("comment-before")]
    [InlineData("comment-after")]
    [InlineData("close-before")]
    [InlineData("close-after")]
    [InlineData("cleanup-before")]
    [InlineData("cleanup-after")]
    public async Task CompletionRestartsDeduplicateEachDurableSuccessEffect(string failurePhase)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.GitHub.CompletionFailurePhase = failurePhase;
        h.Git.CompletionFailurePhase = failurePhase;
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        h.GitHub.CompletionFailurePhase = null;
        h.Git.CompletionFailurePhase = null;
        var pending = Assert.Single(await history.ReadAllAsync());
        // Recreate the coordinator with retained external effects and durable history.
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var completed = Assert.Single(await history.ReadAllAsync());
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Equal("Completed", completed.State);
        Assert.True(h.GitHub.Closed);
        Assert.Single(h.GitHub.Comments);
        Assert.Single(h.GitHub.Labels, label => label == "working->done");
        Assert.Equal(1, h.GitHub.CloseCalls);
        Assert.True(ExecutionCompletion.Read(completed).NotificationAttempted);
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA COMPLETADA", StringComparison.Ordinal));
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ManuallyClosedPublishedCompletionAcknowledgesSuccessWithoutGitHubEffects()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.GitHub.CompletionFailurePhase = "comment-before";
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        h.GitHub.CompletionFailurePhase = null;
        await h.GitHub.CloseAsync(1, CancellationToken.None);
        var closeCalls = h.GitHub.CloseCalls;
        var pending = Assert.Single(await history.ReadAllAsync());
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var completed = Assert.Single(await history.ReadAllAsync());
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Equal("Completed", completed.State);
        Assert.True(ExecutionCompletion.Read(completed).Finished);
        Assert.True(ExecutionCompletion.Read(completed).TerminalIssueAcknowledged);
        Assert.Empty(h.GitHub.Comments);
        Assert.Equal(closeCalls, h.GitHub.CloseCalls);
        Assert.Single(h.GitHub.Labels, label => label == "working->done");
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ConfirmedBaseWithPendingArchiveRemainsInCompletionLifecycle()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationFailure = new GitIntegrationArchiveException("Archive response uncertain",
            new WorkerInfrastructureException("Original archive failure"));
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", pending.State);
        Assert.True(ExecutionCompletion.Read(pending).RemoteConfirmed);
        Assert.DoesNotContain(h.GitHub.Labels, label => label.Contains("codex-integration-conflict", StringComparison.Ordinal));
        Assert.Empty(h.GitHub.Comments);
        await h.Worker.ResumeCompletionAsync(pending, CancellationToken.None);
        Assert.True(h.GitHub.Closed);
        Assert.Single(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task PendingIntegrationRecoveryCompletionRetainsClaimUntilSuccessAndNeverReportsAnotherRecoveryRejection()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var source = PreservedConflict();
        await history.CreateAsync(source);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationException = new WorkerInfrastructureException("Lost push response");
        h.GitHub.ReturnConflictIssue = true;
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var entries = await history.ReadAllAsync();
        var pending = entries.Single(entry => entry.ExecutionId != source.ExecutionId);
        Assert.Equal(pending.ExecutionId, entries.Single(entry => entry.ExecutionId == source.ExecutionId).IntegrationRecoveryClaim);
        var commentCount = h.GitHub.Comments.Count;
        var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram, history: history);
        await restarted.ReconcileIntegrationRecoveryAsync(CancellationToken.None);
        Assert.Equal(commentCount, h.GitHub.Comments.Count);
        await restarted.ResumeCompletionAsync(pending, CancellationToken.None);
        var reconciled = await history.ReadExecutionAsync(source.ExecutionId);
        Assert.NotNull(reconciled);
        Assert.Null(reconciled.IntegrationRecoveryClaim);
        Assert.Equal("integration-recovered", reconciled.RecoveryState);
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(1, h.Git.Integrations);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("body")]
    [InlineData("ready")]
    [InlineData("blocked")]
    [InlineData("closed")]
    [InlineData("new-attempt")]
    [InlineData("corrupt")]
    [InlineData("corrupt-terminal")]
    [InlineData("inconsistent-terminal")]
    [InlineData("missing")]
    [InlineData("validation")]
    public async Task CompletionFailsClosedOnConflictingIntentOwnershipOrProvenance(string conflict)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationException = new WorkerInfrastructureException("Lost push response");
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync());
        switch (conflict)
        {
            case "body": h.GitHub.Issue = h.GitHub.Issue with { Body = "Operator changed intent" }; break;
            case "ready": h.GitHub.CompletionLabels = ["ready"]; break;
            case "blocked": h.GitHub.CompletionLabels = ["blocked"]; break;
            case "closed": h.GitHub.Closed = true; break;
            case "new-attempt": await history.CreateAsync(pending with { ExecutionId = Guid.NewGuid(), AttemptNumber = 2 }); break;
            case "corrupt": pending = pending with { CompletionJson = "{" }; break;
            case "corrupt-terminal":
                pending = pending with { CompletionJson = "{" };
                h.GitHub.Closed = true;
                h.GitHub.CompletionLabels = ["done"];
                break;
            case "inconsistent-terminal":
                pending = pending with { CompletionJson = "{}" };
                h.GitHub.Closed = true;
                h.GitHub.CompletionLabels = ["done"];
                break;
            case "missing": pending = pending with { CompletionJson = null }; break;
            case "validation": pending = pending with { ValidationOutcome = null }; break;
        }
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => h.Worker.ResumeCompletionAsync(pending, CancellationToken.None));
        Assert.Empty(h.GitHub.Comments);
        Assert.DoesNotContain("working->done", h.GitHub.Labels);
        Assert.Single(h.Codex.Issues);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task ManagedHistoricalCompletionQuarantinesWithoutReplayingEffects(bool remoteContainsCommit, bool legacy, bool withoutCommit)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.GitHub.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.Git.IntegrationException = new WorkerInfrastructureException("Lost push response");
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync()) with
        {
            ServerExecutionId = "original-server-execution", AssignmentId = "original-assignment", OwnershipGeneration = 7
        };
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE executions SET server_execution_id=$server, assignment_id=$assignment, ownership_generation=$generation WHERE execution_id=$id";
            command.Parameters.AddWithValue("$server", pending.ServerExecutionId);
            command.Parameters.AddWithValue("$assignment", pending.AssignmentId);
            command.Parameters.AddWithValue("$generation", 7);
            command.Parameters.AddWithValue("$id", pending.ExecutionId.ToString());
            await command.ExecuteNonQueryAsync();
        }
        if (legacy)
        {
            pending = pending with { CompletionJson = null };
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE executions SET completion_json=NULL WHERE execution_id=$id";
            command.Parameters.AddWithValue("$id", pending.ExecutionId.ToString());
            await command.ExecuteNonQueryAsync();
        }
        if (withoutCommit)
        {
            // Validation passed but the execution stopped before integration recorded a commit.
            pending = pending with { CommitSha = null, IntegrationBranch = null };
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE executions SET commit_sha=NULL, integration_branch=NULL WHERE execution_id=$id";
            command.Parameters.AddWithValue("$id", pending.ExecutionId.ToString());
            await command.ExecuteNonQueryAsync();
        }
        h.Git.RemoteContainsIntegration = remoteContainsCommit;
        var config = h.Worker.Configuration;
        var global = new GlobalWorkerConfiguration();
        var runtime = new WorkerRuntimeReadModel(global, [("project.yml", config)], history);
        var host = new WorkerHost(global, [("project.yml", config)]);
        await host.ReconcileCompletionAsync(h.Worker, pending, runtime, CancellationToken.None);
        var retained = await history.ReadExecutionAsync(pending.ExecutionId);
        Assert.NotNull(retained);
        Assert.Equal(pending.CompletionJson, retained.CompletionJson);
        Assert.Equal(pending.ServerExecutionId, retained.ServerExecutionId);
        Assert.Equal(pending.AssignmentId, retained.AssignmentId);
        Assert.Equal(pending.OwnershipGeneration, retained.OwnershipGeneration);
        Assert.Equal(pending.CommitSha, retained.CommitSha);
        // Proven integration is quarantined; a commit absent from the remote base or never
        // recorded is not integrated. Neither fences the project or replays completion effects.
        var expectedState = remoteContainsCommit && !withoutCommit ? "managed-completion-quarantined" : "managed-not-integrated";
        Assert.Equal(expectedState, retained.RecoveryState);
        Assert.False(WorkerHost.NeedsCompletionReconciliation(retained));
        Assert.Equal(ProjectLifecycleState.Enabled, runtime.Registry.Get(config.Project.Name)?.State);
        Assert.True(runtime.Registry.TryReserve(config.Project.Name, config));
        runtime.Registry.Release(config.Project.Name);
        Assert.Empty(h.GitHub.Comments);
        Assert.DoesNotContain("working->done", h.GitHub.Labels);
        Assert.Equal(0, h.GitHub.CloseCalls);
        Assert.Single(h.Codex.Issues);
        {
            using var reopened = new ExecutionHistoryStore(database.Path);
            var restartedEntry = await reopened.ReadExecutionAsync(pending.ExecutionId);
            Assert.NotNull(restartedEntry);
            Assert.False(WorkerHost.NeedsCompletionReconciliation(restartedEntry));
            h.Git.IntegrationException = null;
            h.Git.CompletionMode = false;
            h.GitHub.CompletionMode = false;
            h.GitHub.ReadyIssueCount = 2;
            h.GitHub.ReturnDistinctIssues = true;
            Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
            Assert.Equal(2, h.Codex.Issues.Count);
            Assert.Equal(expectedState, (await reopened.ReadExecutionAsync(pending.ExecutionId))?.RecoveryState);
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task TerminalLegacyCompletionLeavesSchedulingAvailableAcrossRestarts()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.GitHub.Closed = true;
        h.GitHub.CompletionLabels = ["DONE", "unrelated-label"];
        for (var i = 0; i < 3; i++)
            await history.CreateAsync(PreservedConflict() with
            {
                IssueNumber = 100 + i, State = "InfrastructureFailure",
                RecoveryState = GitHubOperationException.ReconciliationRequiredState,
                CompletionJson = null
            });
        var entries = await history.ReadAllAsync();
        for (var restart = 0; restart < 2; restart++)
        {
            var config = h.Worker.Configuration;
            var global = new GlobalWorkerConfiguration();
            var runtime = new WorkerRuntimeReadModel(global, [("project.yml", config)], history);
            var host = new WorkerHost(global, [("project.yml", config)]);
            var worker = new Worker(config, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram,
                history: history, operationalLog: h.OperationalMessages.Add);
            foreach (var entry in entries)
                await host.ReconcileCompletionAsync(worker, entry, runtime, CancellationToken.None);
            Assert.Equal(ProjectLifecycleState.Enabled, runtime.Registry.Get(config.Project.Name)?.State);
            Assert.True(runtime.Registry.TryReserve(config.Project.Name, config));
            runtime.Registry.Release(config.Project.Name);
        }
        Assert.Equal(6, h.OperationalMessages.Count(message => message.Contains("legacy completion record acknowledged", StringComparison.Ordinal)));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(entries),
            System.Text.Json.JsonSerializer.Serialize(await history.ReadAllAsync()));
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(0, h.Git.Started);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Empty(h.GitHub.Labels);
        Assert.Empty(h.GitHub.Comments);
        Assert.Equal(0, h.GitHub.CloseCalls);
        Assert.Empty(h.TelegramMessages);
        // Discovery and claiming of a different ready Issue remain available.
        h.GitHub.Closed = false;
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
        Assert.Single(h.Codex.Issues);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(true, "working")]
    [InlineData(true, "done")]
    [InlineData(false, "")]
    [InlineData(false, "failed")]
    [InlineData(false, "done,ready")]
    [InlineData(false, "done,working")]
    [InlineData(false, "done,blocked")]
    [InlineData(false, "done,failed")]
    [InlineData(false, "done,codex-integration-conflict")]
    [InlineData(false, "done,codex-integration-recovery")]
    [InlineData(false, "unverifiable")]
    public async Task AmbiguousLegacyCompletionStillPausesProject(bool open, string labels)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var entry = PreservedConflict() with { State = "InfrastructureFailure",
            RecoveryState = GitHubOperationException.ReconciliationRequiredState };
        await history.CreateAsync(entry);
        h.GitHub.Closed = !open;
        h.GitHub.CompletionLabels = labels.Split(',');
        if (labels == "unverifiable") h.GitHub.StateReadFailure = new WorkerInfrastructureException("Issue read unavailable");
        var config = h.Worker.Configuration;
        var global = new GlobalWorkerConfiguration();
        var runtime = new WorkerRuntimeReadModel(global, [("project.yml", config)], history);
        var host = new WorkerHost(global, [("project.yml", config)]);
        await host.ReconcileCompletionAsync(h.Worker, entry, runtime, CancellationToken.None);
        Assert.Equal(ProjectLifecycleState.Unavailable, runtime.Registry.Get(config.Project.Name)?.State);
        Assert.False(runtime.Registry.TryReserve(config.Project.Name, config));
        Assert.Empty(h.Codex.Issues);
        Assert.Empty(h.GitHub.Labels);
        Assert.Empty(h.GitHub.Comments);
        Assert.Equal(0, h.GitHub.CloseCalls);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(entry),
            System.Text.Json.JsonSerializer.Serialize(await history.ReadExecutionAsync(entry.ExecutionId)));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task PullRequestIntegrationOpensAReviewAndLeavesTheIssueOpen()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.Result = new GitIntegrationResult(true, "Committed as `0123456789ab`. Pushed as `completed/17` for review into `main`.",
            "0123456789abcdef0123456789abcdef01234567", null, "completed/17", PullRequestBase: "main");

        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);

        var pullRequest = Assert.Single(h.GitHub.PullRequests);
        Assert.Equal(("completed/17", "main"), (pullRequest.Head, pullRequest.Base));
        Assert.StartsWith("Closes #17.", pullRequest.Body);
        // GitHub closes the Issue when the pull request is merged; the Worker does not.
        Assert.Equal(0, h.GitHub.CloseCalls);
        Assert.Contains("working->done", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("https://github.com/owner/repo/pull/41", StringComparison.Ordinal) &&
            comment.Contains("Awaiting review into: `main`", StringComparison.Ordinal));
        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Completed", entry.State);
        Assert.Equal("https://github.com/owner/repo/pull/41", entry.Metrics?.PullRequestUrl);
    }
}
