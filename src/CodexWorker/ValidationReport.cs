using System.Text.Json;

namespace CodexWorker;

/// <summary>Optional metadata protocol emitted by a project-owned validation command.
/// Contains scope and measurements only, never raw process logs.</summary>
public sealed record ValidationReport(string Mode, string Reason, string BuildScope, IReadOnlyList<ValidationRun> Runs)
{
    public const string OutputPrefix = "CODEX_VALIDATION_RESULT=";

    public static ValidationReport? Parse(string output, bool commandSucceeded, IReadOnlyList<string>? secrets = null)
    {
        var lines = output.Split('\n').Where(line => line.StartsWith(OutputPrefix, StringComparison.Ordinal)).ToArray();
        if (lines.Length == 0) return null;
        if (lines.Length != 1 || lines[0].Length > 16_000)
            throw new InvalidDataException("Validation metadata is duplicated or exceeds supported bounds.");
        var report = JsonSerializer.Deserialize<ValidationReport>(lines[0][OutputPrefix.Length..], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (report is null || !ValidText(report.Mode, 64) || !ValidText(report.Reason, 512) ||
            report.BuildScope is null || report.BuildScope.Length > 1024 || report.Runs is null || report.Runs.Count > 64)
            throw new InvalidDataException("Validation metadata has invalid scope fields.");
        var runs = new List<ValidationRun>();
        foreach (var run in report.Runs)
        {
            if (run is null || !ValidText(run.Kind, 64) || run.Project is null || run.Project.Length > 1024 ||
                run.Filter is null || run.Filter.Length > 1024 || run.Cases < 0 || !double.IsFinite(run.DurationSeconds) ||
                run.DurationSeconds < 0 || (commandSucceeded && (!run.Succeeded || (run.Kind == "test" && run.Cases == 0))))
                throw new InvalidDataException("Validation metadata has invalid run measurements or an empty test selection.");
            runs.Add(run with { Project = FailureDiagnosticRedactor.Redact(run.Project, secrets),
                Filter = FailureDiagnosticRedactor.Redact(run.Filter, secrets), Kind = FailureDiagnosticRedactor.Redact(run.Kind, secrets) });
        }
        if (commandSucceeded && runs.Count == 0)
            throw new InvalidDataException("Successful validation metadata must include executed checks.");
        return report with { Mode = FailureDiagnosticRedactor.Redact(report.Mode, secrets),
            Reason = FailureDiagnosticRedactor.Redact(report.Reason, secrets),
            BuildScope = FailureDiagnosticRedactor.Redact(report.BuildScope, secrets), Runs = runs };
    }

    private static bool ValidText(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit;
}

public sealed record ValidationRun(string Kind, string Project, string Filter, int Cases, double DurationSeconds, bool Succeeded);
