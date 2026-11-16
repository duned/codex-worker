using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task InitialValidationSuccessDoesNotInvokeRepair()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Empty(h.Codex.RepairAttempts);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal("preflight", h.Events[0]);
        Assert.Equal("find", h.Events[1]);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("## " + IssueFormatting.Display(h.GitHub.Issue), StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Implementation", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Validation\n\nValidation passed successfully.", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task SuccessfulIssuePersistsWorkerLifecycleAndIntegrationFacts()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using (var h = new Harness(history: history))
        {
            h.Validation.Results.Enqueue(Failure("authoritative-check", 1, "failed once"));
            h.Validation.Results.Enqueue(ValidationResult.Success);
            h.Codex.Repairs.Enqueue(Success("fixed check"));
            await h.RunAsync();
        }

        using var reopened = new ExecutionHistoryStore(database);
        var execution = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("Completed", execution.State);
        Assert.Equal("implemented", execution.ImplementationSummary);
        Assert.Equal("passed", execution.ValidationOutcome);
        Assert.Equal(1, execution.RepairCount);
        Assert.True(Assert.Single(execution.Repairs).PassedAfterRepair);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", execution.CommitSha);
        Assert.Equal("main", execution.IntegrationBranch);
        Assert.Equal("completed/17", execution.CompletedBranch);
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task CodexAndValidationUseTheExecutionWorktreeDirectory()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(ValidationResult.Success);

        await h.RunAsync();

        Assert.Equal(h.Git.ExecutionDirectory, h.Codex.InitialDirectory);
        Assert.Equal(h.Git.ExecutionDirectory, h.Validation.LastDirectory);
        Assert.NotEqual(h.Worker.Configuration.Project.Directory, h.Codex.InitialDirectory);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task InitialValidationFailureThenSuccessfulRepairIsRevalidatedAndIntegrated()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("dotnet test", 1, "restore error"));
        h.Validation.Results.Enqueue(ValidationResult.Success);
        h.Codex.Repairs.Enqueue(Success("fixed restore issue"));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(2, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.Contains("working->done", h.GitHub.Labels);
        var comment = Assert.Single(h.GitHub.Comments);
        Assert.StartsWith("## Example task #17\n\n", comment);
        Assert.Contains("implemented", comment);
        Assert.Contains("fixed restore issue", comment);
        Assert.Contains("Initial validation failed: `dotnet test`.", comment);
        Assert.Contains("Validation passed after repair 1/2.", comment);
        Assert.DoesNotContain("stdout", comment);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task SupportsMultipleBoundedRepairAttempts()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("check 1", 1, "first"));
        h.Validation.Results.Enqueue(Failure("check 1", 1, "second"));
        h.Validation.Results.Enqueue(ValidationResult.Success);
        h.Codex.Repairs.Enqueue(Success("repair one"));
        h.Codex.Repairs.Enqueue(Success("repair two"));

        await h.RunAsync();

        Assert.Equal(new[] { 1, 2 }, h.Codex.RepairAttempts);
        Assert.Equal(3, h.Validation.Calls);
        Assert.Equal(1, h.Git.Integrations);
        Assert.Equal(0, h.Git.Cleanups);
        var comment = Assert.Single(h.GitHub.Comments);
        Assert.True(comment.IndexOf("repair one", StringComparison.Ordinal) < comment.IndexOf("repair two", StringComparison.Ordinal));
        Assert.Contains("implemented", comment);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ExhaustedRepairAttemptsFailTaskWithoutIntegratingEvenWhenRepairsReportSuccess()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "initial failure"));
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "repair one still fails"));
        h.Validation.Results.Enqueue(Failure("authoritative check", 7, "repair two still fails"));
        h.Codex.Repairs.Enqueue(Success("repair one says ready"));
        h.Codex.Repairs.Enqueue(Success("repair two says ready"));

        await h.RunAsync();

        Assert.Equal(new[] { 1, 2 }, h.Codex.RepairAttempts);
        Assert.Equal(3, h.Validation.Calls);
        var diagnostics = h.ErrorOutput.ToString();
        Assert.Equal(3, diagnostics.Split("Validation diagnostic", StringSplitOptions.None).Length - 1);
        Assert.Contains("repair attempt 0", diagnostics);
        Assert.Contains("repair attempt 1", diagnostics);
        Assert.Contains("repair attempt 2", diagnostics);
        Assert.Contains("exit 7", diagnostics);
        Assert.Contains("initial failure", diagnostics);
        Assert.Contains("repair two still fails", diagnostics);
        Assert.Equal(0, h.Git.Integrations);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("authoritative check", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Implementation attempt", StringComparison.Ordinal));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task BlockedRepairStopsLoopAndUsesBlockedWorkflow()
    {
        var database = Path.Combine(Path.GetTempPath(), $"codex-worker-history-{Guid.NewGuid():N}.db");
        using var history = new ExecutionHistoryStore(database);
        using var h = new Harness(history: history);
        h.Validation.Results.Enqueue(Failure("check", 1, "failure"));
        h.Codex.Repairs.Enqueue(new CodexOutcome("blocked", "Need a decision", [], true, "Which API?"));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(1, h.Validation.Calls);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->blocked", h.GitHub.Labels);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("Which API?", StringComparison.Ordinal));
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("### Work performed", StringComparison.Ordinal));
        var execution = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Blocked", execution.State);
        Assert.Equal(1, execution.RepairCount);
        Assert.Contains("Which API?", execution.FailureReason);
        File.Delete(database);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task FailedRepairStopsLoopAndUsesFailedWorkflow()
    {
        using var h = new Harness();
        h.Validation.Results.Enqueue(Failure("check", 1, "failure"));
        h.Codex.Repairs.Enqueue(new CodexOutcome("failed", "Could not repair", [], false, null));

        await h.RunAsync();

        Assert.Equal(new[] { 1 }, h.Codex.RepairAttempts);
        Assert.Equal(1, h.Git.Cleanups);
        Assert.Contains("working->failed", h.GitHub.Labels);
    }
}
