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

[JsonConverter(typeof(JsonStringEnumConverter<AuthenticationDependencyKind>))]
public enum AuthenticationDependencyKind { GitHubCliLogin, CodexCliLogin }

[JsonConverter(typeof(JsonStringEnumConverter<ProvidedToolKind>))]
public enum ProvidedToolKind { Git, GitHubCli, CodexCli, DotNetSdk, DotNetRuntime, AspNetCoreRuntime, DockerEngine }
[JsonConverter(typeof(JsonStringEnumConverter<LocalConfigurationDependencyKind>))]
public enum LocalConfigurationDependencyKind { GitIdentity, DockerDaemonAccess }

/// <summary>Required node-local authentication, independent of installation and repository authorization.
/// An empty dependency list declares that the tool needs no authentication.</summary>
public sealed record CapabilityDefinition(string Id, string DisplayName, string Executable,
    IReadOnlyList<AuthenticationDependencyKind> AuthenticationDependencies, bool RequiresConfiguration,
    IReadOnlyList<string> SupportedActions)
{
    public IReadOnlyList<ProvidedToolKind> Provides { get; init; } = [];
    public LocalConfigurationDependencyKind? ConfigurationDependency { get; init; }
    public int? RequiredMajorVersion { get; init; }
    // Generic execution prerequisites only; workload tools remain project requirements.
    public bool RequiredForExecution { get; init; } = true;
    public bool RequiresAuthentication => AuthenticationDependencies.Count > 0;
}
public sealed record CapabilityOperation(CapabilityOperationState State, string? Action = null, string? DiagnosticCode = null);
/// <summary>Machine observations only. Diagnostics are codes, never process output or credential material.</summary>
public sealed record CapabilityState(string Id, InstallationState Installation, string? DetectedVersion,
    UpdateState Update, RequirementState? Authentication, RequirementState? Configuration,
    CapabilityHealth Health, CapabilityOperation Operation, DateTimeOffset? DetectedAtUtc,
    string? DiagnosticCode = null);
public sealed record CapabilityReadiness(bool Available, IReadOnlyList<string> BlockingReasons);
public sealed record NodeCapability(CapabilityDefinition Definition, CapabilityState State, IReadOnlyList<string> AvailableActions)
{
    public CapabilityReadiness Readiness => CapabilityCatalog.Evaluate(Definition, State);
}

/// <summary>Runs Docker probes as the packaged Worker account, even when local administration uses another identity.</summary>
internal static class DockerDaemonAccessProbe
{
    public static (string Executable, IReadOnlyList<string> Arguments) ForCurrentProcess(string executable,
        IReadOnlyList<string> arguments)
    {
        IReadOnlyList<string> localArguments = OperatingSystem.IsLinux()
            ? ["--host", "unix:///var/run/docker.sock", .. arguments] : arguments;
        return OperatingSystem.IsLinux() && Environment.UserName != "codex-worker"
            ? ("/usr/sbin/runuser", ["-u", "codex-worker", "--", executable, .. localArguments])
            : (executable, localArguments);
    }
}

public sealed record ProvisionableNode(string Id, string Kind, string DisplayName, string Connectivity,
    string ExecutionReadiness, string ProvisioningReadiness, bool ObservationsStale, IReadOnlyList<NodeCapability> Capabilities, string Health = "unknown");

