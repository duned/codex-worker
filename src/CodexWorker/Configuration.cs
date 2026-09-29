using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodexWorker;

public sealed class WorkerConfiguration
{
    public ProjectSettings Project { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    [YamlMember(Alias = "github")]
    public GitHubSettings GitHub { get; set; } = new();
    public CodexSettings Codex { get; set; } = new();
    public ValidationSettings Validation { get; set; } = new();
    public ProjectEnvironmentSettings Environment { get; set; } = new();
    public WorkerSettings Worker { get; set; } = new();

    public static WorkerConfiguration Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();
        WorkerConfiguration config;
        try { config = deserializer.Deserialize<WorkerConfiguration>(File.ReadAllText(path)) ?? throw new InvalidDataException("Configuration YAML is empty."); }
        catch (YamlDotNet.Core.YamlException ex) { throw new InvalidDataException($"Invalid YAML configuration: {ex.Message}", ex); }
        config.ResolvePaths(Path.GetFullPath(path));
        config.Validate();
        return config;
    }

    public void Validate()
    {
        var errors = new List<string>();
        Required(Project.Name, "project.name", errors);
        Required(Project.Repository, "project.repository", errors);
        if (!System.Text.RegularExpressions.Regex.IsMatch(Project.Repository, "^[^/\\s]+/[^/\\s]+$"))
            errors.Add("project.repository must be in owner/repository form.");
        Required(Project.Directory, "project.directory", errors);
        Required(Git.BaseBranch, "git.baseBranch", errors);
        Required(Git.FeaturePrefix, "git.featurePrefix", errors);
        Required(Git.CompletedPrefix, "git.completedPrefix", errors);
        Required(GitHub.ReadyLabel, "github.readyLabel", errors);
        Required(GitHub.WorkingLabel, "github.workingLabel", errors);
        Required(GitHub.BlockedLabel, "github.blockedLabel", errors);
        Required(GitHub.FailedLabel, "github.failedLabel", errors);
        Required(GitHub.DoneLabel, "github.doneLabel", errors);
        var labels = new[] { GitHub.ReadyLabel, GitHub.WorkingLabel, GitHub.BlockedLabel, GitHub.FailedLabel, GitHub.DoneLabel };
        if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Length)
            errors.Add("github labels must be distinct.");
        Required(Codex.InstructionsFile, "codex.instructionsFile", errors);
        if (Codex.TimeoutMinutes <= 0) errors.Add("codex.timeoutMinutes must be greater than zero.");
        if (!new[] { "low", "medium", "high", "xhigh" }.Contains(Codex.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
            errors.Add("codex.reasoningEffort must be low, medium, high, or xhigh.");
        if (Worker.GitTimeoutSeconds <= 0) errors.Add("worker.gitTimeoutSeconds must be greater than zero.");
        if (Worker.GitHubTimeoutSeconds <= 0) errors.Add("worker.githubTimeoutSeconds must be greater than zero.");
        if (Worker.MaxParallelTasks < 1 || Worker.MaxParallelTasks > 8)
            errors.Add("worker.maxParallelTasks must be between 1 and 8.");
        if (!new[] { "restart", "resume" }.Contains(Worker.RetryMode, StringComparer.OrdinalIgnoreCase))
            errors.Add("worker.retryMode must be restart or resume.");
        if (Worker.RecoveryRetentionDays < 1 || Worker.RecoveryRetentionDays > 3650)
            errors.Add("worker.recoveryRetentionDays must be between 1 and 3650.");
        if (Validation.TimeoutSeconds <= 0) errors.Add("validation.timeoutSeconds must be greater than zero.");
        if (Validation.MaxFixAttempts < 0 || Validation.MaxFixAttempts > 5)
            errors.Add("validation.maxFixAttempts must be between 0 and 5.");
        if (Validation.Commands.Any(string.IsNullOrWhiteSpace)) errors.Add("validation.commands cannot contain empty commands.");
        if (errors.Count > 0) throw new InvalidDataException("Invalid configuration:\n- " + string.Join("\n- ", errors));
    }

    internal void ResolvePaths(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;
        if (!string.IsNullOrWhiteSpace(Project.Directory)) Project.Directory = Path.GetFullPath(Project.Directory, configDirectory);
        if (!string.IsNullOrWhiteSpace(Environment.File))
        {
            Environment.File = Path.GetFullPath(Environment.File, configDirectory);
            Environment.Variables = ProjectEnvironmentFile.Load(Environment.File);
        }
        if (!string.IsNullOrWhiteSpace(Codex.InstructionsFile) && !Path.IsPathRooted(Codex.InstructionsFile) && !string.IsNullOrWhiteSpace(Project.Directory))
            Codex.InstructionsFile = Path.GetFullPath(Codex.InstructionsFile, Project.Directory);
    }

    private static void Required(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name} is required.");
    }
}

