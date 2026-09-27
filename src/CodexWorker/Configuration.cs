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
    public TelegramSettings Telegram { get; set; } = new();
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
        if (Codex.PreflightTimeoutSeconds <= 0 || Codex.PreflightTimeoutSeconds > 300)
            errors.Add("codex.preflightTimeoutSeconds must be between 1 and 300.");
        if (!new[] { "low", "medium", "high", "xhigh" }.Contains(Codex.ReasoningEffort, StringComparer.OrdinalIgnoreCase))
            errors.Add("codex.reasoningEffort must be low, medium, high, or xhigh.");
        if (Worker.PollingSeconds <= 0) errors.Add("worker.pollingSeconds must be greater than zero.");
        if (Worker.GitTimeoutSeconds <= 0) errors.Add("worker.gitTimeoutSeconds must be greater than zero.");
        if (Worker.GitHubTimeoutSeconds <= 0) errors.Add("worker.githubTimeoutSeconds must be greater than zero.");
        if (Validation.TimeoutSeconds <= 0) errors.Add("validation.timeoutSeconds must be greater than zero.");
        if (Validation.MaxFixAttempts < 0 || Validation.MaxFixAttempts > 5)
            errors.Add("validation.maxFixAttempts must be between 0 and 5.");
        if (Validation.Commands.Any(string.IsNullOrWhiteSpace)) errors.Add("validation.commands cannot contain empty commands.");
        if (errors.Count > 0) throw new InvalidDataException("Invalid configuration:\n- " + string.Join("\n- ", errors));
    }

    private void ResolvePaths(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;
        if (!string.IsNullOrWhiteSpace(Project.Directory)) Project.Directory = Path.GetFullPath(Project.Directory, configDirectory);
        if (!string.IsNullOrWhiteSpace(Codex.InstructionsFile) && !Path.IsPathRooted(Codex.InstructionsFile) && !string.IsNullOrWhiteSpace(Project.Directory))
            Codex.InstructionsFile = Path.GetFullPath(Codex.InstructionsFile, Project.Directory);
    }

    private static void Required(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name} is required.");
    }
}

public sealed class ProjectSettings { public string Name { get; set; } = ""; public string Repository { get; set; } = ""; public string Directory { get; set; } = ""; }
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
}
public sealed class CodexSettings
{
    public string InstructionsFile { get; set; } = "AGENTS.md";
    public string? Model { get; set; }
    public string ReasoningEffort { get; set; } = "medium";
    public int TimeoutMinutes { get; set; } = 60;
    public int PreflightTimeoutSeconds { get; set; } = 60;
}
public sealed class ValidationSettings
{
    public List<string> Commands { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 900;
    public int MaxFixAttempts { get; set; } = 2;
}
public sealed class TelegramSettings { public bool Enabled { get; set; } }
public sealed class WorkerSettings
{
    public int PollingSeconds { get; set; } = 60;
    public int GitTimeoutSeconds { get; set; } = 120;
    [YamlMember(Alias = "githubTimeoutSeconds")]
    public int GitHubTimeoutSeconds { get; set; } = 60;
}
