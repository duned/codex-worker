namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Json;

public sealed partial class WorkerRegistrationClient
{
    private async Task RetrieveJournalAsync(WorkerServerSettings settings, string worker, CancellationToken ct)
    {
        try
        {
            await RetrieveJournalCoreAsync(settings, worker, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException or IOException)
        {
            // Journal inspection is optional: its failure must not invalidate a successful heartbeat.
            // Never include response content or exception messages in local diagnostics.
            Console.Error.WriteLine("Worker journal retrieval failed; inspect Server connectivity and journal availability.");
        }
    }

    private async Task RetrieveJournalCoreAsync(WorkerServerSettings settings, string worker, CancellationToken ct)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, settings, $"api/v1/workers/{worker}/journal/request");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
        var command = await response.Content.ReadFromJsonAsync<WorkerJournalRequest>(cancellationToken: ct);
        if (command is null || command.Id == Guid.Empty || command.Lines is < 1 or > WorkerJournalProtocol.MaximumLines) return;
        var result = await WorkerJournalReader.ReadAsync(command.Lines, ct);
        using var report = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{worker}/journal/{command.Id}");
        report.Content = JsonContent.Create(result);
        using var acknowledgement = await SendAsync(report, ct);
        acknowledgement.EnsureSuccessStatusCode();
    }
}

public static class WorkerJournalReader
{
    public static async Task<WorkerJournalResult> ReadAsync(int lines, CancellationToken ct)
    {
        if (lines is < 1 or > WorkerJournalProtocol.MaximumLines) throw new ArgumentOutOfRangeException(nameof(lines));
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/run/systemd/system")) return new("unsupported", []);
        try
        {
            // Only this process's systemd unit; never expose another service's journal.
            var cgroup = await File.ReadAllTextAsync("/proc/self/cgroup", ct);
            if (!cgroup.Split('/').Any(part => part.Trim() == "codex-worker.service")) return new("unsupported", []);
            var output = await new ProcessRunner().RunAsync("/usr/bin/sudo", ["-n", "/usr/bin/journalctl", "--unit=codex-worker.service", "--no-pager", "--quiet", "--output=short-iso-precise", "--lines=200"],
                "/", TimeSpan.FromSeconds(5), ct, captureTail: true);
            return FormatResult(output, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ProcessTimeoutException)
        {
            return new("unavailable", []);
        }
    }

    internal static WorkerJournalResult FormatResult(ProcessResult output, int lines)
    {
        if (lines is < 1 or > WorkerJournalProtocol.MaximumLines) throw new ArgumentOutOfRangeException(nameof(lines));
        if (output.ExitCode != 0)
            return new(output.StandardError.Contains("sudo:", StringComparison.OrdinalIgnoreCase) ||
                output.StandardError.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                    ? "denied" : "error", []);
        var safeOutput = System.Text.RegularExpressions.Regex.Replace(output.StandardOutput,
            @"-----BEGIN [^\r\n]*PRIVATE KEY-----[\s\S]*?(?:-----END [^\r\n]*PRIVATE KEY-----|$)", "[redacted private key]");
        var entries = safeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines)
            .Select(line => SecretSanitizer.Sanitize(line, Math.Min(1000, 100_000 / lines))).ToArray();
        return new("ready", entries);
    }

}
