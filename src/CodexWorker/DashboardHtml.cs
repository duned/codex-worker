namespace CodexWorker;

using System.Reflection;

internal static class DashboardHtml
{
    public static string Content { get; } = Load();

    private static string Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexWorker.dashboard.html")
            ?? throw new InvalidOperationException("The worker dashboard resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
