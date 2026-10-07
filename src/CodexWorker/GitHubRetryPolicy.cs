namespace CodexWorker;

/// <summary>Bounded provider retries; uncertain writes require authoritative proof before replay.</summary>
internal sealed class GitHubRetryPolicy
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly IReadOnlyList<TimeSpan> delays;

    internal GitHubRetryPolicy(Func<TimeSpan, CancellationToken, Task>? delay = null,
        IReadOnlyList<TimeSpan>? delays = null)
    {
        this.delay = delay ?? Task.Delay;
        this.delays = delays ?? [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    }

    internal async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> execute,
        Func<Exception, bool> transient, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? verify = null, T? satisfied = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await execute(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && transient(ex))
            {
                if (attempt >= delays.Count)
                {
                    if (verify is not null && await verify(ct))
                    {
                        Console.WriteLine($"GitHub {operation}: remote verification confirms success.");
                        return satisfied ?? throw new InvalidOperationException("Verified operation requires a success result.");
                    }
                    Console.WriteLine($"GitHub {operation}: transient retries exhausted; reconciliation required.");
                    throw;
                }
                Console.WriteLine($"GitHub {operation}: TransientProvider; retry {attempt + 1}/{delays.Count} in approximately {delays[attempt].TotalSeconds:0} seconds.");
                await delay(delays[attempt], ct);
                if (verify is not null && await verify(ct))
                {
                    Console.WriteLine($"GitHub {operation}: remote verification confirms success.");
                    return satisfied ?? throw new InvalidOperationException("Verified operation requires a success result.");
                }
            }
        }
    }
}
