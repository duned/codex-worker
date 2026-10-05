using System.Text.RegularExpressions;

namespace CodexWorker;

/// <summary>Reads only the CLI's own bounded startup header, never agent/tool output or configuration files.</summary>
internal static class CodexCliModel
{
    internal static string? TryReadStartupHeader(string prefix)
    {
        using var reader = new StringReader(prefix.Length > 8192 ? prefix[..8192] : prefix);
        var started = false;
        var inHeader = false;
        string? model = null;
        while (reader.ReadLine() is { } line)
        {
            if (!started)
            {
                if (line.StartsWith("OpenAI Codex v", StringComparison.Ordinal)) started = true;
                else if (line.Length > 0 && !line.StartsWith("WARNING:", StringComparison.Ordinal)) return null;
                continue;
            }
            if (line == "--------")
            {
                if (inHeader) return model;
                inHeader = true;
                continue;
            }
            if (!inHeader || !line.StartsWith("model: ", StringComparison.Ordinal)) continue;
            var candidate = line[7..].Trim();
            if (model is not null || !Regex.IsMatch(candidate, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
                return null;
            model = candidate;
        }
        return null;
    }
}
