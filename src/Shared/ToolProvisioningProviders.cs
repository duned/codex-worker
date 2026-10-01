namespace CodexProvisioning;

internal sealed record ToolProvisioningStep(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Local product policy, never caller-supplied packages, paths or shell commands.</summary>
internal static class ToolProvisioningProviders
{
    internal const string NpmRegistry = "https://registry.npmjs.org";
    internal static string? AptPackage(string id) => id switch
    {
        "git" => "git",
        "github-cli" => "gh",
        _ => null
    };

    internal static string ManagedExecutable(string id) => id switch
    {
        "git" => "/usr/bin/git",
        "github-cli" => "/usr/bin/gh",
        "codex-cli" => "/usr/local/bin/codex",
        _ => throw new InvalidOperationException("Unsupported managed tool.")
    };

    internal static ToolProvisioningStep CandidateProbe(string id) => AptPackage(id) is { } package
        ? new("/usr/bin/apt-cache", ["policy", package])
        : new("/usr/bin/npm", ["view", "@openai/codex@latest", "version", "--global",
            "--registry", NpmRegistry, "--userconfig", "/dev/null", "--globalconfig", "/dev/null"]);

    internal static IReadOnlyList<ToolProvisioningStep> Plan(string id, ProvisioningCommandAction action)
    {
        var remove = action == ProvisioningCommandAction.Uninstall;
        if (AptPackage(id) is { } package)
            return remove ? [Apt("remove", "-y", package)] :
                [Apt("update"), new("/usr/bin/apt-get", ["install", "-y", "--no-install-recommends", package, .. id == "git" ? new[] { "openssh-client" } : Array.Empty<string>()])];
        if (id != "codex-cli") throw new InvalidOperationException("Unsupported tool provider.");
        // Use the stable official npm channel, a system prefix and private product cache.
        // No nvm, shell initialization, operator HOME, npmrc, purge or autoremove.
        var npm = new ToolProvisioningStep("/usr/bin/npm",
            [remove ? "uninstall" : "install", "--global", "--prefix", "/usr/local",
                "--registry", NpmRegistry, "--userconfig", "/dev/null", "--globalconfig", "/dev/null",
                "--cache", "/var/cache/codex-provisioning/npm", "--no-audit", "--no-fund",
                remove ? "@openai/codex" : "@openai/codex@latest"]);
        return remove ? [npm] : [Apt("update"), Apt("install", "-y", "--no-install-recommends", "nodejs", "npm"), npm,
            // Packaged services use umask 077. Make only the tool and its standard
            // system parent directories traversable by the effective service account.
            new("/usr/bin/chmod", ["a+rx", "/usr/local/bin", "/usr/local/lib", "/usr/local/lib/node_modules", "/usr/local/lib/node_modules/@openai"]),
            new("/usr/bin/chmod", ["-R", "a+rX", "/usr/local/lib/node_modules/@openai/codex"])];
    }

    private static ToolProvisioningStep Apt(params string[] arguments) => new("/usr/bin/apt-get", arguments);
}
