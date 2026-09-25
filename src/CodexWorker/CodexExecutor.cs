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
        if (status == "success" && (needsHumanInput || !string.IsNullOrWhiteSpace(question)))
            throw new InvalidDataException("A success Codex result cannot request unresolved human input.");
        if (status == "blocked" && (!needsHumanInput || string.IsNullOrWhiteSpace(question)))
            throw new InvalidDataException("A blocked Codex result must require human input and provide a non-empty question.");
        if (status == "failed" && (needsHumanInput || !string.IsNullOrWhiteSpace(question)))
            throw new InvalidDataException("A failed Codex result cannot request unresolved human input; use blocked for that outcome.");
        return new CodexOutcome(status, summary, checks, needsHumanInput, question);
    }
}

public sealed class CodexExecutor(ProcessRunner runner, CodexSettings settings)
{
    internal const string OutputSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["status", "summary", "testsOrValidationPerformed", "needsHumanInput", "question"],
          "properties": {
            "status": {
              "type": "string",
              "enum": ["success", "blocked", "failed"],
              "description": "Use success when implementation is complete, even if optional local checks could not run because of sandbox, network, or environment restrictions. Use failed only when the implementation itself cannot be completed. Use blocked only when human input or a decision is required."
            },
            "summary": { "type": "string", "minLength": 1 },
            "testsOrValidationPerformed": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Report checks performed. If an optional check could not run, include the command/check and the reason. The worker's configured validation commands are authoritative and run separately after Codex."
            },
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
            var args = BuildArguments(settings, schemaPath, outputPath, prompt);
            var environment = CodexEnvironment.Create();
            ProcessResult result;
            try
            {
                result = await runner.RunAsync("codex", args, projectDirectory,
                    TimeSpan.FromMinutes(settings.TimeoutMinutes), ct, environment.Variables);
            }
            catch (ProcessTimeoutException ex) { throw new TaskFailureException($"Codex task timed out: {ex.Message}", ex); }
            finally { environment.Dispose(); }
            if (result.ExitCode != 0)
                throw new TaskFailureException($"Codex exited with code {result.ExitCode}. {Tail(result.StandardError)}");
            if (!File.Exists(outputPath)) throw new InvalidDataException("Codex did not produce its structured final response.");
            try { return CodexResultParser.Parse(await File.ReadAllTextAsync(outputPath, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new TaskFailureException($"Codex did not return a valid structured task result: {ex.Message}", ex); }
        }
        finally
        {
            TryDelete(schemaPath);
            TryDelete(outputPath);
        }
    }

    public static IReadOnlyList<string> BuildArguments(CodexSettings settings, string schemaPath, string outputPath, string prompt)
    {
        var args = new List<string> { "exec", "--approve-for-me", "--output-schema", schemaPath,
            "--output-last-message", outputPath };
        if (!string.IsNullOrWhiteSpace(settings.Model)) { args.Add("--model"); args.Add(settings.Model); }
        args.Add("-c"); args.Add($"model_reasoning_effort=\"{settings.ReasoningEffort.ToLowerInvariant()}\"");
        args.Add(prompt);
        return args;
    }

    internal static string BuildPrompt(string projectInstructions, string instructionsFile, GitHubIssue issue) => $"""
        # Generic worker task instructions
        Implement the requested change in the current project checkout. Inspect relevant code, make a focused change, and perform useful local validation while developing whenever possible. Local self-validation is best effort: if a check cannot run because of sandbox, network, missing-tool, package-restore, or other environment restrictions, do not treat that alone as implementation failure. If the implementation is complete, return `success` and identify the check and reason it could not run in `testsOrValidationPerformed`. The worker will run the configured validation commands separately after Codex; those commands are the authoritative validation gate before commit and integration. Do not claim those worker checks have passed.

        Use `success` when the requested implementation is complete, even if an optional Codex-run check was unavailable. Use `failed` only when you could not complete the implementation for a technical reason. Use `blocked` only when a requirement is missing or a human decision/input is needed, and state it in `question`; environment restrictions on optional self-validation alone are not a reason to use `blocked`. Summarize implementation and checks attempted, completed, or unavailable (including reasons) in `testsOrValidationPerformed`.

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

internal sealed class CodexEnvironment : IDisposable
{
    private readonly string _directory;
    public IReadOnlyDictionary<string, string?> Variables { get; }

    private CodexEnvironment(string directory, Dictionary<string, string?> variables)
    { _directory = directory; Variables = variables; }

    public static CodexEnvironment Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-worker-child-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "gh"));
        var gitConfig = Path.Combine(directory, "gitconfig");
        File.WriteAllText(gitConfig, "");
        var variables = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.StartsWith("GH_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("ACTIONS_ID_TOKEN_", StringComparison.OrdinalIgnoreCase)) variables[key] = null;
        }
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        variables["HOME"] = directory;
        variables["USERPROFILE"] = directory;
        variables["GH_CONFIG_DIR"] = Path.Combine(directory, "gh");
        variables["GIT_CONFIG_GLOBAL"] = gitConfig;
        variables["GIT_CONFIG_NOSYSTEM"] = "1";
        variables["GIT_CONFIG_COUNT"] = "1";
        variables["GIT_CONFIG_KEY_0"] = "credential.helper";
        variables["GIT_CONFIG_VALUE_0"] = "";
        variables["GIT_TERMINAL_PROMPT"] = "0";
        variables["GIT_ASKPASS"] = null;
        variables["SSH_ASKPASS"] = null;
        variables["CODEX_HOME"] = codexHome;
        return new CodexEnvironment(directory, variables);
    }

    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch { /* best effort temp cleanup */ } }
}

public sealed class ValidationRunner(ProcessRunner runner, int timeoutSeconds)
{
    public async Task RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
    {
        var index = 0;
        foreach (var command in commands)
        {
            index++;
            var shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh";
            var args = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-Command", command } : new[] { "-c", command };
            ProcessResult result;
            try { result = await runner.RunAsync(shell, args, directory, TimeSpan.FromSeconds(timeoutSeconds), ct); }
            catch (ProcessTimeoutException ex) { throw new TaskFailureException($"Validation command {index} timed out: {command}. {ex.Message}", ex); }
            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                throw new TaskFailureException($"Validation command {index} failed (exit {result.ExitCode}): {command}\n{Tail(detail)}");
            }
        }
    }

    private static string Tail(string value) => value.Length <= 1800 ? value : value[^1800..];
}
