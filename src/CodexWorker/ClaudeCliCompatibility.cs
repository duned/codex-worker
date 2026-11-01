namespace CodexWorker;

/// <summary>The installed CLI must support the Worker's unattended permission boundary.</summary>
internal static class ClaudeCliCompatibility
{
    internal static bool SupportsVersion(string? version) =>
        Version.TryParse(version, out var installed) && installed >= new Version(2, 1, 259);

    internal static bool SupportsRestrictedExecution(ProcessResult help)
    {
        string[] required = ["--restricted", "--safe-mode", "--permission-prompts", "--permission-mode", "--json-schema",
            "--effort", "--model", "--tools", "--allowedTools", "--strict-mcp-config", "--no-session-persistence"];
        return help.ExitCode == 0 && required.All(flag => help.StandardOutput.Contains(flag, StringComparison.Ordinal));
    }
}
