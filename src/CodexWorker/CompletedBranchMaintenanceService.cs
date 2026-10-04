namespace CodexWorker;

/// <summary>Routes local developer maintenance through current Worker configuration and execution repository gates.</summary>
public sealed class CompletedBranchMaintenanceService(ProjectRuntimeRegistry registry, ExecutionHistoryStore history,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _maintenanceLock = new();
    private readonly Dictionary<string, (WorkerConfiguration Configuration, GitRepository Git, SemaphoreSlim Gate)> _maintenance = new(StringComparer.OrdinalIgnoreCase);

    public void Register(WorkerConfiguration configuration, GitRepository git, SemaphoreSlim gate)
    {
        lock (_maintenanceLock) _maintenance[configuration.Project.Name] = (configuration, git, gate);
    }

    public async Task<CompletedBranchCleanupResult?> CleanupAsync(CompletedBranchCleanupRequest request, CancellationToken ct)
    {
        if (request.OlderThanDays is < 1 or > 36500 || string.IsNullOrWhiteSpace(request.RepositoryDirectory))
            throw new InvalidDataException("Repository directory and olderThanDays between 1 and 36500 are required.");
        var directory = Path.GetFullPath(request.RepositoryDirectory);
        var projects = registry.Snapshot().Where(p => Path.GetFullPath(p.Configuration.Project.Directory).Equals(directory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        if (projects.Length != 1) return null;
        var config = projects[0].Configuration;
        (WorkerConfiguration Configuration, GitRepository Git, SemaphoreSlim Gate) operation;
        lock (_maintenanceLock)
        {
            if (!_maintenance.TryGetValue(config.Project.Name, out operation) || !ReferenceEquals(operation.Configuration, config))
                return null;
        }
        await operation.Gate.WaitAsync(ct);
        try
        {
            if (!registry.Snapshot().Any(p => ReferenceEquals(p.Configuration, config))) return null;
            return await operation.Git.CleanupCompletedBranchesAsync(config.Project.Name, await history.ReadAllAsync(ct),
                request.OlderThanDays, request.Apply, _clock.GetUtcNow(), ct);
        }
        finally { operation.Gate.Release(); }
    }

}
