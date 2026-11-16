using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Theory]
    [InlineData(false, "auth-error")]
    [InlineData(false, "denied")]
    [InlineData(false, "malformed")]
    [InlineData(true, "auth-error")]
    [InlineData(true, "denied")]
    [InlineData(true, "malformed")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ClaudeCliFailurePreservesWorkspaceAndNeverFallsBack(bool managed, string mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var cli = new FakeClaudeCli(mode);
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, engine: cli.CreateEngine());
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\nengine: ccc" };
        async Task RunAsync()
        {
            if (!managed) { await h.ProcessOneAsync(); return; }
            var now = DateTimeOffset.UtcNow;
            var assignment = new WorkerAssignmentContract("assignment", "server-execution",
                new ServerProjectContract("project", "Test Project", "owner/repo", "main", "", [], 1, now, now),
                new ServerWorkReferenceContract("github-issue", "17") { Selection = new("ccc") },
                "worker-id", new Dictionary<string, string>(),
                new ServerExecutionLeaseContract("server-execution", "worker-id", 1, now, now.AddMinutes(5), "Active"));
            var execution = await h.Worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
            Assert.NotNull(execution);
            await execution;
        }
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(RunAsync);
        Assert.DoesNotContain("private-cli-diagnostic", failure.ToString());
        Assert.Equal(1, cli.Invocations);
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(0, h.Validation.Calls);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.True(File.Exists(Path.Combine(h.Git.ExecutionDirectory, "implemented.txt")));
        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("ccc", entry.Engine);
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.DoesNotContain("private-cli-diagnostic", System.Text.Json.JsonSerializer.Serialize(entry));
        Assert.DoesNotContain("private-cli-diagnostic", h.Output.ToString());
        Assert.All(h.GitHub.Comments, comment => Assert.DoesNotContain("private-cli-diagnostic", comment));
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
    }

    [Theory]
    [InlineData(false, IssueOutcomeKind.Succeeded, 2)]
    [InlineData(true, IssueOutcomeKind.Failed, 3)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ClaudeProcessUsesAuthoritativeValidationAndBoundedRepairs(bool exhausted, IssueOutcomeKind expected, int invocations)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var cli = new FakeClaudeCli();
        var engine = cli.CreateEngine();
        using var h = new Harness(engine: engine);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\nengine: ccc\nmodel: sonnet\neffort: low" };
        h.Validation.Results.Enqueue(Failure("check", 1, "repair required"));
        if (exhausted)
        {
            h.Validation.Results.Enqueue(Failure("check", 1, "repair required"));
            h.Validation.Results.Enqueue(Failure("check", 1, "repair required"));
        }
        else h.Validation.Results.Enqueue(ValidationResult.Success);
        Assert.Equal(expected, (await h.ProcessOneAsync())?.Kind);
        Assert.Equal(invocations, cli.Invocations);
        Assert.Equal(invocations, h.Validation.Calls);
        Assert.Empty(h.Codex.Issues);
        Assert.Empty(h.Codex.RepairAttempts);
        Assert.Equal(exhausted ? 0 : 1, h.Git.Integrations);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task InjectedEngineUsesWorkerValidationIntegrationAndHistory()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var engine = new ImplementationEngineTests.Engine { RepairStatus = "success" };
        using var h = new Harness(history: history, engine: engine);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\nengine: ccc\nmodel: sonnet\neffort: low" };
        h.Validation.Results.Enqueue(Failure("check", 1, "repair required"));
        h.Validation.Results.Enqueue(ValidationResult.Success);

        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
        Assert.Empty(h.Codex.Issues);
        Assert.Empty(h.Codex.RepairAttempts);
        Assert.Equal(2, engine.Requests.Count);
        Assert.All(engine.Requests, request =>
        {
            Assert.Equal(h.Git.ExecutionDirectory, request.ProjectDirectory);
            Assert.Equal("sonnet", request.Model);
            Assert.Equal("low", request.Effort);
        });
        Assert.Equal(2, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal("ccc", Assert.Single(await history.ReadAllAsync()).Engine);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("success", IssueOutcomeKind.Succeeded)]
    [InlineData("failed", IssueOutcomeKind.Failed)]
    public async Task QuotaLookupErrorsDoNotChangeExecutionOutcome(string status, IssueOutcomeKind expected)
    {
        using var h = new Harness();
        h.Codex.QuotaReader = new ThrowingQuotaReader();
        h.Codex.InitialOutcome = new(status, "task outcome", [], false, null);
        Assert.Equal(expected, (await h.ProcessOneAsync())?.Kind);
        Assert.Equal(2, h.OperationalMessages.Count(message => message.StartsWith("↳ Limits ·", StringComparison.Ordinal)));
        Assert.DoesNotContain("↳ Limits", h.Output.ToString());
        Assert.DoesNotContain(h.OperationalMessages, message => message.Contains("private quota error", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task QuotaObservationsDoNotSendExtraTelegramMessages()
    {
        using var h = new Harness(telegramEnabled: true);
        h.Codex.QuotaReader = new AvailableQuotaReader();
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())?.Kind);
        Assert.Single(h.TelegramMessages, message => message.Contains("TAREA COMPLETADA", StringComparison.Ordinal));
        Assert.Single(h.TelegramMessages, message => message.Contains("Codex account quota: 5h 80% left", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("↳ Limits", StringComparison.Ordinal));
        Assert.Equal(2, h.OperationalMessages.Count(message => message.StartsWith("↳ Limits ·", StringComparison.Ordinal)));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("success")]
    [InlineData("failed")]
    public async Task TerminalQuotaIsReadAfterRepairsIntegrationAndReporting(string status)
    {
        using var h = new Harness();
        h.Codex.InitialOutcome = new(status, "task outcome", [], false, null);
        if (status == "success")
        {
            h.Validation.Results.Enqueue(Failure("check", 1, "repair required"));
            h.Codex.Repairs.Enqueue(Success("repaired"));
            h.Validation.Results.Enqueue(ValidationResult.Success);
        }
        var reader = new LifecycleQuotaReader(() =>
        {
            if (status == "success")
            {
                Assert.True(h.Validation.Calls >= 2);
                Assert.Equal(1, Assert.Single(h.Codex.RepairAttempts));
                Assert.True(h.Git.Integrations > 0);
                Assert.Equal(1, h.GitHub.CloseCalls);
            }
            Assert.DoesNotContain(" · completed · ", h.Output.ToString());
        });
        h.Codex.QuotaReader = reader;
        var result = await h.ProcessOneAsync();
        Assert.NotNull(result);
        Assert.Equal(37d, Assert.Single(Assert.IsType<CodexQuotaObservation>(result.Report.QuotaAtEnd).Windows).RemainingPercent);
        Assert.Equal(1, reader.FinalReads);
        Assert.Equal(2, h.OperationalMessages.Count(message => message.StartsWith("↳ Limits ·", StringComparison.Ordinal)));
        Assert.Contains("5h 80% left", h.OperationalMessages.First(message => message.StartsWith("↳ Limits ·", StringComparison.Ordinal)));
        Assert.Contains("5h 37% left", h.OperationalMessages.Last(message => message.StartsWith("↳ Limits ·", StringComparison.Ordinal)));
    }

    private sealed class LifecycleQuotaReader(Action onFinal) : ICodexQuotaReader
    {
        public int FinalReads { get; private set; }
        public CodexQuotaObservation CachedObservation => new(DateTimeOffset.UtcNow, "available", [new(300, 80, null)]);
        public Task<CodexQuotaObservation> ReadAsync(CancellationToken ct) => Task.FromResult(CachedObservation);
        public Task<CodexQuotaObservation> ReadFinalAsync(CancellationToken ct)
        {
            FinalReads++;
            onFinal();
            return Task.FromResult(new CodexQuotaObservation(DateTimeOffset.UtcNow, "available", [new(300, 37, null)]));
        }
    }

    private sealed class AvailableQuotaReader : ICodexQuotaReader
    {
        public Task<CodexQuotaObservation> ReadAsync(CancellationToken ct) =>
            Task.FromResult(new CodexQuotaObservation(DateTimeOffset.UtcNow, "available", [new(300, 80, null)]));
    }

    private sealed class ThrowingQuotaReader : ICodexQuotaReader
    {
        public Task<CodexQuotaObservation> ReadAsync(CancellationToken ct) =>
            Task.FromException<CodexQuotaObservation>(new IOException("private quota error"));
    }
}
