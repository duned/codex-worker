namespace CodexProvisioning;

/// <summary>Bounded Worker observations, never authority to schedule or raw process diagnostics.</summary>
public sealed record ManagedProjectDiagnostic(string ProjectId, long Revision, string State, string? DiagnosticCode = null);

public sealed record ManagedWorkerDiagnostics(string Retrieval, string Synchronization, string Source,
    string? DesiredVersion, string? AppliedVersion, string? FailureStage, string? DiagnosticCode,
    IReadOnlyList<ManagedProjectDiagnostic> Projects, string? FailedProjectId = null, long? FailedProjectRevision = null)
{
    public static bool Valid(ManagedWorkerDiagnostics? value) => value is null ||
        value.Retrieval is ("unverified" or "retrieved" or "unavailable" or "unauthorized") &&
        value.Synchronization is ("not-synchronized" or "synchronized" or "cached" or "unavailable" or "error") &&
        value.Source is ("none" or "server-retrieved" or "cached") &&
        value.FailureStage is (null or "retrieval" or "contract-validation" or "synchronization") &&
        (value.FailedProjectId is null || value.FailedProjectId.Length <= 120 && !value.FailedProjectId.Any(char.IsControl)) &&
        (value.FailedProjectRevision is null or > 0) &&
        ValidCode(value.DiagnosticCode) && ValidVersion(value.DesiredVersion) && ValidVersion(value.AppliedVersion) &&
        value.Projects is { Count: <= 1000 } && value.Projects.All(project => project is not null &&
            !string.IsNullOrWhiteSpace(project.ProjectId) && project.ProjectId.Length <= 120 && !project.ProjectId.Any(char.IsControl) &&
            project.Revision > 0 && project.State is ("unverified" or "not-materialized" or "materializing" or "ready" or "blocked" or "failed") &&
            ValidCode(project.DiagnosticCode)) && value.Projects.Select(project => project.ProjectId).Distinct(StringComparer.Ordinal).Count() == value.Projects.Count;

    private static bool ValidVersion(string? value) => value is null || value.Length <= 128 && !value.Any(char.IsControl);
    private static bool ValidCode(string? value) => value is null or "server-unavailable" or "server-unauthorized" or
        "managed-contract-invalid" or "managed-local-configuration-invalid" or "managed-synchronization-failed" or
        "project-capabilities-missing" or "project-preparation-failed";
}
