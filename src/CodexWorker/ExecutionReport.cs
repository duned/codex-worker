namespace CodexWorker;

public sealed record ValidationRepairRecord(string FailedCommand, int Attempt, int MaximumAttempts,
    string? RepairSummary, bool PassedAfterRepair, string? ValidationBeforeRepair = null,
    string? ValidationAfterRepair = null, bool IntegrationRepair = false);

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
    IReadOnlyList<string>? SecretValues = null,
    string? FailureCategory = null,
    string? EffectiveModel = null,
    string? EffectiveEffort = null)
{
    public string PostRebaseValidationOutcome => ValidationRepairs.Any(repair => repair.IntegrationRepair)
        ? $"failed: post-rebase validation ({ValidationRepairs.Count(repair => repair.IntegrationRepair)} integration repair attempt(s))"
        : "failed: post-rebase validation (2 attempts)";

    public string ToMarkdown(IssueOutcomeKind kind)
    {
        var sections = new List<string>();
        if (ExecutionId is { } executionId)
        {
            sections.Add("### Execution");
            sections.Add($"Execution `{ExecutionFormatting.Display(executionId)}` (`{executionId}`).");
        }
        if (EffectiveEffort is not null)
            sections.Add($"Codex model: `{EffectiveModel ?? "unknown (CLI model unavailable)"}` · effort: `{EffectiveEffort}`.");
        if (AttemptNumber > 1)
            sections.Add($"### Attempt history\n\nCurrent execution " +
                (ExecutionId is { } currentId ? $"`{ExecutionFormatting.Display(currentId)}` (`{currentId}`)" : "identity unavailable") +
                $" (attempt {AttemptNumber}); " +
                $"{(Resumed ? "resumed from" : "restarted after")} previous execution " +
                (RetryOfExecutionId is { } previousId ? $"`{ExecutionFormatting.Display(previousId)}` (`{previousId}`)." : "identity unavailable."));
        if (kind == IssueOutcomeKind.Blocked)
        {
            if (!string.IsNullOrWhiteSpace(ImplementationSummary)) sections.Add($"### Work performed\n\n{ImplementationSummary}");
            sections.Add($"### Required prerequisite\n\n{HumanInput ?? Failure ?? "A required prerequisite is unavailable."}");
            if (WorkspacePreserved || RetryAvailable || !string.IsNullOrWhiteSpace(RecoveryBranch))
            {
                var recovery = new List<string> { "### Recovery" };
                if (ExecutionId is not null) recovery.Add($"- **Execution:** `{ExecutionFormatting.Display(ExecutionId.Value)}` (`{ExecutionId}`)");
                if (!string.IsNullOrWhiteSpace(RecoveryBranch)) recovery.Add($"- **Branch:** `{RecoveryBranch}`");
                recovery.Add($"- **Workspace:** {(WorkspacePreserved ? "Preserved" : "Not preserved")}");
                recovery.Add($"- **Retry/resume:** {(RetryAvailable ? "Available" : "Unavailable")}");
                sections.Add(recovery[0] + "\n\n" + string.Join("\n", recovery.Skip(1)));
            }
            return FailureDiagnosticRedactor.Redact(string.Join("\n\n", sections), SecretValues);
        }

        if (!string.IsNullOrWhiteSpace(ImplementationSummary))
            sections.Add($"### {(kind == IssueOutcomeKind.Succeeded ? "Implementation" : "Implementation attempt")}\n\n{ImplementationSummary}");
        var implementationRepairs = ValidationRepairs.Where(repair => !repair.IntegrationRepair).ToArray();
        if (implementationRepairs.Length > 0 || FinalValidationFailure is not null && kind != IssueOutcomeKind.IntegrationConflict)
        {
            var validation = new List<string> { "### Validation & repairs" };
            if (implementationRepairs.Length > 0)
                validation.Add($"Initial validation failed: `{implementationRepairs[0].FailedCommand}`.");
            else if (FinalValidationFailure is not null)
                validation.Add($"Initial validation failed: `{FinalValidationFailure}`.");
            foreach (var repair in implementationRepairs)
            {
                validation.Add($"#### Repair {repair.Attempt}/{repair.MaximumAttempts}");
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
                validation.Add($"Validation failed after {(implementationRepairs.Length == 0 ? "no repair attempts" : $"{implementationRepairs.Length} repair attempt(s)")}: `{FinalValidationFailure}`.");
                validation.Add($"Final validation command: `{FinalValidationFailure}` (exit {FinalValidationExitCode?.ToString() ?? "unavailable; timed out"}).");
                if (!string.IsNullOrWhiteSpace(FinalValidationDiagnostics))
                    validation.Add("Remaining problems:\n" + string.Join("\n", FinalValidationDiagnostics.Split('\n').Select(line => $"- {line}")));
            }
            sections.Add(string.Join("\n\n", validation));
        }
        else if (kind == IssueOutcomeKind.Succeeded) sections.Add("### Validation\n\nValidation passed successfully.");

        var integrationRepairs = ValidationRepairs.Where(repair => repair.IntegrationRepair).ToArray();
        if (integrationRepairs.Length > 0)
        {
            var reconciliation = new List<string> { "### Integration repairs",
                $"Initial validation passed before integration. Post-rebase validation required {integrationRepairs.Length} integration repair attempt(s)." };
            foreach (var repair in integrationRepairs)
            {
                reconciliation.Add($"#### Integration repair {repair.Attempt}/{repair.MaximumAttempts}");
                if (!string.IsNullOrWhiteSpace(repair.RepairSummary)) reconciliation.Add(repair.RepairSummary);
                reconciliation.Add(repair.PassedAfterRepair ? "Authoritative validation passed after integration repair." :
                    $"Validation did not pass after this repair: `{repair.FailedCommand}`.");
            }
            reconciliation.Add(kind == IssueOutcomeKind.Succeeded ? "Integration repair succeeded; the Issue integrated." :
                "Integration stopped; the implementation is preserved for verified integration recovery.");
            sections.Add(string.Join("\n\n", reconciliation));
        }
        if (kind == IssueOutcomeKind.IntegrationConflict && FinalValidationDiagnostics is not null)
            sections.Add($"### Final validation failure\n\n{FinalValidationDiagnostics}");

        if (kind == IssueOutcomeKind.Succeeded)
        {
            var result = new List<string> { "### Result", $"- Duration: {WorkerConsole.FormatDuration(Duration)}" };
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
            var failureText = Failure ?? "The task could not be completed.";
            var category = FailureCategory ?? (FinalValidationFailure is not null ? "Authoritative validation failed" : "Execution failed");
            var normalizedSummary = Normalize(ImplementationSummary ?? "");
            var normalizedFailure = Normalize(failureText);
            var duplicate = normalizedSummary.Length > 0 && (normalizedSummary == normalizedFailure ||
                normalizedFailure.Length >= 40 && normalizedSummary.Contains(normalizedFailure, StringComparison.OrdinalIgnoreCase) ||
                normalizedSummary.Length >= 40 && normalizedFailure.Contains(normalizedSummary, StringComparison.OrdinalIgnoreCase));
            sections.Add($"### {(kind == IssueOutcomeKind.IntegrationConflict ? "Integration conflict recovery" : "Failure")}\n\n**Reason:** {category}.{(duplicate ? "" : $"\n\n{failureText}")}");
            if (WorkspacePreserved || RetryAvailable || !string.IsNullOrWhiteSpace(RecoveryBranch))
            {
                var recovery = new List<string> { "### Recovery" };
                if (ExecutionId is not null) recovery.Add($"- **Execution:** `{ExecutionFormatting.Display(ExecutionId.Value)}` (`{ExecutionId}`)");
                if (!string.IsNullOrWhiteSpace(RecoveryBranch)) recovery.Add($"- **Branch:** `{RecoveryBranch}`");
                recovery.Add($"- **Workspace:** {(WorkspacePreserved ? "Preserved" : "Not preserved")}");
                recovery.Add($"- **Retry/resume:** {(RetryAvailable ? "Available" : "Unavailable")}");
                sections.Add(recovery[0] + "\n\n" + string.Join("\n", recovery.Skip(1)));
            }
        }
        var markdown = string.Join("\n\n", sections);
        return kind is IssueOutcomeKind.Failed or IssueOutcomeKind.IntegrationConflict
            ? FailureDiagnosticRedactor.Redact(markdown, SecretValues) : markdown;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
        return new string(safe.Where(character => !char.IsControl(character) || character is '\t' or '\n').ToArray());
    }
}
