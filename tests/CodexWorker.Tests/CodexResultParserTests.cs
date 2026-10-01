using CodexWorker;

namespace CodexWorker.Tests;

public sealed class CodexResultParserTests
{
    [Fact]
    public void ParsesSuccessStructuredResponse()
    {
        var result = CodexResultParser.Parse("""
            {"status":"success","summary":"Added the feature.","testsOrValidationPerformed":["Ran focused checks."],"needsHumanInput":false,"question":null,"blockerType":null}
            """);
        Assert.Equal("success", result.Status);
        Assert.Equal("Added the feature.", result.Summary);
        Assert.Equal("Ran focused checks.", Assert.Single(result.TestsOrValidationPerformed));
    }

    [Fact]
    public void RejectsInvalidStatusAndMissingSchemaFields()
    {
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"finished","summary":"ok","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}
            """));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => CodexResultParser.Parse("{"));
    }

    [Fact]
    public void EnforcesConsistentBlockedAndSuccessResults()
    {
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":" ","blockerType":"human_input"}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"success","summary":"Done.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"Need access","blockerType":null}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"success","summary":"Done.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":"Need access","blockerType":null}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"failed","summary":"Failed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"Need access","blockerType":null}
            """));
        var result = CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"Which API should be used?","blockerType":"human_input"}
            """);
        Assert.Equal("Which API should be used?", result.Question);
        Assert.Equal("human_input", result.BlockerType);
    }

    [Fact]
    public void ParsesExplicitExternalPrerequisiteBlockerWithoutHumanInput()
    {
        var result = CodexResultParser.Parse("""
            {"status":"blocked","summary":"Restore could not complete.","testsOrValidationPerformed":["dotnet restore could not reach api.nuget.org."],"needsHumanInput":false,"question":"The required NuGet feed api.nuget.org is unreachable.","blockerType":"external_prerequisite"}
            """);

        Assert.Equal("blocked", result.Status);
        Assert.False(result.NeedsHumanInput);
        Assert.Equal("external_prerequisite", result.BlockerType);
        Assert.Contains("NuGet feed", result.Question);
    }

    [Fact]
    public void RejectsAmbiguousExternalBlockerClassification()
    {
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":true,"question":"The required NuGet feed is unreachable.","blockerType":"external_prerequisite"}
            """));
        Assert.Throws<InvalidDataException>(() => CodexResultParser.Parse("""
            {"status":"blocked","summary":"Cannot proceed.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":"The required NuGet feed is unreachable.","blockerType":"unknown"}
            """));
    }

    [Fact]
    public void FailedResultCannotLeaveHumanInputUnresolved()
    {
        var result = CodexResultParser.Parse("""
            {"status":"failed","summary":"A technical error occurred.","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}
            """);
        Assert.Equal("failed", result.Status);
    }
}
