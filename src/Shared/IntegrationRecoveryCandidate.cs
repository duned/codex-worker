namespace CodexProvisioning;

/// <summary>A node-local preserved implementation offered for a Server-owned recovery assignment.</summary>
public sealed record IntegrationRecoveryCandidate(string ProjectId, string ServerExecutionId,
    string WorkerExecutionId, string IntegrationBase);
