namespace CodexProvisioning;

/// <summary>A node-local preserved implementation offered for a Server-owned recovery assignment.</summary>
public sealed record IntegrationRecoveryCandidate(string ProjectId, string ServerExecutionId,
    string WorkerExecutionId, string IntegrationBase, string Kind = "Integration", bool AutomaticRetryExhausted = false,
    string? PreservedCommit = null, string? RefusalReason = null, string? RefusalCode = null, string? ParentProvenance = null);
