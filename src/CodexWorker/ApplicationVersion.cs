namespace CodexWorker;

public static class ApplicationVersion
{
    public static string Display
    {
        get
        {
            var version = typeof(ApplicationVersion).Assembly.GetName().Version ?? new Version(0, 0);
            return version.Build < 0
                ? $"{version.Major}.{version.Minor}"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
