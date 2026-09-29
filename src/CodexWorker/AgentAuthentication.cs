namespace CodexWorker;

/// <summary>Provider-neutral readiness boundary for AI agents used by the Worker.</summary>
public interface IAgentAuthenticationProvider
{
    string Provider { get; }
    Task ValidateAsync(CancellationToken cancellationToken);
}

/// <summary>Checks the locally configured Codex CLI and authentication through its normal preflight.</summary>
public sealed class CodexAgentAuthenticationProvider(CodexExecutor executor, int timeoutSeconds) : IAgentAuthenticationProvider
{
    public string Provider => "codex";

    public Task ValidateAsync(CancellationToken cancellationToken) => executor.PreflightAsync(cancellationToken, timeoutSeconds);
}
