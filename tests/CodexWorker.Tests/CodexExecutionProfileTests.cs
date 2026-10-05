namespace CodexWorker.Tests;

public sealed class CodexExecutionProfileTests
{
    [Theory]
    [InlineData("", "project-model", "high")]
    [InlineData("## Codex\nmodel: task-model", "task-model", "high")]
    [InlineData("## Codex\neffort: low", "project-model", "low")]
    [InlineData("## Codex\nmodel: task-model\neffort: medium", "task-model", "medium")]
    [InlineData("## Codex\n\neffort: XHIGH\n## Tests\nUnrelated prose", "project-model", "xhigh")]
    public void EachOverrideFallsBackIndependently(string body, string model, string effort)
    {
        var defaults = new CodexSettings { Model = "project-model", ReasoningEffort = "high" };
        Assert.Equal(new CodexExecutionProfile(model, effort), CodexExecutionProfile.Resolve(body, defaults));
        Assert.Equal("project-model", defaults.Model);
        Assert.Equal("high", defaults.ReasoningEffort);
    }

    [Fact]
    public void UnconfiguredProjectRetainsCliModelAndWorkerEffortDefaults()
    {
        Assert.Equal(new CodexExecutionProfile(null, "medium"), CodexExecutionProfile.Resolve("Task", new CodexSettings()));
        Assert.Equal(new CodexExecutionProfile("task-model", "medium"),
            CodexExecutionProfile.Resolve("## Codex\nmodel: task-model", new CodexSettings()));
        Assert.Equal(new CodexExecutionProfile(null, "low"),
            CodexExecutionProfile.Resolve("## Codex\neffort: low", new CodexSettings()));
    }

    [Theory]
    [InlineData("## Codex\neffort: turbo")]
    [InlineData("## Codex\nmodel:")]
    [InlineData("## Codex\nmodel: model with spaces")]
    [InlineData("## Codex\nmodel: model\neffort: low\neffort: high")]
    [InlineData("## Codex\nmodel: first\nmodel: second")]
    [InlineData("## Codex\nunknown: value")]
    [InlineData("## Codex\nPlease use a cheap model")]
    [InlineData("## Codex\n```yaml\neffort: low\n```")]
    [InlineData("## Codex\neffort: low\n## Codex\nmodel: model")]
    [InlineData("## Codex\nmodel: bad\"value")]
    public void InvalidMetadataHasActionableDiagnostics(string body)
    {
        var error = Assert.Throws<InvalidDataException>(() => CodexExecutionProfile.Resolve(body, new CodexSettings()));
        Assert.StartsWith("Invalid ## Codex metadata:", error.Message);
    }

    [Fact]
    public void UnrelatedProseHeadingsAndFencedExamplesAreIgnored()
    {
        var body = """
            Use effort: high in this sentence.
            ## Implementation
            model: unrelated
            ### Codex
            effort: invalid
            ```md
            ## Codex
            effort: invalid
            ```
            ## Codex examples
            effort: invalid
            ## Codex
            effort: low
            ## Acceptance
            model: unrelated
            """;
        Assert.Equal(new CodexExecutionProfile(null, "low"), CodexExecutionProfile.Resolve(body, new CodexSettings()));
    }

    [Fact]
    public void RecordedCliDefaultDoesNotBecomeAnExplicitModelOnRetry()
    {
        var source = Entry() with { EffectiveModel = null, EffectiveEffort = "low" };
        Assert.Equal(new CodexExecutionProfile(null, "low"), CodexExecutionProfile.Resolve(
            "## Codex\nmodel: edited", new CodexSettings { Model = "new-default" }, source));
    }

    [Fact]
    public void ResolvedCliModelIsReportedOnRetryWithoutForcingSelection()
    {
        var source = Entry() with { EffectiveModel = "cli-model", EffectiveEffort = "low", ModelSelectedByCli = true };
        var profile = CodexExecutionProfile.Resolve("## Codex\nmodel: edited", new CodexSettings { Model = "project" }, source);
        Assert.Null(profile.Model);
        Assert.Equal("cli-model", profile.EffectiveModel);
        Assert.Equal("low", profile.Effort);
        Assert.DoesNotContain("--model", CodexExecutor.BuildArguments(new CodexSettings { Model = profile.Model }, "schema", "output", "prompt"));
    }

    [Fact]
    public void LegacyRecoveryUsesProjectDefaultsWithoutApplyingIssueEdits()
    {
        Assert.Equal(new CodexExecutionProfile("project", "medium"), CodexExecutionProfile.Resolve(
            "## Codex\neffort: invalid", new CodexSettings { Model = "project" }, Entry()));
    }

    [Fact]
    public void LongerFencesAndIndentedCodeDoNotSelectSettings()
    {
        var body = "````md\n```\n## Codex\neffort: invalid\n```\n````\n    ## Codex\n    effort: invalid\n## Task\nWork";
        Assert.Equal(new CodexExecutionProfile(null, "medium"), CodexExecutionProfile.Resolve(body, new CodexSettings()));
    }

    [Fact]
    public void BlankConfiguredModelRetainsCliSelection()
    {
        Assert.Null(CodexExecutionProfile.Resolve("Task", new CodexSettings { Model = " " }).Model);
    }

    private static ExecutionHistoryEntry Entry() => new(Guid.NewGuid(), "project", "owner/repo", 1, "title",
        "feature/task", "main", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Failed", 0, null,
        null, 0, [], null, null, null, "Partial work");
}
