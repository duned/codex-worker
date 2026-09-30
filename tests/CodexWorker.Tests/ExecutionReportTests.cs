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
        Assert.Contains("Validation failed after 2 repair attempt(s): `dotnet test`.", markdown);
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

    [Fact]
    public void FailedValidationReportIncludesCompilerAndTestDiagnosticsAndRecoveryMetadata()
    {
        var id = Guid.NewGuid();
        var report = new IssueExecutionReport("Implemented the feature.",
            [new("dotnet test", 1, 1, "Updated the failing test.", false,
                "error CS1002: ; expected", "Failed Example.Tests.ParserTests.ReadsHeader\nExpected: 1\nActual: 0")],
            FinalValidationFailure: "dotnet test", Failure: "Validation failed after 1 repair attempt(s).",
            ExecutionId: id, FinalValidationDiagnostics: "Failed Example.Tests.ParserTests.ReadsHeader\nExpected: 1\nActual: 0",
            FinalValidationExitCode: 1, RecoveryBranch: "feature/example-59", WorkspacePreserved: true, RetryAvailable: true,
            SecretValues: ["private-value"]);

        var markdown = report.ToMarkdown(IssueOutcomeKind.Failed);

        Assert.Contains("Final validation command: `dotnet test` (exit 1).", markdown);
        Assert.Contains("Failed Example.Tests.ParserTests.ReadsHeader", markdown);
        Assert.Contains("## Recovery", markdown);
        Assert.Contains($"- **Execution:** `[{ExecutionFormatting.ShortId(id)}]` (`{id}`)", markdown);
        Assert.Contains("- **Branch:** `feature/example-59`", markdown);
        Assert.Contains("- **Workspace:** Preserved", markdown);
        Assert.Contains("- **Retry/resume:** Available", markdown);
        Assert.DoesNotContain("/home/", markdown);

        var secretReport = report with { ValidationRepairs = [new("dotnet test", 1, 1, "Output private-value", false)] };
        Assert.DoesNotContain("private-value", secretReport.ToMarkdown(IssueOutcomeKind.Failed));
    }

    [Fact]
    public void IntegrationConflictReportSeparatesCompletedImplementationFromPreservedRecoveryState()
    {
        var id = Guid.NewGuid();
        var report = new IssueExecutionReport("Implementation and validation completed.", [],
            Failure: "Rebase conflict could not be resolved automatically.", FailureCategory: "Integration conflict",
            ExecutionId: id, RecoveryBranch: "feature/example-59", WorkspacePreserved: true,
            RetryAvailable: false, SecretValues: []);

        var markdown = report.ToMarkdown(IssueOutcomeKind.IntegrationConflict);

        Assert.Contains("## Implementation attempt", markdown);
        Assert.Contains("## Integration conflict recovery", markdown);
        Assert.Contains($"`{id}`", markdown);
        Assert.Contains("- **Workspace:** Preserved", markdown);
        Assert.Contains("- **Retry/resume:** Unavailable", markdown);
    }

    [Fact]
    public void ValidationDiagnosticSummarySelectsActionableLinesRedactsSecretsAndBoundsOutput()
    {
        var failure = new ValidationFailure(2, "dotnet test", 1,
            string.Join('\n', Enumerable.Repeat("noise output", 500)) + "\nFailed Example.Tests.ParserTests.ReadsHeader\nExpected: token=supersecret\nActual: hidden-value",
            "error CS1002: ; expected", false, ["hidden-value"]);

        var summary = failure.ToSummary();

        Assert.Contains("error CS1002: ; expected", summary);
        Assert.Contains("Failed Example.Tests.ParserTests.ReadsHeader", summary);
        Assert.Contains("[redacted]", summary);
        Assert.DoesNotContain("supersecret", summary);
        Assert.DoesNotContain("hidden-value", summary);
        Assert.True(summary.Length <= 800);
        Assert.DoesNotContain("noise output", summary);
    }

    [Fact]
    public void NoRepairFailureIncludesFinalDiagnosticAndSuccessfulReportHasNoRecoverySection()
    {
        var failed = new IssueExecutionReport("Implementation summary", [], FinalValidationFailure: "dotnet build",
            Failure: "Validation failed after 0 repair attempt(s).", FinalValidationDiagnostics: "error CS1002: ; expected",
            FinalValidationExitCode: 1, ExecutionId: Guid.NewGuid());
        var succeeded = new IssueExecutionReport("Implementation summary", [],
            Integration: new GitIntegrationResult(false, "No changes."), ExecutionId: Guid.NewGuid());

        var failedMarkdown = failed.ToMarkdown(IssueOutcomeKind.Failed);
        var succeededMarkdown = succeeded.ToMarkdown(IssueOutcomeKind.Succeeded);

        Assert.Contains("error CS1002: ; expected", failedMarkdown);
        Assert.Contains("exit 1", failedMarkdown);
        Assert.DoesNotContain("## Recovery", failedMarkdown);
        Assert.DoesNotContain("## Recovery", succeededMarkdown);
        Assert.Contains("## Validation\n\nValidation passed successfully.", succeededMarkdown);
    }

    [Fact]
    public void IncompleteCodexReportHasSeparatedMarkdownSectionsCorrelationAndRecoveryWithoutDuplicateText()
    {
        var id = Guid.Parse("8d80eeb9-c246-4348-b5cb-dc3710faf11b");
        const string summary = "Codex did not complete the requested implementation because the existing implementation still uses a shared long-lived token.";
        var report = new IssueExecutionReport(summary, [], Failure: summary,
            ExecutionId: id, RecoveryBranch: "feature/example-74", WorkspacePreserved: true,
            RetryAvailable: true, FailureCategory: "Codex reported incomplete task");

        var markdown = report.ToMarkdown(IssueOutcomeKind.Failed);

        Assert.Contains("## Execution\n\nExecution `[8d80eeb9]` (`8d80eeb9-c246-4348-b5cb-dc3710faf11b`).", markdown);
        Assert.Contains("## Implementation attempt\n\n" + summary, markdown);
        Assert.Contains("## Failure\n\n**Reason:** Codex reported incomplete task.", markdown);
        Assert.DoesNotContain("**Reason:** Codex reported incomplete task.\n\n" + summary, markdown);
        Assert.Contains("## Recovery\n\n- **Execution:** `[8d80eeb9]` (`8d80eeb9-c246-4348-b5cb-dc3710faf11b`)\n- **Branch:** `feature/example-74`\n- **Workspace:** Preserved\n- **Retry/resume:** Available", markdown);
        Assert.DoesNotContain("## ExecutionExecution", markdown);
        Assert.DoesNotContain("## Failure**Reason", markdown);
    }

    [Fact]
    public void FailureMarkdownIdentifiesValidationAndProcessFailureCategoriesAndRedactsSecrets()
    {
        var validation = new IssueExecutionReport("Implementation summary", [], FinalValidationFailure: "dotnet test",
            Failure: "Validation failed after 0 repair attempts.", ExecutionId: Guid.NewGuid());
        var process = new IssueExecutionReport("Implementation summary", [],
            Failure: "Codex process exited with code 7.", ExecutionId: Guid.NewGuid(),
            FailureCategory: "Codex process failure");
        var sensitive = process with { Failure = "Request failed token=secret-value", SecretValues = ["secret-value"] };

        Assert.Contains("**Reason:** Authoritative validation failed.", validation.ToMarkdown(IssueOutcomeKind.Failed));
        Assert.Contains("**Reason:** Codex process failure.", process.ToMarkdown(IssueOutcomeKind.Failed));
        Assert.DoesNotContain("secret-value", sensitive.ToMarkdown(IssueOutcomeKind.Failed));
        Assert.Contains("[redacted]", sensitive.ToMarkdown(IssueOutcomeKind.Failed));
    }
}
