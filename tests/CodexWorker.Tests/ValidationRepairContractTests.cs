using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ValidationRepairContractTests
{
    [Fact]
    public void IntegrationRepairPromptIncludesCombinedSourceContextAndCompleteRedactedDiagnostics()
    {
        var failure = new ValidationFailure(2, "dotnet test", 1,
            "first actionable error\n" + new string('a', 7000) + "\nExpected: Installed\nActual: Missing\nprivate-value",
            "stderr detail", false, ["private-value"]);
        var context = new IntegrationRepairContext(failure, "main", "original-base", "implementation-tip", "new-base", "rebased-tip");
        var prompt = CodexExecutor.BuildIntegrationRepairPrompt("Project instructions", "AGENTS.md",
            new GitHubIssue(42, "Original title", "Original goal", DateTimeOffset.UnixEpoch), context,
            "Implemented the provider", ["dotnet build", "dotnet test"],
            new CodexSettings { Model = "effective-model", ReasoningEffort = "high" }, 1, 2);

        Assert.Contains("Integration repair 1/2", prompt);
        Assert.Contains("Initial authoritative validation passed before integration", prompt);
        Assert.Contains("CURRENT rebased source", prompt);
        Assert.Contains("integrated architecture", prompt);
        Assert.Contains("genuinely", prompt);
        Assert.Contains("Do not weaken validation, skip tests, or merely make tests green", prompt);
        Assert.Contains("original-base", prompt);
        Assert.Contains("implementation-tip", prompt);
        Assert.Contains("new-base", prompt);
        Assert.Contains("rebased-tip", prompt);
        Assert.Contains("Original goal", prompt);
        Assert.Contains("Implemented the provider", prompt);
        Assert.Contains("effective-model", prompt);
        Assert.Contains("reasoning effort: high", prompt);
        Assert.Contains("dotnet build\ndotnet test", prompt.Replace("\r", ""));
        Assert.Contains("first actionable error", prompt);
        Assert.Contains("Expected: Installed", prompt);
        Assert.Contains("stderr detail", prompt);
        Assert.DoesNotContain("private-value", prompt);
    }

    [Fact]
    public void RepairPromptIncludesIssueInstructionsFailureAndAttemptContext()
    {
        var issue = new GitHubIssue(42, "Original title", "Original body", DateTimeOffset.UnixEpoch);
        var failure = new ValidationFailure(2, "dotnet test", 1, "stdout detail", "stderr detail", false);

        var prompt = CodexExecutor.BuildRepairPrompt("Project instructions", "AGENTS.md", issue, failure, 1, 2);

        Assert.Contains("repair attempt 1 of 2", prompt);
        Assert.Contains("Original title", prompt);
        Assert.Contains("Original body", prompt);
        Assert.Contains("Project instructions", prompt);
        Assert.Contains("Command 2: dotnet test", prompt);
        Assert.Contains("Exit code: 1", prompt);
        Assert.Contains("stdout detail", prompt);
        Assert.Contains("stderr detail", prompt);
        Assert.Contains("worker's configured validation commands remain authoritative", prompt);
        Assert.Contains("do not run GitHub CLI commands", prompt);
    }

    [Fact]
    public void RepairDiagnosticsStayBoundedAndRetainBothOutputTailsAndFailureHeader()
    {
        var failure = new ValidationFailure(1, "verify", 9,
            new string('a', 2000) + "stdout-tail-marker",
            new string('b', 2000) + "stderr-tail-marker", false);

        var diagnostics = failure.ToRepairDiagnostics(500);

        Assert.True(diagnostics.Length <= 500);
        Assert.Contains("Command 1: verify", diagnostics);
        Assert.Contains("Exit code: 9", diagnostics);
        Assert.Contains("stdout-tail-marker", diagnostics);
        Assert.Contains("stderr-tail-marker", diagnostics);
        Assert.Contains("truncated", diagnostics);
    }
}
