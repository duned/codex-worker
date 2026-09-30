using CodexWorker;

namespace CodexWorker.Tests;

public sealed class SchedulerCapacityLogTests
{
    [Fact]
    public void CloselySpacedClaimsAndCompletionsReportEveryCapacityTransition()
    {
        var messages = new List<string>();
        var log = new SchedulerCapacityLog(2, messages.Add);
        static (string Name, int Active, int Limit)[] State(int active) => [("Example", active, 2)];

        log.Report(0, State(0));
        log.Report(0, State(0));
        log.Report(1, State(1));
        log.Report(2, State(2));
        log.Report(1, State(1));
        log.Report(0, State(0));

        Assert.Equal(new[]
        {
            "Scheduler · global 0/2",
            "Scheduler · global 0/2 · Example 0/2",
            "Scheduler · global 1/2",
            "Scheduler · global 1/2 · Example 1/2",
            "Scheduler · global 2/2",
            "Scheduler · global 2/2 · Example 2/2",
            "Scheduler · global 1/2",
            "Scheduler · global 1/2 · Example 1/2",
            "Scheduler · global 0/2",
            "Scheduler · global 0/2 · Example 0/2"
        }, messages);
    }

    [Fact]
    public void ProjectCapacityChangeIsReportedAndReconfigurationDropsRemovedProjectState()
    {
        var messages = new List<string>();
        var log = new SchedulerCapacityLog(2, messages.Add);
        log.Report(0, [("Removed", 0, 1)]);
        log.Reconfigure(2);
        log.Report(0, [("Current", 0, 2)]);
        log.Report(1, [("Current", 1, 2)]);

        Assert.Contains("Scheduler · global 0/2 · Removed 0/1", messages);
        Assert.Contains("Scheduler · global 0/2 · Current 0/2", messages);
        Assert.Contains("Scheduler · global 1/2 · Current 1/2", messages);
        var currentProjectReported = messages.IndexOf("Scheduler · global 0/2 · Current 0/2");
        Assert.DoesNotContain(messages.Skip(currentProjectReported + 1),
            message => message.Contains("Removed", StringComparison.Ordinal));
    }

    [Fact]
    public void IdleHeartbeatIsConciseAndRepeatsOnlyEveryFifteenMinutes()
    {
        var messages = new List<string>();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var heartbeat = new IdleWorkerHeartbeat(start);

        heartbeat.EmitIfDue(start.AddMinutes(14), messages.Add);
        heartbeat.EmitIfDue(start.AddMinutes(15), messages.Add);
        heartbeat.EmitIfDue(start.AddMinutes(29), messages.Add);
        heartbeat.EmitIfDue(start.AddMinutes(30), messages.Add);

        Assert.Equal(new[] { "Worker heartbeat · idle", "Worker heartbeat · idle" }, messages);
    }

    [Fact]
    public void ResumeAndCompletionLogsUseOnlyAttemptLineageContext()
    {
        var writer = new StringWriter();
        var output = new WorkerConsole(writer, interactive: false);
        var issue = new GitHubIssue(74, "Add Worker registration", "", DateTimeOffset.UtcNow);
        var firstId = Guid.Parse("8d80eeb9-0000-0000-0000-000000000000");
        var second = WorkerExecution.Create(new ProjectSettings { Name = "Example", Repository = "owner/repo" },
            new GitSettings { BaseBranch = "main" }, issue, retryOfExecutionId: firstId, attemptNumber: 2, resumed: true);
        output.IssueStarted("Example", issue, second);
        output.IssueCompleted(issue, TimeSpan.FromSeconds(12), second.ExecutionId, second.AttemptNumber, second.RetryOfExecutionId);

        var text = writer.ToString();
        Assert.Contains($"↳ Attempt 2 · resume from [{firstId.ToString("N")[..8]}]", text);
        Assert.Contains($"(Attempt 2 · from [{firstId.ToString("N")[..8]}])", text);
        Assert.DoesNotContain($"execution {second.ExecutionId.ToString("N")[..8]}", text);
        Assert.DoesNotContain("Retry 2", text);
        Assert.DoesNotContain("(Issue ·", text);
    }

    [Fact]
    public void FirstAttemptCompletionHasNoLineageSuffixAndLaterAttemptUsesImmediatePredecessor()
    {
        var writer = new StringWriter();
        var output = new WorkerConsole(writer, interactive: false);
        var issue = new GitHubIssue(75, "Update plan", "", DateTimeOffset.UtcNow);
        var first = WorkerExecution.Create(new ProjectSettings { Name = "Example", Repository = "owner/repo" },
            new GitSettings { BaseBranch = "main" }, issue);
        output.IssueCompleted(issue, TimeSpan.FromSeconds(1), first.ExecutionId, first.AttemptNumber, first.RetryOfExecutionId);
        var second = WorkerExecution.Create(new ProjectSettings { Name = "Example", Repository = "owner/repo" },
            new GitSettings { BaseBranch = "main" }, issue, retryOfExecutionId: first.ExecutionId, attemptNumber: 2, resumed: true);
        var third = WorkerExecution.Create(new ProjectSettings { Name = "Example", Repository = "owner/repo" },
            new GitSettings { BaseBranch = "main" }, issue, retryOfExecutionId: second.ExecutionId, attemptNumber: 3, resumed: true);
        output.IssueStarted("Example", issue, third);
        output.IssueCompleted(issue, TimeSpan.FromSeconds(1), third.ExecutionId, third.AttemptNumber, third.RetryOfExecutionId);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("(", lines[0]);
        Assert.Contains($"↳ Attempt 3 · resume from [{second.ExecutionId.ToString("N")[..8]}]", lines[2]);
        Assert.Contains($"(Attempt 3 · from [{second.ExecutionId.ToString("N")[..8]}])", lines[3]);
    }
}
