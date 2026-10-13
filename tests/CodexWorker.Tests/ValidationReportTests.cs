using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ValidationReportTests
{
    private static string Output(int cases = 3, bool succeeded = true) => ValidationReport.OutputPrefix +
        JsonSerializer.Serialize(new ValidationReport("focused", "reviewed coverage", "solution.sln",
            [new ValidationRun("test", "tests.csproj", "TestType=Unit", cases, 1.25, succeeded)]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ReportsScopeFiltersCountsAndDurationsWithoutRawOutput()
    {
        var report = Assert.IsType<ValidationReport>(ValidationReport.Parse("raw output\n" + Output(), true));
        Assert.Equal("solution.sln", report.BuildScope);
        Assert.Equal("reviewed coverage", report.Reason);
        var run = Assert.Single(report.Runs);
        Assert.Equal("TestType=Unit", run.Filter);
        Assert.Equal(3, run.Cases);
        Assert.Equal(1.25, run.DurationSeconds);
        Assert.Null(ValidationReport.Parse("ordinary configured command", true));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    [InlineData(-1, true)]
    public void SuccessfulCommandCannotReportEmptyOrFailedTests(int cases, bool succeeded) =>
        Assert.Throws<InvalidDataException>(() => ValidationReport.Parse(Output(cases, succeeded), true));

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void FailedCommandRetainsPartialMeasurementsAndRedactsSecrets()
    {
        var report = Assert.IsType<ValidationReport>(ValidationReport.Parse(Output(0, false), false, ["reviewed"]));
        Assert.DoesNotContain("reviewed", report.Reason);
        Assert.False(Assert.Single(report.Runs).Succeeded);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void DuplicateOrMalformedMetadataIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ValidationReport.Parse(Output() + "\n" + Output(), true));
        Assert.Throws<JsonException>(() => ValidationReport.Parse(ValidationReport.OutputPrefix + "invalid", true));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ConfiguredCommandMetadataReachesValidationResultAndInvalidSelectionFails()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new ValidationRunner(new ProcessRunner(), 10);
        var result = await runner.RunAsync(["printf '%s\\n' '" + Output() + "'"], Path.GetTempPath(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ValidationReport>>(result.Reports));
        result = await runner.RunAsync(["printf '%s\\n' '" + Output(0) + "'"], Path.GetTempPath(), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("empty test selection", Assert.IsType<ValidationFailure>(result.Failure).StandardError);
    }
}
