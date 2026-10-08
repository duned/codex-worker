using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexServer;

/// <summary>Immutable embedded Vite output. Only manifest-listed assets become endpoints.</summary>
internal sealed class EmbeddedDashboardAssets
{
    private const string ResourcePrefix = "CodexServer.dashboard-preview/";
    public string Shell { get; }
    public IReadOnlyDictionary<string, byte[]> Assets { get; }

    public EmbeddedDashboardAssets() : this(Read) { }

    internal EmbeddedDashboardAssets(Func<string, byte[]> read)
    {
        var manifest = JsonSerializer.Deserialize<Asset[]>(read("assets.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Dashboard asset manifest is missing.");
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var asset in manifest)
        {
            if (string.IsNullOrWhiteSpace(asset.Path) || !asset.Path.StartsWith("assets/", StringComparison.Ordinal)
                || asset.Path.Contains("..", StringComparison.Ordinal) || asset.Path.Contains('\\')
                || asset.Path.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.')))
                throw new InvalidOperationException("Dashboard asset manifest contains an invalid path.");
            var bytes = read(asset.Path);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256, StringComparison.Ordinal)
                || !assets.TryAdd(asset.Path, bytes))
                throw new InvalidOperationException("Dashboard embedded assets are stale or duplicated.");
        }
        if (!assets.Keys.Any(path => path.EndsWith(".js", StringComparison.Ordinal))
            || !assets.Keys.Any(path => path.EndsWith(".css", StringComparison.Ordinal)))
            throw new InvalidOperationException("Dashboard embedded JavaScript or CSS is missing.");
        Shell = Encoding.UTF8.GetString(read("index.html"));
        var references = Regex.Matches(Shell, "(?:src|href)=\"/dashboard-assets/preview/([^\"]+)\"");
        if (!references.Any(reference => reference.Groups[1].Value.EndsWith(".js", StringComparison.Ordinal))
            || !references.Any(reference => reference.Groups[1].Value.EndsWith(".css", StringComparison.Ordinal)))
            throw new InvalidOperationException("Dashboard shell requires JavaScript and CSS references.");
        foreach (Match reference in references)
        {
            if (!assets.ContainsKey(reference.Groups[1].Value))
                throw new InvalidOperationException("Dashboard shell references missing assets.");
        }
        Assets = assets;
    }

    internal static EmbeddedDashboardAssets LoadDevelopmentOverride() => LoadDevelopmentOverride(
        Environment.GetEnvironmentVariable("CODEX_SERVER_DEVELOPMENT_DASHBOARD_DIR"),
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));

    internal static EmbeddedDashboardAssets LoadDevelopmentOverride(string? directory, string? environment)
    {
        if (string.IsNullOrEmpty(directory)) return new EmbeddedDashboardAssets();
        if (environment != "Development"
            || !Path.IsPathFullyQualified(directory) || Path.GetFullPath(directory) != directory)
            throw new InvalidOperationException("Dashboard override requires an absolute directory and Development environment.");
        VerifyDirectory(directory);
        var current = new DirectoryInfo(Path.Combine(directory, "current"));
        // An empty opt-in directory uses embedded assets until the first deployment,
        // and permits rollback of that first deployment without editing service settings.
        if (!Path.Exists(current.FullName) && current.LinkTarget is null) return new EmbeddedDashboardAssets();
        VerifyDirectory(current.FullName);
        return new EmbeddedDashboardAssets(name =>
        {
            var path = Path.Combine(directory, "current", name);
            VerifyDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Missing asset directory."));
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Dashboard override cannot contain symbolic links.");
            return File.ReadAllBytes(path);
        });
    }

    private static void VerifyDirectory(string directory)
    {
        for (DirectoryInfo? entry = new(directory); entry is not null; entry = entry.Parent)
            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Dashboard override requires existing directories without symbolic links.");
    }

    public static string ContentType(string path) => Path.GetExtension(path) switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        _ => "application/octet-stream"
    };

    private static byte[] Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException("A dashboard embedded resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed record Asset(string Path, string Sha256);
}
