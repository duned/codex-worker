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
    public void EnforcesConsistentBlockedAndSuccessResults()
    {
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":" "}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"success","summary":"Done.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"Need access"}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"success","summary":"Done.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":"Need access"}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"failed","summary":"Failed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"Need access"}
            """));
        var result = CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"Which API should be used?"}
            """);
        Assert.Equal("Which API should be used?", result.Question);
    }

    [Fact]
    public void FailedResultCannotLeaveHumanInputUnresolved()
    {
        var result = CodexResultParser.Parse("""
            {"status":"failed","summary":"A technical error occurred.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null}
            """);
        Assert.Equal("failed", result.Status);
    }
}
