using CodexWorker;

namespace CodexWorker.Tests;

public sealed class CodexCapacityRetryTests
{
    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task PendingCooldownDoesNotStartAnotherTurn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = CodexCapacityRetry.RunAsync(() => ++calls == 1
            ? Task.FromException<CodexOutcome>(new CodexExecutionInfrastructureException(CodexFailure.ModelCapacity, "capacity"))
            : Task.FromResult(new CodexOutcome("failed", "task failure", [], false, null)), async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }, CancellationToken.None);
        await entered.Task;
        Assert.Equal(1, calls);
        Assert.False(running.IsCompleted);
        release.SetResult();
        Assert.Equal("failed", (await running).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CapacityRetriesWaitForEachAttemptAndReturnTheSuccessfulTurn()
    {
        var calls = 0;
        var waits = 0;
        var success = new CodexOutcome("success", "done", [], false, null);
        var result = await CodexCapacityRetry.RunAsync(() =>
        {
            Assert.Equal(waits + 1, ++calls);
            var failure = CodexFailure.Classify(CodexFailure.ProcessEvidence(
                "", "Selected model is at capacity. Please try a different model."));
            return calls <= 4
                ? Task.FromException<CodexOutcome>(new CodexExecutionInfrastructureException(failure.Category, failure.Reason))
                : Task.FromResult(success);
        }, _ => { waits++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Same(success, result);
        Assert.Equal(4, waits);
        // A successful turn leaves no retry budget/state attached to the next turn.
        Assert.Same(success, await CodexCapacityRetry.RunAsync(() => Task.FromResult(success),
            _ => throw new InvalidOperationException("Unexpected cooldown"), CancellationToken.None));
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CancellationDuringCooldownStopsAttemptsAndGenericFailuresEscape()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => CodexCapacityRetry.RunAsync(() =>
        {
            calls++;
            throw new CodexExecutionInfrastructureException(CodexFailure.ModelCapacity, "capacity");
        }, token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => CodexCapacityRetry.RunAsync(
            () => throw new CodexExecutionInfrastructureException("Codex CLI/process failure", "failure"),
            _ => throw new InvalidOperationException("Unexpected cooldown"), CancellationToken.None));
    }
}
