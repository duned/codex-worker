namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Json;

public sealed partial class WorkerRegistrationClient
{
    private string? _managedRestartId;

    private static bool RestartSupported => OperatingSystem.IsLinux() &&
        string.Equals(Environment.ProcessPath, "/opt/codex-worker/CodexWorker", StringComparison.Ordinal);
    private static bool SelfUpdateSupported => OperatingSystem.IsLinux() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 &&
        string.Equals(Environment.ProcessPath, "/opt/codex-worker/CodexWorker", StringComparison.Ordinal) &&
        File.Exists("/usr/local/libexec/codex-worker-self-update");
    internal async Task<bool> ExecuteWorkerUpdateAsync(WorkerServerSettings settings, ProvisioningPolicy policy,
        WorkerRuntimeReadModel runtime, CancellationToken ct) =>
        await ExecuteWorkerUpdateAsync(settings, policy, runtime, RestartSupported, RunUpdateControlAsync, ct);

    // The service boundary is injected for deterministic handoff/shutdown regression coverage.
    internal async Task<bool> ExecuteWorkerUpdateAsync(WorkerServerSettings settings, ProvisioningPolicy policy,
        WorkerRuntimeReadModel runtime, bool restartSupported,
        Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>> runControl, CancellationToken ct)
    {
        if (!SelfUpdateSupported && !restartSupported) return false;
        var worker = await LoadRegisteredIdentityAsync(settings, ct);
        using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{worker}/update/request");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        var operation = await response.Content.ReadFromJsonAsync<WorkerUpdateOperation>(cancellationToken: ct)
            ?? throw new InvalidDataException("Empty Worker update command.");
        // Consume the existing explicit dashboard recovery acknowledgement only for the
        // failed restart owned by this process. A released Server fence is not restart success.
        if (operation.WorkerId == worker && operation is { Restart: true, State: "failed", DrainReleasedAtUtc: not null })
        {
            if (operation.OperationId == _managedRestartId)
            {
                runtime.State = WorkerLifecycleStates.NotReady;
                if (runtime.Registry.TryReleaseFailedRestart()) _managedRestartId = null;
            }
            return false;
        }
        if (operation.WorkerId != worker || !WorkerUpdateProtocol.ValidId(operation.OperationId) ||
            operation.State is not ("waiting-for-idle" or "updating" or "reconnecting") || operation.DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Invalid Worker update command.");
        if (operation.State is "updating" or "reconnecting")
        {
            if (operation.Restart || operation.InstallerVerified) return false;
            string state;
            try { state = await ReadUpdaterStateAsync(ct); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ProcessTimeoutException or System.ComponentModel.Win32Exception)
            { state = "failed"; }
            if (state == "running") return false;
            using var outcome = CreateAuthorizedRequest(HttpMethod.Post, settings,
                $"api/v1/workers/{worker}/update/{operation.OperationId}/{(state == "succeeded" ? "verified" : "fail")}");
            using var acknowledgement = await SendAsync(outcome, ct);
            acknowledgement.EnsureSuccessStatusCode();
            return false;
        }
        // Local lifecycle shares its reservation gate with execution admission. Never interrupt work.
        if (runtime.Lifecycle.Snapshot.ActiveExecutions != 0) return false;
        var priorDrain = runtime.Lifecycle.Snapshot.DrainRequested;
        var localDrainStarted = false;
        async Task<bool> RecordFailureAsync()
        {
            if (localDrainStarted && operation.Restart)
            {
                _managedRestartId = operation.OperationId;
                runtime.Lifecycle.RecordRestartFailure("Inspect codex-worker.service status and journal before retrying.");
            }
            else if (localDrainStarted) runtime.Lifecycle.RecordUpdateResult("Inspect service status and the codex-worker-self-update journal before retrying.", false);
            if (!ct.IsCancellationRequested)
            {
                using var failure = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{worker}/update/{operation.OperationId}/fail");
                using var acknowledgement = await SendAsync(failure, ct);
                acknowledgement.EnsureSuccessStatusCode();
            }
            return true;
        }
        try
        {
            if (!(operation.Restart ? restartSupported : SelfUpdateSupported) ||
                !TryDrainForWorkerUpdate(runtime.Registry, policy, operation.Restart))
                return await RecordFailureAsync();
            localDrainStarted = true;
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var release = operation.Restart ? null : await new PackagedSelfUpdate(http).FindReleaseAsync(
                new("worker", "Codex Worker", ApplicationVersion.Display, "/opt/codex-worker/CodexWorker"), ct);
            using var start = CreateAuthorizedRequest(HttpMethod.Post, settings,
                $"api/v1/workers/{worker}/update/{operation.OperationId}/start");
            start.Content = JsonContent.Create(new WorkerUpdatePreparation(release?.Version ?? ApplicationVersion.Display));
            using var authorization = await SendAsync(start, ct);
            authorization.EnsureSuccessStatusCode();
            var authorized = await authorization.Content.ReadFromJsonAsync<WorkerUpdateOperation>(cancellationToken: ct);
            if (authorized?.OperationId != operation.OperationId || authorized.WorkerId != worker || (authorized.ExpectedVersion != (release?.Version ?? ApplicationVersion.Display) && !(authorized.InstallerVerified && authorized.ExpectedVersion == ApplicationVersion.Display)))
                throw new InvalidDataException("Invalid Worker update authorization.");
            if (authorized.State == "succeeded" || authorized.State == "reconnecting" && authorized.InstallerVerified)
            {
                if (!priorDrain) runtime.Registry.CancelWorkerDrain();
                return true;
            }
            if (authorized.State != "updating") throw new InvalidDataException("Worker update is not authorized.");
            if (operation.Restart)
            {
                if (!runtime.Registry.TryPrepareRestart()) throw new InvalidOperationException("Worker restart drain reservation is unavailable.");
                _managedRestartId = operation.OperationId;
            }
            else runtime.Lifecycle.BeginUpdate();
            if (operation.Restart)
            {
                // Persist authorization before dispatch. Never replay an uncertain service-manager request.
                using var verified = CreateAuthorizedRequest(HttpMethod.Post, settings,
                    $"api/v1/workers/{worker}/update/{operation.OperationId}/verified");
                using var acknowledgement = await SendAsync(verified, ct);
                acknowledgement.EnsureSuccessStatusCode();
            }
            var launched = await runControl("/usr/bin/sudo", operation.Restart ? ["-n", "/usr/bin/systemctl", "--no-block", "restart", "codex-worker.service"] : ["-n", "/usr/local/libexec/codex-worker-self-update"],
                TimeSpan.FromSeconds(30), ct);
            if (launched.ExitCode != 0) throw new InvalidOperationException(operation.Restart ? "Service-manager restart request failed." : "Update helper failed.");
            runtime.Events.Publish(operation.Restart ? "worker.restarting" : "worker.updating",
                operation.Restart ? "Worker restart requested; waiting for a new ready instance." : "Worker self-update started; waiting for restart and readiness.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // systemd stops this process as part of an acknowledged restart. Preserve the
            // handoff for Server verification rather than reporting shutdown as failure.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or ProcessTimeoutException)
        {
            return await RecordFailureAsync();
        }
    }

    internal static bool TryDrainForWorkerUpdate(ProjectRuntimeRegistry registry, ProvisioningPolicy policy, bool restart)
    {
        var action = restart ? WorkerUpdateProtocol.RestartPolicyKey : WorkerUpdateProtocol.PolicyKey;
        // Rejection must not turn a healthy idle process into restart-failed or leave a local drain.
        if (!policy.Enabled || !policy.AllowedPrivilegedActions.Contains(action, StringComparer.Ordinal) ||
            policy.DeniedActions.Contains(action, StringComparer.Ordinal)) return false;
        registry.DrainWorker();
        return true;
    }

    private static async Task<string> ReadUpdaterStateAsync(CancellationToken ct)
    {
        var result = await RunUpdateControlAsync("/usr/bin/systemctl",
            ["show", "codex-worker-self-update.service", "--property=SubState", "--property=Result", "--property=ExecMainStatus"],
            TimeSpan.FromSeconds(10), ct);
        var value = result.StandardOutput;
        if (result.ExitCode != 0 || value.Length > 1024) return "failed";
        var fields = value.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (fields.Contains("SubState=exited") && fields.Contains("Result=success") && fields.Contains("ExecMainStatus=0")) return "succeeded";
        return fields.Contains("SubState=running") || fields.Contains("SubState=start") ? "running" : "failed";
    }

    private static Task<ProcessResult> RunUpdateControlAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .ToDictionary(key => key, _ => (string?)null, StringComparer.Ordinal);
        environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        environment["LANG"] = "C.UTF-8";
        return new ProcessRunner().RunAsync(executable, arguments, Directory.GetCurrentDirectory(), timeout,
            ct, environment, standardInput: "");
    }

}
