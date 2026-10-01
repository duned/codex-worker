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

public sealed class CodexExecutor(ProcessRunner runner, CodexSettings settings,
    IReadOnlyDictionary<string, string>? projectEnvironment = null) : ICodexExecutor
{
    public ICodexExecutor WithProfile(CodexExecutionProfile profile) => new CodexExecutor(runner, new CodexSettings
    {
        Model = profile.Model, ReasoningEffort = profile.Effort,
        InstructionsFile = settings.InstructionsFile, TimeoutMinutes = settings.TimeoutMinutes
    }, projectEnvironment);

    private static string Executable => CodexProvisioning.CodexServiceEnvironment.Executable;
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
        var instructions = await ReadExecutionInstructionsAsync(instructionsFile, ct);
        return await RunStructuredAsync(projectDirectory, BuildPrompt(instructions, instructionsFile, issue), ct);
    }

    public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
        ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct)
    {
        var instructions = await ReadExecutionInstructionsAsync(instructionsFile, ct);
        var prompt = BuildPrompt(instructions, instructionsFile, issue);
        if (retryOf is not null)
            prompt += $"""

                # Retry history
                This is attempt {attemptNumber}, following failed execution {retryOf.ExecutionId} (attempt {retryOf.AttemptNumber}).
                Mode: {(resumed ? "resume" : "restart")}.
                Previous outcome: {retryOf.FailureReason ?? retryOf.State}.
                Previous Codex summary: {retryOf.ImplementationSummary ?? "(not recorded)"}.
                Previous recovery state: {retryOf.RecoveryStatus ?? "No recoverable implementation state was recorded."}
                {(resumed ? "Useful files from the previous attempt are present in this workspace. Inspect them critically; do not assume they are correct. Complete the full original Issue." : "Ignore implementation state from the previous attempt. Start from this attempt's current authoritative base branch and complete the full original Issue.")}
                """;
        return await RunStructuredAsync(projectDirectory, prompt, ct);
    }

    public async Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
        ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct)
    {
        var instructions = await ReadExecutionInstructionsAsync(instructionsFile, ct);
        var prompt = BuildRepairPrompt(instructions, instructionsFile, issue, failure, attempt, maximumAttempts);
        return await RunStructuredAsync(projectDirectory, prompt, ct);
    }

    public async Task<CodexOutcome> ResolveIntegrationConflictAsync(string projectDirectory, string instructionsFile,
        GitHubIssue issue, string conflictDetails, CancellationToken ct)
    {
        var instructions = await ReadExecutionInstructionsAsync(instructionsFile, ct);
        var prompt = $"""
            {instructions}

            # Integration conflict recovery
            The implementation for Issue #{issue.Number} is complete and passed the project's configured validation before integration.
            Git is currently rebasing that completed implementation onto the latest configured base branch and has stopped on conflicts.
            Resolve only the current rebase conflicts in this checkout. Preserve the intended Issue changes and compatible changes from the base branch.
            Do not redesign or reimplement the Issue. Do not run Git commands that continue, abort, reset, commit, or otherwise change the rebase lifecycle; the Worker owns those operations.
            Inspect and edit the conflicted files, then return success only when the conflict markers are removed and the intended combined code is ready.

            Issue title: {issue.Title}
            Issue body:
            {issue.Body}

            Git conflict diagnostics:
            {conflictDetails}
            """;
        return await RunStructuredAsync(projectDirectory, prompt, ct);
    }

    public Task PreflightAsync(CancellationToken ct) => PreflightAsync(ct, 60);

    public Task PreflightAsync(CancellationToken ct, int timeoutSeconds) => PreflightAsync(ct, timeoutSeconds, Path.GetTempPath());

    internal async Task PreflightAsync(CancellationToken ct, int timeoutSeconds, string temporaryRoot)
    {
        var tempDirectory = Path.Combine(temporaryRoot, $"codex-worker-preflight-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(tempDirectory, "result.txt");
        try
        {
            try
            {
                Directory.CreateDirectory(tempDirectory);
                // Probe write access as the effective Worker account before launching the child.
                await File.WriteAllTextAsync(Path.Combine(tempDirectory, ".access-probe"), "", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new WorkerInfrastructureException($"Codex preflight temporary working directory could not be created/accessed: '{tempDirectory}'. Check the service account's TMPDIR and permissions.", ex);
            }
            var args = BuildPreflightArguments(settings, outputPath);
            using var environment = CodexEnvironment.Create();
            ProcessResult result;
            try
            {
                result = await runner.RunAsync(Executable, args, tempDirectory,
                    TimeSpan.FromSeconds(timeoutSeconds), ct, environment.Variables);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var reason = ex is TimeoutException
                    ? "Codex process started but preflight timed out. Check service-account authentication and network access."
                    : $"Codex could not start in the effective Worker environment: {FailureDiagnosticRedactor.Redact(ex.Message)}";
                throw new WorkerInfrastructureException(reason, ex);
            }
            if (result.ExitCode != 0)
                throw new WorkerInfrastructureException($"Codex process started but preflight exited with code {result.ExitCode}. Check Codex authentication, its runtime, and network access as the Worker service account. Process output is omitted to protect authentication material.");
            var response = File.Exists(outputPath) ? (await File.ReadAllTextAsync(outputPath, ct)).Trim() : "";
            if (!response.Equals("OK", StringComparison.Ordinal))
                throw new WorkerInfrastructureException("Codex process started but preflight returned an unexpected response; expected exactly 'OK'. Response omitted to protect authentication material.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new WorkerInfrastructureException($"Codex execution capability unavailable. {FailureDiagnosticRedactor.Redact(ex.Message)} Worker installation and any completed registration/identity remain valid. Correct the service capability/environment and restart the Worker. No installation or authentication is performed automatically.", ex);
        }
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
            var environment = CodexEnvironment.Create(projectEnvironment);
            ProcessResult result;
            try
            {
                result = await runner.RunAsync(Executable, args, projectDirectory,
                    TimeSpan.FromMinutes(settings.TimeoutMinutes), ct, environment.Variables);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new CodexExecutionInfrastructureException(ExecutionFailureCategory(ex),
                    $"Codex execution could not complete reliably: {ex.Message}", ex);
            }
            finally { environment.Dispose(); }
            if (result.ExitCode != 0)
                throw new CodexExecutionInfrastructureException("Codex process failure",
                    $"Codex execution exited with code {result.ExitCode}; service/authentication/CLI failure is possible.{Diagnostics(result.StandardOutput, result.StandardError)}");
            if (!File.Exists(outputPath)) throw new CodexExecutionInfrastructureException("Unknown Codex failure",
                "Codex execution produced no structured final response.");
            try { return CodexResultParser.Parse(await File.ReadAllTextAsync(outputPath, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new CodexExecutionInfrastructureException("Unknown Codex failure",
                $"Codex execution did not return a valid structured task result: {ex.Message}", ex); }
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

    internal static string ExecutionFailureCategory(Exception exception) => exception is TimeoutException
        ? "Codex timeout"
        : "Codex service/authentication/infrastructure failure";

    private static async Task<string> ReadExecutionInstructionsAsync(string instructionsFile, CancellationToken ct)
    {
        try { return await ReadInstructionsAsync(instructionsFile, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new CodexExecutionInfrastructureException("Unknown Codex failure",
                $"Codex execution could not read its configured instructions: {ex.Message}", ex);
        }
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

        In `summary`, provide a concise, self-contained explanation of what was implemented, the important behavior or design decisions, the main application layers affected, and important tests added or changed. Explain the task outcome rather than listing modified files.

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

        In `summary`, describe only what this repair attempt corrected. Keep it concise and never restate or replace the original implementation summary.

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

    public static CodexEnvironment Create(IReadOnlyDictionary<string, string>? projectEnvironment = null)
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
        var codexHome = CodexProvisioning.CodexServiceEnvironment.Home;
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
        if (projectEnvironment is not null)
        {
            foreach (var (key, value) in projectEnvironment)
                if (!IsProtected(key)) variables[key] = value;
        }
        return new CodexEnvironment(directory, variables);
    }

    private static bool IsProtected(string key) =>
        key.StartsWith("GH_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("ACTIONS_ID_TOKEN_", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("HOME", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("GH_CONFIG_DIR", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("GIT_TERMINAL_PROMPT", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("GIT_ASKPASS", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("SSH_ASKPASS", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("CODEX_HOME", StringComparison.OrdinalIgnoreCase);

    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch { /* best effort temp cleanup */ } }
}

public sealed record ValidationFailure(int CommandNumber, string Command, int? ExitCode, string StandardOutput,
    string StandardError, bool TimedOut, IReadOnlyList<string>? SecretValues = null)
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

    public string ToSummary(int maximumCharacters = 800)
    {
        var lines = (StandardError + "\n" + StandardOutput).Split('\n')
            .Select(line => FailureDiagnosticRedactor.Redact(line.Trim(), SecretValues))
            .Where(IsActionable)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        if (lines.Length == 0)
        {
            var fallback = FailureDiagnosticRedactor.Redact((StandardError + "\n" + StandardOutput).Trim(), SecretValues);
            lines = string.IsNullOrWhiteSpace(fallback) ? ["No diagnostic output was produced."] : [fallback];
        }
        var summary = $"Command {CommandNumber}: {FailureDiagnosticRedactor.Redact(Command, SecretValues)} (exit {ExitCode?.ToString() ?? "unavailable; timed out"})\n" +
                      string.Join("\n", lines);
        return summary.Length <= maximumCharacters ? summary : summary[..Math.Max(0, maximumCharacters - 15)] + " [truncated]";
    }

    private static bool IsActionable(string line) =>
        line.Length > 0 && (System.Text.RegularExpressions.Regex.IsMatch(line,
            @"(?i)(error\s+[A-Z]+\d+|warning\s+[A-Z]+\d+|failed\s+(?:test|tests|to\b)|^failed\s+\S+|\bassert(?:ion)?\b|expected\b|actual\b|exception|timed?\s*out|\bCS\d{4}\b)") || line.Contains(':'));

    private static string Tail(string value, int length) => length <= 0 ? "" : value.Length <= length ? value : value[^length..];
}

public sealed record ValidationResult(ValidationFailure? Failure)
{
    public bool Succeeded => Failure is null;
    public static ValidationResult Success { get; } = new((ValidationFailure?)null);
}

public sealed class ValidationRunner(ProcessRunner runner, int timeoutSeconds,
    IReadOnlyDictionary<string, string>? projectEnvironment = null) : IValidationRunner
{
    public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string?>? environment = projectEnvironment?.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        var index = 0;
        foreach (var command in commands)
        {
            index++;
            var shell = OperatingSystem.IsWindows() ? "powershell" : "/bin/sh";
            var args = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-Command", command } : new[] { "-c", command };
            ProcessResult result;
            try { result = await runner.RunAsync(shell, args, directory, TimeSpan.FromSeconds(timeoutSeconds), ct, environment); }
            catch (ProcessTimeoutException ex)
            {
                return new ValidationResult(new ValidationFailure(index, command, null, ex.StandardOutput, ex.StandardError, true,
                    projectEnvironment?.Values.ToArray()));
            }
            if (result.ExitCode != 0)
                return new ValidationResult(new ValidationFailure(index, command, result.ExitCode, result.StandardOutput, result.StandardError, false,
                    projectEnvironment?.Values.ToArray()));
        }
        return ValidationResult.Success;
    }
}
