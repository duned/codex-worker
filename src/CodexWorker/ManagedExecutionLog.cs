namespace CodexWorker;

/// <summary>Bounded, single-line correlation for managed operational events.</summary>
internal static class ManagedExecutionLog
{
    private static string Safe(string value, IReadOnlyList<string>? secrets = null, int limit = 1000)
    {
        var safe = FailureDiagnosticRedactor.Redact(value, secrets);
        safe = new string(safe.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return safe.Length <= limit ? safe : safe[..limit] + "…";
    }

    public static string Assignment(WorkerAssignmentContract assignment, string message, IReadOnlyList<string>? secrets = null) =>
        $"Managed · project {Safe(assignment.Project.Id, secrets, 128)}/{Safe(assignment.Project.Name, secrets, 128)} · " +
        $"work {Safe(assignment.Work.Type, secrets, 64)} #{Safe(assignment.Work.Id, secrets, 64)} · " +
        $"assignment {Safe(assignment.AssignmentId, secrets, 128)} · Server execution {Safe(assignment.ServerExecutionId, secrets, 128)} · " +
        $"lease generation {assignment.Lease?.Generation.ToString() ?? "missing"} · {Safe(message, secrets)}";

    public static string Execution(ExecutionHistoryEntry entry, string message, IReadOnlyList<string>? secrets = null) =>
        $"Managed · project {Safe(entry.Project, secrets, 128)} · Issue #{entry.IssueNumber} · " +
        $"execution {entry.ExecutionId} · assignment {Safe(entry.AssignmentId ?? "missing", secrets, 128)} · " +
        $"Server execution {Safe(entry.ServerExecutionId ?? "missing", secrets, 128)} · " +
        $"lease generation {entry.OwnershipGeneration?.ToString() ?? "missing"} · {Safe(message, secrets)}";

    public static void Write(WorkerConsole output, Action<string> operationalLog, string message)
    {
        output.OperationalEvent(message);
        operationalLog(message);
    }
}
