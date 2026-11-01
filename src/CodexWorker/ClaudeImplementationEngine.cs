using System.Text.Json;
using CodexProvisioning;

namespace CodexWorker;

/// <summary>File-only unattended adapter. Restricted mode is the trusted file-tool boundary;
/// shell execution and external tools are never granted to the implementation engine.</summary>
public sealed class ClaudeImplementationEngine : IImplementationEngine
{
    private readonly Func<IReadOnlyList<string>, string, string?, IReadOnlyDictionary<string, string?>, TimeSpan, CancellationToken, Task<ProcessResult>> _run;
    private readonly CodexSettings _settings;

    public ClaudeImplementationEngine(ProcessRunner runner, CodexSettings settings)
        : this((args, cwd, input, environment, timeout, ct) => runner.RunAsync(ClaudeServiceEnvironment.Executable,
            args, cwd, timeout, ct, environment, standardInput: input), settings) { }

    internal ClaudeImplementationEngine(Func<IReadOnlyList<string>, string, string?, IReadOnlyDictionary<string, string?>,
        TimeSpan, CancellationToken, Task<ProcessResult>> run, CodexSettings settings)
    { _run = run; _settings = settings; }

    public string Identity => "ccc";

    public Task<ImplementationResult> RunAsync(ImplementationRequest request, CancellationToken ct) =>
        ExecuteAsync(request, null, ct);

    public Task<ImplementationResult> RepairAsync(ImplementationRequest request, ValidationFailure failure,
        int attempt, int maximumAttempts, CancellationToken ct) =>
        ExecuteAsync(request, (instructions) => CodexExecutor.BuildRepairPrompt(instructions, request.InstructionsFile,
            request.Issue, failure, attempt, maximumAttempts), ct);

    public Task<ImplementationResult> RepairIntegrationAsync(ImplementationRequest request, IntegrationRepairContext context,
        string? summary, IReadOnlyList<string> commands, int attempt, int maximumAttempts, CancellationToken ct) =>
        ExecuteAsync(request, instructions => CodexExecutor.BuildIntegrationRepairPrompt(instructions, request.InstructionsFile,
            request.Issue, context, summary, commands, _settings, attempt, maximumAttempts), ct);

    public Task<ImplementationResult> ResolveIntegrationConflictAsync(ImplementationRequest request, string details, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ImplementationResult("failed", "Integration conflicts require manual reconciliation.", [], false, null));
    }

    private async Task<ImplementationResult> ExecuteAsync(ImplementationRequest request, Func<string, string>? promptFactory, CancellationToken ct)
    {
        try
        {
            using var isolated = CodexEnvironment.Create();
            var environment = isolated.Variables.ToDictionary(pair => pair.Key, pair => pair.Value);
            environment["HOME"] = ClaudeServiceEnvironment.Home;
            environment["USERPROFILE"] = ClaudeServiceEnvironment.Home;
            // Do not inherit user-controlled configuration or alternate permission/auth helpers.
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (((string)entry.Key).StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) &&
                    !((string)entry.Key).Equals("CLAUDE_CODE_OAUTH_TOKEN", StringComparison.OrdinalIgnoreCase))
                    environment[(string)entry.Key] = null;
            var version = await _run(["--version"], request.ProjectDirectory, null, environment, TimeSpan.FromSeconds(5), ct);
            if (version.ExitCode != 0 || !ClaudeCliCompatibility.SupportsVersion(WorkerCapabilityDiscovery.ParseVersion(version.StandardOutput)))
                throw new WorkerInfrastructureException("CCC requires Claude Code 2.1.259 or later with restricted unattended execution support.");
            var help = await _run(["--help"], request.ProjectDirectory, null, environment, TimeSpan.FromSeconds(5), ct);
            if (!ClaudeCliCompatibility.SupportsRestrictedExecution(help))
                throw new WorkerInfrastructureException("CCC installed CLI does not confirm required headless security/output capabilities.");
            if (request.Model is not (null or "sonnet" or "opus" or "haiku") || request.Effort is not (null or "low" or "medium" or "high"))
                throw new WorkerInfrastructureException("CCC model or effort is unsupported.");
            string instructions;
            try { instructions = await File.ReadAllTextAsync(request.InstructionsFile, ct); }
            catch (Exception exception) when (_settings.AllowMissingDefaultInstructions && exception is (FileNotFoundException or DirectoryNotFoundException))
            { instructions = "Follow the generic Worker task instructions."; }
            var agentsPath = Path.Combine(request.ProjectDirectory, "AGENTS.md");
            if (File.Exists(agentsPath)) instructions += "\n# Repository AGENTS.md\n" + await File.ReadAllTextAsync(agentsPath, ct);
            var prompt = promptFactory?.Invoke(instructions) ?? CodexExecutor.BuildPrompt(instructions, request.InstructionsFile, request.Issue);
            prompt += $"\nEngine: CCC. Read and follow repository AGENTS.md explicitly. Edit only this worktree. Shell commands are unavailable; Worker performs validation. Attempt {request.AttemptNumber}; resumed: {request.Resumed}; previous execution: {request.RetryOf?.ExecutionId}. Inspect preserved files before proceeding.\n";
            var args = new List<string> { "-p", "--restricted", "--safe-mode", "--permission-mode", "dontAsk", "--permission-prompts", "none",
                "--tools", "Read,Glob,Grep,Edit,Write", "--allowedTools", "Read,Glob,Grep,Edit,Write",
                "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence",
                "--output-format", "json", "--json-schema", CodexExecutor.OutputSchema,
                "--settings", "{\"disableAllHooks\":true,\"permissions\":{\"deny\":[\"Edit(.git)\",\"Write(.git)\",\"Edit(.git/**)\",\"Write(.git/**)\",\"Edit(/**/.git/**)\",\"Write(/**/.git/**)\",\"Edit(/**/.git)\",\"Write(/**/.git)\"]}}",
                "--model", request.Model ?? "sonnet", "--effort", request.Effort ?? "high" };
            var result = await _run(args, request.ProjectDirectory, prompt, environment, TimeSpan.FromMinutes(_settings.TimeoutMinutes), ct);
            if (result.ExitCode != 0) throw new WorkerInfrastructureException("CCC execution rejected or failed; workspace preserved. Check service authentication, model access and permission policy.");
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "result" || root.GetProperty("is_error").GetBoolean())
                throw new InvalidDataException();
            if (root.TryGetProperty("permission_denials", out var denials) &&
                (denials.ValueKind != JsonValueKind.Array || denials.GetArrayLength() != 0))
                throw new WorkerInfrastructureException("CCC permission policy rejected an operation; workspace preserved.");
            var secrets = new[] { Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") }.OfType<string>().ToArray();
            var outcome = CodexResultParser.Parse(root.GetProperty("structured_output").GetRawText());
            return ImplementationResult.FromCodex(outcome with
            {
                Summary = FailureDiagnosticRedactor.Redact(outcome.Summary, secrets),
                Question = outcome.Question is null ? null : FailureDiagnosticRedactor.Redact(outcome.Question, secrets),
                TestsOrValidationPerformed = outcome.TestsOrValidationPerformed.Select(value => FailureDiagnosticRedactor.Redact(value, secrets)).ToArray()
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (WorkerInfrastructureException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or ProcessTimeoutException or JsonException or KeyNotFoundException)
        {
            // CLI output and exception details may include authentication material. Never retain them.
            throw new WorkerInfrastructureException("CCC execution unavailable, interrupted, timed out or returned malformed output; workspace preserved.");
        }
    }
}
