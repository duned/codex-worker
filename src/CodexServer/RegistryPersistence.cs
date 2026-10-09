namespace CodexServer;

using CodexProvisioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>Persistence boundary for future worker and project registry services.</summary>
public interface IRegistryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    Task<string> CreateWorkerBootstrapTokenAsync(TimeSpan lifetime, CancellationToken cancellationToken = default);
    Task<string> CreateWorkerAuthorizationAsync(string workerId, string operation, TimeSpan lifetime, CancellationToken cancellationToken = default);
    Task<bool> RevokeWorkerBootstrapTokenAsync(string token, CancellationToken cancellationToken = default);
    Task<bool> BootstrapWorkerAsync(string token, WorkerRegistrationRequest worker, string workerToken, CancellationToken cancellationToken = default, string operation = "enroll");
    Task<bool> IsWorkerTokenValidAsync(string workerId, string token, CancellationToken cancellationToken = default);
    Task<bool> RevokeWorkerTokenAsync(string workerId, CancellationToken cancellationToken = default);
    Task RegisterWorkerAsync(WorkerRegistrationRequest worker, CancellationToken cancellationToken = default);
    Task HeartbeatWorkerAsync(WorkerHeartbeatRequest heartbeat, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkerRegistrationResponse>> GetWorkersAsync(CancellationToken cancellationToken = default);
    Task<WorkerRegistrationResponse?> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default);
    Task<WorkerRegistrationResponse?> SetWorkerSchedulingPolicyAsync(string workerId, string policy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CentralProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<CentralProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default);
    Task<CentralProject> CreateProjectAsync(CentralProjectDefinition definition, CancellationToken cancellationToken = default);
    Task<CentralProject?> UpdateProjectAsync(string projectId, CentralProjectDefinition definition, long expectedRevision, CancellationToken cancellationToken = default);
    Task<CentralProject?> UpdateProjectLifecycleAsync(string projectId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default);
    Task<bool> RemoveProjectAsync(string projectId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ExecutionRequest> EnqueueExecutionAsync(EnqueueExecutionRequest request, CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> UpdateManagedEligibilityAsync(string executionRequestId, ManagedEligibilityUpdate update,
        CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> RejectManagedAssignmentAsync(string executionRequestId, string assignmentId, string workerId,
        ManagedEligibilityUpdate update, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionRequest>> ListQueuedByIssueAsync(string projectId, string issueNumber,
        CancellationToken cancellationToken = default);
    Task<WorkAssignmentResponse> RequestAssignmentAsync(WorkerAssignmentRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionRequest>> GetExecutionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionRequest>> ListExecutionsAsync(ExecutionQuery query, CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> GetExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> CancelQueuedExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default);
    Task<ExecutionReconciliationResult?> ReconcileUncertainExecutionAsync(string executionRequestId,
        ExecutionReconciliationRequest request, CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> TransitionExecutionAsync(string executionRequestId, ExecutionStateTransition transition, CancellationToken cancellationToken = default);
    Task<ExecutionRequest?> ReportExecutionAsync(string executionRequestId, WorkerExecutionReport report, CancellationToken cancellationToken = default);
    Task<ExecutionLease?> RenewExecutionLeaseAsync(string executionId, ExecutionLeaseRenewal renewal, CancellationToken cancellationToken = default);
    Task ExpireLeasesAsync(CancellationToken cancellationToken = default);
    Task<ProvisioningPlan> CreateProvisioningPlanAsync(CreateProvisioningPlanRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProvisioningPlan>> GetProvisioningPlansAsync(CancellationToken cancellationToken = default,
        int limit = 100, int offset = 0);
    Task<ProvisioningPlan?> GetProvisioningPlanAsync(string planId, CancellationToken cancellationToken = default);
    Task<ProvisioningPlan?> AcceptProvisioningPlanAsync(string workerId, CancellationToken cancellationToken = default);
    Task<ProvisioningPlan?> ReportProvisioningPlanAsync(string planId, ProvisioningWorkerReport report, CancellationToken cancellationToken = default);
    Task<ProvisioningPlan?> TransitionProvisioningPlanAsync(string planId, ProvisioningStateTransition transition, CancellationToken cancellationToken = default);
}

public sealed record WorkReference(string Type, string Id, string? Url = null);
public sealed record EnqueueExecutionRequest(string ProjectId, WorkReference WorkReference,
    [property: JsonIgnore] long? ExpectedProjectRevision = null, [property: JsonIgnore] bool AutomaticDiscovery = false,
    [property: JsonIgnore] DateTimeOffset? ExpectedProjectCreatedAtUtc = null);
public sealed record ManagedEligibilityUpdate(string State, IReadOnlyList<string> Reasons, DateTimeOffset CheckedAtUtc);
public sealed record ExecutionQuery(string? ProjectId = null, string? State = null, string? WorkType = null,
    string? WorkId = null, int Limit = 50, int Offset = 0);
public sealed record ExecutionReconciliationRequest(string Disposition, string Evidence, string? IntegrationCommit = null);
public sealed record ExecutionReconciliationResult(ExecutionRequest Execution, ExecutionRequest? Retry);
public sealed record ExecutionRequest(string Id, string ProjectId, WorkReference WorkReference,
    DateTimeOffset CreatedAtUtc, string State, string? AssignedWorkerId, DateTimeOffset? AssignedAtUtc, string? ExecutionId,
    string? AssignmentId = null, string? CurrentStage = null, string? WorkerExecutionId = null,
    DateTimeOffset? StartedAtUtc = null, DateTimeOffset? CompletedAtUtc = null, long? DurationMilliseconds = null,
    string? ValidationResult = null, string? IntegrationResult = null, string? FailureClassification = null,
    bool Recoverable = false, string? CompletionSummary = null, ExecutionLease? Lease = null,
    string? RecoveryState = null, string? RecoveryReason = null, string? RetryOfExecutionId = null, int AttemptNumber = 1,
    string? WorkspaceRecovery = null, string? PendingReason = null, IReadOnlyList<string>? MissingRequirements = null,
    string ManagedEligibilityState = "eligible", IReadOnlyList<string>? ManagedEligibilityReasons = null,
    DateTimeOffset? ManagedEligibilityCheckedAtUtc = null);
public sealed record ExecutionLease(string ExecutionId, string WorkerId, long Generation,
    DateTimeOffset AcquiredAtUtc, DateTimeOffset ExpiresAtUtc, string State, int RenewalIntervalSeconds = 60);
public sealed record ExecutionLeaseRenewal(string WorkerId, long Generation);
public sealed record ExecutionStateTransition(string State, string? AssignedWorkerId = null, string? ExecutionId = null);
public sealed record WorkerExecutionReport(string WorkerId, string AssignmentId, string WorkerExecutionId, string State,
    string? Stage = null, DateTimeOffset? StartedAtUtc = null, DateTimeOffset? CompletedAtUtc = null,
    long? DurationMilliseconds = null, string? ValidationResult = null, string? IntegrationResult = null,
    string? FailureClassification = null, bool Recoverable = false, string? Summary = null, long Generation = 0);
public sealed record WorkerAssignmentRequest(string WorkerId, bool WorkerEnabled, int AvailableCapacity, IReadOnlyDictionary<string, int> ProjectCapacities, IReadOnlyList<IntegrationRecoveryCandidate>? IntegrationRecoveries = null);
public sealed record WorkAssignmentResponse(bool HasWork, WorkAssignment? Assignment,
    IReadOnlyDictionary<string, string>? IntegrationRecoveryRejections = null, string? Reason = null);
public sealed record WorkAssignment(string AssignmentId, string ServerExecutionId, CentralProject Project,
    WorkReference Work, string WorkerId, IReadOnlyDictionary<string, string> Metadata, ExecutionLease? Lease = null);

public sealed record ProvisioningAction(string Id, string Type, string Name, string? Version = null, string Operation = "ensure",
    string? CredentialId = null, string? Scope = null);
public sealed record CreateProvisioningPlanRequest(string WorkerId, IReadOnlyList<ProvisioningAction> Actions);
public sealed record ProvisioningPlan(string Id, string WorkerId, DateTimeOffset CreatedAtUtc, string State,
    IReadOnlyList<ProvisioningAction> Actions, string? CurrentActionId = null, DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null, string? Result = null, string? Failure = null);
public sealed record ProvisioningWorkerReport(string WorkerId, string State, string? CurrentActionId = null,
    string? Result = null, string? Failure = null);
public sealed record ProvisioningStateTransition(string State);

public static class ProvisioningPlanValidation
{
    private static readonly HashSet<string> ActionTypes = new(StringComparer.Ordinal) { "runtime", "tool", "service", "authentication", "refresh-capabilities" };
    public static string? Error(CreateProvisioningPlanRequest? request)
    {
        if (request is null || !Guid.TryParseExact(request.WorkerId, "N", out _)) return "workerId must be a valid Worker identity.";
        if (request.Actions is null || request.Actions.Count > 100) return "actions must contain at most 100 provisioning actions.";
        if (request.Actions.Any(action => action is null)) return "Provisioning actions cannot be null.";
        if (request.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != request.Actions.Count) return "Provisioning action IDs must be unique.";
        foreach (var action in request.Actions)
        {
            var validOperation = action.Type switch
            {
                "runtime" or "tool" or "service" => action.Operation is "ensure" or "install" or "configure",
                "authentication" => action.Operation is "ensure" or "provision",
                "refresh-capabilities" => action.Operation == "refresh",
                _ => false
            };
            if (!Printable(action.Id, 80) || !ActionTypes.Contains(action.Type) || !validOperation || !Printable(action.Name, 200) ||
                action.Version is { } version && !Printable(version, 100) || ContainsCredentialValue(action.Id) || ContainsCredentialValue(action.Name) || ContainsCredentialValue(action.Version))
                return "Each provisioning action must have a valid ID, supported type, name, and optional version.";
            if (action.Type == "authentication" && action.Operation == "provision" &&
                (!Guid.TryParseExact(action.CredentialId, "N", out _) || action.Scope is null ||
                 !Regex.IsMatch(action.Scope, "^[^/\\s]+/[^/\\s]+$")))
                return "Credential provisioning actions require an assigned credential ID and repository scope.";
        }
        return null;
    }
    private static bool Printable(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
    private static bool ContainsCredentialValue(string? value) => value is not null && Regex.IsMatch(value, "(?i)(token|password|secret|credential|api[_-]?key)\\s*[:=]");
}

public static class ExecutionRequestValidation
{
    private static readonly Regex IssueUrlPattern = new("^/([^/]+)/([^/]+)/issues/([1-9][0-9]*)$", RegexOptions.CultureInvariant);

    public static string? Error(EnqueueExecutionRequest? request)
    {
        if (request is null) return "Execution request is required.";
        if (!Printable(request.ProjectId, 80)) return "projectId must contain 1 to 80 printable characters.";
        if (request.WorkReference is null) return "workReference is required.";
        if (!CanonicalIssueType(request.WorkReference.Type)) return "workReference.type must identify a supported GitHub Issue (issue or github-issue).";
        if (!Printable(request.WorkReference.Id, 300) || !int.TryParse(request.WorkReference.Id.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var issueNumber) || issueNumber <= 0)
            return "workReference.id must be a positive decimal GitHub Issue number.";
        if (request.WorkReference.Url is { } url &&
            (!TryParseIssueUrl(url, out var urlIssueNumber, out _, out _) || urlIssueNumber != issueNumber))
            return "workReference.url must be a canonical GitHub Issue URL matching workReference.id.";
        return null;
    }

    public static WorkReference Canonicalize(WorkReference reference, string repository)
    {
        var identity = NormalizeIdentity(reference);
        var canonicalId = identity.Id;
        if (reference.Url is { } url)
        {
            if (!TryParseIssueUrl(url, out var urlIssueNumber, out var owner, out var name) ||
                urlIssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) != canonicalId ||
                !string.Equals(repository, $"{owner}/{name}", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("workReference.url must identify this project's GitHub repository and Issue.");
            return new("github-issue", canonicalId, $"https://github.com/{repository}/issues/{canonicalId}");
        }
        return new("github-issue", canonicalId);
    }

    public static WorkReference NormalizeIdentity(WorkReference reference)
    {
        if (!CanonicalIssueType(reference.Type) || !Printable(reference.Id, 300) ||
            !int.TryParse(reference.Id.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var issueNumber) || issueNumber <= 0)
            throw new InvalidDataException("Only positive numbered GitHub Issue work references are supported.");
        return new("github-issue", issueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool CanonicalIssueType(string? type) => type is { Length: > 0 and <= 80 } raw && !raw.Any(char.IsControl) &&
        raw.Trim() is { } value &&
        (string.Equals(value, "issue", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "github-issue", StringComparison.OrdinalIgnoreCase));

    private static bool Printable(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static bool TryParseIssueUrl(string value, out int issueNumber, out string owner, out string repository)
    {
        issueNumber = 0;
        owner = string.Empty;
        repository = string.Empty;
        if (value.Length > 2000 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var match = IssueUrlPattern.Match(uri.AbsolutePath);
        return match.Success && int.TryParse(match.Groups[3].Value, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out issueNumber) &&
               (owner = match.Groups[1].Value).Length > 0 && (repository = match.Groups[2].Value).Length > 0;
    }
}

public sealed class ExecutionRequestConflictException() : Exception("An active execution request already exists for this project and work reference.") { }
public sealed class ExecutionRequestTransitionException() : Exception("The requested execution state transition is invalid.") { }
public sealed class ExecutionRequestOwnershipException() : Exception("Execution report was rejected because its lease is stale or lifecycle state has advanced.") { }
public sealed class ExecutionRequestCancellationException() : Exception("Only queued execution requests can be cancelled by an operator.") { }
public sealed class ExecutionRequestReconciliationException(string message) : Exception(message);

public static class ExecutionAdministrationValidation
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
        { "Queued", "Assigned", "Running", "Completed", "Failed", "Cancelled" };

    public static string? QueryError(ExecutionQuery? query)
    {
        if (query is null) return "Execution query is required.";
        if (query.ProjectId is { } projectId && (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 80 || projectId.Any(char.IsControl)))
            return "projectId must contain at most 80 printable characters.";
        if (query.State is { } state && !States.Contains(state)) return "state is not a supported execution state.";
        if (query.WorkType is { } type && !string.Equals(type, "issue", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "github-issue", StringComparison.OrdinalIgnoreCase))
            return "workType must identify a supported GitHub Issue.";
        if (query.WorkId is { } workId && (!int.TryParse(workId.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var issueNumber) || issueNumber <= 0))
            return "workId must be a positive decimal GitHub Issue number.";
        if (query.Limit is < 1 or > 100) return "limit must be between 1 and 100.";
        if (query.Offset is < 0 or > 10000) return "offset must be between 0 and 10000.";
        return null;
    }

    public static string? ReconciliationError(ExecutionReconciliationRequest? request)
    {
        if (request is null || request.Disposition is not ("NotIntegrated" or "Integrated"))
            return "disposition must be NotIntegrated or Integrated.";
        if (string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 1000 || request.Evidence.Any(char.IsControl))
            return "evidence must contain 1 to 1000 printable characters.";
        if (request.Disposition == "Integrated" && (request.IntegrationCommit is null ||
                !Regex.IsMatch(request.IntegrationCommit, "^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant)))
            return "Integrated disposition requires a full 40 or 64 character integration commit ID.";
        if (request.Disposition == "NotIntegrated" && request.IntegrationCommit is not null)
            return "NotIntegrated disposition cannot include an integration commit ID.";
        return null;
    }
}

/// <summary>Portable Server-owned project definition. It deliberately excludes Worker paths and secrets.</summary>
public sealed record CentralProjectDefinition(string Name, string Repository, string DefaultBranch,
    string Description, IReadOnlyList<ProjectRequirement>? Requirements = null, string? IssueReadyLabel = null,
    string? IssueBlockedLabel = null, AutomaticIssueDiscovery? AutomaticDiscovery = null, int? MaxParallelTasks = null);
public sealed record CentralProject(string Id, string Name, string Repository, string DefaultBranch,
    string Description, IReadOnlyList<ProjectRequirement> Requirements, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool Enabled = true, string? IssueReadyLabel = null,
    string? IssueBlockedLabel = null, AutomaticIssueDiscovery? AutomaticDiscovery = null, int? MaxParallelTasks = null);
public sealed record ProjectLifecycleUpdateRequest(bool Enabled, long ExpectedRevision);

/// <summary>A centrally declared capability required by a project.</summary>
[JsonConverter(typeof(ProjectRequirementJsonConverter))]
public sealed record ProjectRequirement(string Type, string Name, string? Version = null, string? Scope = null) : CodexProvisioning.ICapabilityDescriptor;

/// <summary>Reads the previous string form as a runtime while writing the structured contract.</summary>
public sealed class ProjectRequirementJsonConverter : JsonConverter<ProjectRequirement>
{
    public override ProjectRequirement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var legacy = reader.GetString() ?? string.Empty;
            var separator = legacy.IndexOf(':');
            return separator < 0
                ? new ProjectRequirement("runtime", legacy)
                : new ProjectRequirement("runtime", legacy[..separator], legacy[(separator + 1)..]);
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A project requirement must be an object.");
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        string? requirementType = null;
        string? requirementName = null;
        string? version = null;
        var hasVersion = false;
        string? scope = null;
        var hasScope = false;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type" when requirementType is null && property.Value.ValueKind == JsonValueKind.String:
                    requirementType = property.Value.GetString();
                    break;
                case "name" when requirementName is null && property.Value.ValueKind == JsonValueKind.String:
                    requirementName = property.Value.GetString();
                    break;
                case "version" when !hasVersion && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    hasVersion = true;
                    version = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                case "scope" when !hasScope && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                    hasScope = true;
                    scope = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                    break;
                default:
                    throw new JsonException($"Project requirement field '{property.Name}' is unknown, duplicated, or has an invalid value.");
            }
        }
        if (string.IsNullOrEmpty(requirementType) || string.IsNullOrEmpty(requirementName))
            throw new JsonException("A project requirement requires string type and name fields.");
        return new ProjectRequirement(requirementType, requirementName, version, scope);
    }

    public override void Write(Utf8JsonWriter writer, ProjectRequirement value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteString("name", value.Name);
        if (value.Version is not null) writer.WriteString("version", value.Version);
        if (value.Scope is not null) writer.WriteString("scope", value.Scope);
        writer.WriteEndObject();
    }
}

public static class CentralProjectValidation
{
    public static string? Error(CentralProjectDefinition? value)
    {
        if (value is null) return "Project definition is required.";
        if (value.MaxParallelTasks is < 1 or > 8) return "maxParallelTasks must be null (Automatic) or 1 to 8.";
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 120) return "name must contain 1 to 120 characters.";
        if (!Regex.IsMatch(value.Name, "^[\\p{L}\\p{N}][\\p{L}\\p{N} ._-]*$")) return "name contains unsupported characters.";
        if (string.IsNullOrWhiteSpace(value.Repository) || !Regex.IsMatch(value.Repository, "^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?/(?!\\.{1,2}$)[A-Za-z0-9_.-]+$")) return "repository must be a GitHub owner/repository identifier.";
        if (string.IsNullOrWhiteSpace(value.DefaultBranch) || value.DefaultBranch.Length > 200 || value.DefaultBranch.Any(char.IsControl)) return "defaultBranch must contain 1 to 200 printable characters.";
        if (value.Description is null || value.Description.Length > 4000) return "description must contain at most 4000 characters.";
        if (!ValidLabel(value.IssueReadyLabel) || !ValidLabel(value.IssueBlockedLabel))
            return "issueReadyLabel and issueBlockedLabel must be empty or contain at most 100 printable characters.";
        if (value.IssueReadyLabel is { } ready && value.IssueBlockedLabel is { } blocked &&
            string.Equals(ready.Trim(), blocked.Trim(), StringComparison.OrdinalIgnoreCase))
            return "issueReadyLabel and issueBlockedLabel must be different.";
        if (value.AutomaticDiscovery is { } discovery && AutomaticIssueDiscoveryValidation.Error(discovery) is { } discoveryError)
            return discoveryError;
        if (value.Requirements is null) return null;
        if (value.Requirements.Count > 64) return "requirements must contain at most 64 entries.";
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in value.Requirements)
        {
            if (requirement is null || string.IsNullOrWhiteSpace(requirement.Type) || string.IsNullOrWhiteSpace(requirement.Name) ||
                requirement.Type.Length > 40 || requirement.Name.Length > 100 || requirement.Type.Any(char.IsControl) || requirement.Name.Any(char.IsControl) ||
                !Regex.IsMatch(requirement.Type.Trim(), "^[a-zA-Z][a-zA-Z0-9_-]*$") ||
                !Regex.IsMatch(requirement.Name.Trim(), "^[\\p{L}\\p{N}.][\\p{L}\\p{N}._+-]*$"))
                return "Each requirement must have a valid type and name.";
            var key = requirement.Type.Trim().ToLowerInvariant() + ":" + requirement.Name.Trim().ToLowerInvariant();
            if (!normalized.Add(key)) return $"Requirement '{requirement.Type.Trim()}:{requirement.Name.Trim()}' is duplicated or contradictory.";
            if (requirement.Version is { } version && !ValidVersionConstraint(version))
                return $"Requirement '{requirement.Name.Trim()}' has an invalid version; use an exact numeric version or >= numeric version (for example 10.0 or >=10.0.0).";
            if (requirement.Scope is { } scope && (!requirement.Type.Trim().Equals("authentication", StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(scope, "^[^/\\s]+/[^/\\s]+$")))
                return $"Requirement '{requirement.Name.Trim()}' has an invalid scope; authentication scope must be a repository in owner/repository form.";
        }
        return null;
    }

    private static bool ValidLabel(string? value) => value is null || value.Length <= 100 && !value.Any(char.IsControl);

    private static bool ValidVersionConstraint(string value)
    {
        var match = Regex.Match(value.Trim(), "^(?:>=)?([0-9]+(?:\\.[0-9]+){0,3})$");
        return match.Success && match.Groups[1].Value.Split('.').All(part => int.TryParse(part,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));
    }

    public static ProjectRequirement Normalize(ProjectRequirement requirement)
    {
        var version = requirement.Version?.Trim();
        if (version is not null && version.StartsWith(">=", StringComparison.Ordinal))
            version = ">=" + NormalizeVersion(version[2..]);
        else if (version is not null)
            version = NormalizeVersion(version);
        return requirement with { Type = requirement.Type.Trim().ToLowerInvariant(), Name = requirement.Name.Trim().ToLowerInvariant(), Version = version,
            Scope = requirement.Scope?.Trim().ToLowerInvariant() };
    }

    private static string NormalizeVersion(string version) => string.Join('.', version.Split('.').Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static string IdFor(string name)
    {
        var id = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (id.Length == 0) id = "project-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
        return id.Length > 80 ? id[..80].TrimEnd('-') : id;
    }
}

public sealed class ProjectRevisionConflictException(long currentRevision)
    : Exception($"Project revision is stale; current revision is {currentRevision}.")
{
    public long CurrentRevision { get; } = currentRevision;
}

public sealed class ProjectInUseException(int queued, int assigned, int running)
    : Exception($"Project cannot be deleted while it has active execution requests (Queued: {queued}, Assigned: {assigned}, Running: {running}).")
{
    public int Queued { get; } = queued;
    public int Assigned { get; } = assigned;
    public int Running { get; } = running;
}

public sealed class ProjectDisabledException(string message = "Project is disabled and cannot accept new execution requests.")
    : Exception(message);

/// <summary>Versioned public registration request; intentionally independent of persistence entities.</summary>
public sealed record WorkerRegistrationRequest(int ContractVersion, string WorkerId, string DisplayName,
    string WorkerVersion, string Platform, int Capacity, IReadOnlyList<WorkerCapability> Capabilities,
    IReadOnlyList<CapabilityState>? CapabilityInventory = null)
{
    internal bool IsValid() => ContractVersion is 1 or 2 && Guid.TryParseExact(WorkerId, "N", out _) &&
        !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Length <= 200 &&
        !string.IsNullOrWhiteSpace(WorkerVersion) && WorkerVersion.Length <= 100 &&
        !string.IsNullOrWhiteSpace(Platform) && Platform.Length <= 300 && Capacity is >= 1 and <= 8 &&
        Capabilities is not null && Capabilities.Count <= 32 && Capabilities.All(ValidCapability) &&
        CapabilityCatalog.ValidInventory(CapabilityInventory);

    internal static bool ValidCapability(WorkerCapability value) => value is not null &&
        !string.IsNullOrWhiteSpace(value.Type) && value.Type.Length <= 40 &&
        !string.IsNullOrWhiteSpace(value.Name) && value.Name.Length <= 100 &&
        !value.Type.Any(char.IsControl) && !value.Name.Any(char.IsControl) &&
        (value.Version is null || (value.Version.Length <= 100 && !value.Version.Any(char.IsControl)));

}
public sealed record WorkerHeartbeatRequest(int ContractVersion, string WorkerId, string WorkerVersion,
    string LifecycleState, int ActiveExecutions, int MaximumCapacity, IReadOnlyList<WorkerCapability> Capabilities,
    IReadOnlyList<string> ActiveProjects, string? ConfigurationSynchronization = null, string? ConfigurationVersion = null, IReadOnlyList<CapabilityState>? CapabilityInventory = null, ManagedWorkerDiagnostics? ManagedDiagnostics = null, WorkerHostResources? HostResources = null);
public sealed record WorkerRegistrationResponse(int ContractVersion, string WorkerId, string DisplayName,
    string WorkerVersion, string Platform, int Capacity, IReadOnlyList<WorkerCapability> Capabilities,
    DateTimeOffset FirstRegisteredAtUtc, DateTimeOffset LastSeenAtUtc, string Availability,
    int ActiveExecutions, int MaximumCapacity, int AvailableCapacity, string LifecycleState,
    IReadOnlyList<string> ActiveProjects, DateTimeOffset? LastHeartbeatAtUtc = null,
    string? ConfigurationSynchronization = null, string? ConfigurationVersion = null, IReadOnlyList<CapabilityState>? CapabilityInventory = null,
    string SchedulingPolicy = WorkerSchedulingPolicy.Enabled, int ActiveAssignments = 0,
    string AuthenticationCredentialStatus = "not-configured", DateTimeOffset? AuthenticationCredentialRevokedAtUtc = null, ManagedWorkerDiagnostics? ManagedDiagnostics = null, WorkerHostResources? HostResources = null);

public static class WorkerSchedulingPolicy
{
    public const string Enabled = "Enabled";
    public const string Draining = "Draining";
    public const string Disabled = "Disabled";
    public static bool IsValid(string? value) => value is Enabled or Draining or Disabled;
}

public sealed record WorkerSchedulingPolicyRequest(string Policy);
public sealed record WorkerCredentialAccessStatus(string Status, DateTimeOffset? RevokedAtUtc = null);

/// <summary>A runtime, tool, or service currently available to a worker.</summary>
[JsonConverter(typeof(WorkerCapabilityJsonConverter))]
public sealed record WorkerCapability(string Type, string Name, string? Version = null, string? Scope = null) : CodexProvisioning.ICapabilityDescriptor;

/// <summary>Reads legacy string capabilities and the extensible structured capability contract.</summary>
public sealed class WorkerCapabilityJsonConverter : JsonConverter<WorkerCapability>
{
    public override WorkerCapability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var legacy = reader.GetString();
            if (string.IsNullOrWhiteSpace(legacy)) throw new JsonException("Capability names cannot be empty.");
            return new WorkerCapability("tool", legacy);
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A capability must be an object.");
        string? type = null;
        string? name = null;
        string? version = null;
        string? scope = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid capability object.");
            var property = reader.GetString()!;
            if (!seen.Add(property) || !reader.Read()) throw new JsonException("Capability fields cannot be duplicated.");
            switch (property)
            {
                case "type" when reader.TokenType == JsonTokenType.String: type = reader.GetString(); break;
                case "name" when reader.TokenType == JsonTokenType.String: name = reader.GetString(); break;
                case "version" when reader.TokenType is JsonTokenType.String or JsonTokenType.Null:
                    version = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "scope" when reader.TokenType is JsonTokenType.String or JsonTokenType.Null:
                    scope = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                default: throw new JsonException($"Capability field '{property}' is unknown or invalid.");
            }
        }
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name))
            throw new JsonException("A capability requires string type and name fields.");
        return new WorkerCapability(type, name, version, scope);
    }

    public override void Write(Utf8JsonWriter writer, WorkerCapability value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteString("name", value.Name);
        if (value.Version is not null) writer.WriteString("version", value.Version);
        if (value.Scope is not null) writer.WriteString("scope", value.Scope);
        writer.WriteEndObject();
    }
}

/// <summary>Creates the server's durable registry schema without coupling APIs to SQLite.</summary>
public sealed class SqliteRegistryStore(string databasePath, int staleAfterSeconds = 90, TimeProvider? timeProvider = null,
    int leaseDurationSeconds = 900, int leaseRenewalIntervalSeconds = 60, ILogger<SqliteRegistryStore>? logger = null) : IRegistryStore
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteRegistryStore>.Instance;

    private const string ExecutionSelect = "SELECT id, project_id, work_reference_json, created_at_utc, state, assigned_worker_id, assigned_at_utc, execution_id, assignment_id, current_stage, worker_execution_id, started_at_utc, completed_at_utc, duration_ms, validation_result, integration_result, failure_classification, recoverable, completion_summary, (SELECT worker_id FROM execution_leases l WHERE l.execution_id=execution_requests.id ORDER BY generation DESC LIMIT 1), (SELECT generation FROM execution_leases l WHERE l.execution_id=execution_requests.id ORDER BY generation DESC LIMIT 1), (SELECT acquired_at_utc FROM execution_leases l WHERE l.execution_id=execution_requests.id ORDER BY generation DESC LIMIT 1), (SELECT expires_at_utc FROM execution_leases l WHERE l.execution_id=execution_requests.id ORDER BY generation DESC LIMIT 1), (SELECT state FROM execution_leases l WHERE l.execution_id=execution_requests.id ORDER BY generation DESC LIMIT 1), recovery_state, recovery_reason, retry_of_execution_id, attempt_number, workspace_recovery, managed_eligibility_state, managed_eligibility_reasons_json, managed_eligibility_checked_at_utc FROM execution_requests";
    public const int CurrentSchemaVersion = 15;
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(staleAfterSeconds);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(leaseDurationSeconds);
    private readonly int _leaseRenewalIntervalSeconds = leaseRenewalIntervalSeconds;
    private static readonly JsonSerializerOptions ProjectJson = new(JsonSerializerDefaults.Web);
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidDataException("Database path must include a directory.");
        Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_metadata (
                    singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                    schema_version INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO schema_metadata (singleton, schema_version) VALUES (1, 15);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton = 1;";
            var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion is < 1 or > CurrentSchemaVersion)
                throw new InvalidDataException($"Database schema version {schemaVersion} is not supported; expected {CurrentSchemaVersion}.");
            if (schemaVersion == 1)
            {
                command.CommandText = "ALTER TABLE workers ADD COLUMN registration_json TEXT NULL; ALTER TABLE workers ADD COLUMN last_seen_at_utc TEXT NULL; UPDATE schema_metadata SET schema_version = 2 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 2;
            }
            if (schemaVersion == 2)
            {
                command.CommandText = "ALTER TABLE workers ADD COLUMN heartbeat_json TEXT NULL; UPDATE schema_metadata SET schema_version = 3 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 3;
            }
            if (schemaVersion == 3)
            {
                command.CommandText = "UPDATE schema_metadata SET schema_version = 4 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 4;
            }
            if (schemaVersion == 4)
            {
                command.CommandText = "ALTER TABLE execution_requests ADD COLUMN assignment_id TEXT NULL; UPDATE execution_requests SET assignment_id = id WHERE state IN ('Assigned', 'Running'); UPDATE schema_metadata SET schema_version = 5 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 5;
            }
            if (schemaVersion == 5)
            {
                command.CommandText = "ALTER TABLE execution_requests ADD COLUMN current_stage TEXT NULL; ALTER TABLE execution_requests ADD COLUMN worker_execution_id TEXT NULL; ALTER TABLE execution_requests ADD COLUMN started_at_utc TEXT NULL; ALTER TABLE execution_requests ADD COLUMN completed_at_utc TEXT NULL; ALTER TABLE execution_requests ADD COLUMN duration_ms INTEGER NULL; ALTER TABLE execution_requests ADD COLUMN validation_result TEXT NULL; ALTER TABLE execution_requests ADD COLUMN integration_result TEXT NULL; ALTER TABLE execution_requests ADD COLUMN failure_classification TEXT NULL; ALTER TABLE execution_requests ADD COLUMN recoverable INTEGER NOT NULL DEFAULT 0; ALTER TABLE execution_requests ADD COLUMN completion_summary TEXT NULL; UPDATE schema_metadata SET schema_version = 6 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 6;
            }
            if (schemaVersion == 6)
            {
                command.CommandText = "CREATE TABLE execution_leases (execution_id TEXT NOT NULL, generation INTEGER NOT NULL, worker_id TEXT NOT NULL, acquired_at_utc TEXT NOT NULL, expires_at_utc TEXT NOT NULL, state TEXT NOT NULL CHECK (state IN ('Active', 'Released')), PRIMARY KEY (execution_id, generation)); INSERT INTO execution_leases (execution_id, generation, worker_id, acquired_at_utc, expires_at_utc, state) SELECT id, 1, assigned_worker_id, assigned_at_utc, assigned_at_utc, CASE WHEN state IN ('Assigned', 'Running') THEN 'Active' ELSE 'Released' END FROM execution_requests WHERE assigned_worker_id IS NOT NULL AND assigned_at_utc IS NOT NULL; UPDATE schema_metadata SET schema_version = 7 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 7;
            }
            if (schemaVersion == 7)
            {
                command.CommandText = "ALTER TABLE execution_leases RENAME TO execution_leases_old; CREATE TABLE execution_leases (execution_id TEXT NOT NULL, generation INTEGER NOT NULL, worker_id TEXT NOT NULL, acquired_at_utc TEXT NOT NULL, expires_at_utc TEXT NOT NULL, state TEXT NOT NULL CHECK (state IN ('Active', 'Released', 'Expired')), PRIMARY KEY (execution_id, generation)); INSERT INTO execution_leases SELECT execution_id, generation, worker_id, acquired_at_utc, expires_at_utc, state FROM execution_leases_old; DROP TABLE execution_leases_old; UPDATE schema_metadata SET schema_version = 8 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 8;
            }
            if (schemaVersion == 8)
            {
                command.CommandText = "ALTER TABLE execution_requests ADD COLUMN recovery_state TEXT NULL; ALTER TABLE execution_requests ADD COLUMN recovery_reason TEXT NULL; ALTER TABLE execution_requests ADD COLUMN retry_of_execution_id TEXT NULL; ALTER TABLE execution_requests ADD COLUMN attempt_number INTEGER NOT NULL DEFAULT 1; UPDATE schema_metadata SET schema_version = 9 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 9;
            }
            if (schemaVersion == 9)
            {
                command.CommandText = "ALTER TABLE execution_requests ADD COLUMN workspace_recovery TEXT NULL; UPDATE schema_metadata SET schema_version = 10 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 10;
            }
            if (schemaVersion == 10)
            {
                command.CommandText = "UPDATE schema_metadata SET schema_version = 11 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 11;
            }
            if (schemaVersion == 11)
            {
                command.CommandText = "ALTER TABLE workers ADD COLUMN scheduling_policy TEXT NOT NULL DEFAULT 'Enabled' CHECK (scheduling_policy IN ('Enabled','Draining','Disabled')); UPDATE schema_metadata SET schema_version = 12 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 12;
            }
            if (schemaVersion == 12)
            {
                command.CommandText = """
                    ALTER TABLE execution_requests RENAME TO execution_requests_v12;
                    CREATE TABLE execution_requests (
                        queue_order INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        id TEXT NOT NULL UNIQUE,
                        project_id TEXT NOT NULL,
                        work_type TEXT NOT NULL,
                        work_id TEXT NOT NULL,
                        work_reference_json TEXT NOT NULL,
                        created_at_utc TEXT NOT NULL,
                        state TEXT NOT NULL CHECK (state IN ('Queued', 'Assigned', 'Running', 'Completed', 'Failed', 'Cancelled')),
                        assigned_worker_id TEXT NULL,
                        assigned_at_utc TEXT NULL,
                        execution_id TEXT NULL,
                        assignment_id TEXT NULL,
                        current_stage TEXT NULL,
                        worker_execution_id TEXT NULL,
                        started_at_utc TEXT NULL,
                        completed_at_utc TEXT NULL,
                        duration_ms INTEGER NULL,
                        validation_result TEXT NULL,
                        integration_result TEXT NULL,
                        failure_classification TEXT NULL,
                        recoverable INTEGER NOT NULL DEFAULT 0,
                        completion_summary TEXT NULL,
                        recovery_state TEXT NULL,
                        recovery_reason TEXT NULL,
                        retry_of_execution_id TEXT NULL,
                        attempt_number INTEGER NOT NULL DEFAULT 1,
                        workspace_recovery TEXT NULL
                    );
                    INSERT INTO execution_requests SELECT * FROM execution_requests_v12;
                    DROP TABLE execution_requests_v12;
                    UPDATE schema_metadata SET schema_version = 13 WHERE singleton = 1;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 13;
            }
            if (schemaVersion == 13)
            {
                command.CommandText = "ALTER TABLE execution_requests ADD COLUMN managed_eligibility_state TEXT NOT NULL DEFAULT 'eligible'; ALTER TABLE execution_requests ADD COLUMN managed_eligibility_reasons_json TEXT NULL; ALTER TABLE execution_requests ADD COLUMN managed_eligibility_checked_at_utc TEXT NULL; UPDATE schema_metadata SET schema_version = 14 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                schemaVersion = 14;
            }
            if (schemaVersion == 14)
            {
                command.CommandText = "ALTER TABLE worker_bootstrap_tokens ADD COLUMN worker_id TEXT NULL; ALTER TABLE worker_bootstrap_tokens ADD COLUMN operation TEXT NOT NULL DEFAULT 'enroll'; UPDATE schema_metadata SET schema_version = 15 WHERE singleton = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS workers (
                    worker_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    registered_at_utc TEXT NOT NULL,
                    status_json TEXT NULL,
                    registration_json TEXT NULL,
                    last_seen_at_utc TEXT NULL,
                    heartbeat_json TEXT NULL,
                    scheduling_policy TEXT NOT NULL DEFAULT 'Enabled' CHECK (scheduling_policy IN ('Enabled','Draining','Disabled'))
                );
                CREATE TABLE IF NOT EXISTS worker_bootstrap_tokens (token_hash TEXT NOT NULL PRIMARY KEY, expires_at_utc TEXT NOT NULL, consumed_at_utc TEXT NULL, worker_id TEXT NULL, operation TEXT NOT NULL DEFAULT 'enroll');
                CREATE TABLE IF NOT EXISTS worker_auth_tokens (worker_id TEXT NOT NULL PRIMARY KEY, token_hash TEXT NOT NULL, created_at_utc TEXT NOT NULL, revoked_at_utc TEXT NULL);
                CREATE TABLE IF NOT EXISTS projects (
                    project_id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    configuration_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_repository ON projects (lower(json_extract(configuration_json, '$.repository')));
                CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_name ON projects (lower(display_name));
                CREATE TABLE IF NOT EXISTS execution_metadata (
                    execution_id TEXT NOT NULL PRIMARY KEY,
                    worker_id TEXT NULL,
                    project_id TEXT NULL,
                    state TEXT NOT NULL,
                    metadata_json TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS execution_requests (
                    queue_order INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    id TEXT NOT NULL UNIQUE,
                    project_id TEXT NOT NULL,
                    work_type TEXT NOT NULL,
                    work_id TEXT NOT NULL,
                    work_reference_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    state TEXT NOT NULL CHECK (state IN ('Queued', 'Assigned', 'Running', 'Completed', 'Failed', 'Cancelled')),
                    assigned_worker_id TEXT NULL,
                    assigned_at_utc TEXT NULL,
                    execution_id TEXT NULL,
                    assignment_id TEXT NULL,
                    current_stage TEXT NULL,
                    worker_execution_id TEXT NULL,
                    started_at_utc TEXT NULL,
                    completed_at_utc TEXT NULL,
                    duration_ms INTEGER NULL,
                    validation_result TEXT NULL,
                    integration_result TEXT NULL,
                    failure_classification TEXT NULL,
                    recoverable INTEGER NOT NULL DEFAULT 0,
                    completion_summary TEXT NULL,
                    recovery_state TEXT NULL,
                    recovery_reason TEXT NULL,
                        retry_of_execution_id TEXT NULL,
                        attempt_number INTEGER NOT NULL DEFAULT 1,
                        workspace_recovery TEXT NULL,
                        managed_eligibility_state TEXT NOT NULL DEFAULT 'eligible',
                        managed_eligibility_reasons_json TEXT NULL,
                        managed_eligibility_checked_at_utc TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS execution_leases (
                    execution_id TEXT NOT NULL,
                    generation INTEGER NOT NULL,
                    worker_id TEXT NOT NULL,
                    acquired_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    state TEXT NOT NULL CHECK (state IN ('Active', 'Released', 'Expired')),
                    PRIMARY KEY (execution_id, generation)
                );
                CREATE TABLE IF NOT EXISTS provisioning_plans (
                    id TEXT NOT NULL PRIMARY KEY,
                    worker_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    state TEXT NOT NULL CHECK (state IN ('Pending', 'Accepted', 'Running', 'Completed', 'Failed', 'Cancelled', 'Rejected')),
                    actions_json TEXT NOT NULL,
                    current_action_id TEXT NULL,
                    started_at_utc TEXT NULL,
                    completed_at_utc TEXT NULL,
                    result TEXT NULL,
                    failure TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_provisioning_plans_worker_state ON provisioning_plans (worker_id, state, created_at_utc);
                CREATE INDEX IF NOT EXISTS ix_provisioning_plans_history ON provisioning_plans (created_at_utc DESC, id DESC);
                -- Preserve canonical repository identity for supported pre-existing requests that omitted a URL.
                -- Do this while the owning project still exists; later project edits/deletion must not reidentify history.
                UPDATE execution_requests SET work_reference_json = json_set(work_reference_json, '$.url',
                    'https://github.com/' || (SELECT json_extract(configuration_json, '$.repository') FROM projects
                        WHERE project_id=execution_requests.project_id) || '/issues/' || CAST(CAST(trim(work_id) AS INTEGER) AS TEXT))
                WHERE json_extract(work_reference_json, '$.url') IS NULL
                  AND lower(trim(work_type)) IN ('issue','github-issue')
                  AND trim(work_id) NOT GLOB '*[^0-9]*'
                  AND CAST(trim(work_id) AS INTEGER) BETWEEN 1 AND 2147483647
                  AND EXISTS (SELECT 1 FROM projects WHERE project_id=execution_requests.project_id);
                CREATE INDEX IF NOT EXISTS ix_execution_requests_issue_number ON execution_requests (CAST(trim(work_id) AS INTEGER));
                CREATE UNIQUE INDEX IF NOT EXISTS ux_execution_requests_active_work ON execution_requests (project_id, work_type, work_id) WHERE state IN ('Queued', 'Assigned', 'Running');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<ExecutionRequest> EnqueueExecutionAsync(EnqueueExecutionRequest request, CancellationToken cancellationToken = default) =>
        ServerOperationalDiagnostics.RunAsync(_logger, "enqueue", () => EnqueueExecutionCoreAsync(request, cancellationToken),
            cancellationToken, projectId: request?.ProjectId, work: request?.WorkReference);

    private async Task<ExecutionRequest> EnqueueExecutionCoreAsync(EnqueueExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var error = ExecutionRequestValidation.Error(request);
        if (error is not null) throw new InvalidDataException(error);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        var id = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();
        // Take SQLite's write lock before scanning canonical aliases so concurrent enqueues serialize.
        command.CommandText = "UPDATE projects SET project_id=project_id WHERE project_id=$projectId;";
        command.Parameters.AddWithValue("$projectId", request.ProjectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.Parameters.Clear();
        command.CommandText = "SELECT configuration_json, created_at_utc FROM projects WHERE project_id=$projectId;";
        command.Parameters.AddWithValue("$projectId", request.ProjectId);
        await using var projectReader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await projectReader.ReadAsync(cancellationToken)) throw new KeyNotFoundException($"Project '{request.ProjectId}' was not found.");
        var project = ToProject(request.ProjectId, projectReader.GetString(0), projectReader.GetString(1));
        await projectReader.DisposeAsync();
        if (!project.Enabled) throw new ProjectDisabledException();

        if (request.ExpectedProjectRevision is { } revision && revision != project.Revision ||
            request.ExpectedProjectCreatedAtUtc is { } createdAt && createdAt != project.CreatedAtUtc)
            throw new ProjectRevisionConflictException(project.Revision);
        if (request.AutomaticDiscovery && (request.ExpectedProjectRevision is null || request.ExpectedProjectCreatedAtUtc is null || project.AutomaticDiscovery?.Enabled != true))
            throw new ProjectDisabledException();
        var canonicalWork = ExecutionRequestValidation.Canonicalize(request.WorkReference, project.Repository);
        canonicalWork = canonicalWork with { Url = $"https://github.com/{project.Repository}/issues/{canonicalWork.Id}" };
        var diagnosticWork = canonicalWork;
        command.Parameters.Clear();
        command.CommandText = """
            SELECT id FROM execution_requests
            WHERE CAST(trim(work_id) AS INTEGER)=$number
              AND trim(work_id) NOT GLOB '*[^0-9]*'
              AND lower(trim(work_type)) IN ('issue','github-issue')
              AND (lower(json_extract(work_reference_json, '$.url'))=$url OR
                (json_extract(work_reference_json, '$.url') IS NULL AND project_id IN
                  (SELECT project_id FROM projects WHERE lower(json_extract(configuration_json, '$.repository'))=$repository)))
              AND ($automatic OR state IN ('Queued','Assigned','Running') OR
                (state='Failed' AND recovery_state='LeaseExpiredUncertain'))
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$number", int.Parse(canonicalWork.Id, System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$url", diagnosticWork.Url?.ToLowerInvariant() ?? throw new InvalidDataException("Canonical Issue URL is missing."));
        command.Parameters.AddWithValue("$repository", project.Repository.ToLowerInvariant());
        command.Parameters.AddWithValue("$automatic", request.AutomaticDiscovery);
        if (await command.ExecuteScalarAsync(cancellationToken) is string existingId)
        {
            ServerOperationalDiagnostics.Write(_logger, request.AutomaticDiscovery ? LogLevel.Debug : LogLevel.Information,
                "enqueue", request.AutomaticDiscovery ? "previously-enqueued" : "duplicate-active-work", request.ProjectId, diagnosticWork, existingId);
            throw new ExecutionRequestConflictException();
        }

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO execution_requests (id, project_id, work_type, work_id, work_reference_json, created_at_utc, state) VALUES ($id, $projectId, $workType, $workId, $workReference, $created, 'Queued');";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$projectId", request.ProjectId);
        command.Parameters.AddWithValue("$workType", canonicalWork.Type);
        command.Parameters.AddWithValue("$workId", canonicalWork.Id);
        command.Parameters.AddWithValue("$workReference", JsonSerializer.Serialize(canonicalWork, ProjectJson));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            ServerOperationalDiagnostics.Write(_logger, request.AutomaticDiscovery ? LogLevel.Debug : LogLevel.Information, "enqueue", "duplicate-active-work", request.ProjectId, diagnosticWork);
            throw new ExecutionRequestConflictException();
        }
        await transaction.CommitAsync(cancellationToken);
        ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "enqueue", "accepted", request.ProjectId, diagnosticWork, id);
        return new(id, request.ProjectId, canonicalWork, now, "Queued", null, null, null);
    }

    public async Task<ExecutionRequest?> UpdateManagedEligibilityAsync(string executionRequestId, ManagedEligibilityUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (update is null || update.State is not ("eligible" or "blocked" or "unavailable") || update.Reasons is null ||
            update.Reasons.Count > 10 || update.Reasons.Any(reason => !Printable(reason, 300)))
            throw new InvalidDataException("Managed eligibility update is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var read = connection.CreateCommand();
        read.Transaction = (SqliteTransaction)transaction;
        read.CommandText = ExecutionSelect + " WHERE id=$id;";
        read.Parameters.AddWithValue("$id", executionRequestId);
        ExecutionRequest? previous;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            previous = await reader.ReadAsync(cancellationToken) ? ReadExecution(reader) : null;
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE execution_requests SET managed_eligibility_state=$state, managed_eligibility_reasons_json=$reasons, managed_eligibility_checked_at_utc=$checked WHERE id=$id AND state IN ('Queued','Assigned');";
        command.Parameters.AddWithValue("$state", update.State);
        command.Parameters.AddWithValue("$reasons", JsonSerializer.Serialize(update.Reasons, ProjectJson));
        command.Parameters.AddWithValue("$checked", update.CheckedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", executionRequestId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 0)
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = (SqliteTransaction)transaction;
            exists.CommandText = "SELECT 1 FROM execution_requests WHERE id=$id;";
            exists.Parameters.AddWithValue("$id", executionRequestId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        if (updated > 0 && previous is not null && previous.ManagedEligibilityState != update.State)
            ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "queue-eligibility", "eligibility-" + update.State,
                previous.ProjectId, previous.WorkReference, previous.Id, previous.AssignedWorkerId, previous.AssignmentId, previous.Lease?.Generation);
        return await GetExecutionAsync(executionRequestId, cancellationToken);
    }

    public async Task<ExecutionRequest?> RejectManagedAssignmentAsync(string executionRequestId, string assignmentId, string workerId,
        ManagedEligibilityUpdate update, CancellationToken cancellationToken = default)
    {
        if (update is null || update.State is not ("blocked" or "unavailable") || update.Reasons is null ||
            update.Reasons.Count > 10 || update.Reasons.Any(reason => !Printable(reason, 300)) ||
            !Printable(executionRequestId, 80) || !Printable(assignmentId, 80) || !Printable(workerId, 128))
            throw new InvalidDataException("Managed assignment rejection is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE execution_requests SET state='Queued', assigned_worker_id=NULL, assigned_at_utc=NULL, assignment_id=NULL, managed_eligibility_state=$eligibility, managed_eligibility_reasons_json=$reasons, managed_eligibility_checked_at_utc=$checked WHERE id=$id AND state='Assigned' AND assigned_worker_id=$worker AND assignment_id=$assignment AND execution_id IS NULL;";
        command.Parameters.AddWithValue("$eligibility", update.State);
        command.Parameters.AddWithValue("$reasons", JsonSerializer.Serialize(update.Reasons, ProjectJson));
        command.Parameters.AddWithValue("$checked", update.CheckedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", executionRequestId);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$assignment", assignmentId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        command.Parameters.Clear();
        command.CommandText = "UPDATE execution_leases SET state='Released' WHERE execution_id=$id AND generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=$id) AND worker_id=$worker AND state='Active';";
        command.Parameters.AddWithValue("$id", executionRequestId);
        command.Parameters.AddWithValue("$worker", workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var rejected = await GetExecutionAsync(executionRequestId, cancellationToken);
        ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "assignment", "eligibility-" + update.State,
            rejected?.ProjectId, rejected?.WorkReference, executionRequestId, workerId, assignmentId, rejected?.Lease?.Generation);
        return rejected;
    }

    public async Task<IReadOnlyList<ExecutionRequest>> ListQueuedByIssueAsync(string projectId, string issueNumber,
        CancellationToken cancellationToken = default)
    {
        if (!Printable(projectId, 80) || !int.TryParse(issueNumber, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new InvalidDataException("Queued Issue query is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ExecutionSelect + " WHERE project_id=$project AND state='Queued' AND work_type='github-issue' AND work_id=$issue ORDER BY queue_order;";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$issue", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = new List<ExecutionRequest>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadExecution(reader));
        return result;
    }

    public Task<WorkAssignmentResponse> RequestAssignmentAsync(WorkerAssignmentRequest request, CancellationToken cancellationToken = default) =>
        ServerOperationalDiagnostics.RunAsync(_logger, "assignment", () => RequestAssignmentCoreAsync(request, cancellationToken),
            cancellationToken, workerId: request?.WorkerId);

    private async Task<WorkAssignmentResponse> RequestAssignmentCoreAsync(WorkerAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !Printable(request.WorkerId, 128) || request.AvailableCapacity is < 0 or > 8 ||
            request.ProjectCapacities is null || request.ProjectCapacities.Count > 128 ||
            request.ProjectCapacities.Any(p => !Printable(p.Key, 80) || p.Value is < 0 or > 8) ||
            request.IntegrationRecoveries is { Count: > 128 } || request.IntegrationRecoveries?.Any(item => item is null ||
                !Printable(item.ProjectId, 80) || !Guid.TryParse(item.ServerExecutionId, out _) ||
                item.Kind is not ("Integration" or "Codex") || !Guid.TryParse(item.WorkerExecutionId, out _) || item.IntegrationBase is null || !Regex.IsMatch(item.IntegrationBase, "^(?:[0-9a-f]{40}|[0-9a-f]{64})$")) == true)
            throw new InvalidDataException("Worker assignment request contract is invalid.");
        if (!request.WorkerEnabled) return NoAssignment(request.WorkerId, "worker-disabled-or-no-capacity");
        if (request.AvailableCapacity == 0) return NoAssignment(request.WorkerId, "worker-capacity");
        if (request.ProjectCapacities.Count == 0 || request.ProjectCapacities.All(p => p.Value == 0))
            return NoAssignment(request.WorkerId, "no-eligible-work");

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        // Serialize admission before reading project policy and fleet reservations, including other store instances.
        command.CommandText = "UPDATE workers SET worker_id=worker_id WHERE worker_id=$worker;";
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.Parameters.Clear();
        command.CommandText = "SELECT heartbeat_json, last_seen_at_utc, scheduling_policy FROM workers WHERE worker_id = $worker AND registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        string? heartbeatJson;
        DateTimeOffset? lastSeen;
        string schedulingPolicy;
        await using (var workerReader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await workerReader.ReadAsync(cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return NoAssignment(request.WorkerId, "worker-unregistered");
            }
            heartbeatJson = workerReader.IsDBNull(0) ? null : workerReader.GetString(0);
            lastSeen = workerReader.IsDBNull(1) ? null : DateTimeOffset.Parse(workerReader.GetString(1));
            schedulingPolicy = workerReader.GetString(2);
        }
        if (schedulingPolicy != WorkerSchedulingPolicy.Enabled)
        {
            await transaction.CommitAsync(cancellationToken);
            return NoAssignment(request.WorkerId, "scheduling-paused");
        }
        var heartbeat = heartbeatJson is null ? null : JsonSerializer.Deserialize<WorkerHeartbeatRequest>(heartbeatJson);
        if (heartbeat is null || lastSeen is null || _timeProvider.GetUtcNow() - lastSeen > _staleAfter ||
            heartbeat.LifecycleState != "running" ||
            heartbeat.CapabilityInventory is { } inventory && !CapabilityCatalog.ExecutionReadiness(inventory).Available)
        {
            await transaction.CommitAsync(cancellationToken);
            return NoAssignment(request.WorkerId, "worker-not-ready");
        }

        command.Parameters.Clear();
        // Heartbeat and request capacities already account for Running work; only reservations not yet
        // reflected by the Worker consume additional slots here.
        command.CommandText = "SELECT COUNT(*) FROM execution_requests WHERE assigned_worker_id = $worker AND state = 'Assigned';";
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        var assigned = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (assigned >= Math.Min(request.AvailableCapacity, heartbeat.MaximumCapacity - heartbeat.ActiveExecutions))
        {
            await transaction.CommitAsync(cancellationToken);
            return NoAssignment(request.WorkerId, "worker-capacity");
        }

        var registration = await ReadWorkerRegistrationAsync(command, request.WorkerId, cancellationToken);
        var workerCapabilities = heartbeat.Capabilities ?? registration?.Capabilities ?? [];
        var projects = await ReadProjectsByIdAsync(command, cancellationToken);

        var recoveryRejections = await QueueIntegrationRecoveriesAsync(command, request, projects, cancellationToken);
        var issueReservations = new Dictionary<(string ProjectId, string WorkType, string WorkId), string>();
        command.Parameters.Clear();
        command.CommandText = "SELECT id, project_id, work_reference_json FROM execution_requests WHERE state IN ('Queued','Assigned','Running') OR (state='Failed' AND recovery_state='LeaseExpiredUncertain') ORDER BY queue_order;";
        await using (var reservationsReader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reservationsReader.ReadAsync(cancellationToken))
            {
                var projectId = reservationsReader.GetString(1);
                if (!projects.ContainsKey(projectId)) continue;
                var work = JsonSerializer.Deserialize<WorkReference>(reservationsReader.GetString(2), ProjectJson);
                if (work is null) continue;
                try
                {
                    var canonical = ExecutionRequestValidation.NormalizeIdentity(work);
                    issueReservations.TryAdd((projectId, canonical.Type, canonical.Id), reservationsReader.GetString(0));
                }
                catch (InvalidDataException) { }
            }
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT id, project_id, work_reference_json, created_at_utc, workspace_recovery FROM execution_requests WHERE state = 'Queued' AND managed_eligibility_state!='blocked' ORDER BY queue_order;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var queued = new List<(string Id, string ProjectId, WorkReference Work, string Created, string? WorkspaceRecovery)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var projectId = reader.GetString(1);
            if (!request.ProjectCapacities.TryGetValue(projectId, out var projectCapacity) || projectCapacity <= 0) continue;
            queued.Add((reader.GetString(0), projectId,
                JsonSerializer.Deserialize<WorkReference>(reader.GetString(2), ProjectJson) ?? throw new InvalidDataException("Stored work reference is invalid."),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        await reader.DisposeAsync();
        var projectCapacityBlocked = false;
        (string Id, string ProjectId, WorkReference Work, string Created)? candidate = null;
        IReadOnlyDictionary<string, string>? recoveryMetadata = null;
        foreach (var queuedItem in queued)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT worker_id, metadata_json FROM execution_metadata WHERE execution_id=$id AND state='IntegrationRecovery';";
            command.Parameters.AddWithValue("$id", queuedItem.Id);
            await using (var metadataReader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await metadataReader.ReadAsync(cancellationToken))
                {
                    if (metadataReader.GetString(0) != request.WorkerId) continue;
                    try { recoveryMetadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataReader.GetString(1)); }
                    catch (JsonException) { recoveryMetadata = null; }
                }
                else recoveryMetadata = null;
            }
            if (queuedItem.WorkspaceRecovery == "ReturnedCodexWorkspaceRequired") continue;
            if (queuedItem.WorkspaceRecovery is "PreservedIntegrationImplementation" or "PreservedCodexImplementation" &&
                (recoveryMetadata is null || !recoveryMetadata.TryGetValue(queuedItem.WorkspaceRecovery == "PreservedCodexImplementation" ? "codexRecoveryExecutionId" : "integrationRecoveryExecutionId", out var localId) ||
                    !Guid.TryParse(localId, out _) || !recoveryMetadata.TryGetValue("originalServerExecutionId", out var originalId) ||
                    !Guid.TryParse(originalId, out _) || !recoveryMetadata.ContainsKey("integrationBase")))
            {
                command.Parameters.Clear();
                command.CommandText = "UPDATE execution_requests SET managed_eligibility_state='blocked', managed_eligibility_reasons_json=$reason WHERE id=$id AND state='Queued';";
                command.Parameters.AddWithValue("$id", queuedItem.Id);
                command.Parameters.AddWithValue("$reason", JsonSerializer.Serialize(new[] { "Preserved integration recovery ownership metadata is missing or corrupt; human reconciliation is required." }));
                await command.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }
            if (!projects.TryGetValue(queuedItem.ProjectId, out var candidateProject) ||
                !candidateProject.Enabled ||
                !WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(candidateProject), workerCapabilities, heartbeat.CapabilityInventory).IsEligible) continue;
            WorkReference canonicalWork;
            try { canonicalWork = ExecutionRequestValidation.Canonicalize(queuedItem.Work, candidateProject.Repository); }
            catch (InvalidDataException) { continue; }
            if (!issueReservations.TryGetValue((queuedItem.ProjectId, canonicalWork.Type, canonicalWork.Id), out var reservationId) ||
                reservationId != queuedItem.Id) continue;
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM execution_requests WHERE project_id = $project AND (state IN ('Assigned','Running') OR (state='Failed' AND recovery_state='LeaseExpiredUncertain'));";
            command.Parameters.AddWithValue("$project", queuedItem.ProjectId);
            var activeForProject = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (candidateProject.MaxParallelTasks is { } limit && activeForProject >= limit)
            {
                projectCapacityBlocked = true;
                continue;
            }
            candidate = (queuedItem.Id, queuedItem.ProjectId, canonicalWork, queuedItem.Created);
            break;
        }
        if (candidate is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return NoAssignment(request.WorkerId, projectCapacityBlocked ? "project-capacity" : "no-eligible-work", recoveryRejections);
        }

        var now = _timeProvider.GetUtcNow();
        var expires = now.Add(_leaseDuration);
        var assignmentId = Guid.NewGuid().ToString("N");
        command.Parameters.Clear();
        command.CommandText = "UPDATE execution_requests SET state = 'Assigned', assigned_worker_id = $worker, assigned_at_utc = $now, assignment_id = $assignment WHERE id = $id AND state = 'Queued' AND EXISTS (SELECT 1 FROM workers WHERE worker_id=$worker AND scheduling_policy='Enabled') AND EXISTS (SELECT 1 FROM projects p WHERE p.project_id = execution_requests.project_id AND COALESCE(json_extract(p.configuration_json, '$.enabled'), 1) = 1);";
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$assignment", assignmentId);
        command.Parameters.AddWithValue("$id", candidate.Value.Id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return NoAssignment(request.WorkerId, "assignment-race", recoveryRejections);
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COALESCE(MAX(generation), 0) + 1 FROM execution_leases WHERE execution_id = $id;";
        command.Parameters.AddWithValue("$id", candidate.Value.Id);
        var generation = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO execution_leases (execution_id, generation, worker_id, acquired_at_utc, expires_at_utc, state) VALUES ($id, $generation, $worker, $acquired, $expires, 'Active');";
        command.Parameters.AddWithValue("$id", candidate.Value.Id);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$worker", request.WorkerId);
        command.Parameters.AddWithValue("$acquired", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.Parameters.Clear();
        command.CommandText = "SELECT project_id, configuration_json, created_at_utc FROM projects WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", candidate.Value.ProjectId);
        await using var projectReader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await projectReader.ReadAsync(cancellationToken)) throw new InvalidDataException("Assigned project was removed during assignment.");
        var project = ToProject(projectReader.GetString(0), projectReader.GetString(1), projectReader.GetString(2));
        await projectReader.DisposeAsync();
        var assignedExecution = await ReadExecutionInTransactionAsync(connection, (SqliteTransaction)transaction, candidate.Value.Id, cancellationToken);
        ExecutionRequest? interrupted = null;
        if (assignedExecution is { WorkspaceRecovery: "FreshWorkspaceRequired", RetryOfExecutionId: { } previousId })
            interrupted = await ReadExecutionInTransactionAsync(connection, (SqliteTransaction)transaction, previousId, cancellationToken);
        var freshRecovery = assignedExecution is not null && interrupted is { State: "Failed", RecoveryState: "LeaseExpiredRequeued", IntegrationResult: null,
            CurrentStage: "Preparing" or "Codex" or "Validation", Lease.State: "Expired" } &&
            interrupted.ProjectId == assignedExecution.ProjectId && interrupted.WorkReference == assignedExecution.WorkReference &&
            interrupted.AttemptNumber + 1 == assignedExecution.AttemptNumber &&
            string.Equals(interrupted.WorkReference.Url, $"https://github.com/{project.Repository}/issues/{assignedExecution.WorkReference.Id}",
                StringComparison.OrdinalIgnoreCase);
        await transaction.CommitAsync(cancellationToken);
        var metadata = recoveryMetadata is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(recoveryMetadata, StringComparer.Ordinal);
        metadata["assignedAtUtc"] = now.ToString("O");
        if (freshRecovery && interrupted is not null)
            metadata["freshRecoveryExecutionId"] = interrupted.Id;
        ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "assignment", "lease-acquired", project.Id, candidate.Value.Work with { Url = $"https://github.com/{project.Repository}/issues/{candidate.Value.Work.Id}" }, candidate.Value.Id, request.WorkerId, assignmentId, generation);
        return new(true, new WorkAssignment(assignmentId, candidate.Value.Id, project, candidate.Value.Work, request.WorkerId, metadata,
            new ExecutionLease(candidate.Value.Id, request.WorkerId, generation, now, expires, "Active", _leaseRenewalIntervalSeconds)), recoveryRejections);
    }

    private WorkAssignmentResponse NoAssignment(string workerId, string reason,
        IReadOnlyDictionary<string, string>? recoveryRejections = null)
    {
        ServerOperationalDiagnostics.Write(_logger, LogLevel.Debug, "assignment", reason, workerId: workerId);
        return new(false, null, recoveryRejections, reason);
    }

    private async Task<IReadOnlyDictionary<string, string>> QueueIntegrationRecoveriesAsync(SqliteCommand command, WorkerAssignmentRequest request,
        IReadOnlyDictionary<string, CentralProject> projects, CancellationToken ct)
    {
        var rejections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var recovery in request.IntegrationRecoveries ?? [])
        {
            if (!request.ProjectCapacities.TryGetValue(recovery.ProjectId, out var capacity) || capacity <= 0 ||
                !projects.TryGetValue(recovery.ProjectId, out var project) || !project.Enabled) continue;
            if (recovery.Kind == "Codex")
                await AttachReturnedCodexWorkspaceAsync(command, request.WorkerId, recovery, _timeProvider.GetUtcNow(), ct);
            command.Parameters.Clear();
            // Server outcome and node ownership must independently corroborate the local candidate.
            // Existing reservations include uncertain integration, so another Worker cannot take over its workspace.
            command.CommandText = """
                SELECT work_type, work_id, work_reference_json,
                    (SELECT MAX(attempt_number) FROM execution_requests other WHERE other.project_id=source.project_id AND other.work_id=source.work_id)
                FROM execution_requests source
                WHERE id=$source AND project_id=$project AND assigned_worker_id=$worker AND worker_execution_id=$local
                    AND state='Failed' AND failure_classification=$classification AND recoverable=1 AND recovery_state IS NULL
                    AND NOT EXISTS (SELECT 1 FROM execution_requests other WHERE other.project_id=source.project_id
                        AND other.work_id=source.work_id AND (other.state IN ('Queued','Assigned','Running','Completed')
                            OR other.recovery_state='LeaseExpiredUncertain'))
                    AND NOT EXISTS (SELECT 1 FROM execution_metadata metadata WHERE metadata.state='IntegrationRecovery'
                        AND CASE WHEN json_valid(metadata.metadata_json) THEN COALESCE(json_extract(metadata.metadata_json,'$.integrationRecoveryExecutionId'),json_extract(metadata.metadata_json,'$.codexRecoveryExecutionId')) END=$local
                        AND ($kind='Codex' OR CASE WHEN json_valid(metadata.metadata_json) THEN json_extract(metadata.metadata_json,'$.integrationBase') END=$base))
                """;
            command.Parameters.AddWithValue("$source", recovery.ServerExecutionId);
            command.Parameters.AddWithValue("$project", recovery.ProjectId);
            command.Parameters.AddWithValue("$worker", request.WorkerId);
            command.Parameters.AddWithValue("$local", recovery.WorkerExecutionId);
            command.Parameters.AddWithValue("$base", recovery.IntegrationBase);
            command.Parameters.AddWithValue("$kind", recovery.Kind);
            command.Parameters.AddWithValue("$classification", recovery.Kind == "Codex" ? "CodexInterruption" : "IntegrationConflict");
            string type, workId, workJson;
            int attempt;
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct))
                {
                    await reader.DisposeAsync();
                    command.Parameters.Clear();
                    command.CommandText = "SELECT state, assigned_worker_id, worker_execution_id, failure_classification, recoverable, recovery_state FROM execution_requests WHERE id=$source AND project_id=$project;";
                    command.Parameters.AddWithValue("$source", recovery.ServerExecutionId);
                    command.Parameters.AddWithValue("$project", recovery.ProjectId);
                    await using var sourceReader = await command.ExecuteReaderAsync(ct);
                    string? reason = null;
                    if (!await sourceReader.ReadAsync(ct)) reason = "The original Server execution is missing for this project; reconcile Server ownership before recovery.";
                    else if (sourceReader.IsDBNull(1) || sourceReader.GetString(1) != request.WorkerId ||
                        sourceReader.IsDBNull(2) || sourceReader.GetString(2) != recovery.WorkerExecutionId)
                        reason = "The original Server execution does not corroborate this Worker's local execution identity.";
                    else if (sourceReader.GetString(0) != "Failed" || sourceReader.IsDBNull(3) || sourceReader.GetString(3) != (recovery.Kind == "Codex" ? "CodexInterruption" : "IntegrationConflict") ||
                        !sourceReader.GetBoolean(4) || !sourceReader.IsDBNull(5))
                        reason = "The original Server execution is not a verified recoverable execution interruption/conflict; inspect its outcome and lease reconciliation state.";
                    if (reason is not null) rejections[recovery.WorkerExecutionId] = reason;
                    continue;
                }
                type = reader.GetString(0); workId = reader.GetString(1); workJson = reader.GetString(2); attempt = reader.GetInt32(3) + 1;
            }
            var id = Guid.NewGuid().ToString("N");
            var now = _timeProvider.GetUtcNow().ToString("O");
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO execution_requests (id,project_id,work_type,work_id,work_reference_json,created_at_utc,state,
                    retry_of_execution_id,attempt_number,workspace_recovery)
                VALUES ($id,$project,$type,$workId,$work,$now,'Queued',$source,$attempt,$workspaceRecovery);
                INSERT INTO execution_metadata (execution_id,worker_id,project_id,state,metadata_json,updated_at_utc)
                VALUES ($id,$worker,$project,'IntegrationRecovery',$metadata,$now);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$project", recovery.ProjectId);
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$workId", workId);
            command.Parameters.AddWithValue("$work", workJson);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$source", recovery.ServerExecutionId);
            command.Parameters.AddWithValue("$attempt", attempt);
            command.Parameters.AddWithValue("$worker", request.WorkerId);
            command.Parameters.AddWithValue("$workspaceRecovery", recovery.Kind == "Codex" ? "PreservedCodexImplementation" : "PreservedIntegrationImplementation");
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [recovery.Kind == "Codex" ? "codexRecoveryExecutionId" : "integrationRecoveryExecutionId"] = recovery.WorkerExecutionId,
                ["integrationBase"] = recovery.IntegrationBase,
                ["originalServerExecutionId"] = recovery.ServerExecutionId
            }));
            await command.ExecuteNonQueryAsync(ct);
        }
        return rejections;
    }

    /// <summary>When the original node returns after pre-integration lease expiry, reuse the still-queued
    /// retry under a NEW lease. Never take work back from another active/completed assignment.</summary>
    private static async Task AttachReturnedCodexWorkspaceAsync(SqliteCommand command, string workerId,
        IntegrationRecoveryCandidate recovery, DateTimeOffset now, CancellationToken ct)
    {
        command.Parameters.Clear();
        command.CommandText = """
            SELECT retry.id FROM execution_requests retry JOIN execution_requests source ON source.id=retry.retry_of_execution_id
            WHERE source.id=$source AND source.project_id=$project AND source.assigned_worker_id=$worker
                AND source.worker_execution_id=$local AND source.state='Failed' AND source.recovery_state='LeaseExpiredRequeued'
                AND source.current_stage IN ('Codex','Validation') AND source.integration_result IS NULL
                AND retry.state='Queued' AND retry.workspace_recovery IN ('FreshWorkspaceRequired','ReturnedCodexWorkspaceRequired')
                AND NOT EXISTS (SELECT 1 FROM execution_requests other WHERE other.project_id=source.project_id
                    AND other.work_id=source.work_id AND other.id!=retry.id
                    AND (other.state IN ('Queued','Assigned','Running','Completed') OR other.recovery_state='LeaseExpiredUncertain'))
            """;
        command.Parameters.AddWithValue("$source", recovery.ServerExecutionId);
        command.Parameters.AddWithValue("$project", recovery.ProjectId);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$local", recovery.WorkerExecutionId);
        var retryId = await command.ExecuteScalarAsync(ct) as string;
        if (retryId is null) return;
        command.Parameters.Clear();
        command.CommandText = """
            UPDATE execution_requests SET workspace_recovery='PreservedCodexImplementation' WHERE id=$retry AND state='Queued';
            UPDATE execution_requests SET failure_classification='CodexInterruption', recoverable=1, recovery_state=NULL,
                recovery_reason='Original Worker returned with verified interrupted implementation; continuation requires a new lease.' WHERE id=$source;
            INSERT INTO execution_metadata (execution_id,worker_id,project_id,state,metadata_json,updated_at_utc)
                VALUES ($retry,$worker,$project,'IntegrationRecovery',$metadata,$now);
            """;
        command.Parameters.AddWithValue("$retry", retryId);
        command.Parameters.AddWithValue("$source", recovery.ServerExecutionId);
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$project", recovery.ProjectId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["codexRecoveryExecutionId"] = recovery.WorkerExecutionId,
            ["originalServerExecutionId"] = recovery.ServerExecutionId,
            ["integrationBase"] = recovery.IntegrationBase
        }));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ExecutionRequest>> GetExecutionsAsync(CancellationToken cancellationToken = default)
    {
        await ExpireLeasesAsync(cancellationToken);
        var projects = (await GetProjectsAsync(cancellationToken)).ToDictionary(project => project.Id, StringComparer.Ordinal);
        var workers = await GetWorkersAsync(cancellationToken);
        var projectActivity = await ReadProjectActivityAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ExecutionSelect + " ORDER BY queue_order;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ExecutionRequest>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var execution = ReadExecution(reader);
            result.Add(execution.State == "Queued"
                ? execution with { PendingReason = PendingReasonFor(execution, projects, workers, projectActivity),
                    MissingRequirements = MissingFor(execution, projects, workers) }
                : execution);
        }
        return result;
    }

    public async Task<IReadOnlyList<ExecutionRequest>> ListExecutionsAsync(ExecutionQuery query,
        CancellationToken cancellationToken = default)
    {
        var error = ExecutionAdministrationValidation.QueryError(query);
        if (error is not null) throw new InvalidDataException(error);
        await ExpireLeasesAsync(cancellationToken);
        var workType = query.WorkType is null ? null : "github-issue";
        var workId = query.WorkId is null ? null : int.Parse(query.WorkId.Trim(), System.Globalization.CultureInfo.InvariantCulture)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ExecutionSelect + " WHERE ($project IS NULL OR project_id=$project) AND ($state IS NULL OR state=$state) AND ($type IS NULL OR work_type IN ('issue','github-issue')) AND ($workId IS NULL OR work_id=$workId OR (work_type IN ('issue','github-issue') AND work_id NOT GLOB '*[^0-9]*' AND CAST(work_id AS INTEGER)=CAST($workId AS INTEGER))) ORDER BY queue_order DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$project", (object?)query.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", (object?)query.State ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", (object?)workType ?? DBNull.Value);
        command.Parameters.AddWithValue("$workId", (object?)workId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);
        var result = new List<ExecutionRequest>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadExecution(reader));
        return await DecorateQueuedExecutionsAsync(result, cancellationToken);
    }

    public async Task<ExecutionRequest?> GetExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default)
    {
        await ExpireLeasesAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ExecutionSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", executionRequestId);
        ExecutionRequest? result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            result = await reader.ReadAsync(cancellationToken) ? ReadExecution(reader) : null;
        return result is null ? null : (await DecorateQueuedExecutionsAsync([result], cancellationToken))[0];
    }

    private async Task<IReadOnlyList<ExecutionRequest>> DecorateQueuedExecutionsAsync(IReadOnlyList<ExecutionRequest> executions,
        CancellationToken cancellationToken)
    {
        if (!executions.Any(execution => execution.State == "Queued")) return executions;
        var projects = (await GetProjectsAsync(cancellationToken)).ToDictionary(project => project.Id, StringComparer.Ordinal);
        var workers = await GetWorkersAsync(cancellationToken);
        var projectActivity = await ReadProjectActivityAsync(cancellationToken);
        return executions.Select(execution => execution.State == "Queued"
            ? execution with { PendingReason = PendingReasonFor(execution, projects, workers, projectActivity),
                MissingRequirements = MissingFor(execution, projects, workers) }
            : execution).ToArray();
    }

    public Task<ExecutionRequest?> CancelQueuedExecutionAsync(string executionRequestId, CancellationToken cancellationToken = default) =>
        ServerOperationalDiagnostics.RunAsync(_logger, "queue-cancel", () => CancelQueuedExecutionCoreAsync(executionRequestId, cancellationToken),
            cancellationToken, executionId: executionRequestId);

    private async Task<ExecutionRequest?> CancelQueuedExecutionCoreAsync(string executionRequestId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE execution_requests SET state='Cancelled', completed_at_utc=$now, completion_summary='Cancelled by Server operator before assignment.' WHERE id=$id AND state='Queued';";
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$id", executionRequestId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT 1 FROM execution_requests WHERE id=$id;";
            command.Parameters.AddWithValue("$id", executionRequestId);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            throw new ExecutionRequestCancellationException();
        }
        var result = await ReadExecutionInTransactionAsync(connection, (SqliteTransaction)transaction, executionRequestId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "queue-cancel", "operator-cancelled", result?.ProjectId, result?.WorkReference, executionRequestId);
        return result;
    }

    public async Task<ExecutionReconciliationResult?> ReconcileUncertainExecutionAsync(string executionRequestId,
        ExecutionReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        var error = ExecutionAdministrationValidation.ReconciliationError(request);
        if (error is not null) throw new InvalidDataException(error);
        await ExpireLeasesAsync(cancellationToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = ExecutionSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", executionRequestId);
        ExecutionRequest? source;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            source = await reader.ReadAsync(cancellationToken) ? ReadExecution(reader) : null;
        if (source is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (source.State != "Failed" || source.RecoveryState != "LeaseExpiredUncertain" || source.Lease?.State != "Expired")
            throw new ExecutionRequestReconciliationException("Only an expired uncertain execution can be reconciled by an operator.");

        // Serialize the reservation check and retry insertion with concurrent enqueue operations.
        command.Parameters.Clear();
        command.CommandText = "UPDATE projects SET project_id=project_id WHERE project_id=$projectId;";
        command.Parameters.AddWithValue("$projectId", source.ProjectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.Parameters.Clear();
        command.CommandText = "SELECT configuration_json, created_at_utc FROM projects WHERE project_id=$projectId;";
        command.Parameters.AddWithValue("$projectId", source.ProjectId);
        CentralProject project;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new ExecutionRequestReconciliationException("The execution project no longer exists.");
            project = ToProject(source.ProjectId, reader.GetString(0), reader.GetString(1));
        }
        WorkReference canonicalWork;
        try { canonicalWork = ExecutionRequestValidation.Canonicalize(source.WorkReference, project.Repository); }
        catch (InvalidDataException ex) { throw new ExecutionRequestReconciliationException(ex.Message); }

        var evidence = SecretSanitizer.Sanitize(request.Evidence.Trim(), 1000);
        var resolvedAt = _timeProvider.GetUtcNow();
        ExecutionRequest? retry = null;
        if (request.Disposition == "NotIntegrated")
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT id, work_reference_json, attempt_number FROM execution_requests WHERE project_id=$projectId AND id!=$sourceId AND (state IN ('Queued','Assigned','Running') OR (state='Failed' AND recovery_state='LeaseExpiredUncertain'));";
            command.Parameters.AddWithValue("$projectId", source.ProjectId);
            command.Parameters.AddWithValue("$sourceId", source.Id);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var existing = JsonSerializer.Deserialize<WorkReference>(reader.GetString(1), ProjectJson)
                        ?? throw new InvalidDataException("Stored work reference is invalid.");
                    try
                    {
                        var existingCanonical = ExecutionRequestValidation.NormalizeIdentity(existing);
                        if (existingCanonical.Type == canonicalWork.Type && existingCanonical.Id == canonicalWork.Id)
                            throw new ExecutionRequestReconciliationException("An active request already reserves this GitHub Issue.");
                    }
                    catch (InvalidDataException)
                    {
                        // Keep unsupported historical references visible without treating them as GitHub Issue reservations.
                    }
                }
            }

            command.Parameters.Clear();
            command.CommandText = "UPDATE execution_requests SET recovery_state='OperatorRetryQueued', recovery_reason=$reason WHERE id=$id AND state='Failed' AND recovery_state='LeaseExpiredUncertain' AND EXISTS (SELECT 1 FROM execution_leases WHERE execution_id=$id AND generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=$id) AND state='Expired');";
            command.Parameters.AddWithValue("$reason", $"Operator verified no integration occurred at {resolvedAt:O}. Evidence: {evidence}");
            command.Parameters.AddWithValue("$id", source.Id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ExecutionRequestReconciliationException("Execution recovery state changed before reconciliation.");

            command.Parameters.Clear();
            command.CommandText = "SELECT work_reference_json, attempt_number FROM execution_requests WHERE project_id=$projectId;";
            command.Parameters.AddWithValue("$projectId", source.ProjectId);
            var attemptNumber = source.AttemptNumber;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var existing = JsonSerializer.Deserialize<WorkReference>(reader.GetString(0), ProjectJson);
                    if (existing is null) continue;
                    try
                    {
                        var existingCanonical = ExecutionRequestValidation.NormalizeIdentity(existing);
                        if (existingCanonical.Type == canonicalWork.Type && existingCanonical.Id == canonicalWork.Id)
                            attemptNumber = Math.Max(attemptNumber, reader.GetInt32(1));
                    }
                    catch (InvalidDataException) { }
                }
            }
            attemptNumber++;
            var retryId = Guid.NewGuid().ToString("N");
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO execution_requests (id,project_id,work_type,work_id,work_reference_json,created_at_utc,state,retry_of_execution_id,attempt_number,recovery_reason,workspace_recovery) VALUES ($id,$project,$type,$workId,$work,$created,'Queued',$retryOf,$attempt,'Operator retry after verified non-integration.','FreshWorkspaceRequired');";
            command.Parameters.AddWithValue("$id", retryId);
            command.Parameters.AddWithValue("$project", source.ProjectId);
            command.Parameters.AddWithValue("$type", canonicalWork.Type);
            command.Parameters.AddWithValue("$workId", canonicalWork.Id);
            command.Parameters.AddWithValue("$work", JsonSerializer.Serialize(canonicalWork, ProjectJson));
            command.Parameters.AddWithValue("$created", resolvedAt.ToString("O"));
            command.Parameters.AddWithValue("$retryOf", source.Id);
            command.Parameters.AddWithValue("$attempt", attemptNumber);
            await command.ExecuteNonQueryAsync(cancellationToken);
            retry = await ReadExecutionInTransactionAsync(connection, (SqliteTransaction)transaction, retryId, cancellationToken);
        }
        else
        {
            var reason = $"Operator verified integration at {resolvedAt:O}, commit {request.IntegrationCommit}. Evidence: {evidence}";
            command.Parameters.Clear();
            command.CommandText = "UPDATE execution_requests SET recovery_state='OperatorVerifiedIntegrated', recovery_reason=$reason, integration_result=$integration WHERE id=$id AND state='Failed' AND recovery_state='LeaseExpiredUncertain' AND EXISTS (SELECT 1 FROM execution_leases WHERE execution_id=$id AND generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=$id) AND state='Expired');";
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$integration", $"Verified integrated commit {request.IntegrationCommit}");
            command.Parameters.AddWithValue("$id", source.Id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new ExecutionRequestReconciliationException("Execution recovery state changed before reconciliation.");
        }

        var updatedSource = await ReadExecutionInTransactionAsync(connection, (SqliteTransaction)transaction, source.Id, cancellationToken)
            ?? throw new InvalidDataException("Reconciled execution was not found.");
        await transaction.CommitAsync(cancellationToken);
        return new(updatedSource, retry);
    }

    private static async Task<ExecutionRequest?> ReadExecutionInTransactionAsync(SqliteConnection connection,
        SqliteTransaction transaction, string executionRequestId, CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = ExecutionSelect + " WHERE id=$id;";
        read.Parameters.AddWithValue("$id", executionRequestId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExecution(reader) : null;
    }

    private async Task<IReadOnlyDictionary<string, int>> ReadProjectActivityAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id, COUNT(*) FROM execution_requests WHERE state IN ('Assigned','Running') OR (state='Failed' AND recovery_state='LeaseExpiredUncertain') GROUP BY project_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0), reader.GetInt32(1));
        return result;
    }

    private static string PendingReasonFor(ExecutionRequest execution, IReadOnlyDictionary<string, CentralProject> projects,
        IReadOnlyList<WorkerRegistrationResponse> workers, IReadOnlyDictionary<string, int> projectActivity)
    {
        if (!projects.TryGetValue(execution.ProjectId, out var project)) return "waiting for available worker";
        if (!project.Enabled) return "project is disabled";
        if (execution.ManagedEligibilityState == "blocked") return "work is blocked by managed eligibility";
        if (execution.ManagedEligibilityState == "unavailable") return "managed eligibility is unavailable";
        var compatible = workers.Where(worker => WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(project), worker.Capabilities, worker.CapabilityInventory).IsEligible).ToArray();
        var eligible = compatible.Where(worker => worker.SchedulingPolicy == WorkerSchedulingPolicy.Enabled).ToArray();
        if (eligible.Length == 0 && compatible.Length > 0) return "compatible Workers are disabled or draining";
        if (eligible.Length == 0) return "no compatible worker";
        var accepting = eligible.Where(worker => worker.Availability == "online" && worker.LifecycleState == "running").ToArray();
        if (accepting.Length > 0 && project.MaxParallelTasks is { } limit && projectActivity.GetValueOrDefault(project.Id) >= limit)
            return "project-capacity";
        if (accepting.Length == 0 || accepting.Any(worker => worker.AvailableCapacity > 0)) return "waiting for available worker";
        return "compatible workers currently at capacity";
    }

    private static IReadOnlyList<string> MissingFor(ExecutionRequest execution,
        IReadOnlyDictionary<string, CentralProject> projects, IReadOnlyList<WorkerRegistrationResponse> workers)
    {
        if (!projects.TryGetValue(execution.ProjectId, out var project)) return [];
        if (!project.Enabled) return [];
        if (workers.Any(worker => WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(project), worker.Capabilities, worker.CapabilityInventory).IsEligible)) return [];
        if (workers.Count == 0) return WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(project), []).MissingRequirements;
        return workers.SelectMany(worker => WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(project), worker.Capabilities, worker.CapabilityInventory).MissingRequirements)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<WorkerRegistrationRequest?> ReadWorkerRegistrationAsync(SqliteCommand command, string workerId,
        CancellationToken cancellationToken)
    {
        command.Parameters.Clear();
        command.CommandText = "SELECT registration_json FROM workers WHERE worker_id = $worker AND registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$worker", workerId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<WorkerRegistrationRequest>(json);
    }

    private static async Task<IReadOnlyDictionary<string, CentralProject>> ReadProjectsByIdAsync(SqliteCommand command,
        CancellationToken cancellationToken)
    {
        command.Parameters.Clear();
        command.CommandText = "SELECT project_id, configuration_json, created_at_utc FROM projects;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var projects = new Dictionary<string, CentralProject>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var project = ToProject(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            projects.Add(project.Id, project);
        }
        return projects;
    }

    public async Task<ExecutionLease?> RenewExecutionLeaseAsync(string executionId, ExecutionLeaseRenewal renewal, CancellationToken cancellationToken = default)
    {
        if (renewal is null || !Printable(renewal.WorkerId, 128))
            throw new InvalidDataException("Execution lease renewal contract is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var expired = await ReconcileExpiredLeasesAsync(connection, (SqliteTransaction)transaction, now, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE execution_leases SET expires_at_utc=$expires WHERE execution_id=$id AND worker_id=$worker AND generation=$generation AND state='Active' AND expires_at_utc>$now AND generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=$id) AND EXISTS (SELECT 1 FROM execution_requests WHERE id=$id AND state IN ('Assigned','Running') AND assigned_worker_id=$worker);";
        command.Parameters.AddWithValue("$expires", now.Add(_leaseDuration).ToString("O"));
        command.Parameters.AddWithValue("$id", executionId);
        command.Parameters.AddWithValue("$worker", renewal.WorkerId);
        command.Parameters.AddWithValue("$generation", renewal.Generation);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            var execution = await ReadExecutionInTransactionAsync(connection, (SqliteTransaction)transaction, executionId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LogExpiredLeases(expired);
            ServerOperationalDiagnostics.Write(_logger, LogLevel.Debug, "lease-renewal", "ownership-or-lease-lost",
                execution?.ProjectId, execution?.WorkReference, executionId, renewal.WorkerId, execution?.AssignmentId, renewal.Generation, execution?.WorkerExecutionId);
            return null;
        }
        await transaction.CommitAsync(cancellationToken);
        LogExpiredLeases(expired);
        return new ExecutionLease(executionId, renewal.WorkerId, renewal.Generation, now, now.Add(_leaseDuration), "Active", _leaseRenewalIntervalSeconds);
    }

    public async Task ExpireLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var expired = await ReconcileExpiredLeasesAsync(connection, (SqliteTransaction)transaction, now, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE provisioning_plans SET state='Failed', completed_at_utc=$now, failure='Worker heartbeat expired while provisioning was active.' WHERE state IN ('Accepted','Running') AND worker_id IN (SELECT worker_id FROM workers WHERE last_seen_at_utc IS NULL OR last_seen_at_utc <= $stale);";
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$stale", (now - _staleAfter).ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        LogExpiredLeases(expired);
    }

    private void LogExpiredLeases(IReadOnlyList<ExecutionRequest> expired)
    {
        foreach (var execution in expired)
            ServerOperationalDiagnostics.Write(_logger, LogLevel.Warning, "lease-expiry",
                execution.RecoveryState == "LeaseExpiredRequeued" ? "expired-requeued" : "expired-uncertain",
                execution.ProjectId, execution.WorkReference, execution.Id, execution.AssignedWorkerId,
                execution.AssignmentId, execution.Lease?.Generation, execution.WorkerExecutionId);
    }

    public async Task<ProvisioningPlan> CreateProvisioningPlanAsync(CreateProvisioningPlanRequest request, CancellationToken cancellationToken = default)
    {
        var error = ProvisioningPlanValidation.Error(request);
        if (error is not null) throw new InvalidDataException(error);
        var worker = await GetWorkerAsync(request.WorkerId, cancellationToken);
        if (worker is null) throw new KeyNotFoundException("Worker is not registered.");
        var plan = new ProvisioningPlan(Guid.NewGuid().ToString("N"), request.WorkerId, _timeProvider.GetUtcNow(), "Pending", request.Actions);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO provisioning_plans (id, worker_id, created_at_utc, state, actions_json) VALUES ($id,$worker,$created,$state,$actions);";
        command.Parameters.AddWithValue("$id", plan.Id);
        command.Parameters.AddWithValue("$worker", plan.WorkerId);
        command.Parameters.AddWithValue("$created", plan.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$state", plan.State);
        command.Parameters.AddWithValue("$actions", JsonSerializer.Serialize(plan.Actions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return plan;
    }

    public async Task<IReadOnlyList<ProvisioningPlan>> GetProvisioningPlansAsync(CancellationToken cancellationToken = default,
        int limit = 100, int offset = 0)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Provisioning plan history limit must be between 1 and 100.");
        if (offset is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(offset), "Provisioning plan history offset must be between 0 and 10000.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, worker_id, created_at_utc, state, actions_json, current_action_id, started_at_utc, completed_at_utc, result, failure FROM provisioning_plans ORDER BY created_at_utc DESC, id DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plans = new List<ProvisioningPlan>();
        while (await reader.ReadAsync(cancellationToken)) plans.Add(ReadProvisioningPlan(reader));
        return plans;
    }

    public async Task<ProvisioningPlan?> GetProvisioningPlanAsync(string planId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, worker_id, created_at_utc, state, actions_json, current_action_id, started_at_utc, completed_at_utc, result, failure FROM provisioning_plans WHERE id=$id;";
        command.Parameters.AddWithValue("$id", planId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProvisioningPlan(reader) : null;
    }

    public async Task<ProvisioningPlan?> AcceptProvisioningPlanAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        // Reissuing Accepted or Running plans to the same Worker makes process interruption recoverable.
        // Worker actions are required to be idempotent, and the plan identity remains stable.
        command.CommandText = "SELECT id FROM provisioning_plans WHERE worker_id=$worker AND state IN ('Pending','Accepted','Running') AND EXISTS (SELECT 1 FROM workers WHERE worker_id=$worker AND last_seen_at_utc > $stale) ORDER BY created_at_utc, id LIMIT 1;";
        command.Parameters.AddWithValue("$worker", workerId);
        command.Parameters.AddWithValue("$stale", (_timeProvider.GetUtcNow() - _staleAfter).ToString("O"));
        var id = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (id is null) return null;
        command.Parameters.Clear();
        command.CommandText = "UPDATE provisioning_plans SET state='Accepted' WHERE id=$id AND state IN ('Pending','Accepted','Running');";
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        await transaction.CommitAsync(cancellationToken);
        return await GetProvisioningPlanAsync(id, cancellationToken);
    }

    public async Task<ProvisioningPlan?> ReportProvisioningPlanAsync(string planId, ProvisioningWorkerReport report, CancellationToken cancellationToken = default)
    {
        if (report is null || !Guid.TryParseExact(report.WorkerId, "N", out _) || report.State is not ("Running" or "Completed" or "Failed") ||
            report.Result is { Length: > 1000 } || report.Failure is { Length: > 500 } || report.CurrentActionId is { Length: > 80 })
            throw new InvalidDataException("Worker provisioning report contract is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT worker_id, state, actions_json FROM provisioning_plans WHERE id=$id;";
        command.Parameters.AddWithValue("$id", planId);
        string workerId; string state; string actionsJson;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            workerId = reader.GetString(0); state = reader.GetString(1); actionsJson = reader.GetString(2);
        }
        if (!string.Equals(workerId, report.WorkerId, StringComparison.Ordinal) || state is not ("Accepted" or "Running"))
            throw new InvalidOperationException("Provisioning plan is not owned by this Worker or its lifecycle has advanced.");
        var actions = JsonSerializer.Deserialize<IReadOnlyList<ProvisioningAction>>(actionsJson) ?? [];
        if (report.CurrentActionId is not null && !actions.Any(action => action.Id == report.CurrentActionId))
            throw new InvalidDataException("Worker provisioning report references an unknown action.");
        if (report.State == "Running" && report.CurrentActionId is null) throw new InvalidDataException("A running provisioning report requires a current action.");
        var now = _timeProvider.GetUtcNow();
        var failure = report.Failure is null ? null : SanitizeProvisioningText(report.Failure, 500);
        var result = report.Result is null ? null : SanitizeProvisioningText(report.Result, 1000);
        command.Parameters.Clear();
        command.CommandText = "UPDATE provisioning_plans SET state=$state, current_action_id=$action, started_at_utc=COALESCE(started_at_utc,$now), completed_at_utc=$completed, result=$result, failure=$failure WHERE id=$id AND state IN ('Accepted','Running');";
        command.Parameters.AddWithValue("$state", report.State);
        command.Parameters.AddWithValue("$action", (object?)report.CurrentActionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$completed", report.State is "Completed" or "Failed" ? now.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)failure ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", planId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        await transaction.CommitAsync(cancellationToken);
        return await GetProvisioningPlanAsync(planId, cancellationToken);
    }

    public async Task<ProvisioningPlan?> TransitionProvisioningPlanAsync(string planId, ProvisioningStateTransition transition, CancellationToken cancellationToken = default)
    {
        if (transition is null || transition.State is not ("Cancelled" or "Rejected"))
            throw new InvalidDataException("Provisioning state transition must be Cancelled or Rejected.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE provisioning_plans SET state=$state, completed_at_utc=$now WHERE id=$id AND state='Pending';";
        command.Parameters.AddWithValue("$state", transition.State);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$id", planId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            var existing = await GetProvisioningPlanAsync(planId, cancellationToken);
            if (existing is not null) throw new InvalidOperationException("Only a pending provisioning plan can be cancelled or rejected.");
            return null;
        }
        return await GetProvisioningPlanAsync(planId, cancellationToken);
    }

    private static string SanitizeProvisioningText(string value, int limit)
        => SecretSanitizer.Sanitize(value, limit);

    private static ProvisioningPlan ReadProvisioningPlan(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        DateTimeOffset.Parse(reader.GetString(2)), reader.GetString(3), JsonSerializer.Deserialize<IReadOnlyList<ProvisioningAction>>(reader.GetString(4)) ?? [],
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    private static async Task<IReadOnlyList<ExecutionRequest>> ReconcileExpiredLeasesAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE execution_leases SET state='Expired' WHERE state='Active' AND expires_at_utc <= $now;";
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.Parameters.Clear();
        command.CommandText = "SELECT r.id, r.project_id, r.work_type, r.work_id, r.work_reference_json, r.current_stage, r.integration_result, r.attempt_number, r.workspace_recovery FROM execution_requests r JOIN execution_leases l ON l.execution_id=r.id AND l.generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=r.id) WHERE l.state='Expired' AND r.state IN ('Assigned','Running') AND r.recovery_state IS NULL;";
        var expired = new List<(string Id, string Project, string Type, string WorkId, string WorkJson, string? Stage, string? Integration, int Attempt, string? WorkspaceRecovery)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) expired.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        var decisions = new List<ExecutionRequest>();
        foreach (var item in expired)
        {
            var safe = item.WorkspaceRecovery != "PreservedIntegrationImplementation" && item.Integration is null && (item.Stage is null or "Preparing" or "Codex" or "Validation");
            command.Parameters.Clear();
            command.CommandText = "UPDATE execution_requests SET state='Failed', recovery_state=$recovery, recovery_reason=$reason, workspace_recovery='PreviousWorkerLocalStateUnknown' WHERE id=$id AND recovery_state IS NULL;";
            command.Parameters.AddWithValue("$recovery", safe ? "LeaseExpiredRequeued" : "LeaseExpiredUncertain");
            command.Parameters.AddWithValue("$reason", safe ? "Lease expired before integration began; a new attempt was created." : $"Lease expired during or after stage '{item.Stage ?? "unknown"}'; authoritative integration or completion may have occurred.");
            command.Parameters.AddWithValue("$id", item.Id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) continue;
            var execution = await ReadExecutionInTransactionAsync(connection, transaction, item.Id, cancellationToken);
            if (execution is not null) decisions.Add(execution);
            if (!safe) continue;
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO execution_requests (id, project_id, work_type, work_id, work_reference_json, created_at_utc, state, retry_of_execution_id, attempt_number, recovery_reason, workspace_recovery) VALUES ($id,$project,$type,$workId,$work,$created,'Queued',$retryOf,$attempt,'Requeued after lease expiry before integration.',$workspace);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$project", item.Project);
            command.Parameters.AddWithValue("$type", item.Type);
            command.Parameters.AddWithValue("$workId", item.WorkId);
            command.Parameters.AddWithValue("$work", item.WorkJson);
            command.Parameters.AddWithValue("$created", now.ToString("O"));
            command.Parameters.AddWithValue("$retryOf", item.Id);
            command.Parameters.AddWithValue("$attempt", item.Attempt + 1);
            command.Parameters.AddWithValue("$workspace", item.WorkspaceRecovery == "PreservedCodexImplementation" ? "ReturnedCodexWorkspaceRequired" : "FreshWorkspaceRequired");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return decisions;
    }

    public Task<ExecutionRequest?> ReportExecutionAsync(string executionRequestId, WorkerExecutionReport report, CancellationToken cancellationToken = default) =>
        ServerOperationalDiagnostics.RunAsync(_logger, "result-report", () => ReportExecutionCoreAsync(executionRequestId, report, cancellationToken),
            cancellationToken, executionId: executionRequestId, workerId: report?.WorkerId, assignmentId: report?.AssignmentId, generation: report?.Generation, workerExecutionId: report?.WorkerExecutionId);

    private async Task<ExecutionRequest?> ReportExecutionCoreAsync(string executionRequestId, WorkerExecutionReport report, CancellationToken cancellationToken = default)
    {
        if (report is null || !Printable(report.WorkerId, 128) || !Printable(report.AssignmentId, 200) || report.Generation <= 0 ||
            !Printable(report.WorkerExecutionId, 200) || report.Summary is { Length: > 4000 } ||
            report.Stage is { Length: > 80 } || report.ValidationResult is { Length: > 1000 } || report.IntegrationResult is { Length: > 1000 } ||
            report.FailureClassification is { Length: > 100 }) throw new InvalidDataException("Worker execution report contract is invalid.");
        if (report.State is not ("Running" or "Completed" or "Failed")) throw new InvalidDataException("Worker execution report state is invalid.");
        if (report.State == "Running" && report.Stage is not ("Claiming" or "Preparing" or "Codex" or "Implementing" or "Validation" or "Integration" or "Reporting"))
            throw new InvalidDataException("Worker lifecycle stage is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        var previous = await ReadExecutionInTransactionAsync(connection, (SqliteTransaction)transaction, executionRequestId, cancellationToken);
        command.CommandText = "UPDATE execution_requests SET state=$state, execution_id=COALESCE(execution_id,$workerExecution), worker_execution_id=$workerExecution, current_stage=COALESCE($stage,current_stage), started_at_utc=COALESCE(started_at_utc,$started), completed_at_utc=COALESCE($completed,completed_at_utc), duration_ms=COALESCE($duration,duration_ms), validation_result=COALESCE($validation,validation_result), integration_result=COALESCE($integration,integration_result), failure_classification=COALESCE($failure,failure_classification), recoverable=$recoverable, completion_summary=COALESCE($summary,completion_summary) WHERE id=$id AND assignment_id=$assignment AND assigned_worker_id=$worker AND ((state IN ('Assigned','Running') AND EXISTS (SELECT 1 FROM execution_leases l WHERE l.execution_id=$id AND l.worker_id=$worker AND l.generation=$generation AND l.state='Active' AND l.expires_at_utc>$now AND l.generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=$id)) AND ($state!='Running' OR CASE $stage WHEN 'Claiming' THEN 0 WHEN 'Preparing' THEN 1 WHEN 'Codex' THEN 2 WHEN 'Implementing' THEN 2 WHEN 'Validation' THEN 3 WHEN 'Integration' THEN 4 WHEN 'Reporting' THEN 5 ELSE -1 END >= CASE current_stage WHEN 'Claiming' THEN 0 WHEN 'Preparing' THEN 1 WHEN 'Codex' THEN 2 WHEN 'Implementing' THEN 2 WHEN 'Validation' THEN 3 WHEN 'Integration' THEN 4 WHEN 'Reporting' THEN 5 ELSE 0 END)));";
        command.Parameters.AddWithValue("$state", report.State);
        command.Parameters.AddWithValue("$workerExecution", report.WorkerExecutionId);
        command.Parameters.AddWithValue("$stage", (object?)report.Stage ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", (object?)report.StartedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", (object?)report.CompletedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)report.DurationMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$validation", (object?)report.ValidationResult ?? DBNull.Value);
        command.Parameters.AddWithValue("$integration", (object?)report.IntegrationResult ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)report.FailureClassification ?? DBNull.Value);
        command.Parameters.AddWithValue("$recoverable", report.Recoverable ? 1 : 0);
        command.Parameters.AddWithValue("$summary", (object?)report.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", executionRequestId);
        command.Parameters.AddWithValue("$assignment", report.AssignmentId);
        command.Parameters.AddWithValue("$worker", report.WorkerId);
        command.Parameters.AddWithValue("$generation", report.Generation);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 0)
        {
            // A retry after a lost final acknowledgement returns the already committed result.
            // The generation check prevents an old worker from treating a newer owner's state as its own.
            await using var duplicateRead = connection.CreateCommand();
            duplicateRead.Transaction = (SqliteTransaction)transaction;
            duplicateRead.CommandText = ExecutionSelect + " WHERE id=$id;";
            duplicateRead.Parameters.AddWithValue("$id", executionRequestId);
            await using var duplicateReader = await duplicateRead.ExecuteReaderAsync(cancellationToken);
            var duplicate = await duplicateReader.ReadAsync(cancellationToken) ? ReadExecution(duplicateReader) : null;
            await duplicateReader.DisposeAsync();
            if (duplicate is null)
            {
                ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "result-report", "not-found",
                    executionId: executionRequestId, workerId: report.WorkerId, assignmentId: report.AssignmentId,
                    leaseGeneration: report.Generation, workerExecutionId: report.WorkerExecutionId);
                return null;
            }
            if (report.State is ("Completed" or "Failed") && duplicate.State == report.State && duplicate.WorkerExecutionId == report.WorkerExecutionId &&
                duplicate.AssignmentId == report.AssignmentId && duplicate.Lease?.WorkerId == report.WorkerId &&
                duplicate.Lease?.Generation == report.Generation)
            {
                await transaction.CommitAsync(cancellationToken);
                ServerOperationalDiagnostics.Write(_logger, LogLevel.Debug, "result-report", "duplicate-accepted", duplicate.ProjectId, duplicate.WorkReference, duplicate.Id, report.WorkerId, report.AssignmentId, report.Generation, report.WorkerExecutionId);
                return duplicate;
            }
            await transaction.CommitAsync(cancellationToken);
            ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "result-report", "ownership-or-stage-rejected",
                duplicate.ProjectId, duplicate.WorkReference, executionRequestId, report.WorkerId, report.AssignmentId,
                report.Generation, report.WorkerExecutionId);
            throw new ExecutionRequestOwnershipException();
        }
        if (updated == 1 && report.State is ("Completed" or "Failed"))
        {
            command.Parameters.Clear();
            command.CommandText = "UPDATE execution_leases SET state='Released' WHERE execution_id=$id AND worker_id=$worker AND generation=(SELECT MAX(generation) FROM execution_leases WHERE execution_id=$id);";
            command.Parameters.AddWithValue("$id", executionRequestId);
            command.Parameters.AddWithValue("$worker", report.WorkerId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var read = connection.CreateCommand();
        read.Transaction = (SqliteTransaction)transaction;
        read.CommandText = ExecutionSelect + " WHERE id=$id;";
        read.Parameters.AddWithValue("$id", executionRequestId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var result = await reader.ReadAsync(cancellationToken) ? ReadExecution(reader) : null;
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        var changed = previous?.State != result?.State || previous?.CurrentStage != result?.CurrentStage;
        ServerOperationalDiagnostics.Write(_logger, changed ? LogLevel.Information : LogLevel.Debug, "result-report",
            report.State == "Running" ? "stage-" + report.Stage : "outcome-" + report.State.ToLowerInvariant(),
            result?.ProjectId, result?.WorkReference, executionRequestId, report.WorkerId, report.AssignmentId, report.Generation, report.WorkerExecutionId);
        return result;
    }

    public async Task<ExecutionRequest?> TransitionExecutionAsync(string executionRequestId, ExecutionStateTransition transition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new ExecutionRequestTransitionException();
    }

    private static bool Printable(string value, int maxLength) => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static ExecutionRequest ReadExecution(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        JsonSerializer.Deserialize<WorkReference>(reader.GetString(2), ProjectJson) ?? throw new InvalidDataException("Stored work reference is invalid."),
        DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : reader.GetInt64(13),
        reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16), !reader.IsDBNull(17) && reader.GetInt32(17) != 0,
        reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.IsDBNull(19) ? null : new ExecutionLease(reader.GetString(0), reader.GetString(19), reader.GetInt64(20),
            DateTimeOffset.Parse(reader.GetString(21)), DateTimeOffset.Parse(reader.GetString(22)), reader.GetString(23)),
        reader.IsDBNull(24) ? null : reader.GetString(24), reader.IsDBNull(25) ? null : reader.GetString(25),
        reader.IsDBNull(26) ? null : reader.GetString(26), reader.GetInt32(27), reader.IsDBNull(28) ? null : reader.GetString(28),
        null, null, reader.GetString(29), reader.IsDBNull(30) ? null : JsonSerializer.Deserialize<string[]>(reader.GetString(30), ProjectJson),
        reader.IsDBNull(31) ? null : DateTimeOffset.Parse(reader.GetString(31)));

    public async Task<IReadOnlyList<CentralProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id, configuration_json, created_at_utc FROM projects ORDER BY lower(display_name), project_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CentralProject>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ToProject(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<CentralProject?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id, configuration_json, created_at_utc FROM projects WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ToProject(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public async Task<CentralProject> CreateProjectAsync(CentralProjectDefinition definition, CancellationToken cancellationToken = default)
    {
        var validationError = CentralProjectValidation.Error(definition);
        if (validationError is not null) throw new InvalidDataException(validationError);
        var now = _timeProvider.GetUtcNow();
        var id = CentralProjectValidation.IdFor(definition.Name);
        if (id.Length == 0) throw new InvalidDataException("Project name does not produce a valid project identifier.");
        var project = new CentralProject(id, definition.Name.Trim(), definition.Repository.Trim(), definition.DefaultBranch.Trim(),
            definition.Description.Trim(), (definition.Requirements ?? []).Select(CentralProjectValidation.Normalize).ToArray(), 1, now, now,
            IssueReadyLabel: NormalizeLabel(definition.IssueReadyLabel), IssueBlockedLabel: NormalizeLabel(definition.IssueBlockedLabel), AutomaticDiscovery: definition.AutomaticDiscovery, MaxParallelTasks: definition.MaxParallelTasks);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO projects (project_id, display_name, configuration_json, created_at_utc) VALUES ($id, $name, $json, $created);";
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(project, ProjectJson));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("A project with this identifier, name, or repository already exists.", ex); }
        ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "project", "created", project.Id);
        return project;
    }

    public async Task<CentralProject?> UpdateProjectAsync(string projectId, CentralProjectDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var validationError = CentralProjectValidation.Error(definition);
        if (validationError is not null) throw new InvalidDataException(validationError);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE projects SET display_name = $name, configuration_json = json_set(configuration_json, '$.name', $name, '$.repository', $repository, '$.defaultBranch', $branch, '$.description', $description, '$.requirements', json($requirements), '$.issueReadyLabel', $readyLabel, '$.issueBlockedLabel', $blockedLabel, '$.automaticDiscovery', json($discovery), '$.maxParallelTasks', $parallelism, '$.revision', $nextRevision, '$.updatedAtUtc', $updated) WHERE project_id = $id AND CAST(json_extract(configuration_json, '$.revision') AS INTEGER) = $expectedRevision;";
        write.Parameters.AddWithValue("$name", definition.Name.Trim());
        write.Parameters.AddWithValue("$repository", definition.Repository.Trim());
        write.Parameters.AddWithValue("$branch", definition.DefaultBranch.Trim());
        write.Parameters.AddWithValue("$description", definition.Description.Trim());
        write.Parameters.AddWithValue("$requirements", JsonSerializer.Serialize((definition.Requirements ?? []).Select(CentralProjectValidation.Normalize).ToArray(), ProjectJson));
        write.Parameters.AddWithValue("$readyLabel", (object?)NormalizeLabel(definition.IssueReadyLabel) ?? DBNull.Value);
        write.Parameters.AddWithValue("$blockedLabel", (object?)NormalizeLabel(definition.IssueBlockedLabel) ?? DBNull.Value);
        write.Parameters.AddWithValue("$parallelism", (object?)definition.MaxParallelTasks ?? DBNull.Value);
        write.Parameters.AddWithValue("$discovery", JsonSerializer.Serialize(definition.AutomaticDiscovery, ProjectJson));
        write.Parameters.AddWithValue("$nextRevision", expectedRevision + 1);
        write.Parameters.AddWithValue("$updated", now.ToString("O"));
        write.Parameters.AddWithValue("$id", projectId);
        write.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        try
        {
            if (await write.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "project", "definition-updated", projectId);
                return await GetProjectAsync(projectId, cancellationToken);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("A project with this name or repository already exists.", ex); }
        var current = await GetProjectAsync(projectId, cancellationToken);
        if (current is not null) throw new ProjectRevisionConflictException(current.Revision);
        return null;
    }

    public async Task<CentralProject?> UpdateProjectLifecycleAsync(string projectId, bool enabled, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 1) throw new InvalidDataException("expectedRevision must be positive.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE projects SET configuration_json = json_set(configuration_json, '$.enabled', json(CASE WHEN $enabled THEN 'true' ELSE 'false' END), '$.revision', $nextRevision, '$.updatedAtUtc', $updated) WHERE project_id = $id AND CAST(json_extract(configuration_json, '$.revision') AS INTEGER) = $expectedRevision;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$nextRevision", expectedRevision + 1);
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.Parameters.AddWithValue("$id", projectId);
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "project", enabled ? "enabled" : "disabled", projectId);
            return await GetProjectAsync(projectId, cancellationToken);
        }
        var current = await GetProjectAsync(projectId, cancellationToken);
        if (current is not null) throw new ProjectRevisionConflictException(current.Revision);
        return null;
    }

    public async Task<bool> RemoveProjectAsync(string projectId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 1) throw new InvalidDataException("expectedRevision must be positive.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM projects WHERE project_id = $id AND CAST(json_extract(configuration_json, '$.revision') AS INTEGER) = $revision AND NOT EXISTS (SELECT 1 FROM execution_requests r WHERE r.project_id = projects.project_id AND r.state IN ('Queued', 'Assigned', 'Running'));";
        command.Parameters.AddWithValue("$id", projectId);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            ServerOperationalDiagnostics.Write(_logger, LogLevel.Information, "project", "deleted", projectId);
            return true;
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT configuration_json, created_at_utc FROM projects WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        await using var projectReader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await projectReader.ReadAsync(cancellationToken))
        {
            await projectReader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        var current = ToProject(projectId, projectReader.GetString(0), projectReader.GetString(1));
        await projectReader.DisposeAsync();
        if (current.Revision != expectedRevision)
        {
            await transaction.CommitAsync(cancellationToken);
            throw new ProjectRevisionConflictException(current.Revision);
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT state, COUNT(*) FROM execution_requests WHERE project_id = $id AND state IN ('Queued', 'Assigned', 'Running') GROUP BY state;";
        command.Parameters.AddWithValue("$id", projectId);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var executionsReader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await executionsReader.ReadAsync(cancellationToken))
                counts.Add(executionsReader.GetString(0), executionsReader.GetInt32(1));
        }
        await transaction.CommitAsync(cancellationToken);
        var queued = counts.GetValueOrDefault("Queued");
        var assigned = counts.GetValueOrDefault("Assigned");
        var running = counts.GetValueOrDefault("Running");
        if (queued + assigned + running > 0) throw new ProjectInUseException(queued, assigned, running);
        throw new InvalidOperationException("Project deletion could not be completed because its registry state changed concurrently.");
    }

    private static CentralProject ToProject(string id, string json, string created) =>
        JsonSerializer.Deserialize<CentralProject>(json, ProjectJson) is { } value ? value with { Id = id, Requirements = value.Requirements ?? [], CreatedAtUtc = DateTimeOffset.Parse(created) } :
        throw new InvalidDataException($"Stored project '{id}' is invalid.");

    private static string? NormalizeLabel(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task HeartbeatWorkerAsync(WorkerHeartbeatRequest heartbeat, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workers SET heartbeat_json = $heartbeat, last_seen_at_utc = $now WHERE worker_id = $id AND registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$heartbeat", JsonSerializer.Serialize(heartbeat));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", heartbeat.WorkerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Worker must be registered before sending heartbeats.");
    }

    public async Task RegisterWorkerAsync(WorkerRegistrationRequest worker, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var metadata = JsonSerializer.Serialize(worker);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workers (worker_id, display_name, registered_at_utc, status_json, registration_json, last_seen_at_utc)
            VALUES ($id, $name, $now, NULL, $metadata, $now)
            ON CONFLICT(worker_id) DO UPDATE SET display_name = excluded.display_name,
                registration_json = excluded.registration_json, last_seen_at_utc = excluded.last_seen_at_utc, heartbeat_json = NULL;
            """;
        command.Parameters.AddWithValue("$id", worker.WorkerId);
        command.Parameters.AddWithValue("$name", worker.DisplayName);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$metadata", metadata);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<WorkerRegistrationResponse>> GetWorkersAsync(CancellationToken cancellationToken = default) =>
        ReadWorkersAsync(null, 0, cancellationToken);

    internal Task<IReadOnlyList<WorkerRegistrationResponse>> GetWorkerPageAsync(int limit, int offset, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 101 || offset is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
        return ReadWorkersAsync(limit, offset, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkerRegistrationResponse>> ReadWorkersAsync(int? limit, int offset, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(limit is null ? ConnectionString : new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT w.registration_json, w.registered_at_utc, w.last_seen_at_utc, w.heartbeat_json, w.scheduling_policy, (SELECT COUNT(*) FROM execution_requests e WHERE e.assigned_worker_id=w.worker_id AND e.state IN ('Assigned','Running')), a.worker_id, a.revoked_at_utc FROM workers w LEFT JOIN worker_auth_tokens a ON a.worker_id=w.worker_id WHERE w.registration_json IS NOT NULL ORDER BY w.worker_id;";
        if (limit is { } pageSize)
        {
            command.CommandText = command.CommandText.TrimEnd(';') + " LIMIT $limit OFFSET $offset;";
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", offset);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var workers = new List<WorkerRegistrationResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var request = JsonSerializer.Deserialize<WorkerRegistrationRequest>(reader.GetString(0)) ?? throw new InvalidDataException("Stored Worker registration is invalid.");
            workers.Add(ToResponse(request, DateTimeOffset.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                reader.IsDBNull(6) ? "not-configured" : reader.IsDBNull(7) ? "active" : "revoked",
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))));
        }
        return workers;
    }

    public async Task<WorkerRegistrationResponse?> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT w.registration_json, w.registered_at_utc, w.last_seen_at_utc, w.heartbeat_json, w.scheduling_policy, (SELECT COUNT(*) FROM execution_requests e WHERE e.assigned_worker_id=w.worker_id AND e.state IN ('Assigned','Running')), a.worker_id, a.revoked_at_utc FROM workers w LEFT JOIN worker_auth_tokens a ON a.worker_id=w.worker_id WHERE w.worker_id = $id AND w.registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$id", workerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var request = JsonSerializer.Deserialize<WorkerRegistrationRequest>(reader.GetString(0)) ?? throw new InvalidDataException("Stored Worker registration is invalid.");
        return ToResponse(request, DateTimeOffset.Parse(reader.GetString(1)), DateTimeOffset.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
            reader.IsDBNull(6) ? "not-configured" : reader.IsDBNull(7) ? "active" : "revoked",
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)));
    }

    public async Task<WorkerRegistrationResponse?> SetWorkerSchedulingPolicyAsync(string workerId, string policy,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _) || !WorkerSchedulingPolicy.IsValid(policy))
            throw new InvalidDataException("Worker identity or scheduling policy is invalid.");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE workers SET scheduling_policy=$policy WHERE worker_id=$worker AND registration_json IS NOT NULL;";
        command.Parameters.AddWithValue("$policy", policy);
        command.Parameters.AddWithValue("$worker", workerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        return await GetWorkerAsync(workerId, cancellationToken);
    }

    private WorkerRegistrationResponse ToResponse(WorkerRegistrationRequest request, DateTimeOffset registered, DateTimeOffset seen,
        string? heartbeatJson, string schedulingPolicy, int activeAssignments, string authenticationCredentialStatus,
        DateTimeOffset? authenticationCredentialRevokedAtUtc)
    {
        var heartbeat = heartbeatJson is null ? null : JsonSerializer.Deserialize<WorkerHeartbeatRequest>(heartbeatJson);
        var online = heartbeat is not null && _timeProvider.GetUtcNow() - seen <= _staleAfter;
        var availability = !online ? "stale" : heartbeat!.LifecycleState switch
        {
            "draining" or "drain-requested" or "drained" or "updating" or "update-failed" or "updated" or "restarting" or
                "restart-failed" or "reconnecting" or "capability-regression" or "configuration-incompatible" => "draining",
            "stopped" => "offline",
            _ => "online"
        };
        var active = online ? heartbeat!.ActiveExecutions : 0;
        var capacity = online ? heartbeat!.MaximumCapacity : request.Capacity;
        return new(request.ContractVersion, request.WorkerId, request.DisplayName, heartbeat?.WorkerVersion ?? request.WorkerVersion,
            request.Platform, request.Capacity, heartbeat?.Capabilities ?? request.Capabilities, registered, seen,
            availability, active, capacity, Math.Max(0, capacity - active), heartbeat?.LifecycleState ?? "unknown",
            online ? heartbeat!.ActiveProjects : Array.Empty<string>(), heartbeat is null ? null : seen,
            heartbeat?.ConfigurationSynchronization, heartbeat?.ConfigurationVersion, heartbeat?.CapabilityInventory ?? request.CapabilityInventory,
            schedulingPolicy, activeAssignments, authenticationCredentialStatus, authenticationCredentialRevokedAtUtc, heartbeat?.ManagedDiagnostics, heartbeat?.HostResources);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM schema_metadata WHERE singleton = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == CurrentSchemaVersion;
    }

    public Task<string> CreateWorkerBootstrapTokenAsync(TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        CreateAuthorizationAsync(null, "enroll", lifetime, cancellationToken);

    public Task<string> CreateWorkerAuthorizationAsync(string workerId, string operation, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _) || !WorkerEnrollmentProtocol.ValidOperation(operation))
            throw new ArgumentException("Authorization requires a valid Worker identity and enrollment operation.");
        return CreateAuthorizationAsync(workerId, operation, lifetime, cancellationToken);
    }

    private async Task<string> CreateAuthorizationAsync(string? workerId, string operation, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO worker_bootstrap_tokens (token_hash, expires_at_utc, worker_id, operation) VALUES ($hash, $expires, $worker, $operation);";
        command.Parameters.AddWithValue("$hash", HashToken(token));
        command.Parameters.AddWithValue("$expires", _timeProvider.GetUtcNow().Add(lifetime).ToString("O"));
        command.Parameters.AddWithValue("$worker", (object?)workerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", operation);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return token;
    }

    public async Task<bool> RevokeWorkerBootstrapTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM worker_bootstrap_tokens WHERE token_hash=$hash AND consumed_at_utc IS NULL;";
        command.Parameters.AddWithValue("$hash", HashToken(token));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> BootstrapWorkerAsync(string token, WorkerRegistrationRequest worker, string workerToken, CancellationToken cancellationToken = default, string operation = "enroll")
    {
        if (worker is null || !worker.IsValid() || !WorkerEnrollmentProtocol.ValidOperation(operation) ||
            !WorkerEnrollmentProtocol.ValidToken(token) || !WorkerEnrollmentProtocol.ValidToken(workerToken)) return false;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        var now = _timeProvider.GetUtcNow();
        command.CommandText = "SELECT worker_id FROM worker_bootstrap_tokens WHERE token_hash=$hash;";
        command.Parameters.AddWithValue("$hash", HashToken(token));
        var boundIdentity = await command.ExecuteScalarAsync(cancellationToken) is string;
        command.Parameters.Clear();
        command.CommandText = """
            DELETE FROM worker_bootstrap_tokens
            WHERE token_hash=$hash AND expires_at_utc>$now AND consumed_at_utc IS NULL AND operation=$operation
                AND (operation!='associate' OR NOT EXISTS (SELECT 1 FROM execution_requests WHERE assigned_worker_id=$worker AND state IN ('Assigned','Running')))
                AND NOT EXISTS (SELECT 1 FROM worker_auth_tokens WHERE worker_id=$worker AND token_hash=$newToken)
                AND ((operation='enroll' AND (worker_id IS NULL OR worker_id=$worker)
                        AND NOT EXISTS (SELECT 1 FROM worker_auth_tokens WHERE worker_id=$worker)
                        AND NOT EXISTS (SELECT 1 FROM workers WHERE worker_id=$worker))
                    OR (operation!='enroll' AND worker_id=$worker AND (operation='associate' OR EXISTS (SELECT 1 FROM workers WHERE worker_id=$worker))));
            """;
        command.Parameters.AddWithValue("$hash", HashToken(token));
        command.Parameters.AddWithValue("$worker", worker.WorkerId);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$newToken", HashToken(workerToken));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        command.CommandText = "INSERT INTO worker_auth_tokens (worker_id, token_hash, created_at_utc, revoked_at_utc) VALUES ($worker,$token,$now,NULL) ON CONFLICT(worker_id) DO UPDATE SET token_hash=excluded.token_hash, created_at_utc=excluded.created_at_utc, revoked_at_utc=NULL;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$worker", worker.WorkerId);
        command.Parameters.AddWithValue("$token", HashToken(workerToken));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "INSERT INTO workers (worker_id, display_name, registered_at_utc, status_json, registration_json, last_seen_at_utc, scheduling_policy) VALUES ($id,$name,$now,NULL,$metadata,$now,$policy) ON CONFLICT(worker_id) DO UPDATE SET display_name=excluded.display_name, registration_json=excluded.registration_json, last_seen_at_utc=excluded.last_seen_at_utc, heartbeat_json=NULL, scheduling_policy=CASE WHEN $operation='associate' THEN 'Disabled' ELSE workers.scheduling_policy END;";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", worker.WorkerId);
        command.Parameters.AddWithValue("$name", worker.DisplayName);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(worker));
        command.Parameters.AddWithValue("$policy", boundIdentity || operation == "associate" ? WorkerSchedulingPolicy.Disabled : WorkerSchedulingPolicy.Enabled);
        command.Parameters.AddWithValue("$operation", operation);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> IsWorkerTokenValidAsync(string workerId, string token, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(workerId, "N", out _) || !WorkerEnrollmentProtocol.ValidToken(token)) return false;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM worker_auth_tokens WHERE worker_id=$worker AND revoked_at_utc IS NULL;";
        command.Parameters.AddWithValue("$worker", workerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string expected && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(HashToken(token)));
    }

    public async Task<bool> RevokeWorkerTokenAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE worker_auth_tokens SET revoked_at_utc=$now WHERE worker_id=$worker AND revoked_at_utc IS NULL;";
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$worker", workerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>Expires elapsed leases and conservatively reconciles abandoned Server-managed attempts.</summary>
public sealed class ExecutionLeaseExpirationService(IRegistryStore store, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), _timeProvider);
        try
        {
            do { await store.ExpireLeasesAsync(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
