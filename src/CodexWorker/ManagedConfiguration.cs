using CodexProvisioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexWorker;

/// <summary>Server-owned orchestration snapshot. Machine paths and credentials are deliberately absent.</summary>
public sealed record ServerManagedConfigurationContract(int ContractVersion, string Version,
    IReadOnlyList<ServerProjectContract> Projects);

public sealed record WorkerConfigurationSyncStatus(string? DesiredVersion, string? AppliedVersion,
    string SynchronizationStatus, DateTimeOffset? LastSuccessfulUpdateUtc, string? Error,
    ManagedWorkerDiagnostics? Diagnostics = null);

/// <summary>Maintains the last validated Server snapshot as an atomic, secret-free local cache.</summary>
public sealed class ManagedConfigurationSynchronizer(string cachePath, ManagedProjectRuntimeSettings runtime)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _cachePath = Path.GetFullPath(cachePath);
    private readonly object _gate = new();
    private IReadOnlyList<ServerProjectContract> _appliedProjects = [];
    private WorkerConfigurationSyncStatus _status = new(null, null, "not-synchronized", null, null,
        new("unverified", "not-synchronized", "none", null, null, null, null, []));

    public IReadOnlyList<ServerProjectContract> AppliedProjects { get { lock (_gate) return _appliedProjects; } }

    public WorkerConfigurationSyncStatus Status { get { lock (_gate) return _status; } }
    public bool HasCachedSnapshot => File.Exists(_cachePath);

    public IReadOnlyList<(string Path, WorkerConfiguration Configuration)> LoadLastValid()
    {
        if (!File.Exists(_cachePath)) throw new InvalidDataException("No previously applied Server configuration is available.");
        var snapshot = JsonSerializer.Deserialize<ServerManagedConfigurationContract>(File.ReadAllText(_cachePath), JsonOptions)
            ?? throw new InvalidDataException("The cached Server configuration is empty.");
        if (snapshot.ContractVersion == 1)
        {
            ValidateSnapshot(snapshot, cachedLegacy: true);
            throw new InvalidDataException("Cached contract upgrade required: expected contract version 2, received 1. Upgrade the Worker and retrieve authenticated Server configuration.");
        }
        var result = Apply(snapshot, updateStatus: false);
        lock (_gate) _status = _status with
        {
            AppliedVersion = snapshot.Version,
            SynchronizationStatus = _status.Error is null ? "cached" : _status.SynchronizationStatus,
            LastSuccessfulUpdateUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(_cachePath), TimeSpan.Zero),
            Diagnostics = Diagnostics with { Source = "cached", AppliedVersion = snapshot.Version,
                Synchronization = _status.Error is null ? "cached" : _status.SynchronizationStatus,
                Projects = snapshot.Projects.Select(project => new ManagedProjectDiagnostic(project.Id, project.Revision, "unverified")).ToArray() }
        };
        return result;
    }

    public IReadOnlyList<(string Path, WorkerConfiguration Configuration)> Apply(
        ServerManagedConfigurationContract desired) => Apply(desired, updateStatus: true);

    private ManagedWorkerDiagnostics Diagnostics => _status.Diagnostics ??
        new("unverified", "not-synchronized", "none", null, null, null, null, []);

    public async Task<IReadOnlyList<(string Path, WorkerConfiguration Configuration)>> RetrieveAndApplyAsync(
        Func<CancellationToken, Task<ServerManagedConfigurationContract>> retrieve, CancellationToken token)
    {
        ServerManagedConfigurationContract desired;
        try { desired = await retrieve(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            RecordUnavailable(ex);
            throw;
        }
        return Apply(desired);
    }

    public void RecordUnavailable(Exception exception)
    {
        lock (_gate)
        {
            var invalid = exception is JsonException or InvalidDataException;
            var requestFailure = exception as HttpRequestException ?? exception.InnerException as HttpRequestException;
            var unauthorized = requestFailure is { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            var code = invalid ? "managed-contract-invalid" : unauthorized ? "server-unauthorized" : "server-unavailable";
            _status = _status with
            {
                SynchronizationStatus = invalid ? "error" : "unavailable",
                Error = invalid ? "Server configuration was retrieved but its contract is invalid." :
                    unauthorized ? "Server rejected managed configuration authorization." : "Server unavailable: managed configuration retrieval failed; execution requires authenticated synchronization.",
                Diagnostics = Diagnostics with { Retrieval = invalid ? "retrieved" : unauthorized ? "unauthorized" : "unavailable",
                    Source = _status.AppliedVersion is null ? "none" : "cached",
                    Synchronization = invalid ? "error" : "unavailable", FailureStage = invalid ? "contract-validation" : "retrieval", DiagnosticCode = code,
                    FailedProjectId = null, FailedProjectRevision = null }
            };
        }
    }

    public void RecordCacheFailure(Exception exception)
    {
        // Cache diagnostics never imply that the freshly retrieved Server contract failed.
        var detail = exception is InvalidDataException ? exception.Message : "Cached configuration is corrupt or unreadable.";
        lock (_gate) _status = _status with
        {
            SynchronizationStatus = "error",
            Error = (_status.Error is null ? "" : _status.Error + " ") + detail,
            Diagnostics = Diagnostics with { Source = "none" }
        };
    }

    public void RecordProjectState(ServerProjectContract project, string state, string? diagnosticCode = null)
    {
        lock (_gate)
        {
            var observations = Diagnostics.Projects.Select(item => item.ProjectId == project.Id && item.Revision == project.Revision
                ? item with { State = state, DiagnosticCode = diagnosticCode } : item).ToArray();
            var updated = Diagnostics with { Projects = observations };
            if (!ManagedWorkerDiagnostics.Valid(updated)) throw new ArgumentException("Invalid managed project observation.");
            _status = _status with { Diagnostics = updated };
        }
    }

    public void RecordSynchronizationFailure()
    {
        lock (_gate) _status = _status with
        {
            SynchronizationStatus = "error",
            Error = "Server configuration was retrieved but runtime synchronization failed. Check the local project configuration and active execution ownership.",
            Diagnostics = Diagnostics with { Retrieval = "retrieved", Synchronization = "error", FailureStage = "synchronization",
                DiagnosticCode = "managed-synchronization-failed" }
        };
    }

    private IReadOnlyList<(string Path, WorkerConfiguration Configuration)> Apply(
        ServerManagedConfigurationContract desired,
        bool updateStatus)
    {
        var stage = "contract-validation";
        ServerProjectContract? preparing = null;
        try
        {
            ValidateSnapshot(desired);
            stage = "synchronization";
            var applied = new List<(string Path, WorkerConfiguration Configuration)>();
            foreach (var serverProject in desired.Projects)
            {
                preparing = serverProject;
                // Opaque stable IDs cannot escape the Worker-owned checkout root and survive renames.
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverProject.Id))).ToLowerInvariant();
                var directory = Path.Combine(Path.GetFullPath(runtime.CheckoutDirectory), key);
                var configuration = MaterializeProject(runtime.CreateTemplate(directory), serverProject);
                configuration.ResolvePaths(Path.Combine(directory, "runtime.json"));
                configuration.Validate();
                applied.Add((Path.Combine(directory, "runtime.json"), configuration));
            }
            preparing = null;
            ProjectConfigurationDiscovery.ValidateSet(applied, validateExecutionResources: false);
            if (updateStatus)
            {
                lock (_gate)
                {
                    var unchanged = string.Equals(_status.AppliedVersion, desired.Version, StringComparison.Ordinal) && File.Exists(_cachePath);
                    if (!unchanged) PersistAtomically(desired);
                    _appliedProjects = desired.Projects.ToArray();
                    _status = new(desired.Version, desired.Version, "synchronized",
                        unchanged ? _status.LastSuccessfulUpdateUtc : DateTimeOffset.UtcNow, null,
                        new("retrieved", "synchronized", "server-retrieved", desired.Version, desired.Version, null, null,
                            desired.Projects.Select(project => Diagnostics.Projects.FirstOrDefault(item => item.ProjectId == project.Id && item.Revision == project.Revision)
                                ?? new ManagedProjectDiagnostic(project.Id, project.Revision, "not-materialized")).ToArray()));
                }
            }
            if (!updateStatus)
                lock (_gate) _appliedProjects = desired.Projects.ToArray();
            return applied;
        }
        catch (Exception ex) when (updateStatus)
        {
            var code = stage == "contract-validation" ? "managed-contract-invalid" :
                ex is ArgumentException or InvalidDataException ? "managed-local-configuration-invalid" : "managed-synchronization-failed";
            var context = preparing is null ? "" : $" Project '{preparing.Id}', revision {preparing.Revision}.";
            var error = stage == "contract-validation" ? "Server configuration was retrieved but contract validation failed. " + (ex is InvalidDataException ? ex.Message : "Invalid contract payload.") :
                "Server configuration was retrieved but local configuration synchronization failed.";
            lock (_gate) _status = _status with { DesiredVersion = desired.Version is { Length: <= 128 } version && !version.Any(char.IsControl) ? version : null,
                SynchronizationStatus = "error", Error = error + context,
                Diagnostics = Diagnostics with { Retrieval = "retrieved", Synchronization = "error", FailureStage = stage,
                    DesiredVersion = desired.Version is { Length: <= 128 } desiredVersion && !desiredVersion.Any(char.IsControl) ? desiredVersion : null, DiagnosticCode = code,
                    FailedProjectId = preparing?.Id, FailedProjectRevision = preparing?.Revision } };
            throw new InvalidDataException(error + context + " Diagnostic: " + code + ".", ex);
        }
    }

    private void PersistAtomically(ServerManagedConfigurationContract desired)
    {
        var directory = Path.GetDirectoryName(_cachePath) ?? throw new InvalidDataException("Configuration cache path must include a directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(desired, JsonOptions));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, _cachePath, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_cachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    internal static void ValidateSnapshot(ServerManagedConfigurationContract snapshot, bool cachedLegacy = false)
    {
        if (snapshot.ContractVersion != 2 && !(cachedLegacy && snapshot.ContractVersion == 1))
            throw new InvalidDataException($"Unsupported contract version: expected 2, received {snapshot.ContractVersion}. Upgrade Worker and Server to matching supported contracts.");
        if (string.IsNullOrWhiteSpace(snapshot.Version) || snapshot.Version.Length > 128 ||
            snapshot.Projects is null || snapshot.Projects.Count > 1000)
            throw new InvalidDataException("Server returned an unsupported or invalid managed configuration snapshot.");
        if (snapshot.Projects.Any(project => project is null || project.Revision < 1 || !ValidProject(project)))
            throw new InvalidDataException("Invalid project definition in Server managed configuration.");
        if (snapshot.Projects.Select(project => project.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Projects.Count)
            throw new InvalidDataException("Server returned duplicate managed project IDs.");
        var duplicate = snapshot.Projects.GroupBy(project => project.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException("Server returned duplicate managed project names.");
        var expected = CalculateVersion(snapshot.Projects, cachedLegacy);
        if (!string.Equals(expected, snapshot.Version, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot version/hash mismatch: Server managed configuration version does not match its contents.");
    }

    private static bool ValidProject(ServerProjectContract project) =>
        !string.IsNullOrWhiteSpace(project.Id) && project.Id.Length <= 120 && !project.Id.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(project.Name) && project.Name.Length <= 120 &&
        System.Text.RegularExpressions.Regex.IsMatch(project.Name, "^[\\p{L}\\p{N}][\\p{L}\\p{N} ._-]*$") &&
        !string.IsNullOrWhiteSpace(project.Repository) && System.Text.RegularExpressions.Regex.IsMatch(project.Repository, "^[^/\\s]+/[^/\\s]+$") &&
        !string.IsNullOrWhiteSpace(project.DefaultBranch) && project.DefaultBranch.Length <= 200 && !project.DefaultBranch.Any(char.IsControl) &&
        project.MaxParallelTasks is not (< 1 or > 8) &&
        project.Description is not null && project.Description.Length <= 4000 &&
        project.Requirements is { Count: <= 64 } && project.Requirements.All(requirement =>
            requirement is not null && !string.IsNullOrWhiteSpace(requirement.Type) && requirement.Type.Length <= 40 && !requirement.Type.Any(char.IsControl) &&
            !string.IsNullOrWhiteSpace(requirement.Name) && requirement.Name.Length <= 100 && !requirement.Name.Any(char.IsControl) &&
            (requirement.Version is null || (requirement.Version.Length <= 100 && !requirement.Version.Any(char.IsControl))) &&
            (requirement.Scope is null || (requirement.Scope.Length <= 200 && !requirement.Scope.Any(char.IsControl))));

    public static string CalculateVersion(IReadOnlyList<ServerProjectContract> projects, bool legacy = false)
    {
        var materialBuilder = new StringBuilder();
        foreach (var project in projects.OrderBy(project => project.Id, StringComparer.Ordinal))
        {
            Append(materialBuilder, "project");
            Append(materialBuilder, project.Id);
            Append(materialBuilder, project.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(materialBuilder, project.Name);
            Append(materialBuilder, project.Repository);
            Append(materialBuilder, project.DefaultBranch);
            Append(materialBuilder, project.Description);
            if (!legacy) Append(materialBuilder, project.MaxParallelTasks?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(materialBuilder, project.Requirements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var requirement in project.Requirements.OrderBy(item => item.Type, StringComparer.Ordinal)
                         .ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal)
                         .ThenBy(item => item.Scope, StringComparer.Ordinal))
            {
                Append(materialBuilder, requirement.Type);
                Append(materialBuilder, requirement.Name);
                Append(materialBuilder, requirement.Version);
                Append(materialBuilder, requirement.Scope);
            }
            Append(materialBuilder, "end-project");
        }
        var material = materialBuilder.ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string? value)
    {
        if (value is null) target.Append("-1:");
        else target.Append(value.Length).Append(':').Append(value);
    }

    private static WorkerConfiguration MaterializeProject(WorkerConfiguration defaults, ServerProjectContract server)
    {
        var clone = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = server.Name, Repository = server.Repository, Directory = defaults.Project.Directory },
            Git = new GitSettings
            {
                BaseBranch = server.DefaultBranch, FeaturePrefix = defaults.Git.FeaturePrefix, CompletedPrefix = defaults.Git.CompletedPrefix,
                AutoMerge = defaults.Git.AutoMerge, PushCompletedBranch = defaults.Git.PushCompletedBranch,
                DeleteLocalFeatureBranch = defaults.Git.DeleteLocalFeatureBranch
            },
            GitHub = new GitHubSettings
            {
                ReadyLabel = defaults.GitHub.ReadyLabel, WorkingLabel = defaults.GitHub.WorkingLabel,
                BlockedLabel = defaults.GitHub.BlockedLabel, FailedLabel = defaults.GitHub.FailedLabel,
                IntegrationConflictLabel = defaults.GitHub.IntegrationConflictLabel,
                IntegrationRecoveryLabel = defaults.GitHub.IntegrationRecoveryLabel, DoneLabel = defaults.GitHub.DoneLabel
            },
            Codex = new CodexSettings
            {
                InstructionsFile = defaults.Codex.InstructionsFile, Model = defaults.Codex.Model,
                ReasoningEffort = defaults.Codex.ReasoningEffort, TimeoutMinutes = defaults.Codex.TimeoutMinutes
            },
            Validation = new ValidationSettings
            {
                Commands = [.. defaults.Validation.Commands], TimeoutSeconds = defaults.Validation.TimeoutSeconds,
                MaxFixAttempts = defaults.Validation.MaxFixAttempts
            },
            Environment = new ProjectEnvironmentSettings { File = defaults.Environment.File, Variables = defaults.Environment.Variables },
            Worker = new WorkerSettings
            {
                GitTimeoutSeconds = defaults.Worker.GitTimeoutSeconds, GitHubTimeoutSeconds = defaults.Worker.GitHubTimeoutSeconds,
                // Managed admission uses global node capacity and the Server policy, never a local project default.
                MaxParallelTasks = 8, RetryMode = defaults.Worker.RetryMode,
                RecoveryRetentionDays = defaults.Worker.RecoveryRetentionDays
            }
        };
        return clone;
    }

}
