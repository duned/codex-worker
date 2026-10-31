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
        try
        {
            var version = await RunAsync(["--version"], TimeSpan.FromSeconds(5), token);
            state = "installed";
            if (version.ExitCode != 0 || WorkerCapabilityDiscovery.ParseVersion(version.StandardOutput) is null)
                state = "incompatible";
            else
            {
                var authentication = await RunAsync(["auth", "status", "--json"], TimeSpan.FromSeconds(5), token);
                state = "unauthenticated";
                if (authentication.ExitCode == 0)
                {
                    using var auth = JsonDocument.Parse(authentication.StandardOutput);
                    if (auth.RootElement.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True)
                    {
                        state = "service-unavailable";
                        // Print mode disables interactive login/permission flows. No tools or repository context are needed.
                        var response = await RunAsync(["--print", "--output-format", "json", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "Reply with OK."], TimeSpan.FromSeconds(60), token);
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
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or ProcessTimeoutException or JsonException)
        {
            // Never retain CLI output or exception messages: both can contain authentication material.
        }
        Observation = new("agent-readiness", "ccc", Scope: state);
    }

    private Task<ProcessResult> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token) =>
        run(ClaudeServiceEnvironment.Executable, arguments, Path.GetTempPath(), timeout, token);
}