/// <summary>Global settings shared by one worker process with bounded execution concurrency.</summary>
public sealed class GlobalWorkerConfiguration
{
    public GlobalWorkerSettings Worker { get; set; } = new();
    public ProjectsSettings Projects { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public ManagementApiSettings Api { get; set; } = new();
    public WorkerServerSettings Server { get; set; } = new();

    public static GlobalWorkerConfiguration Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var value = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithDuplicateKeyChecking().Build().Deserialize<GlobalWorkerConfiguration>(File.ReadAllText(fullPath))
                ?? throw new InvalidDataException("Configuration YAML is empty.");
            if (string.IsNullOrWhiteSpace(value.Projects.Directory)) throw new InvalidDataException("projects.directory is required.");
            if (!Path.IsPathRooted(value.Projects.Directory)) value.Projects.Directory = Path.GetFullPath(value.Projects.Directory, Path.GetDirectoryName(fullPath)!);
            if (!string.IsNullOrWhiteSpace(value.Server.IdentityFile))
                value.Server.IdentityFile = WorkerPath.Resolve(value.Server.IdentityFile, Path.GetDirectoryName(fullPath)!);
            if (value.Worker.PollingSeconds <= 0) throw new InvalidDataException("worker.pollingSeconds must be greater than zero.");
            if (value.Worker.MaxParallelTasks < 1 || value.Worker.MaxParallelTasks > 8)
                throw new InvalidDataException("worker.maxParallelTasks must be between 1 and 8.");
            if (value.Worker.Provisioning is null || value.Worker.Provisioning.AllowedPrivilegedActions is null || value.Worker.Provisioning.DeniedActions is null ||
                value.Worker.Provisioning.AllowedPrivilegedActions.Count > 100 || value.Worker.Provisioning.DeniedActions.Count > 100 ||
                value.Worker.Provisioning.AllowedPrivilegedActions.Concat(value.Worker.Provisioning.DeniedActions)
                    .Any(action => string.IsNullOrWhiteSpace(action) || action.Length > 200 || action.Any(char.IsControl)))
                throw new InvalidDataException("worker.provisioning action policy lists must contain at most 100 printable action keys.");
            if (value.Worker.PreflightTimeoutSeconds <= 0 || value.Worker.PreflightTimeoutSeconds > 300)
                throw new InvalidDataException("worker.preflightTimeoutSeconds must be between 1 and 300.");
            if (value.Api.Enabled && (!Uri.TryCreate(value.Api.ListenUrl, UriKind.Absolute, out var listenUri) ||
                listenUri.Scheme is not ("http" or "https") ||
                !System.Net.IPAddress.TryParse(listenUri.Host, out var listenAddress) || !System.Net.IPAddress.IsLoopback(listenAddress)))
                throw new InvalidDataException("api.listenUrl must be an absolute HTTP URL bound to a loopback IP address.");
            if (value.Api.EventHistoryLimit < 1 || value.Api.EventHistoryLimit > 10000)
                throw new InvalidDataException("api.eventHistoryLimit must be between 1 and 10000.");
            value.Server.Validate();
            if (value.Projects.Ownership is not ("standalone" or "managed"))
                throw new InvalidDataException("projects.ownership must be standalone or managed.");
            if (value.Projects.Ownership == "managed" && !value.Server.Enabled)
                throw new InvalidDataException("projects.ownership managed requires server.enabled: true.");
            return value;
        }
        catch (YamlDotNet.Core.YamlException ex) { throw new InvalidDataException($"Invalid global worker configuration '{fullPath}': {ex.Message}", ex); }
    }
}

