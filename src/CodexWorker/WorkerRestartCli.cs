namespace CodexWorker;

using System.Net.Http.Json;
using System.Text.Json;

internal static class WorkerProcessIdentity
{
    internal static readonly string InstanceId = CreateInstanceId();

    private static string CreateInstanceId() => Guid.TryParseExact(Environment.GetEnvironmentVariable("INVOCATION_ID"), "N", out var invocation)
        ? invocation.ToString("N") : Guid.NewGuid().ToString("N");
}

internal static class WorkerRestartCli
{
    internal const string Help = "Usage: sudo codex-worker restart [--json] [--config <path>]\nDrains the running Worker without interrupting executions, restarts the installed service and verifies a new ready instance. Deadline: 24 hours for drain, two minutes for readiness. Requires Linux systemd and the local API. Failures leave scheduling drained for inspection.";

    internal static async Task<int> RunAsync(WorkerCommandLine command, CancellationToken ct)
    {
        try
        {
            if (command.Arguments.Count > 1 || command.Arguments.Count == 1 && command.Arguments[0] != "--json")
                throw new ArgumentException(Help);
            if (!OperatingSystem.IsLinux() || Environment.UserName != "root" || !File.Exists("/opt/codex-worker/CodexWorker"))
                throw new InvalidOperationException("Restart requires sudo and the installed Linux Worker service.");
            // A second local request must not dispatch another restart while this one is pending.
            using var gate = new FileStream("/run/codex-worker-restart.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var config = GlobalWorkerConfiguration.Load(command.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            if (!config.Api.Enabled) throw new InvalidOperationException("Enable the local Worker API before restarting.");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(config.Api.ListenUrl), Timeout = TimeSpan.FromSeconds(10) };
            var service = await new ProcessRunner().RunAsync("/usr/bin/systemctl", ["show", "codex-worker.service", "--property=InvocationID", "--value"],
                "/", TimeSpan.FromSeconds(10), ct);
            if (service.ExitCode != 0 || !Guid.TryParseExact(service.StandardOutput.Trim(), "N", out var invocation))
                throw new InvalidOperationException("The installed Worker service is not online; inspect its service status.");
            await RestartAsync(client, invocation.ToString("N"), async token =>
            {
                var restart = await new ProcessRunner().RunAsync("/usr/bin/systemctl", ["restart", "codex-worker.service"],
                    "/", TimeSpan.FromSeconds(60), token);
                if (restart.ExitCode != 0) throw new InvalidOperationException("Service restart failed; inspect systemctl status codex-worker and its journal.");
            }, ct);
            if (command.Arguments.Contains("--json")) Console.WriteLine(JsonSerializer.Serialize(new { contractVersion = 1, status = "succeeded", diagnostic = "Worker restarted and online." }));
            else Console.WriteLine("Worker restarted and online.");
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or HttpRequestException or OperationCanceledException or System.ComponentModel.Win32Exception or ProcessTimeoutException)
        {
            WorkerCliOutput.Failure(command, new WorkerConsole(), Console.Out, "restart-failed", ex is OperationCanceledException
                ? "Restart timed out. Inspect service status and journal; drain and execution history are retained." : ex.Message);
            return ProcessExitCodes.StartupFailure;
        }
    }

    internal static async Task RestartAsync(HttpClient client, string expectedInstanceId, Func<CancellationToken, Task> restartService, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromHours(24));
        var original = await client.GetFromJsonAsync<WorkerStatus>("/api/status", deadline.Token)
            ?? throw new InvalidDataException("Worker status is unavailable.");
        if (original.InstanceId != expectedInstanceId)
            throw new InvalidOperationException("The configured API does not belong to the installed Worker service instance.");
        if (original.LifecycleState is "updating" or "restarting" or "update-failed" or "restart-failed")
            throw new InvalidOperationException("Worker maintenance requires inspection before another restart.");
        if (string.IsNullOrEmpty(original.InstanceId)) throw new InvalidDataException("Worker does not support verified restart.");
        using var drain = await client.PostAsync("/api/worker/drain", null, deadline.Token);
        drain.EnsureSuccessStatusCode();
        while (true)
        {
            var status = await client.GetFromJsonAsync<WorkerStatus>("/api/status", deadline.Token);
            if (status is { DrainRequested: true, ActiveExecutionCount: 0 }) break;
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
        using var preparation = await client.PostAsJsonAsync("/api/worker/restart/prepare", new WorkerRestartPreparation(original.InstanceId), deadline.Token);
        preparation.EnsureSuccessStatusCode();
        await restartService(deadline.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            try
            {
                var status = await client.GetFromJsonAsync<WorkerStatus>("/api/status", deadline.Token);
                if (status is { State: "running", LifecycleState: "ready" } && status.InstanceId != original.InstanceId && !string.IsNullOrEmpty(status.InstanceId)) break;
            }
            catch (HttpRequestException) { /* Service may still be starting. Deadline bounds reconnection. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

}
