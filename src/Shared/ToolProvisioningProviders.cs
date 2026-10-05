namespace CodexProvisioning;

using System.Text.RegularExpressions;

internal sealed record ToolProvisioningStep(string Executable, IReadOnlyList<string> Arguments,
    ProvisioningProviderStep FailureStep = ProvisioningProviderStep.Unknown, bool RequiresElevation = false);

/// <summary>Local product policy, never caller-supplied packages, paths or shell commands.</summary>
internal static class ToolProvisioningProviders
{
    internal const string DockerHelper = "/usr/local/libexec/codex-provisioning-docker";
    internal const string CodexHelper = "/usr/local/libexec/codex-provisioning-codex";
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
            "--registry", NpmRegistry, "--userconfig", "/dev/null"]) : throw new InvalidOperationException("Unsupported tool provider.");

    internal static IReadOnlyList<ToolProvisioningStep> Plan(string id, ProvisioningCommandAction action,
        bool npmAvailable = false)
    {
        var dockerConfiguration = new ToolProvisioningStep(DockerHelper, ["configure"],
            ProvisioningProviderStep.DockerDaemonConfiguration, RequiresElevation: true);
        if (action == ProvisioningCommandAction.Configure)
            return id == "docker" ? [dockerConfiguration] : throw new InvalidOperationException("Unsupported configuration provider.");
        var remove = action == ProvisioningCommandAction.Uninstall;
        if (AptPackage(id) is { } package)
        {
            List<ToolProvisioningStep> aptSteps = remove ? [Apt("remove", ProvisioningProviderStep.AptPackageRemoval, "-y", package)] :
                [Apt("update", ProvisioningProviderStep.AptIndexRefresh),
                    Apt("install", ProvisioningProviderStep.AptPackageInstall,
                        id == "git"
                            ? ["-y", "--no-install-recommends", package, "openssh-client"]
                            : ["-y", "--no-install-recommends", package])];
            if (id == "docker" && !remove) aptSteps.Add(dockerConfiguration);
            return aptSteps;
        }
        if (id != "codex-cli") throw new InvalidOperationException("Unsupported tool provider.");
        // The root-owned helper accepts only these two fixed operations. It prepares
        // the isolated npm cache and uses a readable installation umask.
        var npm = new ToolProvisioningStep(CodexHelper, [remove ? "uninstall" : "install"],
            remove ? ProvisioningProviderStep.NpmPackageRemoval : ProvisioningProviderStep.NpmPackageInstall,
            RequiresElevation: true);
        if (remove) return [npm];
        var steps = new List<ToolProvisioningStep>();
        if (!npmAvailable)
        {
            steps.Add(Apt("update", ProvisioningProviderStep.AptIndexRefresh));
            steps.Add(Apt("install", ProvisioningProviderStep.AptRuntimeInstall,
                "-y", "--no-install-recommends", "nodejs", "npm"));
        }
        steps.Add(npm);
        return steps;
    }

    private static ToolProvisioningStep Apt(string operation, ProvisioningProviderStep failureStep, params string[] arguments) =>
        new("/usr/bin/apt-get", [operation, .. arguments], failureStep, RequiresElevation: true);
}