public sealed class GlobalWorkerSettings
{
    public int PollingSeconds { get; set; } = 60;
    public int PreflightTimeoutSeconds { get; set; } = 60;
    public int MaxParallelTasks { get; set; } = 1;
    public ProvisioningPolicy Provisioning { get; set; } = new();
}

/// <summary>Worker-side authorization policy for Server-requested provisioning.</summary>
public sealed class ProvisioningPolicy
{
    public bool Enabled { get; set; }
    public bool AllowNonPrivileged { get; set; }
    public bool AllowCredentials { get; set; }
    public List<string> AllowedPrivilegedActions { get; set; } = [];
    public List<string> DeniedActions { get; set; } = [];
}
public sealed class ProjectsSettings
{
    public string Directory { get; set; } = "./projects";
    /// <summary>Standalone means local configuration is authoritative; managed declares Codex Server authoritative.</summary>
    public string Ownership { get; set; } = "standalone";
}

public static class ProjectConfigurationDiscovery
{
    public static IReadOnlyList<(string Path, WorkerConfiguration Configuration)> Load(string directory)
    {
        if (!Directory.Exists(directory)) throw new InvalidDataException($"Projects directory does not exist: {directory}");
        var files = Directory.EnumerateFiles(directory).Where(x =>
            Path.GetExtension(x).Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(x).Equals(".yaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidDataException($"No project YAML files found in {directory}.");
        var projects = new List<(string, WorkerConfiguration)>();
        foreach (var file in files)
        {
            try { projects.Add((Path.GetFullPath(file), WorkerConfiguration.Load(file))); }
            catch (Exception ex) { throw new InvalidDataException($"Project configuration '{file}' is invalid: {ex.Message}", ex); }
        }
        ValidateSet(projects);
        return projects;
    }

    public static void ValidateSet(IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects)
    {
        Duplicate(projects, p => p.Configuration.Project.Name, "project name");
        Duplicate(projects, p => p.Configuration.Project.Repository, "GitHub repository");
        Duplicate(projects, p => Path.GetFullPath(p.Configuration.Project.Directory), "checkout directory");
        foreach (var (path, config) in projects)
        {
            if (!Directory.Exists(config.Project.Directory)) throw new InvalidDataException($"Project configuration '{path}': checkout directory does not exist: {config.Project.Directory}");
            if (!File.Exists(config.Codex.InstructionsFile)) throw new InvalidDataException($"Project configuration '{path}': configured Codex instructions file does not exist: {config.Codex.InstructionsFile}");
        }
    }

    /// <summary>Applies the same filesystem and uniqueness checks used when loading the startup project set.</summary>
    public static void ValidateCandidate(string path, WorkerConfiguration configuration,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> existing, string? replacingPath = null)
    {
        configuration.Validate();
        var candidates = existing.Where(item => replacingPath is null ||
                !string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(replacingPath), StringComparison.OrdinalIgnoreCase))
            .Append((Path.GetFullPath(path), configuration)).ToArray();
        ValidateSet(candidates);
    }

    private static void Duplicate(IReadOnlyList<(string Path, WorkerConfiguration Configuration)> items,
        Func<(string Path, WorkerConfiguration Configuration), string> key, string description)
    {
        var duplicate = items.GroupBy(key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate {description} '{duplicate.Key}' in project configuration files: {string.Join(", ", duplicate.Select(x => x.Path))}");
    }
}

/// <summary>Pure round-robin cursor. A found project advances the next scan to its successor.</summary>
public sealed class ProjectScheduler(int projectCount)
{
    private int _next;
    public int ProjectCount { get; private set; } = projectCount > 0 ? projectCount : throw new ArgumentOutOfRangeException(nameof(projectCount));
    public int NextIndex => _next;
    public IEnumerable<int> ScanOrder()
    {
        for (var i = 0; i < ProjectCount; i++) yield return (_next + i) % ProjectCount;
    }
    public void Selected(int index)
    {
        if ((uint)index >= (uint)ProjectCount) throw new ArgumentOutOfRangeException(nameof(index));
        _next = (index + 1) % ProjectCount;
    }
    public void Reconfigure(int projectCount)
    {
        if (projectCount <= 0) throw new ArgumentOutOfRangeException(nameof(projectCount));
        ProjectCount = projectCount;
        _next %= projectCount;
    }
    public async Task<int?> ScanAsync(Func<int, Task<bool>> processOne)
    {
        foreach (var index in ScanOrder())
        {
            if (!await processOne(index)) continue;
            Selected(index);
            return index;
        }
        return null;
    }
}

public sealed class ProjectSettings { public string Name { get; set; } = ""; public string Repository { get; set; } = ""; public string Directory { get; set; } = ""; }
public sealed class ProjectEnvironmentSettings
{
    public string? File { get; set; }
    [YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();
}
public sealed class GitSettings
{
    public string BaseBranch { get; set; } = "main";
    public string FeaturePrefix { get; set; } = "feature/";
    public string CompletedPrefix { get; set; } = "completed/";
    public bool AutoMerge { get; set; } = true;
    public bool PushCompletedBranch { get; set; } = true;
    public bool DeleteLocalFeatureBranch { get; set; } = true;
}
public sealed class GitHubSettings
{
    public string ReadyLabel { get; set; } = "";
    public string WorkingLabel { get; set; } = "";
    public string BlockedLabel { get; set; } = "";
    public string FailedLabel { get; set; } = "";
    public string DoneLabel { get; set; } = "";

    [YamlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RequiredGitHubLabel> RequiredLabels =>
    [
        new(ReadyLabel, "1D76DB", "Issues ready for Codex Worker"),
        new(WorkingLabel, "FBCA04", "Issue currently being processed by Codex Worker"),
        new(BlockedLabel, "D93F0B", "Issue is blocked and needs human input"),
        new(FailedLabel, "B60205", "Codex Worker could not complete the Issue"),
        new(DoneLabel, "0E8A16", "Issue completed by Codex Worker")
    ];
}
public sealed class CodexSettings
{
    public string InstructionsFile { get; set; } = "AGENTS.md";
    public string? Model { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public int TimeoutMinutes { get; set; } = 60;
}
public sealed class ValidationSettings
{
    public List<string> Commands { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 900;
    public int MaxFixAttempts { get; set; } = 2;
}
public sealed class TelegramSettings { public bool Enabled { get; set; } }
public sealed class ManagementApiSettings
{
    public bool Enabled { get; set; } = true;
    public string ListenUrl { get; set; } = "http://127.0.0.1:5080";
    public int EventHistoryLimit { get; set; } = 500;
}
public sealed class WorkerServerSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public int HeartbeatIntervalSeconds { get; set; } = 20;
    /// <summary>Optional override for the durable identity file (defaults under the user's home directory).</summary>
    public string? IdentityFile { get; set; }

    internal void Validate()
    {
        if (!Enabled) return;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("server.url must be an absolute HTTP or HTTPS URL without credentials, query, or fragment.");
        if (uri.Scheme == "http" && !IsLoopback(uri.Host))
            throw new InvalidDataException("server.url must use HTTPS unless it points to loopback.");
        if (HeartbeatIntervalSeconds is < 5 or > 300)
            throw new InvalidDataException("server.heartbeatIntervalSeconds must be between 5 and 300.");
    }

    private static bool IsLoopback(string host) =>
        System.Net.IPAddress.TryParse(host, out var address) ? System.Net.IPAddress.IsLoopback(address) : host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}

internal static class WorkerPath
{
    public static string Resolve(string path, string relativeTo)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") path = home;
        else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(home, path[2..]);
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, relativeTo);
    }
}

public sealed class WorkerSettings
{
    public int GitTimeoutSeconds { get; set; } = 120;
    [YamlMember(Alias = "githubTimeoutSeconds")]
    public int GitHubTimeoutSeconds { get; set; } = 60;
    public int MaxParallelTasks { get; set; } = 1;
    /// <summary>Retry from the authoritative base by default; resume requires verified recoverable state.</summary>
    public string RetryMode { get; set; } = "restart";
    public int RecoveryRetentionDays { get; set; } = 7;
}
