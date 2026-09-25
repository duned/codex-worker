using System.Globalization;

namespace CodexWorker;

public sealed class WorkerConfiguration
{
    public ProjectSettings Project { get; set; } = new();
    public GitSettings Git { get; set; } = new();
    public GitHubSettings GitHub { get; set; } = new();
    public CodexSettings Codex { get; set; } = new();
    public ValidationSettings Validation { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public WorkerSettings Worker { get; set; } = new();

    public static WorkerConfiguration Load(string path)
    {
        var root = SimpleYaml.Parse(File.ReadAllText(path));
        var config = FromYaml(root);
        config.ResolvePaths(Path.GetFullPath(path));
        config.Validate();
        return config;
    }

    private static WorkerConfiguration FromYaml(Dictionary<string, object?> root)
    {
        CheckKeys(root, "project", "git", "github", "codex", "validation", "telegram", "worker");
        var project = Map(root, "project");
        var git = Map(root, "git");
        var github = Map(root, "github");
        var codex = Map(root, "codex");
        var validation = Map(root, "validation");
        var telegram = Map(root, "telegram");
        var worker = Map(root, "worker");
        CheckKeys(project, "name", "repository", "directory");
        CheckKeys(git, "baseBranch", "featurePrefix", "completedPrefix", "autoMerge", "pushCompletedBranch", "deleteLocalFeatureBranch");
        CheckKeys(github, "readyLabel", "workingLabel", "blockedLabel", "failedLabel", "doneLabel");
        CheckKeys(codex, "instructionsFile", "model", "reasoningEffort", "timeoutMinutes");
        CheckKeys(validation, "commands");
        CheckKeys(telegram, "enabled");
        CheckKeys(worker, "pollingSeconds");

        return new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = String(project, "name"), Repository = String(project, "repository"), Directory = String(project, "directory") },
            Git = new GitSettings
            {
                BaseBranch = String(git, "baseBranch", "main"), FeaturePrefix = String(git, "featurePrefix", "feature/"),
                CompletedPrefix = String(git, "completedPrefix", "completed/"), AutoMerge = Bool(git, "autoMerge", true),
                PushCompletedBranch = Bool(git, "pushCompletedBranch", true), DeleteLocalFeatureBranch = Bool(git, "deleteLocalFeatureBranch", true)
            },
            GitHub = new GitHubSettings
            {
                ReadyLabel = String(github, "readyLabel"), WorkingLabel = String(github, "workingLabel"),
                BlockedLabel = String(github, "blockedLabel"), FailedLabel = String(github, "failedLabel"), DoneLabel = String(github, "doneLabel")
            },
            Codex = new CodexSettings
            {
                InstructionsFile = String(codex, "instructionsFile", "AGENTS.md"), Model = NullableString(codex, "model"),
                ReasoningEffort = String(codex, "reasoningEffort", "medium"), TimeoutMinutes = Integer(codex, "timeoutMinutes", 60)
            },
            Validation = new ValidationSettings { Commands = StringList(validation, "commands") },
            Telegram = new TelegramSettings { Enabled = Bool(telegram, "enabled", false) },
            Worker = new WorkerSettings { PollingSeconds = Integer(worker, "pollingSeconds", 60) }
        };
    }

    private static Dictionary<string, object?> Map(Dictionary<string, object?> parent, string key) =>
        parent.TryGetValue(key, out var value) && value is Dictionary<string, object?> map ? map :
        throw new InvalidDataException($"Configuration section '{key}' must be a YAML mapping.");
    private static string String(Dictionary<string, object?> map, string key, string? fallback = null) =>
        map.TryGetValue(key, out var value) && value is string text ? text : fallback ?? "";
    private static string? NullableString(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is string text ? text : null;
    private static bool Bool(Dictionary<string, object?> map, string key, bool fallback) =>
        !map.TryGetValue(key, out var value) ? fallback : value is string text && bool.TryParse(text, out var result) ? result :
        throw new InvalidDataException($"Configuration value '{key}' must be true or false.");
    private static int Integer(Dictionary<string, object?> map, string key, int fallback) =>
        !map.TryGetValue(key, out var value) ? fallback : value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result :
        throw new InvalidDataException($"Configuration value '{key}' must be an integer.");
    private static List<string> StringList(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value)) return [];
        if (value is not List<object?> list || list.Any(item => item is not string))
            throw new InvalidDataException($"Configuration value '{key}' must be a YAML list of strings.");
        return list.Cast<string>().ToList();
    }
    private static void CheckKeys(Dictionary<string, object?> map, params string[] valid)
    {
        var unknown = map.Keys.Except(valid, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw new InvalidDataException($"Unknown configuration key(s): {string.Join(", ", unknown)}.");
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
        if (Worker.PollingSeconds <= 0) errors.Add("worker.pollingSeconds must be greater than zero.");
        if (Validation.Commands.Any(string.IsNullOrWhiteSpace)) errors.Add("validation.commands cannot contain empty commands.");
        if (errors.Count > 0) throw new InvalidDataException("Invalid configuration:\n- " + string.Join("\n- ", errors));
    }

    private void ResolvePaths(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;
        Project.Directory = Resolve(configDirectory, Project.Directory);
        if (!Path.IsPathRooted(Codex.InstructionsFile))
            Codex.InstructionsFile = Path.GetFullPath(Codex.InstructionsFile, Project.Directory);
    }

    private static string Resolve(string basePath, string path) => Path.GetFullPath(path, basePath);
    private static void Required(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name} is required.");
    }
}

