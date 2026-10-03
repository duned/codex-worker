namespace CodexWorker;

public static class ApplicationVersion
{
    public static string Display => CodexProvisioning.ProductVersion.Display(typeof(ApplicationVersion).Assembly);
}
