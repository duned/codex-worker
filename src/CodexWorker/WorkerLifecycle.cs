namespace CodexWorker;

/// <summary>Process-local, deterministic lifecycle state shared by scheduling and management surfaces.</summary>
public sealed class WorkerLifecycle
{
    private readonly object _gate = new();
    private WorkerLifecycleSnapshot _snapshot = new(ApplicationVersion.Display, "starting", false, 0, null, "not-ready");

    public WorkerLifecycleSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public void SetReady(string readinessResult = "ready") => Update("ready", false, readinessResult: readinessResult);

    public void RequestDrain() => Update("drain-requested", true);

    public void SetDrained(int activeExecutions)
    {
        if (activeExecutions < 0) throw new ArgumentOutOfRangeException(nameof(activeExecutions));
        lock (_gate)
        {
            if (!_snapshot.DrainRequested) throw new InvalidOperationException("A worker must be draining before it can become drained.");
            _snapshot = _snapshot with { State = activeExecutions == 0 ? "drained" : "drain-requested", ActiveExecutions = activeExecutions };
        }
    }

    public void SetActiveExecutions(int activeExecutions)
    {
        if (activeExecutions < 0) throw new ArgumentOutOfRangeException(nameof(activeExecutions));
        lock (_gate)
        {
            _snapshot = _snapshot with { ActiveExecutions = activeExecutions };
            if (_snapshot.DrainRequested && activeExecutions == 0) _snapshot = _snapshot with { State = "drained" };
        }
    }

    public void BeginUpdate() => RequireState("drained", "updating");
    public void RecordUpdateResult(string result, bool succeeded) => Update(succeeded ? "updated" : "update-failed", true, updateResult: result);
    public void BeginRestart() => RequireState("updated", "restarting");
    public void RecordRestartFailure(string result) => Update("restart-failed", true, readinessResult: result);
    public void BeginReconnect() => Update("reconnecting", true);

    public bool CompleteReadiness(IReadOnlyList<WorkerCapabilityContract> previousCapabilities,
        IReadOnlyList<WorkerCapabilityContract> currentCapabilities, bool configurationCompatible, string? configurationFailure = null)
    {
        var previous = previousCapabilities.Select(CapabilityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = currentCapabilities.Select(CapabilityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = previous.Except(current, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            Update("capability-regression", true, readinessResult: $"Required capabilities are missing: {string.Join(", ", missing)}.");
            return false;
        }
        if (!configurationCompatible)
        {
            Update("configuration-incompatible", true, readinessResult: configurationFailure ?? "Configuration is incompatible.");
            return false;
        }
        Update("ready", false, readinessResult: "ready");
        return true;
    }

    private void RequireState(string required, string next)
    {
        lock (_gate)
        {
            if (_snapshot.State != required) throw new InvalidOperationException($"Cannot enter '{next}' while worker lifecycle is '{_snapshot.State}'.");
            _snapshot = _snapshot with { State = next };
        }
    }

    private void Update(string state, bool draining, string? updateResult = null, string? readinessResult = null)
    {
        lock (_gate)
            _snapshot = _snapshot with { State = state, DrainRequested = draining,
                LastUpdateResult = updateResult ?? _snapshot.LastUpdateResult,
                ReconnectReadinessResult = readinessResult ?? _snapshot.ReconnectReadinessResult };
    }

    private static string CapabilityKey(WorkerCapabilityContract capability) =>
        $"{capability.Type}/{capability.Name}/{capability.Scope ?? string.Empty}";
}

public sealed record WorkerLifecycleSnapshot(string CurrentVersion, string State, bool DrainRequested,
    int ActiveExecutions, string? LastUpdateResult, string ReconnectReadinessResult);
