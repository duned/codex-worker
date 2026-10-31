namespace CodexProvisioning;

/// <summary>Native Claude installation belongs to the invoking service account, never an elevated installer.</summary>
public static class ClaudeServiceEnvironment
{
    public static string Home
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("HOME") ??
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(home))
                throw new InvalidOperationException("Claude service HOME must be an absolute path.");
            return home;
        }
    }

    public static string Executable => Path.Combine(Home, ".local", "bin", "claude");

}
