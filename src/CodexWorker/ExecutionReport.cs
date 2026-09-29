namespace CodexWorker;

public sealed record ValidationRepairRecord(string FailedCommand, int Attempt, int MaximumAttempts,
    string? RepairSummary, bool PassedAfterRepair, string? ValidationBeforeRepair = null,
    string? ValidationAfterRepair = null);

public sealed record IssueExecutionReport(string? ImplementationSummary,
    IReadOnlyList<ValidationRepairRecord> ValidationRepairs,
    string? FinalValidationFailure = null,
    string? Failure = null,
    string? HumanInput = null,
    GitIntegrationResult? Integration = null,
    TimeSpan Duration = default,
    Guid? ExecutionId = null,
    int AttemptNumber = 1,
    Guid? RetryOfExecutionId = null,
    bool Resumed = false,
    string? FinalValidationDiagnostics = null,
    int? FinalValidationExitCode = null,
    string? RecoveryBranch = null,
    bool WorkspacePreserved = false,
    bool RetryAvailable = false,
    IReadOnlyList<string>? SecretValues = null)
{
    public string ToMarkdown(IssueOutcomeKind kind)
    {
        var sections = new List<string>();
        if (AttemptNumber > 1)
            sections.Add($"## Attempt history\n\nAttempt {AttemptNumber}{(Resumed ? " resumed from" : " restarted after")} execution `{RetryOfExecutionId}`.");
        if (kind == IssueOutcomeKind.Blocked)
        {
            if (!string.IsNullOrWhiteSpace(ImplementationSummary)) sections.Add($"## Work performed\n\n{ImplementationSummary}");
            sections.Add($"## Human input required\n\n{HumanInput ?? Failure ?? "Human input is required to continue."}");
            return string.Join("\n\n", sections);
        }

        if (!string.IsNullOrWhiteSpace(ImplementationSummary))
            sections.Add($"## {(kind == IssueOutcomeKind.Succeeded ? "Implementation" : "Implementation attempt")}\n\n{ImplementationSummary}");
        if (ValidationRepairs.Count > 0 || FinalValidationFailure is not null)
        {
            var validation = new List<string> { "## Validation & repairs" };
            if (ValidationRepairs.Count > 0)
                validation.Add($"Initial validation failed: `{ValidationRepairs[0].FailedCommand}`.");
            else if (FinalValidationFailure is not null)
                validation.Add($"Initial validation failed: `{FinalValidationFailure}`.");
            foreach (var repair in ValidationRepairs)
            {
                validation.Add($"### Repair {repair.Attempt}/{repair.MaximumAttempts}");
                if (kind == IssueOutcomeKind.Failed && !string.IsNullOrWhiteSpace(repair.ValidationBeforeRepair))
                    validation.Add("Failure before repair:\n" + string.Join("\n", repair.ValidationBeforeRepair.Split('\n').Select(line => $"- {line}")));
                if (!string.IsNullOrWhiteSpace(repair.RepairSummary)) validation.Add(repair.RepairSummary);
                if (repair.PassedAfterRepair) validation.Add($"Validation passed after repair {repair.Attempt}/{repair.MaximumAttempts}.");
                else if (repair.Attempt < repair.MaximumAttempts || FinalValidationFailure is not null)
                    validation.Add($"Validation still failed: `{repair.FailedCommand}`.");
                if (kind == IssueOutcomeKind.Failed && !string.IsNullOrWhiteSpace(repair.ValidationAfterRepair))
                    validation.Add("Validation result after repair:\n" + string.Join("\n", repair.ValidationAfterRepair.Split('\n').Select(line => $"- {line}")));
            }
            if (FinalValidationFailure is not null)
            {
                validation.Add($"Validation failed after {(ValidationRepairs.Count == 0 ? "no repair attempts" : $"{ValidationRepairs.Count} repair attempt(s)")}: `{FinalValidationFailure}`.");
                validation.Add($"Final validation command: `{FinalValidationFailure}` (exit {FinalValidationExitCode?.ToString() ?? "unavailable; timed out"}).");
                if (!string.IsNullOrWhiteSpace(FinalValidationDiagnostics))
                    validation.Add("Remaining problems:\n" + string.Join("\n", FinalValidationDiagnostics.Split('\n').Select(line => $"- {line}")));
            }
            sections.Add(string.Join("\n\n", validation));
        }
        else if (kind == IssueOutcomeKind.Succeeded) sections.Add("## Validation\n\nValidation passed successfully.");

        if (kind == IssueOutcomeKind.Succeeded)
        {
            var result = new List<string> { "## Result", $"- Duration: {WorkerConsole.FormatDuration(Duration)}" };
            var integration = Integration;
            if (integration is { HasChanges: true })
            {
                var commit = System.Text.RegularExpressions.Regex.Match(integration.Summary, "Committed as `([^`]+)`");
                var branch = System.Text.RegularExpressions.Regex.Match(integration.Summary, "Merged into `([^`]+)`");
                var preserved = System.Text.RegularExpressions.Regex.Match(integration.Summary, "Preserved on origin as `([^`]+)`");
                if (commit.Success) result.Add($"- Commit: `{commit.Groups[1].Value}`");
                if (branch.Success) result.Add($"- Merged into: `{branch.Groups[1].Value}`");
                if (preserved.Success) result.Add($"- Preserved as: `{preserved.Groups[1].Value}`");
            }
            else if (integration is not null) result.Add("- Completed without code changes");
            sections.Add(string.Join("\n", result));
        }
        else
        {
            sections.Add($"## Failure\n\n{Failure ?? "The task could not be completed."}");
            if (WorkspacePreserved)
            {
                var recovery = new List<string> { "## Recovery" };
                if (ExecutionId is not null) recovery.Add($"Execution: `{ExecutionId}`");
                if (!string.IsNullOrWhiteSpace(RecoveryBranch)) recovery.Add($"Branch: `{RecoveryBranch}`");
                recovery.Add($"Workspace: {(WorkspacePreserved ? "preserved" : "not preserved")}");
                recovery.Add($"Retry/resume: {(RetryAvailable ? "available" : "unavailable")}");
                sections.Add(string.Join("\n", recovery));
            }
        }
        var markdown = string.Join("\n\n", sections);
        return kind == IssueOutcomeKind.Failed ? FailureDiagnosticRedactor.Redact(markdown, SecretValues) : markdown;
    }
}

internal static class FailureDiagnosticRedactor
{
    public static string Redact(string value, IReadOnlyList<string>? secretValues = null)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(value,
            @"(?i)(token|password|secret|credential|api[_-]?key|connectionstring|authorization)(\s*[:=]\s*)[^\s,;]+", "$1$2[redacted]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"(?i)\bBearer\s+\S+", "Bearer [redacted]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"(?i)(https?://[^:/\s]+):[^@/\s]+@", "$1:[redacted]@");
        if (secretValues is not null)
            foreach (var secret in secretValues.Where(item => item.Length >= 4).Distinct(StringComparer.Ordinal))
                safe = safe.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return new string(safe.Where(character => !char.IsControl(character) || character == '\t').ToArray());
    }
}
