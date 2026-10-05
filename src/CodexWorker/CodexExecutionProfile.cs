using System.Text.RegularExpressions;

namespace CodexWorker;

/// <summary>Selection settings frozen for one execution; null Model delegates to the CLI, whose observed identity is stored separately.</summary>
public sealed record CodexExecutionProfile(string? Model, string Effort, string? CliModel = null)
{
    public string? EffectiveModel => Model ?? CliModel;

    public static CodexExecutionProfile Resolve(string body, CodexSettings defaults, ExecutionHistoryEntry? source = null)
    {
        // Older history has no snapshot. Resolve it once using today's configured defaults,
        // without applying edited Issue metadata to recovery of an existing execution.
        if (source is not null && source.EffectiveEffort is not null)
            return source.ModelSelectedByCli
                ? new(null, source.EffectiveEffort, source.EffectiveModel)
                : new(source.EffectiveModel, source.EffectiveEffort);
        if (source is not null && source.FailureReason?.StartsWith("Invalid ## Codex metadata:", StringComparison.Ordinal) != true)
            return new(DefaultModel(defaults), defaults.ReasoningEffort.ToLowerInvariant());

        string? model = null;
        string? effort = null;
        var seen = false;
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
            if (Regex.IsMatch(line, @"^##\s+Codex\s*(?:#+\s*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                if (seen) throw Invalid("Use only one ## Codex section.");
                seen = true;
                inSection = true;
                continue;
            }
            if (Regex.IsMatch(line, @"^#{1,2}\s+")) inSection = false;
            if (!inSection || line.Length == 0) continue;
            var match = Regex.Match(line, @"^(model|effort):\s*(\S+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) throw Invalid("Use plain 'model: <model-id>' and/or 'effort: low|medium|high|xhigh' lines, then a new level 1 or 2 heading for prose.");
            var value = match.Groups[2].Value;
            if (match.Groups[1].Value == "model")
            {
                if (model is not null) throw Invalid("Remove the duplicate model key.");
                if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
                    throw Invalid("model must be a model ID of at most 128 letters, digits, dots, underscores or hyphens, starting with a letter or digit.");
                model = value;
            }
            else
            {
                if (effort is not null) throw Invalid("Remove the duplicate effort key.");
                if (!new[] { "low", "medium", "high", "xhigh" }.Contains(value, StringComparer.OrdinalIgnoreCase))
                    throw Invalid("effort must be low, medium, high, or xhigh.");
                effort = value.ToLowerInvariant();
            }
        }
        return new(model ?? DefaultModel(defaults), effort ?? defaults.ReasoningEffort.ToLowerInvariant());
    }

    private static string? DefaultModel(CodexSettings defaults) => string.IsNullOrWhiteSpace(defaults.Model) ? null : defaults.Model;

    private static InvalidDataException Invalid(string reason) => new($"Invalid ## Codex metadata: {reason}");
}
