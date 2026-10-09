namespace CodexProvisioning;

using System.Text.Json.Serialization;

// These commands authorize local maintenance only, never implementation, GitHub writes or integration.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceRequest(string OperationId, string WorkerId, string? ServerExecutionId,
    Guid? WorkerExecutionId, string? AssignmentId, long? Generation, string Action = "inspect", bool Apply = false,
    int TimeoutSeconds = 60, int Limit = 50, int Offset = 0, string? Project = null,
    int? IssueNumber = null, string? Outcome = null, string? Origin = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceCommand(ExecutionMaintenanceRequest Request, string Status,
    DateTimeOffset CreatedAtUtc, string AuthorizedBy, DateTimeOffset? DeadlineUtc = null,
    ExecutionMaintenanceReport? Report = null, DateTimeOffset? CompletedAtUtc = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceObservation(Guid ExecutionId, string? ServerExecutionId, string? AssignmentId,
    long? Generation, string State, string RecoveryState, string ReportingStatus, string Project, int IssueNumber, bool Archived = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceReport(string Outcome, string Reason,
    IReadOnlyList<ExecutionMaintenanceObservation> Observations);

public static class ExecutionMaintenanceProtocol
{
    public const string Capability = "execution-maintenance-v1";
    public static bool Valid(ExecutionMaintenanceRequest request) =>
        request is not null && Guid.TryParseExact(request.OperationId, "N", out _) && Guid.TryParseExact(request.WorkerId, "N", out _) &&
        request.TimeoutSeconds is >= 5 and <= 300 && request.Limit is >= 1 and <= 100 && request.Offset is >= 0 and <= 10_000 &&
        (request.Project is null || request.Project is { Length: > 0 and <= 80 } && !request.Project.Any(char.IsControl)) &&
        request.IssueNumber is not <= 0 && request.Outcome is null or "succeeded" or "blocked" or "failed" or "infrastructure-failure" or "cancelled" or "integration-conflict" or "active" &&
        request.Origin is null or "local" or "managed" &&
        (request.Action == "inventory" ? !request.Apply && request.ServerExecutionId is null && request.WorkerExecutionId is null &&
            request.AssignmentId is null && request.Generation is null :
            request.Action is "inspect" or "cleanup" or "archive" or "retry-report" &&
            request.Project is null && request.IssueNumber is null && request.Outcome is null && request.Origin is null && request.Offset == 0 &&
            Guid.TryParseExact(request.ServerExecutionId, "N", out _) && request.WorkerExecutionId is not null &&
            request.WorkerExecutionId != Guid.Empty && Guid.TryParseExact(request.AssignmentId, "N", out _) && request.Generation > 0 &&
            (!request.Apply || request.Action != "inspect"));

    public static bool Valid(ExecutionMaintenanceReport report) =>
        report is not null && report.Outcome is "succeeded" or "refused" or "failed" &&
        report.Reason is { Length: > 0 and <= 100 } && report.Reason.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        report.Observations is { Count: <= 100 } && report.Observations.All(o => o is not null && o.ExecutionId != Guid.Empty &&
            o.State is { Length: <= 100 } && o.RecoveryState is { Length: <= 100 } && o.ReportingStatus is { Length: <= 100 } &&
            o.Project is { Length: <= 200 } && o.IssueNumber > 0 &&
            (o.ServerExecutionId is null || Guid.TryParseExact(o.ServerExecutionId, "N", out _)) &&
            (o.AssignmentId is null || Guid.TryParseExact(o.AssignmentId, "N", out _)));
}
