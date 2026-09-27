using CodexWorker;

namespace CodexWorker.Tests;

public sealed class CodexExecutorArgumentsTests
{
    [Fact]
    public void UsesAutomaticReviewWithoutConflictingSandboxArgument()
    {
        var settings = new CodexSettings { ReasoningEffort = "high", Model = "test-model" };

        var args = CodexExecutor.BuildArguments(settings, "/tmp/schema.json", "/tmp/result.json", "task prompt");

        Assert.Equal("exec", args[0]);
        Assert.Contains("--approve-for-me", args);
        Assert.DoesNotContain("--sandbox", args);
        Assert.Equal(1, args.Count(arg => arg == "--approve-for-me"));
        Assert.Contains("--output-schema", args);
        Assert.Contains("--output-last-message", args);
        Assert.Contains("--model", args);
        Assert.Contains("model_reasoning_effort=\"high\"", args);
        Assert.Equal("task prompt", args[^1]);
    }

    [Fact]
    public void OmitsModelWhenNotConfigured()
    {
        var args = CodexExecutor.BuildArguments(new CodexSettings { Model = null }, "schema", "result", "prompt");

        Assert.DoesNotContain("--model", args);
        Assert.Contains("--approve-for-me", args);
        Assert.DoesNotContain("--sandbox", args);
    }

    [Fact]
    public void PreflightUsesConfiguredModelAndRunsEphemerallyOutsideGit()
    {
        var settings = new CodexSettings { Model = "chosen-model", ReasoningEffort = "high" };

        var args = CodexExecutor.BuildPreflightArguments(settings, "/tmp/preflight.txt");

        Assert.Equal("exec", args[0]);
        Assert.Contains("--approve-for-me", args);
        Assert.DoesNotContain("--sandbox", args);
        Assert.Contains("--skip-git-repo-check", args);
        Assert.Contains("--ephemeral", args);
        Assert.Contains("--model", args);
        Assert.Contains("chosen-model", args);
        Assert.Contains("Reply only with OK. Do not inspect or modify project files.", args);
    }
}
