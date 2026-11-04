namespace CodexWorker.Tests;

public sealed class CodexThreadCoordinatorTests : IDisposable
{
    private static readonly ImplementationResult Success = new("success", "Done", [], false, null);
    private static Task<bool> Valid(CancellationToken _) => Task.FromResult(true);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task ChildrenSharingAThreadSerializeResumeAndCompletionCapture()
    {
        var coordinator = new CodexThreadCoordinator();
        var thread = Guid.NewGuid().ToString();
        var resumed = Signal();
        var capture = Signal();
        var captured = false;
        var first = coordinator.RunAsync(thread, Valid, async (session, ct) =>
        {
            Assert.Equal(thread, session);
            resumed.SetResult();
            await capture.Task.WaitAsync(ct);
            captured = true;
            return Success;
        }, TestContextToken);
        await resumed.Task.WaitAsync(TestContextToken);
        var rechecked = false;
        var second = coordinator.RunAsync(thread.ToUpperInvariant(), _ =>
        {
            Assert.True(captured);
            rechecked = true;
            return Task.FromResult(true);
        }, (session, _) =>
        {
            Assert.Equal(thread.ToUpperInvariant(), session);
            return Task.FromResult(Success);
        }, TestContextToken);
        Assert.False(rechecked);
        Assert.False(second.IsCompleted);
        capture.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TestContextToken);
        Assert.True(rechecked);
    }

    // A deadline here bounds a broken test, not the ordering assertions.
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
    private CancellationToken TestContextToken => _deadline.Token;
    public void Dispose() => _deadline.Dispose();

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task DifferentThreadsAndFreshSessionsRunWhileAnotherTurnIsActive()
    {
        var coordinator = new CodexThreadCoordinator();
        var release = Signal();
        var first = coordinator.RunAsync(Guid.NewGuid().ToString(), Valid, async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Success;
        }, TestContextToken);
        try
        {
            foreach (var thread in new string?[] { Guid.NewGuid().ToString(), null })
                Assert.Equal(Success, await coordinator.RunAsync(thread, Valid,
                    (_, _) => Task.FromResult(Success), TestContextToken));
            Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await first;
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    [InlineData("shutdown")]
    [InlineData("failed")]
    [InlineData("blocked")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task UnsuccessfulTurnsReleaseGateWithoutSupplyingCompletedContext(string failure)
    {
        var coordinator = new CodexThreadCoordinator();
        var thread = Guid.NewGuid().ToString();
        using var cancellation = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, shutdown.Token);
        var release = Signal();
        var first = coordinator.RunAsync(thread, Valid, async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            if (failure == "exception") throw new InvalidOperationException("Turn failed");
            if (failure == "timeout") throw new TimeoutException("Turn timed out");
            return Success with { Status = failure };
        }, linked.Token);
        var second = coordinator.RunAsync(thread, Valid, (session, _) =>
        {
            Assert.Null(session);
            return Task.FromResult(Success);
        }, TestContextToken);
        Assert.False(second.IsCompleted);
        if (failure == "cancellation") cancellation.Cancel();
        else if (failure == "shutdown") shutdown.Cancel();
        else release.SetResult();
        if (failure is "cancellation" or "shutdown") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        else if (failure == "exception") await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        else if (failure == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => first);
        else Assert.Equal(failure, (await first).Status);
        await second.WaitAsync(TestContextToken);
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CancelledWaiterDoesNotAbandonGateOrInvalidateSuccessfulTurn()
    {
        var coordinator = new CodexThreadCoordinator();
        var thread = Guid.NewGuid().ToString();
        var release = Signal();
        var first = coordinator.RunAsync(thread, Valid, async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Success;
        }, TestContextToken);
        using var cancellation = new CancellationTokenSource();
        var waiter = coordinator.RunAsync(thread, Valid, (_, _) => throw new InvalidOperationException("Must not run"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        await first;
        await coordinator.RunAsync(thread, Valid, (session, _) =>
        {
            Assert.Equal(thread, session);
            return Task.FromResult(Success);
        }, TestContextToken);
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task WaitingChildRechecksBlockerAndExecutionBeforeResuming()
    {
        var coordinator = new CodexThreadCoordinator();
        var thread = Guid.NewGuid().ToString();
        var release = Signal();
        var valid = true;
        var first = coordinator.RunAsync(thread, Valid, async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Success;
        }, TestContextToken);
        var waiter = coordinator.RunAsync(thread, _ => Task.FromResult(valid), (session, _) =>
        {
            Assert.Null(session);
            return Task.FromResult(Success);
        }, TestContextToken);
        valid = false;
        release.SetResult();
        await Task.WhenAll(first, waiter).WaitAsync(TestContextToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(thread,
            _ => throw new OperationCanceledException("Ownership lost"),
            (_, _) => throw new InvalidOperationException("Must not resume"), TestContextToken));
        await coordinator.RunAsync(thread, Valid, (_, _) => Task.FromResult(Success), TestContextToken);
    }
}
