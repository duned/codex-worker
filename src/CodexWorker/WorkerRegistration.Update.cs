namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Json;

public sealed partial class WorkerRegistrationClient
{
    private static bool SelfUpdateSupported => OperatingSystem.IsLinux() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 &&
        string.Equals(Environment.ProcessPath, "/opt/codex-worker/CodexWorker", StringComparison.Ordinal) &&
        File.Exists("/usr/local/libexec/codex-worker-self-update");
    internal async Task<bool> ExecuteWorkerUpdateAsync(WorkerServerSettings settings, ProvisioningPolicy policy,
        WorkerRuntimeReadModel runtime, CancellationToken ct)
    {
        if (!SelfUpdateSupported) return false;
        var worker = await LoadRegisteredIdentityAsync(settings, ct);
        using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{worker}/update/request");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        var operation = await response.Content.ReadFromJsonAsync<WorkerUpdateOperation>(cancellationToken: ct)
            ?? throw new InvalidDataException("Empty Worker update command.");
        if (operation.WorkerId != worker || !WorkerUpdateProtocol.ValidId(operation.OperationId) ||
            operation.State is not ("waiting-for-idle" or "updating" or "reconnecting") || operation.DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Invalid Worker update command.");
        if (operation.State is "updating" or "reconnecting")
        {
            if (operation.InstallerVerified) return false;
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
        runtime.Registry.DrainWorker();
        async Task<bool> RecordFailureAsync()
        {
            runtime.Lifecycle.RecordUpdateResult("Inspect service status and the codex-worker-self-update journal before retrying.", false);
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
            if (!policy.Enabled || !policy.AllowedPrivilegedActions.Contains(WorkerUpdateProtocol.PolicyKey, StringComparer.Ordinal) ||
                policy.DeniedActions.Contains(WorkerUpdateProtocol.PolicyKey, StringComparer.Ordinal) ||
                !SelfUpdateSupported)
                return await RecordFailureAsync();
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var release = await new PackagedSelfUpdate(http).FindReleaseAsync(
                new("worker", "Codex Worker", ApplicationVersion.Display, "/opt/codex-worker/CodexWorker"), ct);
            using var start = CreateAuthorizedRequest(HttpMethod.Post, settings,
                $"api/v1/workers/{worker}/update/{operation.OperationId}/start");
            start.Content = JsonContent.Create(new WorkerUpdatePreparation(release.Version));
            using var authorization = await SendAsync(start, ct);
            authorization.EnsureSuccessStatusCode();
            var authorized = await authorization.Content.ReadFromJsonAsync<WorkerUpdateOperation>(cancellationToken: ct);
            if (authorized?.OperationId != operation.OperationId || authorized.WorkerId != worker || (authorized.ExpectedVersion != release.Version && !(authorized.InstallerVerified && authorized.ExpectedVersion == ApplicationVersion.Display)))
                throw new InvalidDataException("Invalid Worker update authorization.");
            if (authorized.State == "succeeded" || authorized.State == "reconnecting" && authorized.InstallerVerified)
            {
                if (!priorDrain) runtime.Registry.CancelWorkerDrain();
                return true;
            }
            if (authorized.State != "updating") throw new InvalidDataException("Worker update is not authorized.");
            runtime.Lifecycle.BeginUpdate();
            var launched = await RunUpdateControlAsync("/usr/bin/sudo", ["-n", "/usr/local/libexec/codex-worker-self-update"],
                TimeSpan.FromSeconds(30), ct);
            if (launched.ExitCode != 0) throw new InvalidOperationException("Update helper failed.");
            runtime.Events.Publish("worker.updating", "Worker self-update started; waiting for restart and readiness.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or ProcessTimeoutException)
        {
            return await RecordFailureAsync();
        }
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
