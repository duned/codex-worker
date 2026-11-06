namespace CodexProvisioning;

using System.Text.Json.Serialization;

// Scoped maintenance never authorizes implementation or integration. Completion reconciliation
// resumes only the durable, validated completion reporting checkpoints.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceRequest(string OperationId, string WorkerId, string? ServerExecutionId,
    Guid? WorkerExecutionId, string? AssignmentId, long? Generation, string Action = "inspect", bool Apply = false,
    int TimeoutSeconds = 60, int Limit = 50, int Offset = 0, string? Project = null,
    int? IssueNumber = null, string? Outcome = null, string? Origin = null, string? ExpectedProof = null, int? OlderThanDays = null, IReadOnlyList<ArchivedBranchSelection>? Branches = null, string? Repository = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ProjectRevision = null);
public sealed record ArchivedBranchSelection(string Branch, string ExpectedTip);
public sealed record ArchivedBranchObservation(string Branch, string? Tip, DateTimeOffset? CompletedAtUtc, string Decision, string Reason, bool LocalDeleted, bool RemoteDeleted);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceCommand(ExecutionMaintenanceRequest Request, string Status,
    DateTimeOffset CreatedAtUtc, string AuthorizedBy, DateTimeOffset? DeadlineUtc = null,
    ExecutionMaintenanceReport? Report = null, DateTimeOffset? CompletedAtUtc = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceObservation(Guid ExecutionId, string? ServerExecutionId, string? AssignmentId,
    long? Generation, string State, string RecoveryState, string ReportingStatus, string Project, int IssueNumber, bool Archived = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionMaintenanceReport(string Outcome, string Reason,
    IReadOnlyList<ExecutionMaintenanceObservation> Observations, CompletionReconciliationProof? CompletionProof = null, IReadOnlyList<ArchivedBranchObservation>? Branches = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompletionReconciliationProof(string Token, string Commit, string BaseBranch,
    string ValidationResult, bool IssueOpen, IReadOnlyList<string> Labels, bool IssueContentChanged,
    string ValidatedIssueFingerprint, string CurrentIssueFingerprint, DateTimeOffset VerifiedAtUtc);

public static class ExecutionMaintenanceProtocol
{
    public static bool SameRequest(ExecutionMaintenanceRequest left, ExecutionMaintenanceRequest right) =>
        left with { Branches = null } == right with { Branches = null } &&
        (left.Branches is null ? right.Branches is null : right.Branches is not null && left.Branches.SequenceEqual(right.Branches));

    public const string Capability = "execution-maintenance-v1";
    public const string BranchCapability = "archived-feature-maintenance-v1";
    public static bool ArchivedBranch(string branch) => branch is { Length: > 0 and <= 250 } &&
        (branch.StartsWith("done/feature/", StringComparison.Ordinal) || branch.StartsWith("complete/feature/", StringComparison.Ordinal)) &&
        !branch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c)) &&
        !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal) &&
        !branch.Contains("//", StringComparison.Ordinal) && !branch.EndsWith('/') && !branch.EndsWith('.') &&
        branch.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
    public const string CompletionCapability = "execution-completion-reconciliation-v1";
    public static bool Valid(ExecutionMaintenanceRequest request) =>
        request is not null &&
        (request.ProjectId is null ? request.ProjectRevision is null : request.Action == "archived-branches" &&
            request.ProjectId is { Length: > 0 and <= 80 } && request.ProjectId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && request.ProjectRevision > 0) &&
        (request.ExpectedProof is null || request.Action == "reconcile-completion" && request.Apply &&
            request.ExpectedProof.Length == 64 && request.ExpectedProof.All(char.IsAsciiHexDigit)) &&
        (request.Action != "reconcile-completion" || !request.Apply || request.ExpectedProof is not null) && Guid.TryParseExact(request.OperationId, "N", out _) && (Guid.TryParseExact(request.WorkerId, "N", out _) || request.Action == "archived-branches" && ObserverProtocol.ValidIdentity(request.WorkerId)) &&
        request.TimeoutSeconds is >= 5 and <= 300 && request.Limit is >= 1 and <= 100 && request.Offset is >= 0 and <= 10_000 &&
        (request.Project is null || request.Project is { Length: > 0 and <= 80 } && !request.Project.Any(char.IsControl)) &&
        request.IssueNumber is not <= 0 && request.Outcome is null or "succeeded" or "blocked" or "failed" or "infrastructure-failure" or "cancelled" or "integration-conflict" or "active" &&
        request.Origin is null or "local" or "managed" &&
        (request.Action == "archived-branches" ? request.Project is not null && request.Repository is { Length: > 0 and <= 300 } && !request.Repository.Any(char.IsControl) && request.OlderThanDays is >= 1 and <= 36500 &&
            request.ServerExecutionId is null && request.WorkerExecutionId is null && request.AssignmentId is null && request.Generation is null &&
            (request.Offset == 0 || !request.Apply && request.Branches is null) && request.IssueNumber is null && request.Outcome is null && request.Origin is null &&
            (request.Branches is null ? !request.Apply : request.Branches.Count is > 0 and <= 100 && request.Branches.Count <= request.Limit &&
                request.Branches.All(b => b is not null) && request.Branches.Select(b => b.Branch).Distinct(StringComparer.Ordinal).Count() == request.Branches.Count &&
                request.Branches.All(b => b is not null && ArchivedBranch(b.Branch) && b.ExpectedTip is { Length: 40 or 64 } && b.ExpectedTip.All(char.IsAsciiHexDigit))) :
            request.Repository is null && request.OlderThanDays is null && request.Branches is null && (request.Action == "inventory" ? !request.Apply && request.ServerExecutionId is null && request.WorkerExecutionId is null &&
            request.AssignmentId is null && request.Generation is null :
            request.Action is "inspect" or "cleanup" or "archive" or "retry-report" or "reconcile-completion" &&
            request.Project is null && request.IssueNumber is null && request.Outcome is null && request.Origin is null && request.Offset == 0 &&
            Guid.TryParseExact(request.ServerExecutionId, "N", out _) && request.WorkerExecutionId is not null &&
            request.WorkerExecutionId != Guid.Empty && Guid.TryParseExact(request.AssignmentId, "N", out _) && request.Generation > 0 &&
            (!request.Apply || request.Action != "inspect")));

    public static bool Valid(CompletionReconciliationProof? proof) =>
        proof is { Token.Length: 64, Commit.Length: 40 or 64, BaseBranch.Length: > 0 and <= 250,
            ValidationResult: "passed", Labels.Count: <= 100, ValidatedIssueFingerprint.Length: 64, CurrentIssueFingerprint.Length: 64 } &&
        proof.Token.All(char.IsAsciiHexDigit) && proof.Commit.All(char.IsAsciiHexDigit) &&
        !proof.BaseBranch.Any(char.IsControl) && proof.Labels.All(label => label is { Length: <= 100 } && !label.Any(char.IsControl)) &&
        proof.ValidatedIssueFingerprint.All(char.IsAsciiHexDigit) && proof.CurrentIssueFingerprint.All(char.IsAsciiHexDigit) &&
        proof.VerifiedAtUtc != default;

    public static bool Valid(ExecutionMaintenanceReport report) =>
        report is not null && report.Outcome is "succeeded" or "refused" or "failed" &&
        (report.CompletionProof is null || Valid(report.CompletionProof)) &&
        (report.Branches is null || report.Branches.Count <= 100 && report.Branches.All(b => b is not null && ArchivedBranch(b.Branch) &&
            b.Reason.Length <= 300 && b.Decision is "review" or "skipped" or "eligible" or "deleted")) &&
        report.Reason is { Length: > 0 and <= 100 } && report.Reason.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        report.Observations is { Count: <= 100 } && report.Observations.All(o => o is not null && o.ExecutionId != Guid.Empty &&
            o.State is { Length: <= 100 } && o.RecoveryState is { Length: <= 100 } && o.ReportingStatus is { Length: <= 100 } &&
            o.Project is { Length: <= 200 } && o.IssueNumber > 0 &&
            (o.ServerExecutionId is null || Guid.TryParseExact(o.ServerExecutionId, "N", out _)) &&
            (o.AssignmentId is null || Guid.TryParseExact(o.AssignmentId, "N", out _)));
}
