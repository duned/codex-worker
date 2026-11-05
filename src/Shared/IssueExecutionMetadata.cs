using System.Text.RegularExpressions;

namespace CodexProvisioning;

/// <summary>Untrusted Issue selection only; never supplies commands or authorization.</summary>
public sealed record IssueExecutionMetadata(string Engine = "codex", string? Model = null, string? Effort = null, string? RetryMode = null)
{
    public static IssueExecutionMetadata Parse(string body)
    {
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>();
        var legacy = new Dictionary<string, string>();
        var section = "Codex";
        var inSection = false;
        string? fence = null;
        foreach (var raw in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (fence is not null)
            {
                if (line.StartsWith(fence, StringComparison.Ordinal) && line.Trim(fence[0]).Length == 0)
                    fence = null;
                continue;
            }
            if (!inSection)
            {
                // Markdown code examples are not execution metadata, including fences
                // longer than three characters that contain shorter nested examples.
                if (raw.StartsWith("    ", StringComparison.Ordinal) || raw.StartsWith('\t')) continue;
                var openingFence = Regex.Match(line, @"^(`{3,}|~{3,})", RegexOptions.CultureInvariant);
                if (openingFence.Success)
                {
                    fence = openingFence.Value;
                    continue;
                }
            }
            if (Regex.IsMatch(line, @"^##\s+(Codex|Worker)\s*(?:#+\s*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                section = Regex.Match(line, @"^##\s+(Codex|Worker)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups[1].Value;
                if (!sections.Add(section)) throw Invalid($"Use only one ## {section} section.");
                inSection = true;
                continue;
            }
            if (Regex.IsMatch(line, @"^#{1,2}\s+")) inSection = false;
            if (!inSection || line.Length == 0) continue;
            var match = Regex.Match(line, @"^(engine|model|effort|retryMode):\s*(\S+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) throw Invalid("Use plain engine:, model:, effort: and retryMode: values, followed by a new level 1 or 2 heading for prose.");
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value;
            var target = section.Equals("Codex", StringComparison.OrdinalIgnoreCase) ? legacy : values;
            if (target == legacy && key == "engine") throw Invalid("engine is supported only in ## Worker.");
            if (!target.TryAdd(key, value)) throw Invalid($"Remove the duplicate {key} key.");
        }
        var engine = values.GetValueOrDefault("engine", "codex").ToLowerInvariant();
        if (engine is not ("codex" or "ccc")) throw Invalid("engine must be codex or ccc.");
        if (engine == "ccc" && legacy.Count > 0) throw Invalid("## Codex settings are incompatible with engine: ccc.");
        foreach (var pair in legacy)
        {
            if (values.TryGetValue(pair.Key, out var other) && !string.Equals(pair.Value, other,
                pair.Key is "effort" or "retryMode" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw Invalid($"Conflicting {pair.Key} values in ## Worker and ## Codex.");
            values.TryAdd(pair.Key, pair.Value);
        }
        var model = values.GetValueOrDefault("model");
        var effort = values.GetValueOrDefault("effort")?.ToLowerInvariant();
        var result = new IssueExecutionMetadata(engine, model, effort, values.GetValueOrDefault("retryMode")?.ToLowerInvariant());
        if (ValidationError(result) is { } error) throw Invalid(error);
        return result;

        InvalidDataException Invalid(string reason) => new($"Invalid ## {(sections.Contains("Worker") ? "Worker" : "Codex")} metadata: {reason}");
    }

    public static string? ValidationError(IssueExecutionMetadata? selection)
    {
        if (selection is null) return "Execution selection is required.";
        var (engine, model, effort, retryMode) = selection;
        if (retryMode is not null && retryMode is not ("restart" or "resume")) return "retryMode must be restart or resume.";
        if (engine is not ("codex" or "ccc")) return "engine must be codex or ccc.";
        if (model is not null && !Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
            return "model must be a model ID of at most 128 letters, digits, dots, underscores or hyphens, starting with a letter or digit.";
        if (engine == "ccc" && model is not null && model is not ("sonnet" or "opus" or "haiku"))
            return "CCC model must be sonnet, opus, or haiku.";
        string[] efforts = engine == "ccc" ? ["low", "medium", "high"] : ["low", "medium", "high", "xhigh"];
        if (effort is not null && !efforts.Contains(effort, StringComparer.Ordinal))
            return engine == "codex" ? "effort must be low, medium, high, or xhigh." : "CCC effort must be low, medium, or high.";
        return null;
    }
}
