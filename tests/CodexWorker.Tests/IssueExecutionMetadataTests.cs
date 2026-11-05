using System.Text.Json;
using CodexProvisioning;
using CodexServer;

namespace CodexWorker.Tests;

public sealed class IssueExecutionMetadataTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("", "codex", null, null)]
    [InlineData("## Codex\nmodel: custom-model\neffort: XHIGH", "codex", "custom-model", "xhigh")]
    [InlineData("## Worker\nengine: ccc\nmodel: sonnet\neffort: low", "ccc", "sonnet", "low")]
    [InlineData("## Worker\nmodel: custom-model", "codex", "custom-model", null)]
    [InlineData("## Worker\neffort: low\n## Codex\nmodel: custom-model", "codex", "custom-model", "low")]
    [InlineData("## Codex\neffort: LOW\n## Worker\neffort: low", "codex", null, "low")]
    [InlineData("```md\n## Worker\nengine: ccc\n```", "codex", null, null)]
    public void ParsesSharedSelection(string body, string engine, string? model, string? effort)
    {
        var metadata = IssueExecutionMetadata.Parse(body);
        Assert.Equal(new IssueExecutionMetadata(engine, model, effort), metadata);
        var profile = CodexExecutionProfile.Resolve(body, new CodexSettings());
        Assert.Equal(engine, profile.Engine);
        Assert.Equal(model, profile.Model);
        Assert.Equal(effort ?? "medium", profile.Effort);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("retryMode: unknown")]
    [InlineData("retryMode:")]
    [InlineData("retryMode: restart\nretryMode: restart")]
    [InlineData("retryMode: restart\n## Codex\nretryMode: resume")]
    [InlineData("retryMode: restart\n## Codex\nretryMode: restart\n## Codex\nretryMode: restart")]
    [InlineData("engine: unknown")]
    [InlineData("engine: ccc\nmodel: gpt-6.1-sol")]
    [InlineData("engine: ccc\neffort: xhigh")]
    [InlineData("engine: ccc\nmodel: unknown")]
    [InlineData("engine: ccc\ncommand: execute")]
    [InlineData("engine: ccc\nengine: codex")]
    [InlineData("engine: ccc\n## Codex\neffort: low")]
    [InlineData("model: first\n## Codex\nmodel: second")]
    [InlineData("effort: high\n## Codex\neffort: low")]
    [InlineData("engine: codex\n## Worker\neffort: low")]
    public void RejectsInvalidOrConflictingWorkerMetadata(string metadata)
    {
        var error = Assert.Throws<InvalidDataException>(() => IssueExecutionMetadata.Parse("## Worker\n" + metadata));
        Assert.StartsWith("Invalid ## Worker metadata:", error.Message);
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void SectionOrderDoesNotChangeResolution()
    {
        const string worker = "## Worker\nengine: codex\nmodel: task-model\nretryMode: RESUME\n";
        const string legacy = "## Codex\neffort: low\nretryMode: resume\n";
        Assert.Equal(IssueExecutionMetadata.Parse(worker + legacy), IssueExecutionMetadata.Parse(legacy + worker));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("", null)]
    [InlineData("## Worker\nretryMode: RESTART", "restart")]
    [InlineData("## Codex\nretryMode: ReSuMe", "resume")]
    [InlineData("## Worker\nretryMode: RESUME\n## Codex\nretryMode: resume", "resume")]
    [InlineData("## Codex\nretryMode: resume\n## Worker\nmodel: task-model", "resume")]
    [InlineData("## Worker\nengine: ccc\nretryMode: restart", "restart")]
    public void ResolvesRetryMode(string body, string? expected)
    {
        Assert.Equal(expected, IssueExecutionMetadata.Parse(body).RetryMode);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ManagedSelectionSurvivesCanonicalizationAndWorkerWireRoundTrip()
    {
        var selection = IssueExecutionMetadata.Parse("## Worker\nengine: ccc\nmodel: sonnet\neffort: low\nretryMode: resume");
        var reference = ExecutionRequestValidation.Canonicalize(new("issue", "001") { Selection = selection }, "owner/repo");
        var json = JsonSerializer.Serialize(reference, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var worker = JsonSerializer.Deserialize<ServerWorkReferenceContract>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(worker);
        Assert.Equal(selection, worker.Selection);
        Assert.Equal("1", worker.Id);
        Assert.Contains(CapabilityEligibility.ForProject([], "owner/repo", worker.Selection.Engine),
            requirement => requirement.Type == "agent-provider" && requirement.Name == "ccc");
        var legacy = JsonSerializer.Deserialize<ServerWorkReferenceContract>("{\"type\":\"issue\",\"id\":\"1\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(legacy);
        Assert.Equal("codex", legacy.Selection.Engine);
    }
}
