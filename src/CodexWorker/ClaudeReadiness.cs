namespace CodexWorker;

using System.Text.Json;
using CodexProvisioning;

/// <summary>Service-account observations, independent of whether this Worker implements CCC execution.</summary>
internal sealed class ClaudeReadiness(
    Func<string, IEnumerable<string>, string, TimeSpan?, CancellationToken, Task<ProcessResult>> run)
{
    internal WorkerCapabilityContract Observation { get; private set; } = new("agent-readiness", "ccc", Scope: "absent");

    internal async Task RefreshAsync(CancellationToken token)
    {
        var state = "absent";
        string? detectedVersion = null;
        try
        {
            var version = await RunAsync(["--version"], TimeSpan.FromSeconds(5), token);
            state = "installed";
            detectedVersion = WorkerCapabilityDiscovery.ParseVersion(version.StandardOutput);
            if (version.ExitCode != 0 || !ClaudeCliCompatibility.SupportsVersion(detectedVersion))
                state = "incompatible";
            else
            {
                var authentication = await RunAsync(["auth", "status"], TimeSpan.FromSeconds(5), token);
                state = "unauthenticated";
                if (ClaudeAuthenticationStatus.TryRead(authentication.ExitCode, authentication.StandardOutput, out var authenticated) && authenticated)
                {
                    state = "incompatible";
                    var help = await RunAsync(["--help"], TimeSpan.FromSeconds(5), token);
                    if (!ClaudeCliCompatibility.SupportsRestrictedExecution(help))
                    {
                        Observation = new("agent-readiness", "ccc", Version: detectedVersion, Scope: "incompatible", CheckedAtUtc: DateTimeOffset.UtcNow);
                        return;
                    }
                    state = "service-unavailable";
                    // Exercise the executor's unattended boundary and default model, without tools or repository context.
                    var response = await RunAsync(["--print", "--restricted", "--safe-mode", "--permission-mode", "dontAsk",
                        "--permission-prompts", "none", "--model", "sonnet", "--effort", "high",
                        "--settings", "{\"disableAllHooks\":true}", "--output-format", "json", "--tools", "",
                        "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence",
                        "Reply with OK."], TimeSpan.FromSeconds(60), token);
                    if (response.ExitCode == 0)
                    {
                        using var result = JsonDocument.Parse(response.StandardOutput);
                        if (result.RootElement.TryGetProperty("type", out var type) && type.GetString() == "result" &&
                            result.RootElement.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.False &&
                            result.RootElement.TryGetProperty("result", out var content) && content.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(content.GetString())) state = "ready";
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or ProcessTimeoutException or JsonException)
        {
            // Never retain CLI output or exception messages: both can contain authentication material.
        }
        Observation = new("agent-readiness", "ccc", Version: detectedVersion, Scope: state, CheckedAtUtc: DateTimeOffset.UtcNow);
    }

    private Task<ProcessResult> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token) =>
        run(ClaudeServiceEnvironment.Executable, arguments, Path.GetTempPath(), timeout, token);
}
