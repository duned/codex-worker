namespace CodexWorker;

/// <summary>Optional conversation context; never grants execution or lease authority.</summary>
internal static class BlockerCodexSession
{
    internal static async Task<(string? Thread, string Reason)> SelectAsync(ImplementationRequest request,
        string project, string repository, string engine, ExecutionHistoryStore? history, IGitHubClient? github,
        string owner, Func<string, CancellationToken, Task<bool>> available, CancellationToken ct)
    {
        if (engine != "codex") return (null, "selected engine is not Codex");
        if (request.RetryOf is not null) return (null, "retry uses its own continuation policy");
        if (request.Issue.ParentIssueNumber is not { } parent) return (null, "no same-repository parent");
        if (history is null || github is null) return (null, "dependency history unavailable");
        try
        {
            var blockers = await github.GetBlockerIssueNumbersAsync(request.Issue.Number, ct);
            if (blockers is null) return (null, "blocker relationships unavailable or outside repository");
            if (blockers.Count == 0) return (null, "no native blockers");
            string? selected = null;
            foreach (var blocker in blockers)
            {
                if (blocker == request.Issue.Number || await github.GetParentIssueNumberAsync(blocker, ct) != parent)
                    return (null, "blocker belongs to another parent");
                var source = await history.FindInheritedCodexSessionAsync(project, repository, blocker, parent, owner, available, ct);
                if (source?.CodexThreadId is not { } thread)
                    return (null, "blocker lacks a successful Codex thread in this Worker's session store");
                if (selected is not null && selected != thread) return (null, "blocker thread IDs differ");
                selected = thread;
            }
            return (selected, "all blockers share one local Codex thread");
        }
        catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested)
        {
            // Optional context reads do not alter the already-established scheduling eligibility.
            return (null, "blocker relationships could not be verified");
        }
    }
}
