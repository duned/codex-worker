using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class CodexTaskContractTests
{
    [Fact]
    public void PromptMakesLocalSelfValidationBestEffortAndWorkerValidationAuthoritative()
    {
        var issue = new GitHubIssue(8, "Add a feature", "Implement the requested behavior.", DateTimeOffset.UtcNow);

        var prompt = CodexExecutor.BuildPrompt("Project rules", "AGENTS.md", issue);

        Assert.Contains("Local self-validation is best effort", prompt);
        Assert.Contains("do not treat that alone as implementation failure", prompt);
        Assert.Contains("return `success`", prompt);
        Assert.Contains("identify the check and reason it could not run", prompt);
        Assert.Contains("authoritative validation gate", prompt);
        Assert.Contains("Use `failed` only when you could not complete the implementation", prompt);
        Assert.Contains("external prerequisite", prompt);
        Assert.Contains("Do not treat optional self-validation restrictions", prompt);
    }

    [Fact]
    public void OutputSchemaExplainsOptionalValidationAndActualFailureSemantics()
    {
        using var schema = JsonDocument.Parse(CodexExecutor.OutputSchema);
        var properties = schema.RootElement.GetProperty("properties");
        var statusDescription = properties.GetProperty("status").GetProperty("description").GetString();
        var checksDescription = properties.GetProperty("testsOrValidationPerformed").GetProperty("description").GetString();

        Assert.Contains("optional local checks could not run", statusDescription);
        Assert.Contains("implementation itself cannot be completed", statusDescription);
        Assert.Contains("configured validation commands are authoritative", checksDescription);
        Assert.Contains("include the command/check and the reason", checksDescription);
    }

    [Fact]
    public void SuccessCanReportOptionalCheckUnavailable()
    {
        var result = CodexResultParser.Parse("""
            {"status":"success","summary":"Implemented the change.","testsOrValidationPerformed":["Could not run dotnet test: package restore to api.nuget.org was blocked in the sandbox."],"needsHumanInput":false,"question":null,"blockerType":null}
            """);

        Assert.Equal("success", result.Status);
        Assert.Contains("package restore", Assert.Single(result.TestsOrValidationPerformed));
    }
}