public static class CapabilityCatalog
{
    public static IReadOnlyList<CapabilityDefinition> Definitions { get; } = Array.AsReadOnly<CapabilityDefinition>(
    [
        new("git", "Git", "git", Array.Empty<AuthenticationDependencyKind>(), true, ["refresh", "install", "update", "uninstall", "checkconfiguration", "generatesshkey", "inspectsshkey", "removesshkey", "verifyrepositoryaccess"]) { Provides = [ProvidedToolKind.Git], ConfigurationDependency = LocalConfigurationDependencyKind.GitIdentity },
        new("github-cli", "GitHub CLI", "gh", Array.AsReadOnly<AuthenticationDependencyKind>([AuthenticationDependencyKind.GitHubCliLogin]), false, ["refresh", "install", "update", "uninstall", "prepareauthentication", "login", "checkauthentication", "logout"]) { Provides = [ProvidedToolKind.GitHubCli] },
        new("codex-cli", "Codex CLI", "codex", Array.AsReadOnly<AuthenticationDependencyKind>([AuthenticationDependencyKind.CodexCliLogin]), false, ["refresh", "install", "update", "uninstall", "login", "checkauthentication", "logout"]) { Provides = [ProvidedToolKind.CodexCli] },
        new("dotnet-sdk", ".NET 10 SDK", "dotnet", [], false, ["refresh", "install", "update", "uninstall"])
            { Provides = [ProvidedToolKind.DotNetSdk, ProvidedToolKind.DotNetRuntime, ProvidedToolKind.AspNetCoreRuntime], RequiredMajorVersion = 10, RequiredForExecution = false },
        new("dotnet-runtime", ".NET 10 / ASP.NET Core Runtime", "dotnet", [], false, ["refresh", "install", "update", "uninstall"])
            { Provides = [ProvidedToolKind.DotNetRuntime, ProvidedToolKind.AspNetCoreRuntime], RequiredMajorVersion = 10, RequiredForExecution = false },
        new("docker", "Docker", "docker", [], true, ["refresh", "install", "update", "uninstall", "checkconfiguration", "configure"])
            { Provides = [ProvidedToolKind.DockerEngine], ConfigurationDependency = LocalConfigurationDependencyKind.DockerDaemonAccess, RequiredForExecution = false }
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

    /// <summary>Installation alone does not satisfy a provider's typed local dependencies.
    /// This result does not establish repository access or agent execution preflight.</summary>
    public static CapabilityReadiness Evaluate(CapabilityDefinition definition, CapabilityState state)
    {
        var reasons = new List<string>();
        if (state.Id != definition.Id || state.Installation == InstallationState.Unknown) reasons.Add("not-detected");
        else if (state.Installation == InstallationState.Missing) reasons.Add("tool-missing");
        if (state.Health == CapabilityHealth.Error) reasons.Add("probe-failed");
        else if (state.Health == CapabilityHealth.Degraded) reasons.Add(state.DiagnosticCode == "docker-daemon-unavailable"
            ? "docker-daemon-unavailable" : "not-detected");
        if (definition.RequiresAuthentication && state.Authentication != RequirementState.Satisfied)
            reasons.Add("authentication-required");
        if (definition.RequiresConfiguration && state.Configuration != RequirementState.Satisfied)
            reasons.Add("configuration-required");
        if (state.Operation.State != CapabilityOperationState.Idle)
            reasons.Add(state.Operation.State == CapabilityOperationState.Running ? "operation-running" : "operation-failed");
        return new(reasons.Count == 0, reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static bool Ready(IEnumerable<CapabilityState> states) => states.All(state =>
        Definitions.FirstOrDefault(definition => definition.Id == state.Id) is { } definition &&
        Evaluate(definition, state).Available);

    /// <summary>Task-specific tool availability; overlapping providers may satisfy the same tool.</summary>
    public static bool ProvidesTool(ProvidedToolKind tool, IReadOnlyList<CapabilityState> states) =>
        Definitions.Where(definition => definition.Provides.Contains(tool)).Any(definition =>
            Evaluate(definition, states.FirstOrDefault(state => state.Id == definition.Id) ?? Unknown(definition)).Available);

    public static CapabilityReadiness ExecutionReadiness(IReadOnlyList<CapabilityState> states)
    {
        var reasons = Definitions.Where(definition => definition.RequiredForExecution).SelectMany(definition =>
            Evaluate(definition, states.FirstOrDefault(state => state.Id == definition.Id) ?? Unknown(definition))
                .BlockingReasons.Select(reason => $"{definition.Id}:{reason}")).ToArray();
        return new(reasons.Length == 0, reasons);
    }

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
            state.DiagnosticCode is null or "not-detected" or "tool-missing" or "probe-failed" or "authentication-required" or "configuration-required" or "docker-daemon-access-required" or "docker-daemon-unavailable" &&
            state.Operation.DiagnosticCode is null or "operation-failed" &&
            state.Operation.Action is null or "refresh" or "detect" or "ensure" or "install" or "update" or "uninstall" or "login" or "logout" or "provision" or "checkauthentication" or "checkconfiguration" or "configure" or "prepareauthentication" or "generatesshkey" or "inspectsshkey" or "removesshkey" or "verifyrepositoryaccess");
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
                    var version = await _run(executable, ToolProvisioningProviders.VersionArguments(definition.Id), cancellationToken);
                    var detectedVersion = ToolProvisioningProviders.ParseVersion(definition.Id, version.Output);
                    state = state with { Installation = version.ExitCode == 0 ? InstallationState.Installed : InstallationState.Unknown,
                        DetectedVersion = version.ExitCode == 0 ? detectedVersion : null,
                        Health = version.ExitCode == 0 ? CapabilityHealth.Healthy : CapabilityHealth.Error,
                        DiagnosticCode = version.ExitCode == 0 ? null : "probe-failed" };
                    if (version.ExitCode == 0 && definition.RequiredMajorVersion is not null && detectedVersion is null)
                        state = state with { Installation = InstallationState.Missing, DiagnosticCode = "tool-missing" };
                    if (version.ExitCode == 0 && definition.ConfigurationDependency == LocalConfigurationDependencyKind.DockerDaemonAccess)
                    {
                        var probe = DockerDaemonAccessProbe.ForCurrentProcess(executable, ["info", "--format", "{{.ServerVersion}}"]);
                        var daemon = await _run(probe.Executable, probe.Arguments, cancellationToken);
                        var accessible = daemon.ExitCode == 0 && !string.IsNullOrWhiteSpace(daemon.Output);
                        var accessDenied = daemon.Output.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
                        state = state with { Configuration = accessible ? RequirementState.Satisfied : RequirementState.Required,
                            Health = accessible || accessDenied ? CapabilityHealth.Healthy : CapabilityHealth.Degraded,
                            DiagnosticCode = accessible ? null : accessDenied ? "docker-daemon-access-required" : "docker-daemon-unavailable" };
                    }
                    if (version.ExitCode == 0 && definition.ConfigurationDependency == LocalConfigurationDependencyKind.GitIdentity)
                    {
                        var name = await _run(executable, ["config", "--get", "user.name"], cancellationToken);
                        var email = await _run(executable, ["config", "--get", "user.email"], cancellationToken);
                        var configured = name.ExitCode == 0 && email.ExitCode == 0 &&
                            !string.IsNullOrWhiteSpace(name.Output) && !string.IsNullOrWhiteSpace(email.Output);
                        state = state with { Configuration = configured ? RequirementState.Satisfied : RequirementState.Required,
                            DiagnosticCode = configured ? null : "configuration-required" };
                    }
                    if (version.ExitCode == 0 && definition.RequiresAuthentication)
                    {
                        var authenticated = true;
                        foreach (var dependency in definition.AuthenticationDependencies)
                        {
                            var probe = AuthenticationDependencyProbes.Get(dependency);
                            var auth = await _run(probe.Executable, probe.Arguments, cancellationToken);
                            authenticated &= auth.ExitCode == 0;
                        }
                        state = state with { Authentication = authenticated ? RequirementState.Satisfied : RequirementState.Required,
                            DiagnosticCode = authenticated ? state.DiagnosticCode : "authentication-required" };
                    }
                    if (state.Installation == InstallationState.Installed)
                        state = state with { Update = await DetectUpdateAsync(definition.Id, state.DetectedVersion, cancellationToken) };
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
                {
                    state = state.Installation == InstallationState.Installed
                        ? state with { Health = CapabilityHealth.Error, DiagnosticCode = "probe-failed" }
                        : state with { Installation = InstallationState.Missing, Health = CapabilityHealth.Healthy, DiagnosticCode = "tool-missing" };
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException ||
            ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            // An unavailable package index does not invalidate installation/authentication facts.
            return UpdateState.Unknown;
        }
    }

    internal async Task<bool> VerifyManagedInstallationAsync(CapabilityState state, CancellationToken token)
    {
        var result = await _run(ToolProvisioningProviders.ManagedExecutable(state.Id), ToolProvisioningProviders.VersionArguments(state.Id), token);
        var version = ToolProvisioningProviders.ParseVersion(state.Id, result.Output);
        return result.ExitCode == 0 && version is not null && version == state.DetectedVersion;
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
            var output = await stdout;
            return (process.ExitCode, process.ExitCode == 0 ? output : await stderr);
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
