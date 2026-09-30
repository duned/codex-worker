namespace CodexWorker;

internal static class SchedulerPollWait
{
    public static Task<Task> WaitForNextEventAsync(Task activeFinished, Task runtimeChanged,
        TimeSpan pollingInterval, bool hasAvailableCapacity, CancellationToken cancellationToken)
    {
        if (!hasAvailableCapacity) return Task.WhenAny(activeFinished, runtimeChanged);
        var pollInterval = Task.Delay(pollingInterval, cancellationToken);
        return Task.WhenAny(activeFinished, runtimeChanged, pollInterval);
    }
}

/// <summary>Emits scheduler capacity snapshots only when their authoritative state changes.</summary>
internal sealed class SchedulerCapacityLog(int globalLimit, Action<string> write)
{
    private int _globalLimit = globalLimit;
    private string? _reportedSnapshot;

    public void Reconfigure(int newGlobalLimit)
    {
        _globalLimit = newGlobalLimit;
    }

    public void Report(int globalActive, IEnumerable<(string Name, int Active, int Limit)> projects)
    {
        var activeProjects = projects.Where(project => project.Active > 0).ToArray();
        var snapshot = $"global {globalActive}/{_globalLimit}" + string.Concat(activeProjects.Select(project =>
            $" · {project.Name} {project.Active}/{project.Limit}"));
        if (string.Equals(_reportedSnapshot, snapshot, StringComparison.Ordinal)) return;

        write($"Scheduler · {snapshot}");
        _reportedSnapshot = snapshot;
    }
}

internal sealed class IdleWorkerHeartbeat(DateTimeOffset startedAtUtc)
{
    private DateTimeOffset _next = startedAtUtc.AddMinutes(15);

    public void EmitIfDue(DateTimeOffset now, Action<string> write)
    {
        if (now < _next) return;
        write("Worker heartbeat · idle");
        _next = now.AddMinutes(15);
    }
}
