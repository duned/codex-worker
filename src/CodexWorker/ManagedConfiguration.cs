using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexWorker;

/// <summary>Server-owned orchestration snapshot. Machine paths and credentials are deliberately absent.</summary>
public sealed record ServerManagedConfigurationContract(int ContractVersion, string Version,
    IReadOnlyList<ServerProjectContract> Projects);

public sealed record WorkerConfigurationSyncStatus(string? DesiredVersion, string? AppliedVersion,
    string SynchronizationStatus, DateTimeOffset? LastSuccessfulUpdateUtc, string? Error);

/// <summary>Maintains the last validated Server snapshot as an atomic, secret-free local cache.</summary>
public sealed class ManagedConfigurationSynchronizer(string cachePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _cachePath = Path.GetFullPath(cachePath);
    private readonly object _gate = new();
    private WorkerConfigurationSyncStatus _status = new(null, null, "not-synchronized", null, null);

    public WorkerConfigurationSyncStatus Status { get { lock (_gate) return _status; } }
    public bool HasCachedSnapshot => File.Exists(_cachePath);

    public IReadOnlyList<(string Path, WorkerConfiguration Configuration)> LoadLastValid(
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> localProjects)
    {
        if (!File.Exists(_cachePath)) throw new InvalidDataException("No previously applied Server configuration is available.");
        var snapshot = JsonSerializer.Deserialize<ServerManagedConfigurationContract>(File.ReadAllText(_cachePath), JsonOptions)
            ?? throw new InvalidDataException("The cached Server configuration is empty.");
        var result = Apply(snapshot, localProjects, updateStatus: false);
        lock (_gate) _status = _status with
        {
            AppliedVersion = snapshot.Version,
            SynchronizationStatus = _status.Error is null ? "cached" : _status.SynchronizationStatus,
            LastSuccessfulUpdateUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(_cachePath), TimeSpan.Zero)
        };
        return result;
    }

    public IReadOnlyList<(string Path, WorkerConfiguration Configuration)> Apply(
        ServerManagedConfigurationContract desired,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> localProjects) => Apply(desired, localProjects, updateStatus: true);

    public void RecordUnavailable(Exception exception)
    {
        lock (_gate) _status = _status with
        {
            SynchronizationStatus = _status.Error is null ? "unavailable" : "error",
            Error = _status.Error ?? SafeError(exception)
        };
    }

    private IReadOnlyList<(string Path, WorkerConfiguration Configuration)> Apply(
        ServerManagedConfigurationContract desired,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> localProjects, bool updateStatus)
    {
        try
        {
            ValidateSnapshot(desired);
            var byName = localProjects.ToDictionary(project => project.Configuration.Project.Name, StringComparer.OrdinalIgnoreCase);
            var applied = new List<(string Path, WorkerConfiguration Configuration)>();
            foreach (var serverProject in desired.Projects)
            {
                if (!byName.TryGetValue(serverProject.Name, out var local))
                    throw new InvalidDataException($"Server project '{serverProject.Name}' has no machine-local project checkout configuration.");
                var configuration = CloneWithServerOwnedProject(local.Configuration, serverProject);
                configuration.Validate();
                applied.Add((local.Path, configuration));
            }
            ProjectConfigurationDiscovery.ValidateSet(applied);
            if (updateStatus)
            {
                lock (_gate)
                {
                    var unchanged = string.Equals(_status.AppliedVersion, desired.Version, StringComparison.Ordinal) && File.Exists(_cachePath);
                    if (!unchanged) PersistAtomically(desired);
                    _status = new(desired.Version, desired.Version, "synchronized",
                        unchanged ? _status.LastSuccessfulUpdateUtc : DateTimeOffset.UtcNow, null);
                }
            }
            return applied;
        }
        catch (Exception ex) when (updateStatus)
        {
            lock (_gate) _status = _status with { DesiredVersion = desired.Version, SynchronizationStatus = "error", Error = SafeError(ex) };
            throw;
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

    private static void ValidateSnapshot(ServerManagedConfigurationContract snapshot)
    {
        if (snapshot.ContractVersion != 1 || string.IsNullOrWhiteSpace(snapshot.Version) || snapshot.Version.Length > 128 ||
            snapshot.Projects is null || snapshot.Projects.Count is 0 or > 1000)
            throw new InvalidDataException("Server returned an unsupported or invalid managed configuration snapshot.");
        if (snapshot.Projects.Any(project => project is null || project.Revision < 1 || !ValidProject(project)))
            throw new InvalidDataException("Server returned an invalid managed project configuration.");
        var duplicate = snapshot.Projects.GroupBy(project => project.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Server returned duplicate managed project '{duplicate.Key}'.");
        var expected = CalculateVersion(snapshot.Projects);
        if (!string.Equals(expected, snapshot.Version, StringComparison.Ordinal))
            throw new InvalidDataException("Server managed configuration version does not match its contents.");
    }

    private static bool ValidProject(ServerProjectContract project) =>
        !string.IsNullOrWhiteSpace(project.Id) && project.Id.Length <= 120 && !project.Id.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(project.Name) && project.Name.Length <= 120 &&
        System.Text.RegularExpressions.Regex.IsMatch(project.Name, "^[\\p{L}\\p{N}][\\p{L}\\p{N} ._-]*$") &&
        !string.IsNullOrWhiteSpace(project.Repository) && System.Text.RegularExpressions.Regex.IsMatch(project.Repository, "^[^/\\s]+/[^/\\s]+$") &&
        !string.IsNullOrWhiteSpace(project.DefaultBranch) && project.DefaultBranch.Length <= 200 && !project.DefaultBranch.Any(char.IsControl) &&
        project.Description is not null && project.Description.Length <= 4000 &&
        project.Requirements is { Count: <= 64 } && project.Requirements.All(requirement =>
            requirement is not null && !string.IsNullOrWhiteSpace(requirement.Type) && requirement.Type.Length <= 40 && !requirement.Type.Any(char.IsControl) &&
            !string.IsNullOrWhiteSpace(requirement.Name) && requirement.Name.Length <= 100 && !requirement.Name.Any(char.IsControl) &&
            (requirement.Version is null || (requirement.Version.Length <= 100 && !requirement.Version.Any(char.IsControl))) &&
            (requirement.Scope is null || (requirement.Scope.Length <= 200 && !requirement.Scope.Any(char.IsControl))));

    public static string CalculateVersion(IReadOnlyList<ServerProjectContract> projects)
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

    private static WorkerConfiguration CloneWithServerOwnedProject(WorkerConfiguration local, ServerProjectContract server)
    {
        var clone = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = server.Name, Repository = server.Repository, Directory = local.Project.Directory },
            Git = new GitSettings
            {
                BaseBranch = server.DefaultBranch, FeaturePrefix = local.Git.FeaturePrefix, CompletedPrefix = local.Git.CompletedPrefix,
                AutoMerge = local.Git.AutoMerge, PushCompletedBranch = local.Git.PushCompletedBranch,
                DeleteLocalFeatureBranch = local.Git.DeleteLocalFeatureBranch
            },
            GitHub = new GitHubSettings
            {
                ReadyLabel = local.GitHub.ReadyLabel, WorkingLabel = local.GitHub.WorkingLabel,
                BlockedLabel = local.GitHub.BlockedLabel, FailedLabel = local.GitHub.FailedLabel, DoneLabel = local.GitHub.DoneLabel
            },
            Codex = new CodexSettings
            {
                InstructionsFile = local.Codex.InstructionsFile, Model = local.Codex.Model,
                ReasoningEffort = local.Codex.ReasoningEffort, TimeoutMinutes = local.Codex.TimeoutMinutes
            },
            Validation = new ValidationSettings
            {
                Commands = [.. local.Validation.Commands], TimeoutSeconds = local.Validation.TimeoutSeconds,
                MaxFixAttempts = local.Validation.MaxFixAttempts
            },
            Environment = local.Environment,
            Worker = new WorkerSettings
            {
                GitTimeoutSeconds = local.Worker.GitTimeoutSeconds, GitHubTimeoutSeconds = local.Worker.GitHubTimeoutSeconds,
                MaxParallelTasks = local.Worker.MaxParallelTasks, RetryMode = local.Worker.RetryMode,
                RecoveryRetentionDays = local.Worker.RecoveryRetentionDays
            }
        };
        return clone;
    }

    private static string SafeError(Exception exception) => exception.Message.Length <= 500 ? exception.Message : exception.Message[..500];
}
