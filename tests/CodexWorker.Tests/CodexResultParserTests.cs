using CodexWorker;

namespace CodexWorker.Tests;

public sealed class CodexResultParserTests
{
    [Fact]
    public void ParsesSuccessStructuredResponse()
    {
        var result = CodexResultParser.Parse("""
            {"status":"success","summary":"Added the feature.","testsOrValidationPerformed":["Ran focused checks."],"needsHumanInput":false,"question":null}
            """);
        Assert.Equal("success", result.Status);
        Assert.Equal("Added the feature.", result.Summary);
        Assert.Equal("Ran focused checks.", Assert.Single(result.TestsOrValidationPerformed));
    }

    [Fact]
    public void RejectsInvalidStatusAndMissingSchemaFields()
    {
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"finished","summary":"ok","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null}
            """));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => CodexResultParser.Parse("{"));
    }

    [Fact]
    public void BlockedResultMustProvideHumanInputOrAQuestion()
    {
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null}
            """));
        var result = CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"Which API should be used?"}
            """);
        Assert.Equal("Which API should be used?", result.Question);
    }
}
