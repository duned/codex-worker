namespace CodexWorker;

/// <summary>Emits scheduler capacity snapshots only when their authoritative state changes.</summary>
internal sealed class SchedulerCapacityLog(int globalLimit, Action<string> write)
{
    private int _globalLimit = globalLimit;
    private (int Active, int Limit)? _reportedGlobalState;
    private readonly Dictionary<string, (int GlobalActive, int ProjectActive, int ProjectLimit)> _reportedProjects =
        new(StringComparer.OrdinalIgnoreCase);

    public void Reconfigure(int newGlobalLimit)
    {
        _globalLimit = newGlobalLimit;
        _reportedProjects.Clear();
    }

    public void Report(int globalActive, IEnumerable<(string Name, int Active, int Limit)> projects)
    {
        var globalState = (globalActive, _globalLimit);
        var globalChanged = _reportedGlobalState != globalState;
        if (globalChanged)
        {
            write($"Scheduler · global {globalActive}/{_globalLimit}");
            _reportedGlobalState = globalState;
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, projectActive, projectLimit) in projects)
        {
            present.Add(name);
            var state = (globalActive, projectActive, projectLimit);
            if (globalChanged || !_reportedProjects.TryGetValue(name, out var previous) || previous != state)
            {
                write($"Scheduler · global {globalActive}/{_globalLimit} · {name} {projectActive}/{projectLimit}");
                _reportedProjects[name] = state;
            }
        }

        foreach (var removed in _reportedProjects.Keys.Where(name => !present.Contains(name)).ToArray())
            _reportedProjects.Remove(removed);
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
