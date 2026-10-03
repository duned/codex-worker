namespace CodexServer;

/// <summary>Server control-plane request to assign a centrally managed credential.</summary>
public sealed record CredentialAssignmentRequest(string WorkerId);
