namespace CodexWorker;

public sealed record ExecutionCleanupRequest(Guid? ExecutionId = null, int? IssueNumber = null,
    bool Stale = false, int Limit = 20, bool Apply = false, string Action = "cleanup", bool ConfirmPurge = false);
public sealed record ExecutionCleanupResult(ExecutionCleanupInspection Inspection, string Outcome);

/// <summary>Worker-owned maintenance. Selection is bounded; each apply obtains fresh history and Git proof.</summary>
public sealed class ExecutionCleanupService(ExecutionHistoryStore history, ProjectRuntimeRegistry registry,
    Func<WorkerConfiguration, GitRepository>? repositoryFactory = null,
    System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>? repositoryGates = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _repositoryGates =
        repositoryGates ?? new(StringComparer.OrdinalIgnoreCase);
    public Task<IReadOnlyList<ExecutionCleanupResult>> RunAsync(ExecutionCleanupRequest request, CancellationToken ct) =>
        RunCoreAsync(request, null, null, ct);

    internal Task<IReadOnlyList<ExecutionCleanupResult>> RunManagedAsync(CodexProvisioning.ExecutionMaintenanceCommand command,
        Func<CancellationToken, Task<bool>> authorize, CancellationToken ct) =>
        RunCoreAsync(new(ExecutionId: command.Request.WorkerExecutionId, Apply: command.Request.Apply,
            Action: command.Request.Action), command, authorize, ct);

    private async Task<IReadOnlyList<ExecutionCleanupResult>> RunCoreAsync(ExecutionCleanupRequest request,
        CodexProvisioning.ExecutionMaintenanceCommand? authority, Func<CancellationToken, Task<bool>>? authorize, CancellationToken ct)
    {
        if ((request.ExecutionId.HasValue ? 1 : 0) + (request.IssueNumber.HasValue ? 1 : 0) + (request.Stale ? 1 : 0) != 1 ||
            request.Action is not ("inspect" or "cleanup" or "archive" or "purge" or "reconcile") ||
            request.ExecutionId == Guid.Empty || request.IssueNumber is <= 0 || request.Limit is < 1 or > 100)
            throw new ArgumentException("Select one execution, one positive Issue number, or stale executions; limit must be 1 through 100.");
        using var maintenance = request.Apply ? authority is null ? registry.TryBeginMaintenance() : registry.TryBeginServerMaintenance() : null;
        if (request.Apply && maintenance is null)
            throw new InvalidOperationException("Cleanup requires a completed Worker drain with no active executions or other maintenance. Drain the Worker and retry.");
        var initial = await history.ReadAllAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var selected = initial.Where(e => request.ExecutionId == e.ExecutionId || request.IssueNumber == e.IssueNumber ||
            request.Stale && e.CompletedAtUtc <= cutoff && (request.Action != "cleanup" || e.RecoveryState is not
                (null or "operator-cleaned" or "expired-cleaned" or "resumed-cleaned" or "discarded" or "cleaned-no-changes")))
            .OrderBy(e => e.StartedAtUtc).ThenBy(e => e.ExecutionId).Take(request.ExecutionId.HasValue ? 1 : request.Limit)
            .Select(e => e.ExecutionId).ToArray();
        var results = new List<ExecutionCleanupResult>();
        foreach (var id in selected)
        {
            ct.ThrowIfCancellationRequested();
            var repository = initial.Single(e => e.ExecutionId == id).Repository;
            var gate = _repositoryGates.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var current = await history.ReadAllAsync(ct);
                var entry = current.Single(e => e.ExecutionId == id);
                if (authority is not null && (entry.ServerExecutionId != authority.Request.ServerExecutionId ||
                    entry.AssignmentId != authority.Request.AssignmentId || entry.OwnershipGeneration != authority.Request.Generation ||
                    authorize is null || !await authorize(ct)))
                {
                    results.Add(new(new(id, "review", "server-authority-rejected", "Current Server scope does not authorize this execution."), "refused"));
                    continue;
                }
                var projects = registry.Snapshot().Where(p => p.Configuration.Project.Name == entry.Project &&
                    p.Configuration.Project.Repository == entry.Repository).ToArray();
                if (projects.Length != 1)
                {
                    results.Add(new(new(id, "review", "project-unavailable", "Execution has no unique configured project; preserve resources."), "refused"));
                    continue;
                }
                var assessment = ExecutionMaintenanceClassifier.Classify(entry, DateTimeOffset.UtcNow, true);
                string? refusal = authority is null && (entry.ServerExecutionId is not null || entry.AssignmentId is not null || entry.OwnershipGeneration is not null)
                    ? "server-authority-required" : request.Action == "purge" ? "purge-proof-retention-required" :
                    request.Action == "reconcile" ? "recovery-protocol-required" :
                    request.Action == "archive" && (assessment.Status != "healthy-terminal" || entry.CompletedAtUtc > DateTimeOffset.UtcNow.AddDays(-30))
                        ? "archive-retention-or-review" : null;
                if (refusal is not null)
                {
                    results.Add(new(new(id, "review", refusal, "Preserve history and resources. Managed maintenance requires scoped Server authority; use the existing recovery protocol for reconciliation. Archive requires a confirmed terminal outcome at least 30 days old with no maintenance ambiguity. Permanent purge cannot retain required provenance."), "refused"));
                    continue;
                }
                var config = projects[0].Configuration;
                using var git = repositoryFactory?.Invoke(config) ?? new GitRepository(new ProcessRunner(), config.Project.Directory,
                    config.Project.Repository, config.Git, config.Worker);
                try
                {
                    var inspection = request.Apply && request.Action == "cleanup"
                        ? await git.CleanupStaleExecutionAsync(entry, current, ct)
                        : await git.InspectOperatorCleanupAsync(entry, current, ct);
                    if (request.Action == "archive" && inspection.Decision == "safe" && inspection.ReasonCode != "already-clean")
                        inspection = inspection with { Decision = "keep", ReasonCode = "archive-resources-remain", Message = "Clean resources separately before archiving history." };
                    if (request.Apply && request.Action == "archive" && inspection.Decision == "safe")
                    {
                        var archived = await history.ReadArchiveAuditAsync(id, ct) is not null;
                        await history.ArchiveAsync(entry, inspection, CancellationToken.None);
                        results.Add(new(inspection, archived ? "already-archived" : "archived"));
                        continue;
                    }
                    if (request.Apply && request.Action == "cleanup" && inspection.Decision == "safe")
                    {
                        // Git succeeded. Finish this single durable write even if the HTTP client disconnects.
                        await history.UpdateRecoveryAsync(id, "operator-cleaned", CancellationToken.None);
                        registry.Publish("execution.operator-cleaned", $"Operator cleaned execution {id}.", entry.Project);
                    }
                    results.Add(new(inspection, inspection.Decision != "safe" ? "refused" : !request.Apply || request.Action == "inspect" ? "dry-run" :
                        inspection.ReasonCode == "already-clean" ? "already-clean" : "cleaned"));
                }
                catch (WorkerInfrastructureException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                catch (WorkerInfrastructureException)
                {
                    // No success metadata on partial Git failure or failed durable recording. Reinspection is required.
                    results.Add(new(new(id, "review", "cleanup-failed", "Cleanup or durable recording failed. Inspect worktree locks, registration, branch/HEAD and history before another apply; success was not recorded."), "failed"));
                }
            }
            finally { gate.Release(); }
        }
        return results;
    }
}
