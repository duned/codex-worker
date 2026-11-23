namespace CodexProvisioning;

using System.Text.Json.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerUpdateRequest(string OperationId, bool Restart = false);
public sealed record WorkerUpdateOperation(string OperationId, string WorkerId, string State,
    DateTimeOffset RequestedAtUtc, DateTimeOffset DeadlineUtc, string PriorPolicy, bool OwnsDrain,
    string OriginalVersion, string? ExpectedVersion = null, DateTimeOffset? StartedAtUtc = null,
    string? Diagnostic = null, IReadOnlyList<string>? RequiredCapabilities = null, bool InstallerVerified = false, bool Restart = false, string? OriginalInstanceId = null, WorkerUpdateRecovery? Recovery = null, string? RetryOfOperationId = null, DateTimeOffset? DrainReleasedAtUtc = null);
public sealed record WorkerUpdateRecovery(bool SchedulingBlocked, DateTimeOffset CheckedAtUtc, IReadOnlyList<WorkerUpdateRecoveryCheck> Checks, string? ReportedVersion = null, bool RetryEligible = false, bool ReleaseDrainEligible = false)
{
    public bool Eligible => Checks.All(check => check.Satisfied);
}
/// <param name="Detail">Optional concrete evidence for an unsatisfied check, such as the missing capability names.</param>
public sealed record WorkerUpdateRecoveryCheck(string Code, bool Satisfied, string Description, string NextStep, string? Detail = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerUpdatePreparation(string Version);
/// <summary>Optional reason a Worker reports with a failed operation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerUpdateFailure(string Reason);
public static class WorkerUpdateProtocol
{
    public const string RestartCapability = "worker-restart-v1";
    public const string RestartPolicyKey = "worker:restart";
    public const string Capability = "worker-self-update-v1";
    public const string PolicyKey = "worker:self-update";
    /// <summary>The Worker refused the operation before draining or dispatching anything, so nothing is uncertain.</summary>
    public const string RefusedReason = "refused";
    /// <summary>Diagnostic of an operation the Worker refused because its installation or local policy does not permit it.</summary>
    public const string RefusedDiagnostic = "refused-by-worker-installation-or-local-policy";
    public static bool ValidId(string value) => Guid.TryParseExact(value, "N", out _);
    public static bool ValidVersion(string? value)
    {
        if (value is not { Length: > 0 and <= 100 }) return false;
        try { return SemanticReleaseVersion.Parse(value).IsStable; }
        catch (ArgumentException) { return false; }
    }
    public static bool NeedsUpdate(string current, string latest) =>
        SemanticReleaseVersion.Parse(latest).CompareTo(SemanticReleaseVersion.Parse(current)) > 0;
}
