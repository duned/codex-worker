namespace CodexProvisioning;

using System.Reflection;

public static class ProductVersion
{
    public static string Display(Assembly assembly)
    {
        // InformationalVersion retains the release prerelease suffix. Build metadata
        // (including the source revision) does not participate in semantic ordering.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+', 2)[0];
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
