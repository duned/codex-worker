namespace CodexProvisioning;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ProvisioningCommandAction>))]
public enum ProvisioningCommandAction { Detect, Install, Update, Uninstall, CheckAuthentication, Logout, CheckConfiguration, PrepareAuthentication, GenerateSshKey, InspectSshKey, RemoveSshKey, VerifyRepositoryAccess, Login, Configure }
[JsonConverter(typeof(JsonStringEnumConverter<ProvisioningCommandStatus>))]
public enum ProvisioningCommandStatus { Pending, Running, Succeeded, Failed, Cancelled, TimedOut }
[JsonConverter(typeof(JsonStringEnumConverter<ProvisioningDiagnostic>))]
public enum ProvisioningDiagnostic { Queued, Executing, Completed, Unsupported, Denied, ProcessFailed, Cancelled, TimedOut, Interrupted }
[JsonConverter(typeof(JsonStringEnumConverter<ProvisioningFailureCode>))]
public enum ProvisioningFailureCode { ElevationDenied, ExecutableNotFound, ProcessExited, VerificationFailed, ProcessStartFailed, CapabilityDetectionFailed, TimedOut, PackageUnavailable, RepositoryAccessFailed, DockerDaemonAccessRequired, SystemPrefixPermissionDenied, ProviderConfigurationFailed, RegistryUnavailable, CachePreparationFailed, DockerDaemonUnavailable, DockerServiceRestartRequired }
[JsonConverter(typeof(JsonStringEnumConverter<ProvisioningProviderStep>))]
public enum ProvisioningProviderStep { Unknown, AptIndexRefresh, AptRuntimeInstall, AptPackageInstall, AptPackageRemoval, NpmPackageInstall, NpmPackageRemoval, ExecutablePermissions, DockerDaemonConfiguration }