// Parses the block mappings, block sequences, and scalar values used by the worker's YAML schema.
// Unsupported YAML syntax fails explicitly instead of silently changing configuration meaning.
internal static class SimpleYaml
{
    private sealed record Line(int Indent, string Text, int Number);
    private static List<Line> _lines = [];
    private static int _index;

    public static Dictionary<string, object?> Parse(string source)
    {
        _lines = source.Split('\n').Select((raw, i) => (raw.TrimEnd('\r'), i + 1))
            .Where(x => !string.IsNullOrWhiteSpace(x.Item1) && !x.Item1.TrimStart().StartsWith('#') && x.Item1.Trim() is not ("---" or "..."))
            .Select(x =>
            {
                var indent = x.Item1.TakeWhile(c => c == ' ').Count();
                if (x.Item1.Take(indent).Contains('\t')) throw new InvalidDataException($"YAML line {x.Item2}: tabs cannot indent configuration.");
                return new Line(indent, StripComment(x.Item1[indent..]).TrimEnd(), x.Item2);
            }).Where(l => l.Text.Length > 0).ToList();
        _index = 0;
        if (_lines.Count == 0) throw new InvalidDataException("Configuration YAML is empty.");
        if (_lines[0].Indent != 0) throw Error(_lines[0], "top-level configuration must start at column 1");
        var root = ParseNode(0);
        if (root is not Dictionary<string, object?> map) throw Error(_lines[0], "top-level configuration must be a mapping");
        if (_index != _lines.Count) throw Error(_lines[_index], "unexpected indentation");
        return map;
    }

    private static object ParseNode(int indent) => _lines[_index].Text.StartsWith("- ", StringComparison.Ordinal) || _lines[_index].Text == "-"
        ? ParseSequence(indent) : ParseMapping(indent);

    private static Dictionary<string, object?> ParseMapping(int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (_index < _lines.Count && _lines[_index].Indent == indent && !_lines[_index].Text.StartsWith("- ", StringComparison.Ordinal))
        {
            var line = _lines[_index++];
            var colon = line.Text.IndexOf(':');
            if (colon <= 0) throw Error(line, "expected 'key: value'");
            var key = line.Text[..colon].Trim();
            if (key.Contains(' ') || key.Contains('{') || key.Contains('[')) throw Error(line, "unsupported mapping key syntax");
            if (!map.TryAdd(key, null)) throw Error(line, $"duplicate key '{key}'");
            var rawValue = line.Text[(colon + 1)..].Trim();
            if (rawValue.Length > 0) map[key] = ParseScalar(rawValue, line);
            else if (_index < _lines.Count && _lines[_index].Indent > indent) map[key] = ParseNode(_lines[_index].Indent);
            else map[key] = new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        return map;
    }

    private static List<object?> ParseSequence(int indent)
    {
        var list = new List<object?>();
        while (_index < _lines.Count && _lines[_index].Indent == indent && (_lines[_index].Text == "-" || _lines[_index].Text.StartsWith("- ", StringComparison.Ordinal)))
        {
            var line = _lines[_index++];
            var scalar = line.Text.Length == 1 ? "" : line.Text[2..].Trim();
            if (scalar.Length == 0)
            {
                if (_index >= _lines.Count || _lines[_index].Indent <= indent) throw Error(line, "empty sequence item");
                list.Add(ParseNode(_lines[_index].Indent));
            }
            else list.Add(ParseScalar(scalar, line));
        }
        return list;
    }

    private static object? ParseScalar(string value, Line line)
    {
        if (value.StartsWith('[') || value.StartsWith('{') || value.StartsWith('|') || value.StartsWith('>'))
            throw Error(line, "flow collections and block scalar values are not supported");
        if (value.StartsWith('"'))
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(value) ?? ""; }
            catch (System.Text.Json.JsonException) { throw Error(line, "invalid double-quoted scalar"); }
        }
        if (value.StartsWith('\''))
        {
            if (value.Length < 2 || !value.EndsWith('\'')) throw Error(line, "invalid single-quoted scalar");
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        if ((value.EndsWith('"') || value.EndsWith('\'')) && value[0] is not ('"' or '\'')) throw Error(line, "unmatched quote");
        if (value is "null" or "Null" or "NULL" or "~") return null;
        return value;
    }

    private static string StripComment(string text)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            if (quote == '\'' && text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
            if (quote != '\0' && text[i] == quote) { quote = '\0'; continue; }
            if (quote == '\0' && text[i] is '\'' or '"') { quote = text[i]; continue; }
            if (quote == '\0' && text[i] == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1]))) return text[..i];
        }
        return text;
    }

    private static InvalidDataException Error(Line line, string message) => new($"YAML line {line.Number}: {message}.");
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
}
public sealed class ValidationSettings { public List<string> Commands { get; set; } = []; }
public sealed class TelegramSettings { public bool Enabled { get; set; } }
public sealed class WorkerSettings { public int PollingSeconds { get; set; } = 60; }
