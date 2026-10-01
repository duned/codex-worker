namespace CodexProvisioning;

using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

[JsonConverter(typeof(JsonStringEnumConverter<InstallationState>))]
public enum InstallationState { Unknown, Missing, Installed }
[JsonConverter(typeof(JsonStringEnumConverter<UpdateState>))]
public enum UpdateState { Unknown, Current, Available }
[JsonConverter(typeof(JsonStringEnumConverter<RequirementState>))]
public enum RequirementState { Unknown, Required, Satisfied }
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityHealth>))]
public enum CapabilityHealth { Healthy, Degraded, Error }
[JsonConverter(typeof(JsonStringEnumConverter<CapabilityOperationState>))]
public enum CapabilityOperationState { Idle, Running, Failed }

public sealed record CapabilityDefinition(string Id, string DisplayName, string Executable,
    bool RequiresAuthentication, bool RequiresConfiguration, IReadOnlyList<string> SupportedActions);
public sealed record CapabilityOperation(CapabilityOperationState State, string? Action = null, string? DiagnosticCode = null);
/// <summary>Machine observations only. Diagnostics are codes, never process output or credential material.</summary>
public sealed record CapabilityState(string Id, InstallationState Installation, string? DetectedVersion,
    UpdateState Update, RequirementState? Authentication, RequirementState? Configuration,
    CapabilityHealth Health, CapabilityOperation Operation, DateTimeOffset? DetectedAtUtc,
    string? DiagnosticCode = null);
public sealed record NodeCapability(CapabilityDefinition Definition, CapabilityState State, IReadOnlyList<string> AvailableActions);
public sealed record ProvisionableNode(string Id, string Kind, string DisplayName, string Connectivity,
    string ExecutionReadiness, string ProvisioningReadiness, bool ObservationsStale, IReadOnlyList<NodeCapability> Capabilities, string Health = "unknown");

public static class CapabilityCatalog
{
    public static IReadOnlyList<CapabilityDefinition> Definitions { get; } = Array.AsReadOnly<CapabilityDefinition>(
    [
        new("git", "Git", "git", false, true, ["refresh", "install", "update", "uninstall", "checkconfiguration", "generatesshkey", "inspectsshkey", "removesshkey", "verifyrepositoryaccess"]),
        new("github-cli", "GitHub CLI", "gh", true, false, ["refresh", "install", "update", "uninstall", "prepareauthentication", "checkauthentication", "logout"]),
        new("codex-cli", "Codex CLI", "codex", true, false, ["refresh", "install", "update", "uninstall", "login", "checkauthentication", "logout"])
    ]);

    public static CapabilityState Unknown(CapabilityDefinition definition) => new(definition.Id,
        InstallationState.Unknown, null, UpdateState.Unknown,
        definition.RequiresAuthentication ? RequirementState.Unknown : null,
        definition.RequiresConfiguration ? RequirementState.Unknown : null,
        CapabilityHealth.Degraded, new(CapabilityOperationState.Idle), null, "not-detected");

    // Only registered executors may advertise actions; metadata does not imply an installer exists.
    public static NodeCapability Describe(CapabilityDefinition definition, CapabilityState state, bool connected) =>
        new(definition, state, connected && state.Operation.State != CapabilityOperationState.Running
            ? definition.SupportedActions : []);

    public static bool Ready(IEnumerable<CapabilityState> states) => states.All(state =>
        state.Installation == InstallationState.Installed && state.Health == CapabilityHealth.Healthy &&
        state.Authentication is null or RequirementState.Satisfied &&
        state.Configuration is null or RequirementState.Satisfied && state.Operation.State == CapabilityOperationState.Idle);

