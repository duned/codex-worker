namespace CodexProvisioning;

using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record UpdateComponent(string Name, string DisplayName, string CurrentVersion, string InstalledExecutable)
{
    public string Installer => $"install-{Name}.sh";
}

public sealed record UpdateRelease(string Version);
public sealed record UpdateResult(int ContractVersion, string Component, string CurrentVersion,
    string? LatestVersion, bool UpdateAvailable, bool UpdateAttempted, string Status, string? FinalVersion, string? Error);

public interface ISelfUpdateOperations
{
    Task<UpdateRelease> FindReleaseAsync(UpdateComponent component, CancellationToken cancellationToken);
    Task<string> InstallAsync(UpdateComponent component, UpdateRelease release, CancellationToken cancellationToken);
}

/// <summary>Shared local operator command. Installers retain ownership of installation and service lifecycle.</summary>
public sealed class SelfUpdateCli(ISelfUpdateOperations operations, TextReader input, TextWriter output, TextWriter error,
    bool interactive)
{
    public static string Help(UpdateComponent component) => $"""
        Usage: codex-{component.Name} update [--check] [--yes] [--json]
        Check the latest stable published release and upgrade through the packaged Linux installer.
        --check  Check only; never prompt or install.
        --yes    Confirm installation without prompting (requires root on Ubuntu 24.04 x64).
        --json   Emit contract version 1 JSON only. Requires --yes to install; otherwise checks only.
        Run updates from a separate operator session, outside the service being upgraded.
        """;

    public async Task<int> RunAsync(UpdateComponent component, string[] args, CancellationToken cancellationToken = default)
    {
        if (args is ["--help"] or ["-h"]) { await output.WriteLineAsync(Help(component)); return 0; }
        var json = args.Contains("--json", StringComparer.Ordinal);
        var result = new UpdateResult(1, component.Name, component.CurrentVersion, null, false, false, "failed", component.CurrentVersion, null);
        var exit = 0;
        if (args.Any(arg => arg is not ("--check" or "--yes" or "--json")) || args.Distinct(StringComparer.Ordinal).Count() != args.Length)
        {
            result = result with { Status = "invalidArguments", Error = "Use update --help for supported options; specify each option once." };
            exit = 2;
        }
        else
        {
            try
            {
                var current = SemanticReleaseVersion.Parse(component.CurrentVersion);
                var release = await operations.FindReleaseAsync(component, cancellationToken);
                var latest = SemanticReleaseVersion.Parse(release.Version);
                var available = latest.CompareTo(current) > 0;
                result = result with { LatestVersion = release.Version, UpdateAvailable = available, Status = available ? "available" : "current" };
                if (!json)
                {
                    await output.WriteLineAsync($"{component.DisplayName} update\n\nCurrent version: {component.CurrentVersion}\nLatest release:  {release.Version}\n");
                    await output.WriteLineAsync(available ? "A newer version is available." : "No update is required.");
                }
                if (available && !args.Contains("--check", StringComparer.Ordinal) && (!json || args.Contains("--yes", StringComparer.Ordinal)))
                {
                    var confirmed = args.Contains("--yes", StringComparer.Ordinal);
                    if (!confirmed)
                    {
                        if (!interactive) throw new InvalidOperationException("Confirmation requires a terminal. Use --check or --yes.");
                        await output.WriteAsync($"Update {component.DisplayName} from {component.CurrentVersion} to {release.Version}? [Y/n]: ");
                        await output.FlushAsync(cancellationToken);
                        var answer = await input.ReadLineAsync(cancellationToken);
                        confirmed = answer is not null && (answer.Length == 0 || answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
                    }
                    if (confirmed)
                    {
                        result = result with { UpdateAttempted = true, FinalVersion = null };
                        if (!json) await output.WriteLineAsync($"Downloading installer and updating {component.DisplayName}...");
                        var final = await operations.InstallAsync(component, release, cancellationToken);
                        if (SemanticReleaseVersion.Parse(final).CompareTo(latest) != 0)
                            throw new InvalidOperationException("Installer completed but the installed version does not match the selected release. Inspect the installation and service status.");
                        result = result with { Status = "updated", FinalVersion = final };
                        if (!json) await output.WriteLineAsync($"{component.DisplayName} updated successfully.\nVersion: {final}");
                    }
                    else
                    {
                        result = result with { Status = "declined" };
                        if (!json) await output.WriteLineAsync("Update declined; installation unchanged.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = result with { Status = "canceled", Error = "Update canceled or timed out. If installation started, inspect service status before retrying." };
                exit = 130;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or KeyNotFoundException)
            {
                // External HTTP/process diagnostics can contain sensitive material; expose only owned messages.
                result = result with { Status = "failed", Error = exception is InvalidOperationException ? exception.Message : "Release lookup or installer failed. Check network access, release availability, root permissions and service status before retrying." };
                exit = 1;
            }
        }
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        else if (result.Error is not null) await error.WriteLineAsync(result.Error);
        return exit;
    }
}

internal sealed class SemanticReleaseVersion : IComparable<SemanticReleaseVersion>
{
    private static readonly Regex Pattern = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly BigInteger[] numbers;
    private readonly string[] prerelease;
    private SemanticReleaseVersion(BigInteger[] numbers, string[] prerelease) { this.numbers = numbers; this.prerelease = prerelease; }
    public bool IsStable => prerelease.Length == 0;
    public static SemanticReleaseVersion Parse(string value)
    {
        if (value.Length > 200) throw new ArgumentException("Invalid semantic version.");
        var match = Pattern.Match(value);
        if (!match.Success) throw new ArgumentException("Invalid semantic version.");
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(part => part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit))) throw new ArgumentException("Invalid semantic version.");
        return new([BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value), BigInteger.Parse(match.Groups[3].Value)], pre);
    }
    public int CompareTo(SemanticReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) { var comparison = numbers[i].CompareTo(other.numbers[i]); if (comparison != 0) return comparison; }
        if (IsStable || other.IsStable) return IsStable.CompareTo(other.IsStable);
        for (var i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            var leftNumeric = prerelease[i].All(char.IsAsciiDigit);
            var rightNumeric = other.prerelease[i].All(char.IsAsciiDigit);
            var comparison = leftNumeric && rightNumeric ? BigInteger.Parse(prerelease[i]).CompareTo(BigInteger.Parse(other.prerelease[i])) : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(prerelease[i], other.prerelease[i]);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }
}
