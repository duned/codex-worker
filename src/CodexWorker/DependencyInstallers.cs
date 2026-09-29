using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed record ProvisioningRequirement(string Type, string Name, string? Version);

public sealed record DependencyInstallPlan(bool Supported, bool AlreadySatisfied, bool RequiresElevation,
    string? Executable = null, IReadOnlyList<string>? Arguments = null, string? Reason = null);

public sealed record DependencyInstallResult(bool Supported, bool Succeeded, bool RequiresElevation,
    string? DetectedVersion = null, WorkerCapabilityContract? DetectedCapability = null, string? Message = null);

/// <summary>Worker-owned installer for one explicitly supported family of dependencies.</summary>
public interface IDependencyInstaller
{
    bool Supports(ProvisioningRequirement requirement);
    DependencyInstallPlan Plan(ProvisioningRequirement requirement, IReadOnlyList<WorkerCapabilityContract> capabilities);
    Task<DependencyInstallResult> InstallAsync(DependencyInstallPlan plan, CancellationToken cancellationToken);
}

/// <summary>Matches the numeric exact and minimum versions used by project requirements.</summary>
public static class CapabilityVersionMatcher
{
    public static bool Satisfies(WorkerCapabilityContract capability, ProvisioningRequirement requirement)
    {
        if (!string.Equals(capability.Type, requirement.Type, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(CanonicalName(capability.Type, capability.Name), CanonicalName(requirement.Type, requirement.Name), StringComparison.OrdinalIgnoreCase)) return false;
        if (requirement.Version is null) return true;
        if (capability.Version is null) return false;

        var minimum = requirement.Version.StartsWith(">=", StringComparison.Ordinal);
        var requested = minimum ? requirement.Version[2..] : requirement.Version;
        if (!TryVersion(capability.Version, out var installed) || !TryVersion(requested, out var required)) return false;
        var comparison = Compare(installed, required);
        return minimum ? comparison >= 0 : comparison == 0;
    }

    private static bool TryVersion(string value, out int[] version)
    {
        if (!Regex.IsMatch(value, @"^\d+(?:\.\d+){0,3}$", RegexOptions.CultureInvariant))
        {
            version = [];
            return false;
        }
        version = value.Split('.').Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : -1).ToArray();
        return version.Length is >= 1 and <= 4 && version.All(number => number >= 0);
    }

    private static int Compare(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var leftPart = index < left.Count ? left[index] : 0;
            var rightPart = index < right.Count ? right[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string CanonicalName(string type, string name) =>
        string.Equals(type, "runtime", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(name.Trim(), ".net", StringComparison.OrdinalIgnoreCase) ? "dotnet" : name.Trim();
}

/// <summary>Uses only fixed Debian/Ubuntu package mappings and never invokes a shell.</summary>
public sealed class DebianAptDependencyInstaller : IDependencyInstaller
{
    private static readonly IReadOnlyDictionary<(string Type, string Name), string> Packages =
        new Dictionary<(string, string), string>
        {
            [("tool", "git")] = "git",
            [("runtime", "node")] = "nodejs",
            [("tool", "docker")] = "docker.io",
            [("service", "postgresql")] = "postgresql-client"
        };

    private readonly Func<bool> _isSupportedPlatform;
    private readonly Func<bool> _requiresElevation;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> _run;

    public DebianAptDependencyInstaller()
        : this(IsDebianOrUbuntu, () => !OperatingSystem.IsLinux() || Environment.UserName != "root",
            (executable, arguments, cancellationToken) => new ProcessRunner().RunAsync(executable, arguments,
                Environment.CurrentDirectory, TimeSpan.FromMinutes(10), cancellationToken)) { }

    public DebianAptDependencyInstaller(Func<bool> isSupportedPlatform, Func<bool> requiresElevation,
        Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run)
    {
        _isSupportedPlatform = isSupportedPlatform;
        _requiresElevation = requiresElevation;
        _run = run;
    }

    public bool Supports(ProvisioningRequirement requirement) =>
        Packages.ContainsKey((requirement.Type.ToLowerInvariant(), requirement.Name.ToLowerInvariant()));

    public DependencyInstallPlan Plan(ProvisioningRequirement requirement, IReadOnlyList<WorkerCapabilityContract> capabilities)
    {
        if (!_isSupportedPlatform())
            return new DependencyInstallPlan(false, false, false, Reason: "The local installer supports Debian and Ubuntu Linux only.");
        if (!Supports(requirement))
            return new DependencyInstallPlan(false, false, false, Reason: "No trusted package mapping is registered for this requirement.");
        if (capabilities.Any(capability => CapabilityVersionMatcher.Satisfies(capability, requirement)))
            return new DependencyInstallPlan(true, true, false);
        if (capabilities.Any(capability => string.Equals(capability.Type, requirement.Type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(capability.Name, requirement.Name, StringComparison.OrdinalIgnoreCase)))
            return new DependencyInstallPlan(false, false, false, Reason: "An incompatible version is already installed; upgrades and downgrades are not supported.");
        if (requirement.Version is not null && !IsSupportedConstraint(requirement.Version))
            return new DependencyInstallPlan(false, false, false, Reason: "Only numeric exact versions and >= minimum versions can be installed.");

        var package = Packages[(requirement.Type.ToLowerInvariant(), requirement.Name.ToLowerInvariant())];
        if (requirement.Version is not null && !requirement.Version.StartsWith(">=", StringComparison.Ordinal))
            package += "=" + requirement.Version;
        var elevated = _requiresElevation();
        var arguments = elevated
            ? new[] { "-n", "apt-get", "install", "-y", "--no-install-recommends", package }
            : new[] { "install", "-y", "--no-install-recommends", package };
        return new DependencyInstallPlan(true, false, elevated, elevated ? "sudo" : "apt-get", arguments);
    }

    public async Task<DependencyInstallResult> InstallAsync(DependencyInstallPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.Supported) return new DependencyInstallResult(false, false, plan.RequiresElevation, Message: plan.Reason);
        if (plan.AlreadySatisfied) return new DependencyInstallResult(true, true, false, Message: "Requirement already satisfied.");
        if (plan.Executable is null || plan.Arguments is null)
            return new DependencyInstallResult(false, false, plan.RequiresElevation, Message: "Installer plan has no command.");
        var result = await _run(plan.Executable, plan.Arguments, cancellationToken);
        return result.ExitCode == ProcessExitCodes.Success
            ? new DependencyInstallResult(true, true, plan.RequiresElevation, Message: "Trusted apt install command completed.")
            : new DependencyInstallResult(true, false, plan.RequiresElevation,
                Message: $"Installer command exited with code {result.ExitCode}: {Bound(result.StandardError)}");
    }

    private static bool IsSupportedConstraint(string version)
    {
        var numeric = version.StartsWith(">=", StringComparison.Ordinal) ? version[2..] : version;
        return Regex.IsMatch(numeric, @"^\d+(?:\.\d+){0,3}$", RegexOptions.CultureInvariant);
    }

    private static string Bound(string output) => output.Length <= 500 ? output : output[^500..];

    private static bool IsDebianOrUbuntu()
    {
        if (!OperatingSystem.IsLinux() || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        try
        {
            var osRelease = File.ReadAllText("/etc/os-release");
            return Regex.IsMatch(osRelease, "(?m)^ID=(?:\\\"?(?:debian|ubuntu)\\\"?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
