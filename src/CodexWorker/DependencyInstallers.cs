using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed record ProvisioningRequirement(string Type, string Name, string? Version);

public sealed record DependencyInstallPlan(bool Supported, bool AlreadySatisfied, bool RequiresElevation,
    string? Package = null, string? Reason = null);

public sealed record DependencyInstallResult(bool Supported, bool Succeeded, bool RequiresElevation,
    string? DetectedVersion = null, WorkerCapabilityContract? DetectedCapability = null, string? Message = null);

/// <summary>Only fixed, Worker-owned privileged operations are exposed to dependency installers.</summary>
public interface IPrivilegedOperationExecutor
{
    Task<DependencyInstallResult> InstallAptPackageAsync(string package, CancellationToken cancellationToken);
}

/// <summary>Runs a fixed apt install operation and uses sudo only in non-interactive mode.</summary>
public sealed class AptPrivilegedOperationExecutor : IPrivilegedOperationExecutor
{
    private readonly Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessResult>> _run;
    private readonly Func<bool> _isRoot;

    public AptPrivilegedOperationExecutor()
        : this((executable, arguments, timeout, cancellationToken) => new ProcessRunner().RunAsync(executable, arguments,
            Environment.CurrentDirectory, timeout, cancellationToken), () => OperatingSystem.IsLinux() && Environment.UserName == "root") { }

    public AptPrivilegedOperationExecutor(Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<ProcessResult>> run,
        Func<bool> isRoot)
    {
        _run = run;
        _isRoot = isRoot;
    }

    public async Task<DependencyInstallResult> InstallAptPackageAsync(string package, CancellationToken cancellationToken)
    {
        if (!DebianAptDependencyInstaller.IsTrustedPackage(package))
            return new DependencyInstallResult(false, false, true, Message: "Privileged operation is not in the Worker allowlist.");

        var elevated = !_isRoot();
        var packageArguments = new[] { "install", "-y", "--no-install-recommends", package };
        if (elevated)
        {
            ProcessResult permission;
            try { permission = await _run("sudo", ["-n", "-l", "/usr/bin/apt-get", .. packageArguments], TimeSpan.FromSeconds(10), cancellationToken); }
            catch (Exception ex) when (IsProcessUnavailable(ex))
            { return new DependencyInstallResult(true, false, true, Message: "Required host privileges are unavailable (non-interactive sudo permission is not configured)."); }
            catch (ProcessTimeoutException)
            { return new DependencyInstallResult(true, false, true, Message: "Required host privileges are unavailable (non-interactive permission check timed out)."); }
            if (permission.ExitCode != ProcessExitCodes.Success)
                return new DependencyInstallResult(true, false, true, Message: "Required host privileges are unavailable (the exact non-interactive apt install operation is not permitted).");
        }

        var executable = elevated ? "sudo" : "apt-get";
        var arguments = packageArguments;
        if (elevated) arguments = ["-n", "/usr/bin/apt-get", .. packageArguments];
        ProcessResult result;
        try { result = await _run(executable, arguments, TimeSpan.FromMinutes(10), cancellationToken); }
        catch (ProcessTimeoutException)
        { return new DependencyInstallResult(true, false, true, Message: "Trusted apt install operation exceeded its 10 minute timeout."); }
        catch (Exception ex) when (IsProcessUnavailable(ex))
        { return new DependencyInstallResult(true, false, true, Message: elevated
            ? "Required host privileges are unavailable (non-interactive sudo is not available)."
            : "Trusted apt install operation could not start."); }
        return result.ExitCode == ProcessExitCodes.Success
            ? new DependencyInstallResult(true, true, true, Message: "Trusted apt install operation completed.")
            : new DependencyInstallResult(true, false, true, Message: result.StandardError.Contains("not allowed", StringComparison.OrdinalIgnoreCase) ||
                result.StandardError.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
                    ? "Required host privileges are unavailable (the exact non-interactive apt install operation is not permitted)."
                    : $"Trusted apt install operation failed with exit code {result.ExitCode}.");
    }

    private static bool IsProcessUnavailable(Exception exception) => exception is System.ComponentModel.Win32Exception or
        FileNotFoundException || exception is InvalidOperationException && exception.Message.StartsWith("Could not start", StringComparison.Ordinal);
}

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
    private readonly IPrivilegedOperationExecutor _privilegedOperations;

    public DebianAptDependencyInstaller()
        : this(IsDebianOrUbuntu, new AptPrivilegedOperationExecutor()) { }

    public DebianAptDependencyInstaller(Func<bool> isSupportedPlatform, Func<bool> requiresElevation,
        Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run)
    {
        _isSupportedPlatform = isSupportedPlatform;
        _privilegedOperations = new AptPrivilegedOperationExecutor((executable, arguments, _, cancellationToken) =>
            run(executable, arguments, cancellationToken), () => !requiresElevation());
    }

    public DebianAptDependencyInstaller(Func<bool> isSupportedPlatform, IPrivilegedOperationExecutor privilegedOperations)
    {
        _isSupportedPlatform = isSupportedPlatform;
        _privilegedOperations = privilegedOperations;
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
        return new DependencyInstallPlan(true, false, true, Package: package);
    }

    public async Task<DependencyInstallResult> InstallAsync(DependencyInstallPlan plan, CancellationToken cancellationToken)
    {
        if (!plan.Supported) return new DependencyInstallResult(false, false, plan.RequiresElevation, Message: plan.Reason);
        if (plan.AlreadySatisfied) return new DependencyInstallResult(true, true, false, Message: "Requirement already satisfied.");
        if (plan.Package is null)
            return new DependencyInstallResult(false, false, true, Message: "Installer plan has no trusted package operation.");
        return await _privilegedOperations.InstallAptPackageAsync(plan.Package, cancellationToken);
    }

    private static bool IsSupportedConstraint(string version)
    {
        var numeric = version.StartsWith(">=", StringComparison.Ordinal) ? version[2..] : version;
        return Regex.IsMatch(numeric, @"^\d+(?:\.\d+){0,3}$", RegexOptions.CultureInvariant);
    }

    internal static bool IsTrustedPackage(string package) => Packages.Values.Contains(package, StringComparer.Ordinal) ||
        Packages.Values.Any(basePackage => package.StartsWith(basePackage + "=", StringComparison.Ordinal) &&
            Regex.IsMatch(package[(basePackage.Length + 1)..], @"^\d+(?:\.\d+){0,3}$", RegexOptions.CultureInvariant));

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
