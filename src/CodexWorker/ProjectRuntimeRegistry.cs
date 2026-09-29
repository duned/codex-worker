namespace CodexWorker;

public enum ProjectLifecycleState { Enabled, Disabled, Draining }
public sealed record ProjectLifecycleInfo(string Name, ProjectLifecycleState State, int ActiveExecutionCount, bool DrainComplete);

/// <summary>Atomic process-local lifecycle and configuration snapshots used by scheduling and control APIs.</summary>
public sealed class ProjectRuntimeRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly RuntimeEventLog? _events;
    private bool _workerDraining;
    private int _workerActive;
    private long _version;
    private TaskCompletionSource<bool> _changed = NewSignal();

    public ProjectRuntimeRegistry(IEnumerable<(string Path, WorkerConfiguration Configuration)> projects, RuntimeEventLog? events = null)
    {
        _events = events;
        foreach (var (path, configuration) in projects)
            _projects.Add(configuration.Project.Name, new Entry(path, configuration));
    }

    public IReadOnlyList<(string Path, WorkerConfiguration Configuration)> Snapshot()
    {
        lock (_gate) return _projects.Values.Select(x => (x.Path, x.Configuration)).ToArray();
    }

    public IReadOnlyList<ProjectLifecycleInfo> Status()
    {
        lock (_gate) return _projects.Select(x => Info(x.Key, x.Value)).ToArray();
    }

    public bool WorkerDraining { get { lock (_gate) return _workerDraining; } }
    public bool WorkerDrainComplete { get { lock (_gate) return _workerDraining && _workerActive == 0; } }
    public int WorkerActiveExecutionCount { get { lock (_gate) return _workerActive; } }
    public long Version { get { lock (_gate) return _version; } }
    public Task WaitForChangeAsync(long observedVersion, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_version != observedVersion) return Task.CompletedTask;
            return _changed.Task.WaitAsync(ct);
        }
    }

    public void Publish(string type, string message, string? project = null) => _events?.Publish(type, message, project);

    public ProjectLifecycleInfo? Get(string name)
    {
        lock (_gate) return _projects.TryGetValue(name, out var entry) ? Info(name, entry) : null;
    }

    /// <summary>Prevents reservations while a configuration is being removed from its provider.</summary>
    public bool TryBeginRemoval(string name)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(name, out var entry) || entry.Active != 0 || entry.Removing) return false;
            entry.StateBeforeRemoval = entry.State;
            entry.Removing = true;
            entry.State = ProjectLifecycleState.Disabled;
            SignalChanged();
            return true;
        }
    }

    public void CompleteRemoval(string name)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(name, out var entry) || !entry.Removing || entry.Active != 0)
                throw new InvalidOperationException($"Project '{name}' is not safely marked for removal.");
            _projects.Remove(name);
            SignalChanged();
        }
    }

    public void CancelRemoval(string name)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(name, out var entry) || !entry.Removing) return;
            entry.Removing = false;
            entry.State = entry.StateBeforeRemoval;
            SignalChanged();
        }
    }

    public ProjectLifecycleInfo? Enable(string name) => Transition(name, ProjectLifecycleState.Enabled, "project.enabled", "Project enabled.");
    public ProjectLifecycleInfo? Disable(string name) => Transition(name, ProjectLifecycleState.Disabled, "project.disabled", "Project disabled.");

    public ProjectLifecycleInfo? Drain(string name)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(name, out var entry)) return null;
            if (entry.State != ProjectLifecycleState.Draining)
            {
                entry.State = ProjectLifecycleState.Draining;
                _events?.Publish("project.drain.started", "Project drain started.", name);
                if (entry.Active == 0) _events?.Publish("project.drain.completed", "Project drain completed.", name);
            }
            return Info(name, entry);
        }
    }

    public void DrainWorker()
    {
        lock (_gate)
        {
            if (_workerDraining) return;
            _workerDraining = true;
            _events?.Publish("worker.drain.started", "Worker drain started.");
            if (_workerActive == 0) _events?.Publish("worker.drain.completed", "Worker drain completed.");
            SignalChanged();
        }
    }

    public bool CancelWorkerDrain(Action cancelLifecycle)
    {
        ArgumentNullException.ThrowIfNull(cancelLifecycle);
        lock (_gate)
        {
            if (!_workerDraining || _workerActive != 0) return false;
            cancelLifecycle();
            _workerDraining = false;
            _events?.Publish("worker.drain.cancelled", "Worker drain was cancelled.");
            SignalChanged();
            return true;
        }
    }

    /// <summary>Reserves execution capacity atomically with the eligibility check.</summary>
    public bool TryReserve(string name, WorkerConfiguration? expectedConfiguration = null)
    {
        lock (_gate)
        {
            if (_workerDraining || !_projects.TryGetValue(name, out var entry) || entry.State != ProjectLifecycleState.Enabled ||
                (expectedConfiguration is not null && !ReferenceEquals(entry.Configuration, expectedConfiguration))) return false;
            entry.Active++;
            _workerActive++;
            return true;
        }
    }

    public void Release(string name)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(name, out var entry) || entry.Active == 0 || _workerActive == 0)
                throw new InvalidOperationException($"No active execution is registered for project '{name}'.");
            entry.Active--;
            _workerActive--;
            if (entry.State == ProjectLifecycleState.Draining && entry.Active == 0)
                _events?.Publish("project.drain.completed", "Project drain completed.", name);
            if (_workerDraining && _workerActive == 0)
                _events?.Publish("worker.drain.completed", "Worker drain completed.");
        }
    }

    /// <summary>Installs a fully validated project set in one lock acquisition, preserving lifecycle state by name.</summary>
    public void ReplaceConfiguration(IReadOnlyList<(string Path, WorkerConfiguration Configuration)> configurations)
    {
        ProjectConfigurationDiscovery.ValidateSet(configurations);
        lock (_gate)
        {
            var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, configuration) in configurations)
            {
                if (_projects.TryGetValue(configuration.Project.Name, out var previous))
                {
                    if (previous.Active > 0 && !SameExecutionIdentity(previous.Configuration, configuration))
                        throw new ProjectConfigurationConflictException($"Project '{configuration.Project.Name}' has active executions and cannot change repository or checkout.");
                    next.Add(configuration.Project.Name, new Entry(path, configuration, previous.State, previous.Active));
                }
                else next.Add(configuration.Project.Name, new Entry(path, configuration));
            }
            if (_projects.Any(x => x.Value.Active > 0 && !next.ContainsKey(x.Key)))
                throw new ProjectConfigurationConflictException("A project with an active execution cannot be removed.");
            _projects.Clear();
            foreach (var pair in next) _projects.Add(pair.Key, pair.Value);
            SignalChanged();
        }
    }

    private ProjectLifecycleInfo? Transition(string name, ProjectLifecycleState target, string eventType, string message)
    {
        lock (_gate)
        {
            if (!_projects.TryGetValue(name, out var entry)) return null;
            if (entry.Removing) return Info(name, entry);
            if (entry.State != target)
            {
                entry.State = target;
                _events?.Publish(eventType, message, name);
                if (target == ProjectLifecycleState.Enabled) SignalChanged();
            }
            return Info(name, entry);
        }
    }

    private static ProjectLifecycleInfo Info(string name, Entry entry) => new(name, entry.State, entry.Active,
        entry.State == ProjectLifecycleState.Draining && entry.Active == 0);

    private static bool SameExecutionIdentity(WorkerConfiguration a, WorkerConfiguration b) =>
        string.Equals(a.Project.Repository, b.Project.Repository, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFullPath(a.Project.Directory), Path.GetFullPath(b.Project.Directory), StringComparison.OrdinalIgnoreCase);

    private void SignalChanged()
    {
        _version++;
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Entry(string path, WorkerConfiguration configuration, ProjectLifecycleState state = ProjectLifecycleState.Enabled, int active = 0)
    {
        public string Path = path;
        public WorkerConfiguration Configuration = configuration;
        public ProjectLifecycleState State = state;
        public int Active = active;
        public bool Removing;
        public ProjectLifecycleState StateBeforeRemoval = state;
    }
}
