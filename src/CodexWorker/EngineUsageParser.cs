using System.Text.Json;
using CodexProvisioning;

namespace CodexWorker;

/// <summary>Reads the usage each engine CLI reports about its own run. Input tokens include cached input,
/// so both engines are counted the same way. Missing or malformed usage yields null, never a failure.</summary>
internal static class EngineUsageParser
{
    /// <summary><c>codex exec --json</c> prints one JSON event per line. Current CLIs end each turn with
    /// <c>turn.completed</c> and its usage; older ones print cumulative <c>token_count</c> events.</summary>
    internal static EngineUsage? FromCodexEvents(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        EngineUsage? turns = null, cumulative = null;
        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] != '{' || !text.Contains("token", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (Text(root, "type") == "turn.completed" && root.TryGetProperty("usage", out var usage) && Tokens(usage) is { } turn)
                    turns = turns?.Add(turn) ?? turn;
                else if (root.TryGetProperty("msg", out var message) && message.ValueKind == JsonValueKind.Object &&
                    Text(message, "type") == "token_count" && message.TryGetProperty("info", out var info) &&
                    info.ValueKind == JsonValueKind.Object && info.TryGetProperty("total_token_usage", out var total) && Tokens(total) is { } latest)
                    cumulative = latest;
            }
            catch (JsonException) { }
        }
        return turns is { } summed ? summed with { Runs = 1 } : cumulative;
    }

    /// <summary>The single result object of <c>claude -p --output-format json</c>: <c>usage</c> counts cache reads and
    /// writes separately from new input, and <c>total_cost_usd</c> is Claude Code's own estimate.</summary>
    internal static EngineUsage? FromClaudeResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        decimal? cost = result.TryGetProperty("total_cost_usd", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var parsed) && parsed >= 0 ? decimal.Round(parsed, 6) : null;
        if (!result.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return cost is null ? null : new EngineUsage(CostUsd: cost);
        var input = Number(usage, "input_tokens");
        var cacheRead = Number(usage, "cache_read_input_tokens");
        var cacheWrite = Number(usage, "cache_creation_input_tokens");
        var output = Number(usage, "output_tokens");
        if (input is null && output is null && cost is null) return null;
        var usageResult = new EngineUsage((input ?? 0) + (cacheRead ?? 0) + (cacheWrite ?? 0), cacheRead ?? 0, output ?? 0, cost);
        return EngineUsage.Valid(usageResult) ? usageResult : null;
    }

    private static EngineUsage? Tokens(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        var input = Number(usage, "input_tokens");
        var output = Number(usage, "output_tokens");
        if (input is null && output is null) return null;
        var result = new EngineUsage(input ?? 0, Number(usage, "cached_input_tokens") ?? 0, output ?? 0);
        return EngineUsage.Valid(result) ? result : null;
    }

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
