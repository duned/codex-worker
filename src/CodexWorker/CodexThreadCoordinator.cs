using System.Collections.Concurrent;

namespace CodexWorker;

/// <summary>Host-local coordination of turns that advance a shared conversation.</summary>
public sealed class CodexThreadCoordinator
{
    private sealed class ThreadState
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal bool Usable = true;
    }

    // Failed conversations remain ineligible for this host's lifetime, even after their gate is released.
    private readonly ConcurrentDictionary<Guid, ThreadState> _threads = new();

    internal async Task<ImplementationResult> RunAsync(string? thread,
        Func<CancellationToken, Task<bool>> revalidate,
        Func<string?, CancellationToken, Task<ImplementationResult>> turn, CancellationToken ct)
    {
        if (thread is null) return await turn(null, ct);
        var state = _threads.GetOrAdd(Guid.Parse(thread), _ => new ThreadState());
        await state.Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var valid = await revalidate(ct);
            var resume = state.Usable && valid;
            if (resume)
            {
                // Mark uncertain before invoking Codex. Only full completion capture restores eligibility.
                state.Usable = false;
                var result = await turn(thread, ct);
                state.Usable = result.Status == "success";
                return result;
            }
        }
        finally { state.Gate.Release(); }

        // A fresh turn does not advance this conversation and must not hold its gate.
        return await turn(null, ct);
    }
}
