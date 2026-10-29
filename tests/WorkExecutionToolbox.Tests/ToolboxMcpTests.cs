using WorkExecutionToolbox.Cli;
using ModelContextProtocol;

namespace WorkExecutionToolbox.Tests;

public sealed partial class ToolboxCommandTests
{
    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task McpMapsRelationshipsAndPreservesConflictAndOperationResult()
    {
        var provider = new FakeProvider { Status = RelationshipChangeStatus.Conflict };
        var tools = new ToolboxTools(provider, provider);
        var result = await tools.SetParent("owner/repo", 3, [9]);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(3, provider.Parent?.ParentIssueNumber);
        Assert.Equal(result, tools.GetOperation(result.OperationId));
        await tools.Dependencies("owner/repo", 9, [3, 4], true);
        Assert.True(provider.Dependencies?.Applied);
        Assert.Equal([3, 4], provider.Dependencies?.BlockerIssueNumbers);
        await tools.ClearParent("owner/repo", 9);
        Assert.Null(provider.Parent?.ParentIssueNumber);
        await tools.Inspect("owner/repo", ToolboxInspection.Graph, 9);
        Assert.Equal("graph", provider.Operation);
    }

    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task McpRejectsUntrustedArgumentsBeforeProviderAccess()
    {
        var provider = new FakeProvider { OnCall = () => throw new InvalidOperationException("Unexpected access") };
        var tools = new ToolboxTools(provider, provider);
        await Assert.ThrowsAsync<McpException>(() => tools.Inspect("../path", ToolboxInspection.Graph, 9));
        await Assert.ThrowsAsync<McpException>(() => tools.Inspect("owner/repo", (ToolboxInspection)99, 9));
        await Assert.ThrowsAsync<McpException>(() => tools.SetParent("owner/repo", 3, [3]));
        await Assert.ThrowsAsync<McpException>(() => tools.Dependencies("owner/repo", 9, [3, 3], true));
        Assert.Null(provider.Operation);
    }

    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task McpBoundsOutputAndSanitizesHostFailureAndCancellation()
    {
        var root = new IssueReference(GitHubRepositoryContext.Create("owner/repo"), 9);
        var provider = new FakeProvider { Graph = new(root, [new(root, new string('x', 40_000), IssueState.Open)], [], false, false) };
        var result = await new ToolboxTools(provider, provider).Inspect("owner/repo", ToolboxInspection.Graph, 9);
        Assert.True(result.Truncated);
        Assert.True(result.Output.Length <= 16_384);
        var secretProvider = new FakeProvider { Graph = new(root,
            [new(root, "ghp_exampleCredentialValue", IssueState.Open)], [], false, false) };
        var redacted = await new ToolboxTools(secretProvider, secretProvider)
            .Inspect("owner/repo", ToolboxInspection.Graph, 9);
        Assert.DoesNotContain("ghp_exampleCredentialValue", redacted.Output);
        Assert.Contains("[REDACTED]", redacted.Output);
        var failed = new ToolboxTools(new FakeProvider { Failure = new InvalidOperationException("private credential") }, provider);
        var failure = await failed.Inspect("owner/repo", ToolboxInspection.Relationships, 9);
        Assert.Equal(1, failure.ExitCode);
        Assert.DoesNotContain("private credential", failure.Error);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await failed.Inspect("owner/repo", ToolboxInspection.Graph, 9, cancellationToken: cancellation.Token);
        Assert.Equal(130, cancelled.ExitCode);
        Assert.Contains("Refresh", cancelled.Error);
    }
    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task McpDeadlineReturnsActionableResultWithoutProviderAccess()
    {
        var provider = new FakeProvider { OnCall = () => throw new InvalidOperationException("Unexpected access") };
        var result = await new ToolboxTools(provider, provider, new ExpiredClock())
            .Inspect("owner/repo", ToolboxInspection.Graph, 9);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("deadline exceeded", result.Error);
        Assert.Null(provider.Operation);
    }

    private sealed class ExpiredClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            callback(state);
            return new InertTimer();
        }
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

}
