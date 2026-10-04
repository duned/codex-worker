namespace CodexProvisioning;

using System.Text.RegularExpressions;

internal sealed record ToolProvisioningStep(string Executable, IReadOnlyList<string> Arguments,
    ProvisioningProviderStep FailureStep = ProvisioningProviderStep.Unknown);

/// <summary>Local product policy, never caller-supplied packages, paths or shell commands.</summary>
internal static class ToolProvisioningProviders
{
    internal const string NpmRegistry = "https://registry.npmjs.org";
    internal static string? AptPackage(string id) => id switch
    {
        "git" => "git",
        "github-cli" => "gh",
        "dotnet-sdk" => "dotnet-sdk-10.0",
        "dotnet-runtime" => "aspnetcore-runtime-10.0",
        "docker" => "docker.io",
        _ => null
    };

    internal static string ManagedExecutable(string id) => id switch
    {
        "git" => "/usr/bin/git",
        "github-cli" => "/usr/bin/gh",
        "codex-cli" => "/usr/local/bin/codex",
        "dotnet-sdk" or "dotnet-runtime" => "/usr/bin/dotnet",
        "docker" => "/usr/bin/docker",
        _ => throw new InvalidOperationException("Unsupported managed tool.")
    };

    internal static IReadOnlyList<string> VersionArguments(string id) => id switch
    {
        "dotnet-sdk" => ["--list-sdks"],
        "dotnet-runtime" => ["--list-runtimes"],
        _ => ["--version"]
    };

    internal static string? ParseVersion(string id, string output)
    {
        // Inventory installed components, independent of global.json and the newest SDK.
        if (id == "dotnet-sdk") return ParseDotNetVersion(output, @"^(10\.0\.\d+)(?=\s+\[)");
        if (id == "dotnet-runtime")
            return ParseDotNetVersion(output, @"^Microsoft\.NETCore\.App (10\.0\.\d+)(?=\s+\[)") is null
                ? null : ParseDotNetVersion(output, @"^Microsoft\.AspNetCore\.App (10\.0\.\d+)(?=\s+\[)");
        var version = Regex.Match(output, @"(?<![\w])v?(\d+(?:\.\d+){0,3}(?:[-+][0-9A-Za-z.-]+)?)(?![\w])");
        return version.Success ? version.Groups[1].Value : null;
    }

    private static string? ParseDotNetVersion(string output, string pattern) =>
        Regex.Matches(output, pattern, RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .Where(version => Version.TryParse(version, out _))
            .OrderByDescending(Version.Parse).FirstOrDefault();

    internal static ToolProvisioningStep CandidateProbe(string id) => AptPackage(id) is { } package
        ? new("/usr/bin/apt-cache", ["policy", package])
        : id == "codex-cli" ? new("/usr/bin/npm", ["view", "@openai/codex@latest", "version", "--global",
            "--registry", NpmRegistry, "--userconfig", "/dev/null", "--globalconfig", "/dev/null"]) : throw new InvalidOperationException("Unsupported tool provider.");

    internal static IReadOnlyList<ToolProvisioningStep> Plan(string id, ProvisioningCommandAction action,
        bool npmAvailable = false)
    {
        var remove = action == ProvisioningCommandAction.Uninstall;
        if (AptPackage(id) is { } package)
            return remove ? [Apt("remove", ProvisioningProviderStep.AptPackageRemoval, "-y", package)] :
                [Apt("update", ProvisioningProviderStep.AptIndexRefresh),
                    Apt("install", ProvisioningProviderStep.AptPackageInstall,
                        id == "git"
                            ? ["-y", "--no-install-recommends", package, "openssh-client"]
                            : ["-y", "--no-install-recommends", package])];
        if (id != "codex-cli") throw new InvalidOperationException("Unsupported tool provider.");
        // Use the stable official npm channel, a system prefix and private product cache.
        // No nvm, shell initialization, operator HOME, npmrc, purge or autoremove.
        var npm = new ToolProvisioningStep("/usr/bin/npm",
            [remove ? "uninstall" : "install", "--global", "--prefix", "/usr/local",
                "--registry", NpmRegistry, "--userconfig", "/dev/null", "--globalconfig", "/dev/null",
                "--cache", "/var/cache/codex-provisioning/npm", "--no-audit", "--no-fund",
                remove ? "@openai/codex" : "@openai/codex@latest"],
            remove ? ProvisioningProviderStep.NpmPackageRemoval : ProvisioningProviderStep.NpmPackageInstall);
        if (remove) return [npm];
        var steps = new List<ToolProvisioningStep>();
        if (!npmAvailable)
        {
            steps.Add(Apt("update", ProvisioningProviderStep.AptIndexRefresh));
            steps.Add(Apt("install", ProvisioningProviderStep.AptRuntimeInstall,
                "-y", "--no-install-recommends", "nodejs", "npm"));
        }
        steps.Add(npm);
        // Packaged services use umask 077. Make only the tool and its standard
        // system parent directories traversable by the effective service account.
        steps.Add(new("/usr/bin/chmod", ["a+rx", "/usr/local/bin", "/usr/local/lib", "/usr/local/lib/node_modules", "/usr/local/lib/node_modules/@openai"], ProvisioningProviderStep.ExecutablePermissions));
        steps.Add(new("/usr/bin/chmod", ["-R", "a+rX", "/usr/local/lib/node_modules/@openai/codex"], ProvisioningProviderStep.ExecutablePermissions));
        return steps;
    }

    private static ToolProvisioningStep Apt(string operation, ProvisioningProviderStep failureStep, params string[] arguments) =>
        new("/usr/bin/apt-get", [operation, .. arguments], failureStep);
}
