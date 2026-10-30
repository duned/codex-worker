namespace CodexServer;

using Microsoft.Extensions.Logging;

public sealed partial class SqliteRegistryStore
{
    // Observations explain admission decisions only; they never authorize scheduling.
    // Bound retained Worker/project identities and expire observations with heartbeat freshness.
    private sealed record AssignmentDecision(string? Reason, IReadOnlyDictionary<string, int> ProjectCapacities, DateTimeOffset ObservedAt);
    private readonly object _assignmentDiagnosticsGate = new();
    private readonly Dictionary<string, AssignmentDecision> _assignmentDecisions = new(StringComparer.Ordinal);

    private void RecordAssignmentDecision(WorkerAssignmentRequest request, WorkAssignmentResponse response)
    {
        lock (_assignmentDiagnosticsGate)
        {
            var previous = _assignmentDecisions.GetValueOrDefault(request.WorkerId);
            if (previous is null && _assignmentDecisions.Count >= 1024)
                _assignmentDecisions.Remove(_assignmentDecisions.MinBy(pair => pair.Value.ObservedAt).Key);
            _assignmentDecisions[request.WorkerId] = new(response.Reason,
                new Dictionary<string, int>(request.ProjectCapacities, StringComparer.Ordinal), _timeProvider.GetUtcNow());
            if (!response.HasWork && (previous is null || previous.Reason != response.Reason))
                ServerOperationalDiagnostics.Write(_logger, response.Reason == "no-eligible-work" ? LogLevel.Debug : LogLevel.Information,
                    "assignment", response.Reason ?? "no-eligible-work", workerId: request.WorkerId);
        }
    }

    private AssignmentDecision? RecentAssignmentDecision(string workerId)
    {
        lock (_assignmentDiagnosticsGate)
            return _assignmentDecisions.TryGetValue(workerId, out var decision) &&
                _timeProvider.GetUtcNow() - decision.ObservedAt <= _staleAfter ? decision : null;
    }
}
