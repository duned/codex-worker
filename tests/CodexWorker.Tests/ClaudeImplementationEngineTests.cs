using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class ClaudeImplementationEngineTests
{
    private const string Help = "--restricted --safe-mode --permission-prompts --permission-mode --json-schema --effort --model --tools --allowedTools --strict-mcp-config --no-session-persistence";

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task RunsFileOnlyInAssignedWorktreeAndRepairsWithSameEngine()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var instructions = Path.Combine(directory, "AGENTS.md");
            await File.WriteAllTextAsync(instructions, "Project policy");
            var calls = 0;
            var engine = new ClaudeImplementationEngine(async (args, cwd, input, environment, timeout, ct) =>
            {
                Assert.Equal(directory, cwd);
                Assert.True(timeout > TimeSpan.Zero);
                if (args[0] == "--version") return new ProcessResult(0, "2.1.259 (Claude Code)", "");
                if (args[0] == "--help") return new ProcessResult(0, Help, "");
                calls++;
                Assert.Contains("--restricted", args);
                Assert.Contains("--safe-mode", args);
                Assert.Contains("dontAsk", args);
                Assert.DoesNotContain("--dangerously-skip-permissions", args);
                Assert.Contains("Read,Glob,Grep,Edit,Write", args);
                Assert.Equal(CodexProvisioning.ClaudeServiceEnvironment.Home, environment["HOME"]);
                Assert.Equal("", environment["GIT_CONFIG_VALUE_0"]);
                Assert.Contains("Repository AGENTS.md", input);
                if (calls == 2) Assert.Contains("repair attempt 1 of 2", input);
                await File.WriteAllTextAsync(Path.Combine(cwd, "implemented.txt"), "change", ct);
                return new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    type = "result", is_error = false,
                    structured_output = new { status = "success", summary = "done", testsOrValidationPerformed = Array.Empty<string>(),
                        needsHumanInput = false, question = (string?)null, blockerType = (string?)null }
                }), "private output");
            }, new CodexSettings());
            var request = new ImplementationRequest(directory, instructions, new GitHubIssue(42, "Task", "Implement", DateTimeOffset.UnixEpoch));
            Assert.Equal("success", (await engine.RunAsync(request, CancellationToken.None)).Status);
            Assert.True(File.Exists(Path.Combine(directory, "implemented.txt")));
            Assert.Equal("success", (await engine.RepairAsync(request, new ValidationFailure(1, "test", 1, "failed", "", false), 1, 2, CancellationToken.None)).Status);
            Assert.Equal(2, calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("malformed")]
    [InlineData("rejected")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task FailsClosedWithoutLeakingProcessOutput(string mode)
    {
        var engine = new ClaudeImplementationEngine((args, _, _, _, _, _) => Task.FromResult(args[0] == "--version" ? new ProcessResult(0, "2.1.259 (Claude Code)", "") : args[0] == "--help"
            ? new ProcessResult(0, mode == "unsupported" ? "private-token" : Help, "")
            : new ProcessResult(mode == "rejected" ? 1 : 0, "private-token", "private-token")),
            new CodexSettings { AllowMissingDefaultInstructions = true });
        var request = new ImplementationRequest(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
            new GitHubIssue(42, "Task", "Implement", DateTimeOffset.UnixEpoch));
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => engine.RunAsync(request, CancellationToken.None));
        Assert.DoesNotContain("private-token", failure.ToString());
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CancellationPropagates()
    {
        var engine = new ClaudeImplementationEngine((_, _, _, _, _, ct) => Task.FromCanceled<ProcessResult>(ct), new CodexSettings());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(new ImplementationRequest(Path.GetTempPath(), "AGENTS.md",
            new GitHubIssue(42, "Task", "Implement", DateTimeOffset.UnixEpoch)), cancellation.Token));
    }
}
