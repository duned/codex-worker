namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("success")]
    [InlineData("missing-commit")]
    [InlineData("stale-generation")]
    [InlineData("authority-lost")]
    public async Task ManualCompletionClosesIntegratedIssueWithOnlyDoneLabel(string scenario)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.CompletionMode = true;
        h.Git.RemoteContainsIntegration = true;
        h.GitHub.CompletionMode = true;
        // A stale conflict label left by an earlier attempt made success reporting refuse.
        h.GitHub.CompletionLabels = ["working", "codex-integration-conflict", "unrelated"];
        await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => h.ProcessOneAsync());
        var failed = Assert.Single(await history.ReadAllAsync());
        Assert.NotEqual("Completed", failed.State);
        Assert.NotNull(failed.CompletedAtUtc);
        var serverId = Guid.NewGuid().ToString("N");
        var assignmentId = Guid.NewGuid().ToString("N");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE executions SET server_execution_id=$server, assignment_id=$assignment, ownership_generation=3 WHERE execution_id=$id";
            update.Parameters.AddWithValue("$server", serverId);
            update.Parameters.AddWithValue("$assignment", assignmentId);
            update.Parameters.AddWithValue("$id", failed.ExecutionId.ToString());
            await update.ExecuteNonQueryAsync();
        }
        var identity = Path.Combine(Path.GetDirectoryName(database.Path) ?? "", "identity");
        var workerId = await WorkerIdentity.LoadOrCreateAsync(identity);
        await WorkerAuthentication.LoadOrCreateTokenAsync(identity);
        var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = identity };
        using var handler = new CompletionReportHandler();
        using var client = new HttpClient(handler);
        var worker = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram,
            history: history, serverSettings: settings, registrationClient: new WorkerRegistrationClient(client));
        var request = new CodexProvisioning.ExecutionMaintenanceRequest(Guid.NewGuid().ToString("N"), workerId, serverId,
            failed.ExecutionId, assignmentId, scenario == "stale-generation" ? 4 : 3, CodexProvisioning.ExecutionMaintenanceProtocol.ManualCompletion, Apply: true);
        var command = new CodexProvisioning.ExecutionMaintenanceCommand(request, "running", DateTimeOffset.UtcNow, "operator", DateTimeOffset.UtcNow.AddMinutes(1));
        if (scenario == "missing-commit") h.Git.RemoteContainsIntegration = false;
        var comments = h.GitHub.Comments.Count;
        var validationCalls = h.Validation.Calls;
        var integrationCalls = h.Git.Integrations;

        var result = await worker.CompleteManuallyAsync(command, _ => Task.FromResult(scenario != "authority-lost"), CancellationToken.None);

        if (scenario == "success")
        {
            Assert.Equal("succeeded", result.Outcome);
            Assert.Contains("codex-integration-conflict", h.GitHub.RemovedLabels);
            Assert.Contains("working->done", h.GitHub.Labels);
            Assert.Equal(1, h.GitHub.CloseCalls);
            Assert.Equal(comments + 1, h.GitHub.Comments.Count);
            Assert.Contains("Manual reconciliation", h.GitHub.Comments[^1], StringComparison.Ordinal);
            var report = Assert.Single(handler.Reports);
            Assert.Equal("Completed", report.State);
            Assert.Equal(request.OperationId, report.CompletionReconciliationOperationId);
            var completed = Assert.IsType<ExecutionHistoryEntry>(await history.ReadExecutionAsync(failed.ExecutionId));
            Assert.Equal("Completed", completed.State);
            Assert.Equal(ExecutionHistoryStore.ManuallyCompletedState, completed.RecoveryState);
            // A repeated request is refused instead of applying effects twice.
            Assert.Equal("refused", (await worker.CompleteManuallyAsync(command, _ => Task.FromResult(true), CancellationToken.None)).Outcome);
            Assert.Equal(1, h.GitHub.CloseCalls);
        }
        else
        {
            Assert.Equal("refused", result.Outcome);
            Assert.Empty(handler.Reports);
            Assert.Equal(0, h.GitHub.CloseCalls);
            Assert.Equal(comments, h.GitHub.Comments.Count);
            Assert.DoesNotContain("working->done", h.GitHub.Labels);
        }
        // Never reruns the engine, validation or integration.
        Assert.Single(h.Codex.Issues);
        Assert.Equal(validationCalls, h.Validation.Calls);
        Assert.Equal(integrationCalls, h.Git.Integrations);
    }
}
