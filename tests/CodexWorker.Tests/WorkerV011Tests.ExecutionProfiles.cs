using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("restart")]
    [InlineData("resume")]
    public async Task RetryPreservesRecordedProfileDespiteIssueAndProjectEdits(string mode)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\nmodel: task-model\neffort: low" };
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "Workspace retained.");
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial work", [], false, null);
        Assert.Equal(IssueOutcomeKind.Failed, (await h.ProcessOneAsync())!.Kind);
        h.Worker.Configuration.Worker.RetryMode = mode;
        h.Worker.Configuration.Codex.Model = "new-project-model";
        h.Worker.Configuration.Codex.ReasoningEffort = "xhigh";
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\nmodel: edited-model\neffort: high" };
        h.GitHub.ReadyIssueCount = 2;
        h.Codex.InitialOutcome = Success("Completed");
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);
        Assert.Equal(2, h.Codex.Profiles.Count);
        Assert.All(h.Codex.Profiles, profile => Assert.Equal(new CodexExecutionProfile("task-model", "low"), profile));
        Assert.All(await history.ReadAllAsync(), entry =>
        {
            Assert.Equal("task-model", entry.EffectiveModel);
            Assert.Equal("low", entry.EffectiveEffort);
        });
        Assert.Contains(h.TelegramMessages, message => message.Contains("Codex · task-model · low", StringComparison.Ordinal));
        Assert.Contains("Codex · model task-model · effort low", h.Output.ToString());
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Codex model: `task-model` · effort: `low`", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("success", IssueOutcomeKind.Succeeded, "account-model")]
    [InlineData("failed", IssueOutcomeKind.Failed, "account-model")]
    [InlineData("success", IssueOutcomeKind.Succeeded, null)]
    public async Task CliModelIsDurableAndUsedInFinalReports(string status, IssueOutcomeKind kind, string? model)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Codex.CliModel = model;
        h.Codex.InitialOutcome = new CodexOutcome(status, "Implementation result", [], false, null);

        Assert.Equal(kind, (await h.ProcessOneAsync())!.Kind);

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(model, entry.EffectiveModel);
        Assert.True(entry.ModelSelectedByCli);
        Assert.Equal("medium", entry.EffectiveEffort);
        Assert.Null(Assert.Single(h.Codex.Profiles).Model);
        Assert.Contains($"Codex · model {model ?? "unknown (CLI model unavailable)"} · effort medium", h.Output.ToString());
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains($"Codex model: `{model ?? "unknown (CLI model unavailable)"}` · effort: `medium`", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("restart")]
    [InlineData("resume")]
    public async Task CliRetryRecordsItsOwnModelWithoutTurningSourceModelIntoOverride(string mode)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Git.Recovery = new GitRecoveryInfo("feature/example-task-17", "base-sha", "Workspace retained.");
        h.Codex.CliModel = "first-model";
        h.Codex.InitialOutcome = new CodexOutcome("failed", "Partial work", [], false, null);
        Assert.Equal(IssueOutcomeKind.Failed, (await h.ProcessOneAsync())!.Kind);
        h.Worker.Configuration.Worker.RetryMode = mode;
        h.Worker.Configuration.Codex.Model = "edited-project-model";
        h.Codex.CliModel = "second-model";
        h.Codex.InitialOutcome = Success("Completed");
        h.GitHub.ReadyIssueCount = 2;

        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);

        Assert.All(h.Codex.Profiles, profile => Assert.Null(profile.Model));
        var entries = await history.ReadAllAsync();
        Assert.Equal("first-model", Assert.Single(entries, entry => entry.AttemptNumber == 1).EffectiveModel);
        Assert.Equal("second-model", Assert.Single(entries, entry => entry.AttemptNumber == 2).EffectiveModel);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Codex model: `second-model`", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("You've hit your usage limit. Try again in 30 minutes. token=private-value")]
    [InlineData("Rate limit reached. Retry after 20 seconds. token=private-value")]
    [InlineData("context_length_exceeded token=private-value")]
    [InlineData("invalid_api_key token=private-value")]
    [InlineData("server_error token=private-value")]
    [InlineData("unknown CLI failure token=private-value")]
    public async Task CodexFailureSummaryIsSharedByHistoryGitHubAndTelegram(string evidence)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, telegramEnabled: true);
        var failure = CodexFailure.Classify(CodexFailure.ProcessEvidence("", "error: " + evidence));
        h.Codex.InitialException = new CodexExecutionInfrastructureException(failure.Category, failure.Summary)
            { Recoverable = failure.Recoverable };

        await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => h.ProcessOneAsync());

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.Contains(failure.Summary, entry.FailureReason);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains(failure.Summary, StringComparison.Ordinal));
        Assert.Contains(h.TelegramMessages, message => message.Contains(failure.Summary, StringComparison.Ordinal));
        Assert.DoesNotContain("private-value", entry.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain(h.GitHub.Comments, comment => comment.Contains("private-value", StringComparison.Ordinal));
        Assert.DoesNotContain(h.TelegramMessages, message => message.Contains("private-value", StringComparison.Ordinal));
        Assert.DoesNotContain("working->failed", h.GitHub.Labels);
        Assert.Equal(0, h.Git.Cleanups);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CodexInfrastructureFailureRetainsObservedModelInHistoryAndInterruptionReport()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.Codex.CliModel = "account-model";
        h.Codex.InitialException = new CodexExecutionInfrastructureException("Codex process failure", "CLI failed after startup");

        await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => h.ProcessOneAsync());

        var entry = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", entry.State);
        Assert.Equal("account-model", entry.EffectiveModel);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Codex model: `account-model` · effort: `medium`", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ImplementationAndValidationRepairUseTheSameProfile()
    {
        using var h = new Harness();
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: high" };
        h.Worker.Configuration.Codex.Model = "project-model";
        h.Validation.Results.Enqueue(new ValidationResult(new ValidationFailure(1, "check", 1, "failed", "", false)));
        h.Codex.Repairs.Enqueue(Success("Repaired"));
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);
        Assert.Single(h.Codex.RepairAttempts);
        Assert.All(h.Codex.Profiles, profile => Assert.Equal(new CodexExecutionProfile("project-model", "high"), profile));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task InvalidMetadataBlocksBeforePreparingGitAndCanBeCorrected()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: invalid" };
        Assert.Equal(IssueOutcomeKind.Blocked, (await h.ProcessOneAsync())!.Kind);
        Assert.Equal(0, h.Git.Started);
        Assert.Empty(h.Codex.Profiles);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("effort must be low, medium, high, or xhigh", StringComparison.Ordinal));
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Codex\neffort: low" };
        h.GitHub.ReadyIssueCount = 2;
        Assert.Equal(IssueOutcomeKind.Succeeded, (await h.ProcessOneAsync())!.Kind);
        Assert.Equal("low", Assert.Single(h.Codex.Profiles).Effort);
    }
}
