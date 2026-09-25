using System.Text.Json;

namespace CodexWorker;

public sealed record CodexOutcome(string Status, string Summary, string[] TestsOrValidationPerformed, bool NeedsHumanInput, string? Question);

public static class CodexResultParser
{
    public static CodexOutcome Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString();
        if (status is not ("success" or "blocked" or "failed"))
            throw new InvalidDataException("Codex result status must be success, blocked, or failed.");
        var summary = root.GetProperty("summary").GetString();
        if (string.IsNullOrWhiteSpace(summary)) throw new InvalidDataException("Codex result summary is required.");
        var checks = root.GetProperty("testsOrValidationPerformed").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        var needsHumanInput = root.GetProperty("needsHumanInput").GetBoolean();
        var questionElement = root.GetProperty("question");
        var question = questionElement.ValueKind == JsonValueKind.Null ? null : questionElement.GetString();
        if (status == "blocked" && !needsHumanInput && string.IsNullOrWhiteSpace(question))
            throw new InvalidDataException("A blocked Codex result must explain the human input it needs.");
        return new CodexOutcome(status, summary, checks, needsHumanInput, question);
    }
}

public sealed class CodexExecutor(ProcessRunner runner, CodexSettings settings)
{
    private const string OutputSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["status", "summary", "testsOrValidationPerformed", "needsHumanInput", "question"],
          "properties": {
            "status": { "type": "string", "enum": ["success", "blocked", "failed"] },
            "summary": { "type": "string", "minLength": 1 },
            "testsOrValidationPerformed": { "type": "array", "items": { "type": "string" } },
            "needsHumanInput": { "type": "boolean" },
            "question": { "type": ["string", "null"] }
          }
        }
        """;

    public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
    {
        if (!File.Exists(instructionsFile)) throw new FileNotFoundException("Configured project instructions file was not found.", instructionsFile);
        var instructions = await File.ReadAllTextAsync(instructionsFile, ct);
        var prompt = BuildPrompt(instructions, instructionsFile, issue);
        var schemaPath = Path.Combine(Path.GetTempPath(), $"codex-worker-schema-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"codex-worker-output-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(schemaPath, OutputSchema, ct);
        try
        {
            var args = new List<string> { "exec", "--sandbox", "workspace-write", "--approve-for-me", "--output-schema", schemaPath,
                "--output-last-message", outputPath };
            if (!string.IsNullOrWhiteSpace(settings.Model)) { args.Add("--model"); args.Add(settings.Model); }
            args.Add("-c"); args.Add($"model_reasoning_effort=\"{settings.ReasoningEffort}\"");
            args.Add(prompt);
            var result = await runner.RunAsync("codex", args, projectDirectory,
                TimeSpan.FromMinutes(settings.TimeoutMinutes), ct);
            if (result.ExitCode != 0)
                throw new CommandFailedException($"Codex exited with code {result.ExitCode}. {Tail(result.StandardError)}", result);
            if (!File.Exists(outputPath)) throw new InvalidDataException("Codex did not produce its structured final response.");
            return CodexResultParser.Parse(await File.ReadAllTextAsync(outputPath, ct));
        }
        finally
        {
            TryDelete(schemaPath);
            TryDelete(outputPath);
        }
    }

    private static string BuildPrompt(string projectInstructions, string instructionsFile, GitHubIssue issue) => $"""
        # Generic worker task instructions
        Implement the requested change in the current project checkout. Inspect relevant code, make a focused change, and run appropriate local checks. Return a final response matching the supplied JSON schema. Use status `success` only when the requested work is implemented and your own reasonable local checks pass. Use `blocked` when specific human input is required and state that requirement in `question`. Use `failed` when the task cannot be completed for a technical reason. Summarize your work and the checks you personally performed in `testsOrValidationPerformed`.

        The worker owns all Git and GitHub lifecycle. Do not create, switch, merge, commit, push, or delete Git branches; do not commit; do not run GitHub CLI commands; do not manipulate Issue labels, comments, or state. Focus only on implementing and locally validating the requested change. The worker will review, validate, commit, and integrate your changes.

        # Project instructions from {instructionsFile}
        {projectInstructions}

        # GitHub Issue
        Number: {issue.Number}
        Title: {issue.Title}
        Body:
        {issue.Body}
        """;

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* temp cleanup is best effort */ } }
    private static string Tail(string value) => value.Length <= 1400 ? value : value[^1400..];
}

public sealed class ValidationRunner(ProcessRunner runner)
{
    public async Task RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
    {
        var index = 0;
        foreach (var command in commands)
        {
            index++;
            var shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh";
            var args = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-Command", command } : new[] { "-c", command };
            var result = await runner.RunAsync(shell, args, directory, cancellationToken: ct);
            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                throw new CommandFailedException($"Validation command {index} failed (exit {result.ExitCode}): {command}\n{Tail(detail)}", result);
            }
        }
    }

    private static string Tail(string value) => value.Length <= 1800 ? value : value[^1800..];
}
