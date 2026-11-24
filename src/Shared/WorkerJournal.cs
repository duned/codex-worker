namespace CodexProvisioning;

public sealed record WorkerJournalRequest(Guid Id, int Lines);
public sealed record WorkerJournalResult(string State, IReadOnlyList<string> Lines);
public static class WorkerJournalProtocol
{
    public const int MaximumLines = 200;
    /// <summary>The systemd journal of the Worker service.</summary>
    public const string Journal = "journal";
    /// <summary>The Worker's in-memory runtime events (project paused, Codex ready, provisioning finished…).</summary>
    public const string Events = "events";
    public static bool Valid(WorkerJournalResult result) =>
        result.State is "ready" or "unsupported" or "denied" or "unavailable" or "error" && result.Lines is not null &&
        result.Lines.Count <= MaximumLines && result.Lines.All(line => line is not null && line.Length <= 1000) &&
        result.Lines.Sum(line => line.Length) <= 100_000;
}