/// <summary>Safe, bounded failure context. It contains no process output or caller-controlled text.</summary>
public sealed record ProvisioningFailureDetail(ProvisioningFailureCode Code, int? ProcessExitCode = null,
    ProvisioningProviderStep ProviderStep = ProvisioningProviderStep.Unknown)
{
    public string Description => Code switch
    {
        ProvisioningFailureCode.ElevationDenied when ProviderStep is ProvisioningProviderStep.NpmPackageInstall or ProvisioningProviderStep.NpmPackageRemoval =>
            "Non-interactive sudo authorization was denied. Authorize the fixed product Codex helper through node-local sudo policy, or invoke local provisioning as an administrator with --allow-elevation. The action must still be allowlisted by local provisioning policy.",
        ProvisioningFailureCode.ElevationDenied => "Non-interactive sudo authorization was denied.",
        ProvisioningFailureCode.SystemPrefixPermissionDenied => "npm could not mutate the managed system prefix /usr/local or cache /var/cache/codex-provisioning/npm. Check node-local non-interactive sudo authorization for the fixed product Codex helper and filesystem permissions, then retry with --allow-elevation and the action allowlisted by local provisioning policy.",
        ProvisioningFailureCode.ProviderConfigurationFailed => "npm configuration is invalid. Repair or reinstall the product provisioning helper.",
        ProvisioningFailureCode.RegistryUnavailable => "npm could not reach the official registry. Check node DNS, network and registry availability.",
        ProvisioningFailureCode.CachePreparationFailed => "The root-owned product npm cache could not be prepared. Check its ownership, links and filesystem availability.",
        ProvisioningFailureCode.ExecutableNotFound => "A required provisioning executable was not found.",
        ProvisioningFailureCode.ProcessExited => $"A provisioning process exited unsuccessfully{(ProcessExitCode is { } code ? $" (exit code {code})" : "")}.",
        ProvisioningFailureCode.VerificationFailed => "The provisioning process completed but capability verification failed.",
        ProvisioningFailureCode.ProcessStartFailed => "A provisioning process could not be started.",
        ProvisioningFailureCode.CapabilityDetectionFailed => "Capability detection failed.",
        ProvisioningFailureCode.PackageUnavailable => "The managed tool package is unavailable. Configure compatible node-local apt sources and refresh their indexes before retrying.",
        ProvisioningFailureCode.RepositoryAccessFailed => "Git could not read the requested repository. Check the repository identifier, network access and node credentials.",
        ProvisioningFailureCode.DockerDaemonAccessRequired => "The Worker service account cannot access the Docker daemon. Run 'codex-worker provision configure docker --allow-elevation' with tool:docker:configure allowlisted by local policy, then rerun the configuration check.",
        ProvisioningFailureCode.DockerDaemonUnavailable => "Docker is installed but the local daemon is unavailable or unhealthy. Restore the Docker service, then rerun the configuration check.",
        ProvisioningFailureCode.DockerServiceRestartRequired => "Docker daemon access is configured, but the running Worker needs a fresh service context. Drain active work, restart codex-worker.service with 'sudo systemctl restart codex-worker', then run 'codex-worker provision check-configuration docker'.",
        ProvisioningFailureCode.TimedOut => "Provisioning exceeded its configured timeout.",
        _ => "Provisioning failed."
    } + (ProviderStep is ProvisioningProviderStep.Unknown ? string.Empty : $" Provider step: {ProviderStepDescription}.");

    private string ProviderStepDescription => ProviderStep switch
    {
        ProvisioningProviderStep.AptIndexRefresh => "refresh apt package indexes",
        ProvisioningProviderStep.AptRuntimeInstall => "install the Node.js and npm runtime",
        ProvisioningProviderStep.AptPackageInstall => "install the managed apt package",
        ProvisioningProviderStep.AptPackageRemoval => "remove the managed apt package",
        ProvisioningProviderStep.NpmPackageInstall => "install @openai/codex with npm",
        ProvisioningProviderStep.NpmPackageRemoval => "remove @openai/codex with npm",
        ProvisioningProviderStep.DockerDaemonConfiguration => "configure fixed Worker Docker daemon access",
        ProvisioningProviderStep.ExecutablePermissions => "set managed tool permissions",
        _ => "run the managed provider command"
    };

    public bool IsValid => Enum.IsDefined(Code) && (ProcessExitCode is null or >= 0) &&
        Enum.IsDefined(ProviderStep) && (Code == ProvisioningFailureCode.ProcessExited || ProcessExitCode is null) &&
        (Code switch
        {
            ProvisioningFailureCode.ProcessExited or ProvisioningFailureCode.ElevationDenied => true,
            ProvisioningFailureCode.SystemPrefixPermissionDenied or ProvisioningFailureCode.ProviderConfigurationFailed or
                ProvisioningFailureCode.RegistryUnavailable or ProvisioningFailureCode.CachePreparationFailed =>
                ProviderStep is ProvisioningProviderStep.NpmPackageInstall or ProvisioningProviderStep.NpmPackageRemoval,
            _ => ProviderStep == ProvisioningProviderStep.Unknown
        });
}

/// <summary>Bounded process result. StandardError is transient and used only to classify known failures.</summary>
public sealed record ProvisioningProcessResult(int ExitCode, string StandardError = "");

// No free-form arguments, shell text, credentials, paths or package names cross this boundary.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisioningCommandRequest(string NodeId, string CapabilityId, ProvisioningCommandAction Action,
    int TimeoutSeconds = 120, bool AllowElevation = false, string? Repository = null);
