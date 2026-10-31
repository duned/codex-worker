namespace CodexWorker;

/// <summary>Selection settings frozen for one execution; null Model delegates to the CLI, whose observed identity is stored separately.</summary>
public sealed record CodexExecutionProfile(string? Model, string Effort, string? CliModel = null)
{
    public string Engine { get; init; } = "codex";

    public string? EffectiveModel => Model ?? CliModel;

    public static CodexExecutionProfile Resolve(string body, CodexSettings defaults, ExecutionHistoryEntry? source = null)
    {
        // Older history has no snapshot. Resolve it once using today's configured defaults,
        // without applying edited Issue metadata to recovery of an existing execution.
        if (source?.RecoveryState == "codex-interrupted" && source.EffectiveModel is not null && source.EffectiveEffort is not null)
            return new(source.EffectiveModel, source.EffectiveEffort) { Engine = source.Engine };
        if (source is not null && source.EffectiveEffort is not null)
            return source.ModelSelectedByCli
                ? new(null, source.EffectiveEffort, source.EffectiveModel) { Engine = source.Engine }
                : new(source.EffectiveModel, source.EffectiveEffort) { Engine = source.Engine };
        if (source is not null && source.FailureReason?.StartsWith("Invalid ## Codex metadata:", StringComparison.Ordinal) != true &&
            source.FailureReason?.StartsWith("Invalid ## Worker metadata:", StringComparison.Ordinal) != true)
            return new(DefaultModel(defaults), defaults.ReasoningEffort.ToLowerInvariant()) { Engine = source.Engine };

        var metadata = CodexProvisioning.IssueExecutionMetadata.Parse(body);
        return metadata.Engine == "codex"
            ? new(metadata.Model ?? DefaultModel(defaults), metadata.Effort ?? defaults.ReasoningEffort.ToLowerInvariant())
            : new(metadata.Model, metadata.Effort ?? "medium") { Engine = metadata.Engine };
    }

    private static string? DefaultModel(CodexSettings defaults) => string.IsNullOrWhiteSpace(defaults.Model) ? null : defaults.Model;
}
