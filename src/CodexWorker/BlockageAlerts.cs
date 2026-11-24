namespace CodexWorker;

/// <summary>A blockage that lasted past the alert threshold, or the recovery of one that was alerted.
/// <see cref="Project"/> is null for the Worker itself.</summary>
public sealed record BlockageAlert(string? Project, string Reason, TimeSpan Duration, bool Recovered);

/// <summary>Tracks when the Worker or each project stopped taking work, and reports each lasting blockage
/// once and its recovery once. Short interruptions below the threshold stay silent.</summary>
internal sealed class BlockageAlertState(TimeSpan threshold)
{
    private readonly Dictionary<string, (DateTimeOffset Since, string Reason, bool Alerted)> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private const string WorkerKey = "";

    /// <param name="workerBlocked">Why the Worker cannot take work, or null when it can.</param>
    /// <param name="projectsBlocked">Why each project that cannot take work is blocked.</param>
    public IReadOnlyList<BlockageAlert> Observe(DateTimeOffset now, string? workerBlocked, IReadOnlyDictionary<string, string> projectsBlocked)
    {
        if (threshold <= TimeSpan.Zero) return [];
        var current = new Dictionary<string, string>(projectsBlocked, StringComparer.OrdinalIgnoreCase);
        current.Remove(WorkerKey);
        if (workerBlocked is not null) current[WorkerKey] = workerBlocked;
        var alerts = new List<BlockageAlert>();
        foreach (var (key, state) in _blocked.ToArray())
        {
            if (current.ContainsKey(key)) continue;
            _blocked.Remove(key);
            if (state.Alerted) alerts.Add(new(Project(key), state.Reason, now - state.Since, Recovered: true));
        }
        foreach (var (key, reason) in current)
        {
            if (!_blocked.TryGetValue(key, out var state)) _blocked[key] = state = (now, reason, false);
            if (state.Alerted || now - state.Since < threshold) continue;
            _blocked[key] = state with { Reason = reason, Alerted = true };
            alerts.Add(new(Project(key), reason, now - state.Since, Recovered: false));
        }
        // The Worker first, then projects by name, so one cycle reads the same way every time.
        return alerts.OrderBy(alert => alert.Project is not null).ThenBy(alert => alert.Project, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? Project(string key) => key == WorkerKey ? null : key;
}
