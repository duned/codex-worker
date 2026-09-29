namespace CodexWorker.Tests;

public sealed class ExecutionReportTests
{
    [Fact]
    public void FirstPassSuccessIncludesImplementationValidationDurationAndIntegration()
    {
        var report = new IssueExecutionReport("Added fiscal year closure behavior.", [],
            Integration: new GitIntegrationResult(true,
                "Committed as `abc123`. Merged into `dev`. Preserved on origin as `done/feature/123-example`."),
            Duration: TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(42));

        var markdown = report.ToMarkdown(IssueOutcomeKind.Succeeded);
        Assert.Contains("## Implementation\n\nAdded fiscal year closure behavior.", markdown);
        Assert.Contains("## Validation\n\nValidation passed successfully.", markdown);
        Assert.Contains("- Duration: 08:42", markdown);
        Assert.Contains("- Commit: `abc123`", markdown);
        Assert.Contains("- Merged into: `dev`", markdown);
        Assert.Contains("- Preserved as: `done/feature/123-example`", markdown);
    }

    [Fact]
    public void CompletionMarkdownRetainsCodexSummaryHeadingsForGitHubReporting()
    {
        const string summary = "## Implementation\n\nImplemented X.\n\n## Validation\n\nTests passed.";
        var report = new IssueExecutionReport(summary, []);

        var markdown = report.ToMarkdown(IssueOutcomeKind.Succeeded);

        Assert.Contains(summary, markdown);
        Assert.Contains("## Implementation", markdown);
        Assert.Contains("## Validation", markdown);
    }

    [Fact]
    public void OneRepairKeepsInitialAndRepairSummariesWithoutValidationLogs()
    {
        var report = new IssueExecutionReport("Implemented fiscal year closure snapshots.",
            [new("dotnet test", 1, 2, "Fixed the snapshot round-trip assertion.", true)],
            Integration: new GitIntegrationResult(true, "Committed as `abc123`. Merged into `dev`."));

        var markdown = report.ToMarkdown(IssueOutcomeKind.Succeeded);
        Assert.Contains("Implemented fiscal year closure snapshots.", markdown);
        Assert.Contains("### Repair 1/2", markdown);
        Assert.Contains("Fixed the snapshot round-trip assertion.", markdown);
        Assert.Contains("Initial validation failed: `dotnet test`.", markdown);
        Assert.Contains("Validation passed after repair 1/2.", markdown);
        Assert.DoesNotContain("stdout", markdown);
        Assert.DoesNotContain("stderr", markdown);
    }

    [Fact]
    public void MultipleRepairsRemainInExecutionOrderAndFinalFailedValidationIsReported()
    {
        var report = new IssueExecutionReport("Implemented the feature.",
            [new("dotnet test", 1, 2, "Corrected the mapping.", false),
             new("dotnet test", 2, 2, "Updated the schema test.", false)],
            FinalValidationFailure: "dotnet test", Failure: "Validation failed after 2 repair attempts.");

        var markdown = report.ToMarkdown(IssueOutcomeKind.Failed);
        Assert.True(markdown.IndexOf("Corrected the mapping.", StringComparison.Ordinal) <
                    markdown.IndexOf("Updated the schema test.", StringComparison.Ordinal));
        Assert.Contains("Validation failed after all repair attempts: `dotnet test`.", markdown);
        Assert.Contains("## Implementation attempt", markdown);
        Assert.DoesNotContain("stdout", markdown);
    }

    [Fact]
    public void BlockedReportRetainsWorkAndHumanQuestionWithoutEnvironmentValues()
    {
        var report = new IssueExecutionReport("Added a migration draft.", [], HumanInput: "Which API should own this behavior?");
        var markdown = report.ToMarkdown(IssueOutcomeKind.Blocked);

        Assert.Contains("## Work performed", markdown);
        Assert.Contains("Added a migration draft.", markdown);
        Assert.Contains("## Human input required", markdown);
        Assert.Contains("Which API should own this behavior?", markdown);
        Assert.DoesNotContain("TELEGRAM_BOT_TOKEN", markdown);
        Assert.DoesNotContain("GITHUB_TOKEN", markdown);
    }
}
