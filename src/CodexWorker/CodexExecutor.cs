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

public sealed class CodexExecutor(ProcessRunner runner, CodexSettings settings) : ICodexExecutor
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
        var instructions = await ReadInstructionsAsync(instructionsFile, ct);
        return await RunStructuredAsync(projectDirectory, BuildPrompt(instructions, instructionsFile, issue), ct);
    }

    public async Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
        ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct)
    {
        var instructions = await ReadInstructionsAsync(instructionsFile, ct);
        var prompt = BuildRepairPrompt(instructions, instructionsFile, issue, failure, attempt, maximumAttempts);
        return await RunStructuredAsync(projectDirectory, prompt, ct);
    }

    public Task PreflightAsync(CancellationToken ct) => PreflightAsync(ct, 60);

    public async Task PreflightAsync(CancellationToken ct, int timeoutSeconds)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"codex-worker-preflight-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(Path.GetTempPath(), $"codex-worker-preflight-output-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var args = BuildPreflightArguments(settings, outputPath);
            using var environment = CodexEnvironment.Create();
            ProcessResult result;
            try
            {
                result = await runner.RunAsync("codex", args, tempDirectory,
                    TimeSpan.FromSeconds(timeoutSeconds), ct, environment.Variables);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new WorkerInfrastructureException($"Codex startup preflight could not execute: {ex.Message}", ex);
            }
            if (result.ExitCode != 0)
                throw new WorkerInfrastructureException($"Codex startup preflight exited with code {result.ExitCode}.{Diagnostics(result.StandardOutput, result.StandardError)}");
            var response = File.Exists(outputPath) ? (await File.ReadAllTextAsync(outputPath, ct)).Trim() : "";
            if (!response.Equals("OK", StringComparison.Ordinal))
                throw new WorkerInfrastructureException($"Codex startup preflight returned an unexpected response; expected exactly 'OK'. Received: {Tail(response, 1000)}");
        }
        catch (WorkerInfrastructureException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Codex startup preflight failed: {ex.Message}", ex); }
        finally
        {
            TryDelete(outputPath);
            try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort temp cleanup */ }
        }
    }

    private async Task<CodexOutcome> RunStructuredAsync(string projectDirectory, string prompt, CancellationToken ct)
    {
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new WorkerInfrastructureException($"Codex execution could not complete reliably: {ex.Message}", ex); }
            finally { environment.Dispose(); }
            if (result.ExitCode != 0)
                throw new WorkerInfrastructureException($"Codex execution exited with code {result.ExitCode}; service/authentication/CLI failure is possible.{Diagnostics(result.StandardOutput, result.StandardError)}");
            if (!File.Exists(outputPath)) throw new WorkerInfrastructureException("Codex execution produced no structured final response.");
            try { return CodexResultParser.Parse(await File.ReadAllTextAsync(outputPath, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new WorkerInfrastructureException($"Codex execution did not return a valid structured task result: {ex.Message}", ex); }
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

    internal static IReadOnlyList<string> BuildPreflightArguments(CodexSettings settings, string outputPath)
    {
        var args = new List<string> { "exec", "--approve-for-me", "--skip-git-repo-check", "--ephemeral", "--output-last-message", outputPath };
        AddModelAndReasoning(args, settings);
        args.Add("Reply only with OK. Do not inspect or modify project files.");
        return args;
    }

    private static void AddModelAndReasoning(List<string> args, CodexSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Model)) { args.Add("--model"); args.Add(settings.Model); }
        args.Add("-c"); args.Add($"model_reasoning_effort=\"{settings.ReasoningEffort.ToLowerInvariant()}\"");
    }

    private static async Task<string> ReadInstructionsAsync(string instructionsFile, CancellationToken ct)
    {
        if (!File.Exists(instructionsFile))
            throw new WorkerInfrastructureException($"Configured Codex instructions file was not found: {instructionsFile}");
        try { return await File.ReadAllTextAsync(instructionsFile, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new WorkerInfrastructureException($"Could not read configured Codex instructions: {ex.Message}", ex); }
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

    internal static string BuildRepairPrompt(string projectInstructions, string instructionsFile, GitHubIssue issue,
        ValidationFailure failure, int attempt, int maximumAttempts) => $"""
        # Generic worker repair instructions
        This is repair attempt {attempt} of {maximumAttempts}, not a new implementation task. Inspect the existing implementation and fix the cause of the authoritative validation failure below while preserving the functionality requested in the original Issue. Make focused changes in-place. You may perform useful local checks, but the worker's configured validation commands remain authoritative and will be run again after this repair. A `success` response means you completed the repair; it does not mean authoritative validation has passed. Return a final response matching the supplied JSON schema. Use `blocked` only when human input or a decision is required, `failed` when you cannot complete the repair for a technical reason, and `success` when the repair changes are ready for the worker validation retry.

        The worker owns the entire Git and GitHub lifecycle. Do not create, switch, merge, commit, push, or delete branches; do not commit; do not run GitHub CLI commands; do not change Issue state, labels, comments, or the queue.

        # Project instructions from {instructionsFile}
        {projectInstructions}

        # Original GitHub Issue
        Number: {issue.Number}
        Title: {issue.Title}
        Body:
        {issue.Body}

        # Authoritative validation failure
        {failure.ToRepairDiagnostics()}
        """;

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* temp cleanup is best effort */ } }
    private static string Tail(string value) => value.Length <= 1400 ? value : value[^1400..];
    private static string Tail(string value, int length) => value.Length <= length ? value : value[^length..];
    private static string Diagnostics(string stdout, string stderr) =>
        $"\nstdout: {Tail(stdout, 2500)}\nstderr: {Tail(stderr, 2500)}";
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

