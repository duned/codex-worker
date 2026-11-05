namespace CodexProvisioning;

using System.Text.Json.Serialization;
using System.Text.Json;

/// <summary>Reporting-only identity; never a managed Worker identity or scheduling authority.</summary>
public static class ObserverProtocol
{
    public const int Version = 1;
    public const string Mode = "standalone-observer";
    public const int MaximumExecutions = 32;
    public const int MaximumPayloadBytes = 65536;
    public static bool ValidIdentity(string? value) => value is { Length: 36 } &&
        value.StartsWith("obs_", StringComparison.Ordinal) && ValidId(value[4..]);
    public static bool ValidId(string? value) => value is not null && Guid.TryParseExact(value, "N", out _) && value == value.ToLowerInvariant();
    public static bool ValidOrigin(string? value) => value is { Length: <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        value == uri.GetLeftPart(UriPartial.Authority);
    public static bool ValidCredential(string? value) => value is { Length: 68 } &&
        value.StartsWith("obr_", StringComparison.Ordinal) && value[4..].All(Uri.IsHexDigit);
    public static bool ValidAuthorization(string? value) => value is { Length: 68 } &&
        value.StartsWith("oba_", StringComparison.Ordinal) && value[4..].All(Uri.IsHexDigit);
    public static bool Display(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    public static bool Timestamp(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };

    public static ObserverStatusSnapshot ReadSnapshot(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaximumPayloadBytes) throw new JsonException("Observer payload exceeds the byte limit.");
        var value = JsonSerializer.Deserialize<ObserverStatusSnapshot>(utf8, Json);
        return value is not null && value.IsValid() ? value : throw new JsonException("Invalid observer snapshot.");
    }

    public static ObserverExecutionEvent ReadEvent(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaximumPayloadBytes) throw new JsonException("Observer payload exceeds the byte limit.");
        var value = JsonSerializer.Deserialize<ObserverExecutionEvent>(utf8, Json);
        return value is not null && value.IsValid() ? value : throw new JsonException("Invalid observer event.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObserverPairingRequest(int ContractVersion, string Mode, string ObserverId, string Server)
{
    public bool IsValid() => ContractVersion == ObserverProtocol.Version && Mode == ObserverProtocol.Mode &&
        ObserverProtocol.ValidIdentity(ObserverId) && ObserverProtocol.ValidOrigin(Server);
}

public sealed record ObserverPairingAuthorization(int ContractVersion, string ObserverId, string Server,
    string Authorization, DateTimeOffset ExpiresAtUtc, string State = "pending")
{
    public bool IsValid() => ContractVersion == ObserverProtocol.Version && ObserverProtocol.ValidIdentity(ObserverId) &&
        ObserverProtocol.ValidOrigin(Server) && ObserverProtocol.ValidAuthorization(Authorization) &&
        ObserverProtocol.Timestamp(ExpiresAtUtc) && State == "pending";
    public override string ToString() => $"Observer pairing {ObserverId}: {State}";
}
public sealed record ObserverRegistration(int ContractVersion, string ObserverId, string Mode, string Server,
    DateTimeOffset AcceptedAtUtc, string State, string PairingState = "accepted");

/// <summary>Allowlisted display data only. Producers must never copy prompts, output, paths or diagnostics here.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObserverExecution(string LocalExecutionId, string Project, string Repository, int IssueNumber,
    string IssueTitle, string Stage, string? Outcome, string? ValidationResult, DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc = null, long? DurationMilliseconds = null)
{
    public bool IsValid() => ObserverProtocol.ValidId(LocalExecutionId) && ObserverProtocol.Display(Project, 200) &&
        ObserverProtocol.Display(Repository, 300) && Repository.Split('/') is [var owner, var name] &&
        ValidRepositoryPart(owner) && ValidRepositoryPart(name) && IssueNumber > 0 && ObserverProtocol.Display(IssueTitle, 500) &&
        Stage is "Created" or "Claimed" or "Preparing" or "Implementing" or "Validating" or "Repairing" or
            "Integrating" or "Reporting" or "Completed" or "Blocked" or "Failed" or "IntegrationConflict" or
            "InfrastructureFailure" or "Cancelled" or "Superseded" &&
        Outcome is null or "success" or "blocked" or "failed" or "cancelled" or "superseded" &&
        ValidationResult is null or "pending" or "running" or "passed" or "failed" or "cancelled" or "unavailable" &&
        ObserverProtocol.Timestamp(StartedAtUtc) && (CompletedAtUtc is null ||
            ObserverProtocol.Timestamp(CompletedAtUtc.Value) && CompletedAtUtc >= StartedAtUtc) &&
        DurationMilliseconds is null or >= 0;

    private static bool ValidRepositoryPart(string value) => value.Length > 0 && value is not "." and not ".." &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObserverStatusSnapshot(int ContractVersion, string ObserverId, string SnapshotId,
    DateTimeOffset ObservedAtUtc, string Lifecycle, IReadOnlyList<ObserverExecution> Executions)
{
    public bool IsValid() => ContractVersion == ObserverProtocol.Version && ObserverProtocol.ValidIdentity(ObserverId) &&
        ObserverProtocol.ValidId(SnapshotId) && ObserverProtocol.Timestamp(ObservedAtUtc) && WorkerLifecycleStates.IsValid(Lifecycle) &&
        Executions is { Count: <= ObserverProtocol.MaximumExecutions } && Executions.All(item => item is not null && item.IsValid()) &&
        Executions.Select(item => item.LocalExecutionId).Distinct(StringComparer.Ordinal).Count() == Executions.Count;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ObserverExecutionEvent(int ContractVersion, string ObserverId, string EventId,
    DateTimeOffset ObservedAtUtc, ObserverExecution Execution)
{
    public bool IsValid() => ContractVersion == ObserverProtocol.Version && ObserverProtocol.ValidIdentity(ObserverId) &&
        ObserverProtocol.ValidId(EventId) && ObserverProtocol.Timestamp(ObservedAtUtc) && Execution is not null && Execution.IsValid();
}
