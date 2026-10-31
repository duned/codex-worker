using System.Net.Http.Json;
using System.Text.Json;

namespace CodexWorker;

internal static class RecoveryDiagnosticsCli
{
    internal const string Help = "Usage: codex-worker recovery blocked [--issue NUMBER] [--execution ID] [--reason CODE] [--limit 1..100] [--offset 0..10000] [--json] [--config <path>]";

    internal static async Task<int> RunAsync(WorkerCommandLine command, WorkerConsole output, CancellationToken ct, TextWriter? writer = null)
    {
        var destination = writer ?? Console.Out;
        try
        {
            var args = command.Arguments;
            if (args.FirstOrDefault() != "blocked") throw new ArgumentException(Help);
            int? issue = null;
            Guid? execution = null;
            string? reason = null;
            var limit = 50;
            var offset = 0;
            var json = false;
            var seen = new HashSet<string>();
            for (var index = 1; index < args.Count; index++)
            {
                var option = args[index];
                if (!seen.Add(option)) throw new ArgumentException(Help);
                if (option == "--json") { json = true; continue; }
                if (option is not ("--issue" or "--execution" or "--reason" or "--limit" or "--offset") ||
                    index + 1 >= args.Count || !seen.Add(option + "-value")) throw new ArgumentException(Help);
                var value = args[++index];
                switch (option)
                {
                    case "--issue" when int.TryParse(value, out var number) && number > 0: issue = number; break;
                    case "--execution" when Guid.TryParse(value, out var id) && id != Guid.Empty: execution = id; break;
                    case "--reason" when value.Length is > 0 and <= 120 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'): reason = value; break;
                    case "--limit" when int.TryParse(value, out var parsedLimit) && parsedLimit is >= 1 and <= 100: limit = parsedLimit; break;
                    case "--offset" when int.TryParse(value, out var parsedOffset) && parsedOffset is >= 0 and <= 10000: offset = parsedOffset; break;
                    default: throw new ArgumentException(Help);
                }
            }
            var config = GlobalWorkerConfiguration.Load(command.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            if (config.Server.Enabled || config.Projects.Ownership == "managed") throw new InvalidOperationException("Standalone recovery diagnostics are available only in standalone mode.");
            if (!config.Api.Enabled) throw new InvalidOperationException("Enable the local Management API and start the Worker to query retained diagnostics.");
            var query = new List<string> { $"limit={limit}", $"offset={offset}" };
            if (issue is not null) query.Add($"issueNumber={issue}");
            if (execution is not null) query.Add($"executionId={execution}");
            if (reason is not null) query.Add("reasonCode=" + Uri.EscapeDataString(reason));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(config.Api.ListenUrl), Timeout = TimeSpan.FromSeconds(30) };
            using var response = await client.GetAsync("/api/recovery/blocked?" + string.Join('&', query), ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<WorkerRuntimeReadModel.StandaloneRecoveryDiagnosticsPage>(ct)
                ?? throw new InvalidOperationException("Missing recovery diagnostics response.");
            if (json) destination.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            else
            {
                if (result.Items.Count == 0) destination.WriteLine("No retained blocked recovery diagnostics found.");
                foreach (var item in result.Items)
                    destination.WriteLine($"Execution {item.ExecutionId} · Issue #{item.IssueNumber} · {item.ReasonCode} · {item.Explanation} · {item.CheckStatus} · checked {item.CheckedAtUtc:O}");
                if (result.HasMore) destination.WriteLine($"More results available; use --offset {offset + limit} (total {result.Total}).");
            }
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or IOException)
        {
            WorkerCliOutput.Failure(command, output, destination, "recovery-diagnostics-rejected", FailureDiagnosticRedactor.Redact(ex.Message));
            return ProcessExitCodes.StartupFailure;
        }
    }
}