    public static bool ValidInventory(IReadOnlyList<CapabilityState>? inventory)
    {
        if (inventory is null) return true; // Older managed clients do not report observations.
        if (inventory.Count > 32 || inventory.Any(state => state is null)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return inventory.All(state => ids.Add(state.Id) && Definitions.Any(definition => definition.Id == state.Id) &&
            Enum.IsDefined(state.Installation) && Enum.IsDefined(state.Update) && Enum.IsDefined(state.Health) &&
            (state.Authentication is null || Enum.IsDefined(state.Authentication.Value)) &&
            (state.Configuration is null || Enum.IsDefined(state.Configuration.Value)) &&
            state.Operation is not null && Enum.IsDefined(state.Operation.State) &&
            (state.DetectedVersion is null || state.DetectedVersion.Length <= 100 && Regex.IsMatch(state.DetectedVersion, @"^\d+(?:\.\d+){0,3}(?:[-+][0-9A-Za-z.-]+)?$")) &&
            state.DiagnosticCode is null or "not-detected" or "tool-missing" or "probe-failed" or "authentication-required" &&
            state.Operation.DiagnosticCode is null or "operation-failed" &&
            state.Operation.Action is null or "refresh" or "detect" or "ensure" or "install" or "update" or "uninstall" or "login" or "logout" or "provision" or "checkauthentication" or "checkconfiguration" or "prepareauthentication" or "generatesshkey" or "inspectsshkey" or "removesshkey" or "verifyrepositoryaccess");
    }
}

/// <summary>Bounded local detection, cached in memory. No release-service lookup or persisted machine facts.</summary>
public sealed class NodeCapabilityDiscovery
{
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> _run;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CapabilityState>? _cached;
    private DateTimeOffset _detected;
    private int _detecting;
    public NodeCapabilityDiscovery(Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>>? run = null) =>
        _run = run ?? RunAsync;

