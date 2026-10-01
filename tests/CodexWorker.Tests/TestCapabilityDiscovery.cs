namespace CodexWorker.Tests;

using CodexProvisioning;

internal static class TestCapabilityDiscovery
{
    public static NodeCapabilityDiscovery Create() => new((_, _, _) =>
        Task.FromException<(int, string)>(new FileNotFoundException()));
}
