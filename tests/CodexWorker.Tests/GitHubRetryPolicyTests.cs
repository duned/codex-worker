using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitHubRetryPolicyTests
{
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 1)]
    public async Task BudgetAndDelaysAreBounded(bool transient, int expectedAttempts)
    {
        var delays = new List<TimeSpan>();
        var policy = new GitHubRetryPolicy((delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<int>("read", _ =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, _ => transient, CancellationToken.None));
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(transient ? new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) } : [], delays);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task VerifiedMutationIsSatisfiedOrSafelyRepeated(bool satisfied, int expectedAttempts)
    {
        var attempts = 0;
        var policy = new GitHubRetryPolicy((_, _) => Task.CompletedTask);
        var result = await policy.ExecuteAsync("push", _ =>
        {
            if (++attempts == 1) throw new InvalidOperationException();
            return Task.FromResult(true);
        }, _ => true, CancellationToken.None, _ => Task.FromResult(satisfied), true);
        Assert.True(result);
        Assert.Equal(expectedAttempts, attempts);
    }

    [Fact]
    public async Task UnexpectedRemoteStateStopsReplay()
    {
        var attempts = 0;
        var policy = new GitHubRetryPolicy((_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => policy.ExecuteAsync<int>("push", _ =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, _ => true, CancellationToken.None, _ => throw new WorkerInfrastructureException("Remote changed")));
        Assert.Equal(1, attempts);
    }
}