    public async Task<IReadOnlyList<CapabilityState>> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!refresh && Volatile.Read(ref _detecting) != 0)
            return (Volatile.Read(ref _cached) ?? CapabilityCatalog.Definitions.Select(CapabilityCatalog.Unknown).ToArray())
                .Select(state => state with { Operation = new(CapabilityOperationState.Running, "refresh") }).ToArray();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cached is not null && DateTimeOffset.UtcNow - _detected < TimeSpan.FromMinutes(5)) return _cached;
            Volatile.Write(ref _detecting, 1);
            var states = new List<CapabilityState>();
            foreach (var definition in CapabilityCatalog.Definitions)
            {
                var state = CapabilityCatalog.Unknown(definition);
                try
                {
                    var executable = definition.Id == "codex-cli" ? CodexServiceEnvironment.Executable : definition.Executable;
                    var version = await _run(executable, ["--version"], cancellationToken);
                    var match = Regex.Match(version.Output, @"(?<![\w])v?(\d+(?:\.\d+){0,3}(?:[-+][0-9A-Za-z.-]+)?)(?![\w])");
                    state = state with { Installation = version.ExitCode == 0 ? InstallationState.Installed : InstallationState.Unknown,
                        DetectedVersion = version.ExitCode == 0 && match.Success ? match.Groups[1].Value : null,
                        Health = version.ExitCode == 0 ? CapabilityHealth.Healthy : CapabilityHealth.Error,
                        DiagnosticCode = version.ExitCode == 0 ? null : "probe-failed" };
                    if (version.ExitCode == 0 && definition.RequiresConfiguration)
                    {
                        var name = await _run(executable, ["config", "--get", "user.name"], cancellationToken);
                        var email = await _run(executable, ["config", "--get", "user.email"], cancellationToken);
                        state = state with { Configuration = name.ExitCode == 0 && email.ExitCode == 0 &&
                            !string.IsNullOrWhiteSpace(name.Output) && !string.IsNullOrWhiteSpace(email.Output)
                            ? RequirementState.Satisfied : RequirementState.Required };
                    }
                    if (version.ExitCode == 0 && definition.RequiresAuthentication)
                    {
                        var auth = await _run(executable, definition.Id == "github-cli" ? ["auth", "status", "--hostname", "github.com"] : ["login", "status"], cancellationToken);
                        state = state with { Authentication = auth.ExitCode == 0 ? RequirementState.Satisfied : RequirementState.Required,
                            DiagnosticCode = auth.ExitCode == 0 ? null : "authentication-required" };
                    }
                    if (version.ExitCode == 0)
                        state = state with { Update = await DetectUpdateAsync(definition.Id, state.DetectedVersion, cancellationToken) };
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    state = state with { Installation = InstallationState.Missing, Health = CapabilityHealth.Healthy, DiagnosticCode = "tool-missing" };
                }
                catch (FileNotFoundException)
                {
                    state = state with { Installation = InstallationState.Missing, Health = CapabilityHealth.Healthy, DiagnosticCode = "tool-missing" };
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or UnauthorizedAccessException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    state = state with { Health = CapabilityHealth.Error, DiagnosticCode = "probe-failed" };
                }
                states.Add(state with { DetectedAtUtc = DateTimeOffset.UtcNow });
            }
            _detected = DateTimeOffset.UtcNow;
            return _cached = states;
        }
        finally
        {
            Volatile.Write(ref _detecting, 0);
            _gate.Release();
        }
    }

    private async Task<UpdateState> DetectUpdateAsync(string id, string? version, CancellationToken token)
    {
        try
        {
            var probe = ToolProvisioningProviders.CandidateProbe(id);
            var candidateResult = await _run(probe.Executable, probe.Arguments, token);
            if (ToolProvisioningProviders.AptPackage(id) is not null)
            {
                var policy = candidateResult;
                var installed = Regex.Match(policy.Output, @"Installed:\s*(\S+)");
                var candidate = Regex.Match(policy.Output, @"Candidate:\s*(\S+)");
                if (policy.ExitCode != 0 || !installed.Success || !candidate.Success ||
                    installed.Groups[1].Value == "(none)" || candidate.Groups[1].Value == "(none)") return UpdateState.Unknown;
                if (installed.Groups[1].Value == candidate.Groups[1].Value) return UpdateState.Current;
                var newer = await _run("/usr/bin/dpkg", ["--compare-versions", candidate.Groups[1].Value,
                    "gt", installed.Groups[1].Value], token);
                return newer.ExitCode == 0 ? UpdateState.Available : newer.ExitCode == 1 ? UpdateState.Current : UpdateState.Unknown;
            }
            var stable = candidateResult;
            var candidateVersion = stable.Output.Trim();
            if (stable.ExitCode != 0 || version is null || !Regex.IsMatch(candidateVersion, @"^\d+\.\d+\.\d+$")) return UpdateState.Unknown;
            if (candidateVersion == version) return UpdateState.Current;
            return Version.TryParse(version, out var installedVersion) && Version.TryParse(candidateVersion, out var availableVersion)
                ? availableVersion > installedVersion ? UpdateState.Available : UpdateState.Current : UpdateState.Unknown;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or TimeoutException or InvalidOperationException ||
            ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            // An unavailable package index does not invalidate installation/authentication facts.
            return UpdateState.Unknown;
        }
    }

    internal async Task<bool> VerifyManagedInstallationAsync(CapabilityState state, CancellationToken token)
    {
        var result = await _run(ToolProvisioningProviders.ManagedExecutable(state.Id), ["--version"], token);
        var match = Regex.Match(result.Output, @"(?<![\w])v?(\d+(?:\.\d+){0,3}(?:[-+][0-9A-Za-z.-]+)?)(?![\w])");
        return result.ExitCode == 0 && match.Success && match.Groups[1].Value == state.DetectedVersion;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["LC_ALL"] = "C";
        await NodeGitHubSetup.ApplyGitHubEnvironmentAsync(process.StartInfo, cancellationToken);
        if (executable == CodexServiceEnvironment.Executable) CodexServiceEnvironment.Apply(process.StartInfo);
        process.Start();
        var stdout = DrainAsync(process.StandardOutput, timeout.Token);
        var stderr = DrainAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            return (process.ExitCode, await stdout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            if (text.Length < 1024) text.Append(buffer, 0, Math.Min(count, 1024 - text.Length));
        return text.ToString();
    }
}
