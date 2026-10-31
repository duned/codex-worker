namespace CodexProvisioning;

/// <summary>Claude uses the running service user's HOME and local provider credentials.
/// No administrator login session or subscription credential is copied to the node.</summary>
public static class ClaudeServiceEnvironment
{
    public static string Prefix => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home && Path.IsPathFullyQualified(home)
            ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local");

    public static string Executable => Path.Combine(Prefix, "bin", "claude");
}
