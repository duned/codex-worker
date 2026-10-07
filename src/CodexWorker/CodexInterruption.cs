using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexWorker;

/// <summary>Only positively identified external failures are automatically resumed.</summary>
internal sealed record CodexFailure(string Category, string Reason, bool Recoverable)
{
    internal static CodexFailure Classify(string evidence, bool timedOut = false)
    {
        var text = evidence.ToLowerInvariant();
        if (timedOut) return new("Codex timeout", "Codex preflight/execution timed out; check network/service availability", true);
        if (Contains(text, "usage limit", "rate limit", "rate_limit", "quota exceeded", "insufficient_quota", "subscription limit", "usage_limit"))
            return new("Codex usage limit", "Codex usage/rate/subscription limit reached; restore quota or wait for the limit to reset", true);
        if (Contains(text, "unauthorized", "authentication required", "not logged in", "please log in", "token expired", "refresh token", "invalid_api_key", "401 unauthorized"))
            return new("Codex authentication", "Codex authentication/session unavailable; use the supported node authentication flow", true);
        if (Contains(text, "connection refused", "connection reset", "network is unreachable", "failed to connect", "service unavailable", "502 bad gateway", "503", "stream disconnected", "error sending request", "temporarily unavailable"))
            return new("Codex service/network", "Codex service/network unavailable; check connectivity and service status", true);
        return new("Codex CLI/process failure", "Codex CLI failed without a recognized external cause; inspect sanitized execution diagnostics and repair the CLI/environment", false);
    }

    private static bool Contains(string text, params string[] patterns) => patterns.Any(text.Contains);

    internal static string ProcessEvidence(string stdout, string stderr)
    {
        var messages = new List<string>();
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String || type.GetString() is not ("error" or "turn.failed")) continue;
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    messages.Add(message.GetString() ?? "");
                if (root.TryGetProperty("error", out var error)) messages.Add(error.ToString());
            }
            catch (JsonException) { /* Do not interpret arbitrary agent/tool output as transport errors. */ }
        }
        foreach (var line in stderr.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("error", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("You've hit", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("You have hit", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Rate limit", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Authentication required", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Your authentication token", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\S+\s+ERROR\s", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) messages.Add(text);
        }
        return string.Join("\n", messages);
    }

    internal static string Evidence(string stdout, string stderr, IReadOnlyList<string>? secrets = null)
    {
        // Select error evidence rather than arbitrary source fragments emitted by the agent.
        var lines = (stderr + "\n" + stdout).Split('\n').Where(line =>
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase)).TakeLast(6);
        var safe = FailureDiagnosticRedactor.Redact(string.Join("\n", lines), secrets);
        return safe.Length <= 2000 ? safe : safe[..2000] + " [truncated]";
    }
}

/// <summary>Workspace ownership and a separate, restart-safe infrastructure resume budget.
/// Configuration is fingerprinted instead of persisting environment secrets. Changed configuration requires inspection.</summary>
public sealed record CodexInterruptionRecovery(Guid WorkspaceExecutionId, int WorkspaceAttemptNumber,
    int ResumeCount, string ConfigurationFingerprint, string? SessionId = null, DateTimeOffset? RetryAfterUtc = null)
{
    public const int MaximumResumes = 3;
    internal static string Fingerprint(WorkerConfiguration config) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { config.Project, config.Git, config.GitHub, config.Codex,
            config.Validation, config.Worker.GitTimeoutSeconds, config.Worker.GitHubTimeoutSeconds, Environment = config.Environment.Variables.OrderBy(pair => pair.Key, StringComparer.Ordinal) }))));
}