public sealed record ValidationFailure(int CommandNumber, string Command, int? ExitCode, string StandardOutput,
    string StandardError, bool TimedOut)
{
    public string ToRepairDiagnostics(int maximumCharacters = 6000)
    {
        var header = $"Command {CommandNumber}: {Command}\nExit code: {ExitCode?.ToString() ?? "unavailable (timeout)"}\nTimed out: {TimedOut}\n";
        maximumCharacters = Math.Max(1, maximumCharacters);
        if (header.Length >= maximumCharacters) return header[..maximumCharacters];
        var output = $"stdout:\n{StandardOutput}\n\nstderr:\n{StandardError}";
        if (header.Length + output.Length <= maximumCharacters) return header + output;

        const string prefix = "[diagnostics truncated; showing tails]\nstdout tail:\n";
        const string separator = "\nstderr tail:\n";
        var available = Math.Max(0, maximumCharacters - header.Length - prefix.Length - separator.Length);
        var stdoutLength = Math.Min(StandardOutput.Length, available / 2);
        var stderrLength = Math.Min(StandardError.Length, available - stdoutLength);
        stdoutLength = Math.Min(StandardOutput.Length, available - stderrLength);
        return header + prefix + Tail(StandardOutput, stdoutLength) + separator + Tail(StandardError, stderrLength);
    }

    public string ToSummary() => $"Validation command {CommandNumber} failed (exit {ExitCode?.ToString() ?? "timeout"}): {Command}\n{ToRepairDiagnostics(1200)}";

    private static string Tail(string value, int length) => length <= 0 ? "" : value.Length <= length ? value : value[^length..];
}

public sealed record ValidationResult(ValidationFailure? Failure)
{
    public bool Succeeded => Failure is null;
    public static ValidationResult Success { get; } = new((ValidationFailure?)null);
}

public sealed class ValidationRunner(ProcessRunner runner, int timeoutSeconds) : IValidationRunner
{
    public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
    {
        var index = 0;
        foreach (var command in commands)
        {
            index++;
            var shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh";
            var args = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-Command", command } : new[] { "-c", command };
            ProcessResult result;
            try { result = await runner.RunAsync(shell, args, directory, TimeSpan.FromSeconds(timeoutSeconds), ct); }
            catch (ProcessTimeoutException ex)
            {
                return new ValidationResult(new ValidationFailure(index, command, null, ex.StandardOutput, ex.StandardError, true));
            }
            if (result.ExitCode != 0)
                return new ValidationResult(new ValidationFailure(index, command, result.ExitCode, result.StandardOutput, result.StandardError, false));
        }
        return ValidationResult.Success;
    }
}
