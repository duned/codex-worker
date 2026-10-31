namespace CodexWorker;

/// <summary>Capacity waits retain the active assignment and do not consume infrastructure resumes.
/// With no configured fallback policy, continue using the selected model until cancellation.</summary>
internal static class CodexCapacityRetry
{
    internal static Task<CodexOutcome> RunAsync(Func<Task<CodexOutcome>> turn,
        Func<CancellationToken, Task> cooldown, CancellationToken ct) => RunAsync<CodexOutcome>(turn, cooldown, ct);

    internal static async Task<T> RunAsync<T>(Func<Task<T>> turn,
        Func<CancellationToken, Task> cooldown, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await turn(); }
            catch (CodexExecutionInfrastructureException ex) when (ex.Category == CodexFailure.ModelCapacity)
            {
                await cooldown(ct);
            }
        }
    }
}
