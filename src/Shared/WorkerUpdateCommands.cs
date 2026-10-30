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
public sealed record WorkerUpdateRecoveryCheck(string Code, bool Satisfied, string Description, string NextStep);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerUpdatePreparation(string Version);
public static class WorkerUpdateProtocol
{
    public const string RestartCapability = "worker-restart-v1";
    public const string RestartPolicyKey = "worker:restart";
    public const string Capability = "worker-self-update-v1";
    public const string PolicyKey = "worker:self-update";
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
