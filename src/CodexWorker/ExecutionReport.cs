namespace CodexWorker;

public sealed record ValidationRepairRecord(string FailedCommand, int Attempt, int MaximumAttempts,
    string? RepairSummary, bool PassedAfterRepair);

public sealed record IssueExecutionReport(string? ImplementationSummary,
    IReadOnlyList<ValidationRepairRecord> ValidationRepairs,
    string? FinalValidationFailure = null,
    string? Failure = null,
    string? HumanInput = null,
    GitIntegrationResult? Integration = null,
    TimeSpan Duration = default,
    Guid? ExecutionId = null)
{
    public string ToMarkdown(IssueOutcomeKind kind)
    {
        var sections = new List<string>();
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
                if (!string.IsNullOrWhiteSpace(repair.RepairSummary)) validation.Add(repair.RepairSummary);
                if (repair.PassedAfterRepair) validation.Add($"Validation passed after repair {repair.Attempt}/{repair.MaximumAttempts}.");
                else if (repair.Attempt < repair.MaximumAttempts || FinalValidationFailure is not null)
                    validation.Add($"Validation still failed: `{repair.FailedCommand}`.");
            }
            if (FinalValidationFailure is not null)
                validation.Add($"Validation failed after all repair attempts: `{FinalValidationFailure}`.");
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
        }
        return string.Join("\n\n", sections);
    }
}
