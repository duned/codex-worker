using System.Net.Http.Json;
using System.Text.Json;

namespace CodexWorker;

internal static class StandaloneMaintenanceCli
{
    internal const string Help = "Usage: codex-worker maintenance clean-executions [--apply --confirm] [--json] [--limit 1..100] [--offset 0..10000] [--config <path>]\nPreview by default. Apply drains the running standalone Worker, waits without interrupting work, reserves maintenance and restores scheduling. Five-minute deadline. History is retained; managed records require Server maintenance.";

    internal static async Task<int> RunAsync(WorkerCommandLine command, WorkerConsole output, CancellationToken ct)
    {
        try
        {
            var args = command.Arguments;
            if (args.FirstOrDefault() != "clean-executions") throw new ArgumentException(Help);
            var limit = 20;
            var offset = 0;
            var seen = new HashSet<string>();
            for (var i = 1; i < args.Count; i++)
            {
                if (!seen.Add(args[i])) throw new ArgumentException(Help);
                if (args[i] is "--apply" or "--confirm" or "--json") continue;
                if (args[i] is not ("--limit" or "--offset") || i + 1 >= args.Count || !int.TryParse(args[i + 1], out var value)) throw new ArgumentException(Help);
                if (args[i++] == "--limit") limit = value; else offset = value;
            }
            if (seen.Contains("--apply") != seen.Contains("--confirm")) throw new ArgumentException("Apply requires both --apply and --confirm.");
            var config = GlobalWorkerConfiguration.Load(command.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            if (config.Server.Enabled || config.Projects.Ownership == "managed") throw new InvalidOperationException("Use Server managed maintenance.");
            if (!config.Api.Enabled) throw new InvalidOperationException("Enable the local Management API and start the Worker before maintenance.");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(config.Api.ListenUrl), Timeout = TimeSpan.FromMinutes(6) };
            using var response = await client.PostAsJsonAsync("/api/maintenance/clean-executions", new StandaloneExecutionMaintenanceRequest(seen.Contains("--apply"), limit, offset), ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<StandaloneExecutionMaintenanceResult>(ct) ?? throw new InvalidOperationException("Missing maintenance result.");
            if (seen.Contains("--json")) Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            else
            {
                if (result.Interrupted) Console.WriteLine("Maintenance interrupted; durable partial results retained. Reinspect before retry.");
                Console.WriteLine($"Scanned {result.Scanned} / Already resolved {result.AlreadyResolved} / Resources cleaned {result.ResourcesCleaned} / Archived {result.Archived} / Needs review {result.NeedsReview}");
                foreach (var item in result.Items) Console.WriteLine($"{item.Inspection.ExecutionId}: {item.Outcome} · {item.Inspection.ReasonCode} · {item.Inspection.Message}");
            }
            return result.NeedsReview == 0 && !result.Interrupted ? ProcessExitCodes.Success : ProcessExitCodes.StartupFailure;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or IOException)
        {
            WorkerCliOutput.Failure(command, output, Console.Out, "maintenance-rejected", FailureDiagnosticRedactor.Redact(ex.Message));
            return ProcessExitCodes.StartupFailure;
        }
    }
}
