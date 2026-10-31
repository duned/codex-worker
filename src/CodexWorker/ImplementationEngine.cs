namespace CodexWorker;

/// <summary>Inputs for file implementation inside a Worker-owned isolated worktree.
/// Engines never own Git mutations, reporting, or authoritative validation.</summary>
public sealed record ImplementationRequest(string ProjectDirectory, string InstructionsFile, GitHubIssue Issue,
    ExecutionHistoryEntry? RetryOf = null, bool Resumed = false, int AttemptNumber = 1, string? Model = null, string? Effort = null);

public sealed record ImplementationResult(string Status, string Summary, string[] TestsOrValidationPerformed,
    bool NeedsHumanInput, string? Question, string? BlockerType = null)
{
    internal static ImplementationResult FromCodex(CodexOutcome outcome) => new(outcome.Status, outcome.Summary,
        outcome.TestsOrValidationPerformed, outcome.NeedsHumanInput, outcome.Question, outcome.BlockerType);
}

/// <summary>Uses the existing structured outcome and infrastructure exception classifications.</summary>
public interface IImplementationEngine
{
    string Identity { get; }
    Task<ImplementationResult> RunAsync(ImplementationRequest request, CancellationToken ct);
    Task<ImplementationResult> RepairAsync(ImplementationRequest request, ValidationFailure failure,
        int attempt, int maximumAttempts, CancellationToken ct);
    Task<ImplementationResult> RepairIntegrationAsync(ImplementationRequest request, IntegrationRepairContext context,
        string? summary, IReadOnlyList<string> commands, int attempt, int maximumAttempts, CancellationToken ct);
    Task<ImplementationResult> ResolveIntegrationConflictAsync(ImplementationRequest request, string details, CancellationToken ct);
}

/// <summary>Selects an explicitly registered engine; unavailable identities never fall back to Codex.</summary>
public sealed class ImplementationEngineSelector(IEnumerable<IImplementationEngine>? engines = null)
{
    private readonly IReadOnlyDictionary<string, IImplementationEngine> _engines =
        (engines ?? []).ToDictionary(engine => engine.Identity, StringComparer.Ordinal);

    public bool IsAvailable(string identity) => identity == "codex" || _engines.ContainsKey(identity);

    public IImplementationEngine? Select(string identity, ICodexExecutor codex) => identity == "codex"
        ? new CodexImplementationEngine(codex) : _engines.GetValueOrDefault(identity);
}

internal sealed class CodexImplementationEngine(ICodexExecutor codex) : IImplementationEngine
{
    public string Identity => "codex";
    public async Task<ImplementationResult> RunAsync(ImplementationRequest request, CancellationToken ct) =>
        ImplementationResult.FromCodex(await codex.RunAsync(request.ProjectDirectory, request.InstructionsFile, request.Issue, request.RetryOf,
            request.Resumed, request.AttemptNumber, ct));
    public async Task<ImplementationResult> RepairAsync(ImplementationRequest request, ValidationFailure failure,
        int attempt, int maximumAttempts, CancellationToken ct) =>
        ImplementationResult.FromCodex(await codex.RepairAsync(request.ProjectDirectory, request.InstructionsFile, request.Issue, failure, attempt, maximumAttempts, ct));
    public async Task<ImplementationResult> RepairIntegrationAsync(ImplementationRequest request, IntegrationRepairContext context,
        string? summary, IReadOnlyList<string> commands, int attempt, int maximumAttempts, CancellationToken ct) =>
        ImplementationResult.FromCodex(await codex.RepairIntegrationAsync(request.ProjectDirectory, request.InstructionsFile, request.Issue,
            context, summary, commands, attempt, maximumAttempts, ct));
    public async Task<ImplementationResult> ResolveIntegrationConflictAsync(ImplementationRequest request, string details, CancellationToken ct) =>
        ImplementationResult.FromCodex(await codex.ResolveIntegrationConflictAsync(request.ProjectDirectory, request.InstructionsFile, request.Issue, details, ct));
}
