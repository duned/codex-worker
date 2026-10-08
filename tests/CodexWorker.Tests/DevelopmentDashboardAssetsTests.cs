using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexServer;
using Xunit;

namespace CodexWorker.Tests;

public sealed class DevelopmentDashboardAssetsTests
{
    [Fact]
    public void OverrideRequiresDevelopmentAndValidManifestAndLoadsSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(Path.Combine(current, "assets"));
        try
        {
            var bytes = Encoding.UTF8.GetBytes("development");
            var paths = new[] { "assets/app.js", "assets/app.css" };
            foreach (var path in paths) File.WriteAllBytes(Path.Combine(current, path), bytes);
            File.WriteAllText(Path.Combine(current, "assets.json"), JsonSerializer.Serialize(paths.Select(path =>
                new { path, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) })));
            File.WriteAllText(Path.Combine(current, "index.html"), "<script src=\"/dashboard-assets/preview/assets/app.js\"></script><link href=\"/dashboard-assets/preview/assets/app.css\">");
            Assert.Throws<InvalidOperationException>(() => EmbeddedDashboardAssets.LoadDevelopmentOverride(root, "Production"));
            var snapshot = EmbeddedDashboardAssets.LoadDevelopmentOverride(root, "Development");
            Assert.Contains("/dashboard-assets/preview/assets/app.js", snapshot.Shell);
            Assert.Equal(bytes, snapshot.Assets["assets/app.js"]);
            File.WriteAllText(Path.Combine(current, "assets/app.js"), "corrupt");
            Assert.Equal(bytes, snapshot.Assets["assets/app.js"]);
            Assert.Throws<InvalidOperationException>(() => EmbeddedDashboardAssets.LoadDevelopmentOverride(root, "Development"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
