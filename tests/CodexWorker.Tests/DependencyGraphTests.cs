using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class DependencyGraphTests
{
    [Fact]
    public void ResolvedSqliteGraphDoesNotContainVulnerableNativePackageVersion()
    {
        var assetsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/CodexWorker/obj/project.assets.json"));
        Assert.True(File.Exists(assetsPath), $"Expected restored dependency graph at {assetsPath}.");

        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var libraries = assets.RootElement.GetProperty("libraries").EnumerateObject().Select(x => x.Name).ToArray();
        var resolvedNativeVersion = Assert.Single(libraries
            .Where(name => name.StartsWith("SQLitePCLRaw.lib.e_sqlite3/", StringComparison.Ordinal))
            .Select(name => name[(name.LastIndexOf('/') + 1)..]));

        Assert.NotEqual("2.1.11", resolvedNativeVersion);
        Assert.True(Version.Parse(resolvedNativeVersion) >= new Version(2, 1, 12));
    }
}
