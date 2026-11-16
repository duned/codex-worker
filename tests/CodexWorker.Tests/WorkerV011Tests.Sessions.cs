using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("success")]
    [InlineData("failed")]
    [InlineData("blocked")]
    public async Task EveryStartedCodexSessionIsRecordedSeparatelyFromRecovery(string status)
    {
        using var database = new TempHistoryDatabase();
        var thread = Guid.NewGuid().ToString().ToUpperInvariant();
        using (var history = new ExecutionHistoryStore(database.Path))
        using (var h = new Harness(history: history))
        {
            h.GitHub.Issue = h.GitHub.Issue with { ParentIssueNumber = 2 };
            h.Codex.ThreadId = thread;
            h.Codex.InitialOutcome = new(status, "Implementation result", [], false, null);
            await h.ProcessOneAsync();
        }
        using var reopened = new ExecutionHistoryStore(database.Path);
        var entry = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal(2, entry.ParentIssueNumber);
        Assert.Equal(thread, entry.CodexThreadId);
        Assert.Equal(CodexSessionOwnership.Current, entry.CodexSessionOwner);
        Assert.Equal(thread, entry.CodexRecovery?.SessionId);
        Assert.Equal(0, entry.CodexRecovery?.ResumeCount);
    }

    private static CodexOutcome Success(string summary) => new("success", summary, [], false, null);

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("success")]
    [InlineData("missing-commit")]
    [InlineData("stale-generation")]
    [InlineData("issue-conflict")]
    [InlineData("changed-preview")]
    [InlineData("missing-provenance")]
    [InlineData("authority-lost")]
    [InlineData("lost-comment-response")]
    [InlineData("ambiguous-ownership")]
    public async Task QuarantinedMetadataChangeCanOnlyResumeWithFreshExactProof(string scenario)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.GitHub.CompletionMode = true;
        h.GitHub.CompletionLabels = ["working", "unrelated"];
        h.Git.BeforeIntegration = () => h.GitHub.Issue = h.GitHub.Issue with
            { Title = "Current operator title", Body = "Current operator body" };
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var pending = Assert.Single(await history.ReadAllAsync());
        var serverId = Guid.NewGuid().ToString("N");
        var assignmentId = Guid.NewGuid().ToString("N");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE executions SET server_execution_id=$server, assignment_id=$assignment, ownership_generation=7 WHERE execution_id=$id";
            update.Parameters.AddWithValue("$server", serverId);
            update.Parameters.AddWithValue("$assignment", assignmentId);
            update.Parameters.AddWithValue("$id", pending.ExecutionId.ToString());
            await update.ExecuteNonQueryAsync();
        }
        pending = Assert.IsType<ExecutionHistoryEntry>(await history.ReadExecutionAsync(pending.ExecutionId));
        await h.Worker.ResumeCompletionAsync(pending, CancellationToken.None);
        Assert.Equal("managed-completion-quarantined", (await history.ReadExecutionAsync(pending.ExecutionId))?.RecoveryState);
        Assert.Empty(h.GitHub.Comments);
        var identity = Path.Combine(Path.GetDirectoryName(database.Path) ?? "", "identity");
        var workerId = await WorkerIdentity.LoadOrCreateAsync(identity);
        await WorkerAuthentication.LoadOrCreateTokenAsync(identity);
        var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = identity };
        using var handler = new CompletionReportHandler();
        using var client = new HttpClient(handler);
        var reconciler = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram,
            history: history, serverSettings: settings, registrationClient: new WorkerRegistrationClient(client));
        var request = new CodexProvisioning.ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), workerId,
            serverId, pending.ExecutionId, assignmentId, 7, "reconcile-completion");
        var command = new CodexProvisioning.ExecutionMaintenanceCommand(request, "running", DateTimeOffset.UtcNow,
            "operator", DateTimeOffset.UtcNow.AddMinutes(1));
        var preview = await reconciler.ReconcileManagedCompletionAsync(command, _ => Task.FromResult(true), CancellationToken.None);
        Assert.Equal("completion-reconciliation-preview", preview.Reason);
        Assert.NotNull(preview.CompletionProof);
        Assert.True(preview.CompletionProof.IssueContentChanged);
        Assert.True(preview.CompletionProof.IssueOpen);
        Assert.Contains("unrelated", preview.CompletionProof.Labels);
        Assert.Empty(h.GitHub.Comments);
        var validationCalls = h.Validation.Calls;
        var integrationCalls = h.Git.Integrations;
        command = command with { Request = request with { Apply = true, ExpectedProof = preview.CompletionProof.Token } };
        if (scenario == "ambiguous-ownership")
            await history.CreateAsync(pending with { ExecutionId = Guid.NewGuid() });
        if (scenario == "missing-commit") h.Git.RemoteContainsIntegration = false;
        if (scenario == "stale-generation") command = command with { Request = command.Request with { Generation = 8 } };
        if (scenario == "issue-conflict") h.GitHub.CompletionLabels = ["ready", "unrelated"];
        if (scenario == "changed-preview") h.GitHub.Issue = h.GitHub.Issue with { Body = "Changed again" };
        if (scenario == "missing-provenance")
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}");
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE executions SET completion_json=NULL WHERE execution_id=$id";
            update.Parameters.AddWithValue("$id", pending.ExecutionId.ToString());
            await update.ExecuteNonQueryAsync();
        }
        if (scenario == "lost-comment-response")
        {
            h.GitHub.CompletionFailurePhase = "comment-after";
            await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => reconciler.ReconcileManagedCompletionAsync(command,
                _ => Task.FromResult(true), CancellationToken.None));
            Assert.Single(h.GitHub.Comments);
            Assert.Empty(handler.Reports);
            h.GitHub.CompletionFailurePhase = null;
            var fresh = await reconciler.ReconcileManagedCompletionAsync(command with { Request = request },
                _ => Task.FromResult(true), CancellationToken.None);
            command = command with { Request = command.Request with { OperationId = Guid.NewGuid().ToString("N"), ExpectedProof = fresh.CompletionProof?.Token } };
        }
        var result = await reconciler.ReconcileManagedCompletionAsync(command, _ => Task.FromResult(scenario != "authority-lost"), CancellationToken.None);
        if (scenario is "success" or "lost-comment-response")
        {
            Assert.Equal("completion-reconciled", result.Reason);
            Assert.Single(h.GitHub.Comments);
            Assert.Equal(1, h.GitHub.CloseCalls);
            Assert.Equal("Current operator title", h.GitHub.Issue.Title);
            Assert.Equal("Current operator body", h.GitHub.Issue.Body);
            Assert.Contains("unrelated", h.GitHub.CompletionLabels);
            Assert.Contains("done", h.GitHub.CompletionLabels);
            Assert.DoesNotContain("working", h.GitHub.CompletionLabels);
            var completed = Assert.IsType<ExecutionHistoryEntry>(await history.ReadExecutionAsync(pending.ExecutionId));
            Assert.Equal("Completed", completed.State);
            Assert.Null(completed.ReportingFailure);
            Assert.Single(handler.Reports);
            Assert.Equal(command.Request.OperationId, handler.Reports[0].CompletionReconciliationOperationId);
            var retainedCompletion = ExecutionCompletion.Read(completed);
            Assert.Equal(command.Request.OperationId, retainedCompletion.ReconciliationOperationId);
            Assert.NotNull(retainedCompletion.ReconciliationProof);
            var repeatedPreview = await reconciler.ReconcileManagedCompletionAsync(command with { Request = request },
                _ => Task.FromResult(true), CancellationToken.None);
            var repeat = command with { Request = command.Request with { ExpectedProof = repeatedPreview.CompletionProof?.Token } };
            Assert.Equal("succeeded", (await reconciler.ReconcileManagedCompletionAsync(repeat,
                _ => Task.FromResult(true), CancellationToken.None)).Outcome);
            Assert.Single(h.GitHub.Comments);
            Assert.Equal(1, h.GitHub.CloseCalls);
            Assert.Single(h.GitHub.Labels, label => label == "working->done");
        }
        else
        {
            Assert.Equal("refused", result.Outcome);
            Assert.Empty(handler.Reports);
            Assert.Empty(h.GitHub.Comments);
            Assert.Equal(0, h.GitHub.CloseCalls);
        }
        Assert.Single(h.Codex.Issues);
        Assert.Equal(validationCalls, h.Validation.Calls);
        Assert.Equal(integrationCalls, h.Git.Integrations);
    }
}