public sealed record ProvisioningCommand(string Id, ProvisioningCommandRequest Request, DateTimeOffset CreatedAtUtc,
    ProvisioningCommandStatus Status, ProvisioningDiagnostic Diagnostic, DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? DeadlineUtc = null, DateTimeOffset? CompletedAtUtc = null, SshPublicIdentity? PublicIdentity = null,
    CodexLoginInstructions? LoginInstructions = null, ProvisioningFailureDetail? FailureDetail = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisioningCommandReport(ProvisioningCommandStatus Status, ProvisioningDiagnostic Diagnostic,
    SshPublicIdentity? PublicIdentity = null, CodexLoginInstructions? LoginInstructions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProvisioningFailureDetail? FailureDetail = null);

public static class ProvisioningCommandProtocol
{
    public static bool Valid(ProvisioningCommandRequest? request) => request is not null &&
        (request.NodeId == "server" || Guid.TryParseExact(request.NodeId, "N", out _)) &&
        CapabilityCatalog.Definitions.Any(item => item.Id == request.CapabilityId) && Enum.IsDefined(request.Action) &&
        request.TimeoutSeconds is >= 5 and <= 600 &&
        (request.Action == ProvisioningCommandAction.VerifyRepositoryAccess
            ? request.Repository is not null && request.Repository.Length <= 140 && System.Text.RegularExpressions.Regex.IsMatch(request.Repository, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z") && !request.Repository.EndsWith("/..", StringComparison.Ordinal) && !request.Repository.EndsWith("/.", StringComparison.Ordinal) && !request.Repository.EndsWith("/.git", StringComparison.Ordinal)
            : request.Repository is null);

    public static bool Supported(ProvisioningCommandRequest request) => request.Action switch
    {
        ProvisioningCommandAction.Detect => true,
        ProvisioningCommandAction.Login => request.CapabilityId is "codex-cli" or "github-cli",
        ProvisioningCommandAction.Install or ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall => ToolProvisioningProviders.AptPackage(request.CapabilityId) is not null || request.CapabilityId == "codex-cli",
        ProvisioningCommandAction.CheckAuthentication or ProvisioningCommandAction.Logout => request.CapabilityId is "github-cli" or "codex-cli",
        ProvisioningCommandAction.Configure => request.CapabilityId == "docker",
        ProvisioningCommandAction.CheckConfiguration => request.CapabilityId is "git" or "docker",
        ProvisioningCommandAction.GenerateSshKey or ProvisioningCommandAction.InspectSshKey or
            ProvisioningCommandAction.RemoveSshKey or ProvisioningCommandAction.VerifyRepositoryAccess => request.CapabilityId == "git",
        ProvisioningCommandAction.PrepareAuthentication => request.CapabilityId == "github-cli",
        _ => false
    };

    public static bool Terminal(ProvisioningCommandStatus status) => status is ProvisioningCommandStatus.Succeeded or
        ProvisioningCommandStatus.Failed or ProvisioningCommandStatus.Cancelled or ProvisioningCommandStatus.TimedOut;

    public static bool ValidReport(ProvisioningCommandReport? report) => report is not null && (report.FailureDetail is null ||
        report.Status is ProvisioningCommandStatus.Failed or ProvisioningCommandStatus.TimedOut && report.FailureDetail.IsValid &&
        ((report.Status == ProvisioningCommandStatus.TimedOut) == (report.FailureDetail.Code == ProvisioningFailureCode.TimedOut))) && (report.PublicIdentity is null ||
        report.Status == ProvisioningCommandStatus.Succeeded && NodeGitHubSetup.ValidIdentity(report.PublicIdentity)) && (report.LoginInstructions is null ||
        report.Status == ProvisioningCommandStatus.Running && CodexDeviceLogin.Valid(report.LoginInstructions)) && (report.Status, report.Diagnostic) switch
    {
        (ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing) => true,
        (ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed) => true,
        (ProvisioningCommandStatus.Cancelled, ProvisioningDiagnostic.Cancelled) => true,
        (ProvisioningCommandStatus.TimedOut, ProvisioningDiagnostic.TimedOut) => true,
        (ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Unsupported or ProvisioningDiagnostic.Denied or
            ProvisioningDiagnostic.ProcessFailed or ProvisioningDiagnostic.Interrupted) => true,
        _ => false
    };
}

/// <summary>Product-owned commands only. Output is drained; bounded stderr is classified transiently and never returned or logged.</summary>
public sealed partial class NodeProvisioningCommandExecutor
{
    private readonly NodeCapabilityDiscovery _discovery;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<ProvisioningProcessResult>> _run;
    private readonly Func<bool> _supportsApt;
    private readonly Func<bool> _isRoot;
    private readonly Func<bool> _npmAvailable;
    private readonly NodeGitHubSetup _githubSetup;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Func<CodexLoginInstructions, CancellationToken, Task>, CancellationToken, Task<int>> _login;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public NodeProvisioningCommandExecutor(NodeCapabilityDiscovery discovery,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? run = null,
        Func<bool>? supportsApt = null, Func<bool>? isRoot = null, Func<bool>? npmAvailable = null, NodeGitHubSetup? githubSetup = null,
        Func<Func<CodexLoginInstructions, CancellationToken, Task>, CancellationToken, Task<int>>? login = null,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ProvisioningProcessResult>>? processRunner = null,
        TimeProvider? timeProvider = null, bool requireManagedGitHubAuthentication = false)
    {
        _discovery = discovery;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _githubSetup = githubSetup ?? new NodeGitHubSetup();
        if (run is not null && processRunner is not null)
            throw new ArgumentException("Specify either a process runner or an exit-code runner, not both.", nameof(processRunner));
        _run = processRunner ?? (run is null
            ? (executable, arguments, token) => RunAsync(executable, arguments, token, requireManagedGitHubAuthentication)
            : async (executable, arguments, token) => new ProvisioningProcessResult(await run(executable, arguments, token)));
        _supportsApt = supportsApt ?? SupportsPackageProvisioning;
        // A username is not proof of effective filesystem privileges (for example after
        // a service-account transition). Only effective UID zero can bypass sudo.
        _isRoot = isRoot ?? (() => OperatingSystem.IsLinux() && NativeMethods.GetEffectiveUserId() == 0);
        _login = login ?? CodexDeviceLogin.RunAsync;
        _npmAvailable = npmAvailable ?? (() => File.Exists("/usr/bin/node") && File.Exists("/usr/bin/npm"));
    }

    public static bool SupportsPackageProvisioning() => OperatingSystem.IsLinux() && File.Exists("/etc/debian_version");

    public async Task<ProvisioningCommandReport> ExecuteAsync(ProvisioningCommand command, bool permitted,
        CancellationToken cancellationToken = default,
        Func<ProvisioningCommandReport, CancellationToken, Task>? reportProgress = null)
    {
        if (!ProvisioningCommandProtocol.Valid(command.Request) || !ProvisioningCommandProtocol.Supported(command.Request))
            return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Unsupported);
        if (!permitted) return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Denied);
        if (command.DeadlineUtc is null) return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Unsupported);
        var remaining = command.DeadlineUtc.Value - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return new(ProvisioningCommandStatus.TimedOut, ProvisioningDiagnostic.TimedOut,
            FailureDetail: new ProvisioningFailureDetail(ProvisioningFailureCode.TimedOut));
        using var deadline = new CancellationTokenSource(remaining, _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        // Package-manager mutations share a gate, including Codex's runtime dependencies.
        var mutation = command.Request.Action is ProvisioningCommandAction.Install or ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall or ProvisioningCommandAction.Configure;
        var gate = _gates.GetOrAdd(mutation ? "tools" : command.Request.CapabilityId, _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        var refreshed = false;
        try
        {
            await gate.WaitAsync(timeout.Token);
            acquired = true;
            var request = command.Request;
            if (request.Action == ProvisioningCommandAction.Detect)
            {
                var states = await _discovery.GetAsync(refresh: true, cancellationToken: timeout.Token);
                refreshed = true;
                return states.Single(state => state.Id == request.CapabilityId).Health == CapabilityHealth.Error
                    ? Failed(ProvisioningFailureCode.CapabilityDetectionFailed)
                    : new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed);
            }
            if (NodeGitHubSetup.Handles(request))
            {
                var result = await _githubSetup.ExecuteAsync(request, timeout.Token, reportProgress is null ? null :
                    async instructions => await reportProgress(new(ProvisioningCommandStatus.Running,
                        ProvisioningDiagnostic.Executing, LoginInstructions: instructions), timeout.Token));
                await _discovery.GetAsync(refresh: true, cancellationToken: timeout.Token);
                refreshed = true;
                return result.Status == ProvisioningCommandStatus.Failed && result.FailureDetail is null
                    ? result with { FailureDetail = new ProvisioningFailureDetail(ProvisioningFailureCode.ProcessExited) }
                    : result;
            }
            if (request.Action == ProvisioningCommandAction.Login)
            {
                if (reportProgress is null) return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Unsupported);
                var code = await _login(async (instructions, token) =>
                {
                    if (!CodexDeviceLogin.Valid(instructions)) throw new InvalidOperationException("Invalid login instructions.");
                    await reportProgress(new(ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, LoginInstructions: instructions), token);
                }, timeout.Token);
                // Login completion alone is not proof of service authentication.
                if (code == 0) code = (await _run(CodexServiceEnvironment.Executable, ["login", "status"], timeout.Token)).ExitCode;
                return code == 0 ? new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed)
                    : Failed(ProvisioningFailureCode.ProcessExited, code);
            }
            string executable;
            IReadOnlyList<string> arguments;
            if (request.Action is ProvisioningCommandAction.Install or ProvisioningCommandAction.Update or ProvisioningCommandAction.Uninstall or ProvisioningCommandAction.Configure)
            {
                if (!_supportsApt()) return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Unsupported);
                if (!request.AllowElevation) return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Denied);
                var before = await _discovery.GetAsync(refresh: true, cancellationToken: timeout.Token);
                if (request.CapabilityId == "codex-cli" && request.Action == ProvisioningCommandAction.Uninstall &&
                    !_npmAvailable() && before.Single(state => state.Id == request.CapabilityId).Installation == InstallationState.Missing)
                {
                    refreshed = true;
                    return new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed);
                }
                ProvisioningProcessResult? processResult = null;
                var failedProviderStep = ProvisioningProviderStep.Unknown;
                var serviceRestartRequired = false;
                foreach (var step in ToolProvisioningProviders.Plan(request.CapabilityId, request.Action,
                    request.CapabilityId == "codex-cli" && _npmAvailable()))
                {
                    var elevate = step.RequiresElevation && !_isRoot();
                    processResult = await _run(elevate ? "/usr/bin/sudo" : step.Executable,
                        elevate ? ["-n", step.Executable, .. step.Arguments] : step.Arguments, timeout.Token);
                    if (step.FailureStep == ProvisioningProviderStep.DockerDaemonConfiguration && processResult.ExitCode == 10)
                    {
                        serviceRestartRequired = true;
                        processResult = new ProvisioningProcessResult(0);
                    }
                    if (processResult.ExitCode != 0)
                    {
                        failedProviderStep = step.FailureStep;
                        break;
                    }
                }
                var states = await _discovery.GetAsync(refresh: true, cancellationToken: timeout.Token);
                refreshed = true;
                var expected = request.Action == ProvisioningCommandAction.Uninstall ? InstallationState.Missing : InstallationState.Installed;
                var observedState = states.Single(state => state.Id == request.CapabilityId);
                if (processResult?.ExitCode == 0 && expected == InstallationState.Installed &&
                    (request.Action != ProvisioningCommandAction.Configure && observedState.Update == UpdateState.Available || !await _discovery.VerifyManagedInstallationAsync(observedState, timeout.Token)))
                    return Failed(ProvisioningFailureCode.VerificationFailed);
                if (request.CapabilityId == "docker" && expected == InstallationState.Installed)
                {
                    if (processResult is { ExitCode: not 0 } && failedProviderStep == ProvisioningProviderStep.DockerDaemonConfiguration)
                    {
                        var code = ClassifyDockerAccess(processResult);
                        return Failed(code, code == ProvisioningFailureCode.ProcessExited ? processResult.ExitCode : null,
                            code is ProvisioningFailureCode.ProcessExited or ProvisioningFailureCode.ElevationDenied
                                ? failedProviderStep : ProvisioningProviderStep.Unknown);
                    }
                    if (processResult?.ExitCode == 0)
                    {
                        if (observedState.DiagnosticCode == "docker-daemon-unavailable")
                            return Failed(ProvisioningFailureCode.DockerDaemonUnavailable);
                        if (observedState.Health == CapabilityHealth.Error)
                            return Failed(ProvisioningFailureCode.VerificationFailed);
                        if (serviceRestartRequired || observedState.Configuration != RequirementState.Satisfied)
                            return Failed(ProvisioningFailureCode.DockerServiceRestartRequired);
                    }
                }
                var providerFailureCode = processResult is { ExitCode: not 0 }
                    ? Classify(processResult, failedProviderStep) : ProvisioningFailureCode.VerificationFailed;
                return processResult?.ExitCode == 0 && observedState.Installation == expected && observedState.Health != CapabilityHealth.Error
                    ? new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed)
                    : processResult is { ExitCode: not 0 } ? Failed(providerFailureCode,
                        providerFailureCode == ProvisioningFailureCode.ProcessExited ? processResult.ExitCode : null,
                        providerFailureCode is ProvisioningFailureCode.ProcessExited or
                            ProvisioningFailureCode.ElevationDenied or ProvisioningFailureCode.SystemPrefixPermissionDenied or
                            ProvisioningFailureCode.ProviderConfigurationFailed or ProvisioningFailureCode.RegistryUnavailable or
                            ProvisioningFailureCode.CachePreparationFailed
                            ? failedProviderStep : ProvisioningProviderStep.Unknown)
                        : Failed(ProvisioningFailureCode.VerificationFailed);
            }
            else
            {
                executable = request.CapabilityId == "codex-cli" ? CodexServiceEnvironment.Executable
                    : CapabilityCatalog.Definitions.Single(item => item.Id == request.CapabilityId).Executable;
                arguments = request.Action switch
                {
                    ProvisioningCommandAction.CheckConfiguration when request.CapabilityId == "docker" => ["info", "--format", "{{.ServerVersion}}"],
                    ProvisioningCommandAction.CheckConfiguration => ["config", "--get", "user.name"],
                    ProvisioningCommandAction.CheckAuthentication => AuthenticationDependencyProbes.Get(
                        CapabilityCatalog.Definitions.Single(item => item.Id == request.CapabilityId)
                            .AuthenticationDependencies.Single()).Arguments,
                    _ => ["logout"]
                };
            }
            if (request.Action == ProvisioningCommandAction.CheckConfiguration && request.CapabilityId == "docker")
            {
                var probe = DockerDaemonAccessProbe.ForCurrentProcess(executable, arguments);
                executable = probe.Executable;
                arguments = probe.Arguments;
            }
            var verificationResult = await _run(executable, arguments, timeout.Token);
            if (verificationResult.ExitCode == 0 && command.Request.Action == ProvisioningCommandAction.CheckConfiguration && command.Request.CapabilityId == "git")
                verificationResult = await _run("git", ["config", "--get", "user.email"], timeout.Token);
            await _discovery.GetAsync(refresh: true, cancellationToken: timeout.Token);
            refreshed = true;
            if (verificationResult.ExitCode == 0)
                return new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed);
            var failureCode = request.Action == ProvisioningCommandAction.CheckConfiguration && request.CapabilityId == "docker"
                ? ClassifyDockerAccess(verificationResult) : Classify(verificationResult);
            return Failed(failureCode, failureCode == ProvisioningFailureCode.ProcessExited ? verificationResult.ExitCode : null);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return cancellationToken.IsCancellationRequested
                ? new(ProvisioningCommandStatus.Cancelled, ProvisioningDiagnostic.Cancelled)
                : new(ProvisioningCommandStatus.TimedOut, ProvisioningDiagnostic.TimedOut,
                    FailureDetail: new ProvisioningFailureDetail(ProvisioningFailureCode.TimedOut));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            var code = ex is FileNotFoundException || ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }
                ? ProvisioningFailureCode.ExecutableNotFound : ProvisioningFailureCode.ProcessStartFailed;
            return Failed(code);
        }
        finally
        {
            if (acquired)
            {
                // A killed/failed installer may have changed the node. Refresh under a separate
                // bounded token before releasing the gate, even after the request deadline.
                using var refresh = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { if (!refreshed) await _discovery.GetAsync(refresh: true, cancellationToken: refresh.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or
                    System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
                finally { gate.Release(); }
            }
        }
    }

    private static ProvisioningCommandReport Failed(ProvisioningFailureCode code, int? exitCode = null,
        ProvisioningProviderStep providerStep = ProvisioningProviderStep.Unknown) =>
        new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.ProcessFailed,
            FailureDetail: new ProvisioningFailureDetail(code, exitCode, providerStep));

    private static ProvisioningFailureCode Classify(ProvisioningProcessResult result,
        ProvisioningProviderStep providerStep = ProvisioningProviderStep.Unknown)
    {
        var error = result.StandardError;
        if (error.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not allowed to execute", StringComparison.OrdinalIgnoreCase))
            return ProvisioningFailureCode.ElevationDenied;
        if (providerStep is ProvisioningProviderStep.NpmPackageInstall or ProvisioningProviderStep.NpmPackageRemoval)
        {
            if (error.Contains("CODEX_CACHE_PREPARATION_FAILED", StringComparison.Ordinal))
                return ProvisioningFailureCode.CachePreparationFailed;
            if (error.Contains("double-loading config", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("ERR_INVALID_ARG", StringComparison.OrdinalIgnoreCase))
                return ProvisioningFailureCode.ProviderConfigurationFailed;
            if (new[] { "EAI_AGAIN", "ENOTFOUND", "ECONNREFUSED", "ETIMEDOUT", "ECONNRESET" }
                .Any(code => error.Contains(code, StringComparison.Ordinal)))
                return ProvisioningFailureCode.RegistryUnavailable;
        }
        if (providerStep is ProvisioningProviderStep.NpmPackageInstall or ProvisioningProviderStep.NpmPackageRemoval &&
            (error.Contains("EACCES", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("EPERM", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("permission denied", StringComparison.OrdinalIgnoreCase)))
            return ProvisioningFailureCode.SystemPrefixPermissionDenied;
        if (error.Contains("Unable to locate package", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("has no installation candidate", StringComparison.OrdinalIgnoreCase))
            return ProvisioningFailureCode.PackageUnavailable;
        return ProvisioningFailureCode.ProcessExited;
    }

    private static ProvisioningFailureCode ClassifyDockerAccess(ProvisioningProcessResult result) =>
        result.StandardError.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            ? ProvisioningFailureCode.DockerDaemonAccessRequired
            : result.StandardError.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase) ||
              result.StandardError.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
              result.StandardError.Contains("docker-daemon-unavailable", StringComparison.Ordinal)
                ? ProvisioningFailureCode.DockerDaemonUnavailable : Classify(result);

    private static async Task<ProvisioningProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken token, bool requireManagedGitHubAuthentication)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        process.StartInfo.Environment["LC_ALL"] = "C";
        await NodeGitHubSetup.ApplyGitHubEnvironmentAsync(process.StartInfo, token,
            requireManagedAuthentication: requireManagedGitHubAuthentication && arguments.Count > 0 && arguments[0] == "auth");
        if (executable == CodexServiceEnvironment.Executable) CodexServiceEnvironment.Apply(process.StartInfo);
        process.Start();
        process.StandardInput.Close();
        using var drainCancellation = new CancellationTokenSource();
        var output = DrainAsync(process.StandardOutput, drainCancellation.Token);
        var error = ReadBoundedAsync(process.StandardError, 4096, drainCancellation.Token);
        try
        {
            await process.WaitForExitAsync(token);
            return new ProvisioningProcessResult(process.ExitCode, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            drainCancellation.CancelAfter(TimeSpan.FromSeconds(3));
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken token)
    {
        var captured = new System.Text.StringBuilder(maximumCharacters);
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            var remaining = maximumCharacters - captured.Length;
            if (remaining > 0) captured.Append(buffer, 0, Math.Min(count, remaining));
        }
        return captured.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "geteuid")]
        internal static partial uint GetEffectiveUserId();
    }
}
