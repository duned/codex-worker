namespace CodexWorker;

using System.Text.Json;

/// <summary>Safe, versioned errors for local CLI adapters; diagnostics stay separate from stdout JSON.</summary>
internal static class WorkerCliOutput
{
    public const int Cancelled = 130;

    public static void Failure(WorkerCommandLine command, WorkerConsole output, TextWriter writer,
        string code, string message, IReadOnlyList<string>? secrets = null)
    {
        var safe = message;
        if (secrets is not null)
            foreach (var secret in secrets.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal))
                safe = safe.Replace(secret, "[redacted]", StringComparison.Ordinal);
        safe = FailureDiagnosticRedactor.Redact(safe);
        if (safe.Length > 1000) safe = safe[..997] + "...";
        if (command.Arguments.Contains("--json", StringComparer.Ordinal))
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                contractVersion = 1,
                status = "failed",
                diagnostic = new WorkerAdministrationDiagnostic(code, safe)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        else
            output.InfrastructureFailure(safe);
    }
}
