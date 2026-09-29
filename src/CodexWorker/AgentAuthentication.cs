namespace CodexWorker;

/// <summary>Provider-independent authentication states safe to report to Codex Server.</summary>
public static class AgentAuthenticationStates
{
    public const string Ready = "ready";
    public const string AuthenticationRequired = "authentication-required";
    public const string Unsupported = "unsupported";
    public const string InvalidOrExpired = "invalid-or-expired";
    public const string Unknown = "unknown";
}

public sealed record AgentAuthenticationContract(string Provider, string State);

/// <summary>A functional AI coding-agent provider available to this worker.</summary>
public interface IAgentProvider
{
    string Id { get; }
    Task<AgentAuthenticationContract> CheckAuthenticationAsync(CancellationToken cancellationToken);
    IReadOnlyDictionary<string, string> MaterializeCredential(WorkerCredentialContract credential);
}

/// <summary>Codex CLI provider. It checks the supported login-status command and never reads auth files.</summary>
public sealed class CodexAgentProvider(
    Func<string, IEnumerable<string>, string, TimeSpan?, CancellationToken, Task<ProcessResult>>? run = null) : IAgentProvider
{
    private readonly Func<string, IEnumerable<string>, string, TimeSpan?, CancellationToken, Task<ProcessResult>> _run = run ??
        ((executable, arguments, directory, timeout, cancellationToken) =>
            new ProcessRunner().RunAsync(executable, arguments, directory, timeout, cancellationToken));

    public string Id => "codex";

    /// <summary>Maps a securely delivered Codex API-key credential to the CLI's process environment.</summary>
    public IReadOnlyDictionary<string, string> MaterializeCredential(WorkerCredentialContract credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!string.Equals(credential.Provider, Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(credential.Type, "api-key", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(credential.Secret))
            throw new InvalidDataException("The credential is not a supported Codex API-key credential.");
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENAI_API_KEY"] = credential.Secret };
    }

    public async Task<AgentAuthenticationContract> CheckAuthenticationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _run("codex", ["login", "status"], Environment.CurrentDirectory,
                TimeSpan.FromSeconds(5), cancellationToken);
            if (result.ExitCode == ProcessExitCodes.Success)
                return new(Id, AgentAuthenticationStates.Ready);

            var output = result.StandardOutput + "\n" + result.StandardError;
            if (output.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("not authenticated", StringComparison.OrdinalIgnoreCase))
                return new(Id, AgentAuthenticationStates.AuthenticationRequired);
            if (output.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("invalid token", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("invalid credential", StringComparison.OrdinalIgnoreCase))
                return new(Id, AgentAuthenticationStates.InvalidOrExpired);
            return new(Id, AgentAuthenticationStates.Unknown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException or ProcessTimeoutException ||
                                   ex is InvalidOperationException && ex.Message.StartsWith("Could not start", StringComparison.Ordinal))
        {
            return new(Id, AgentAuthenticationStates.Unsupported);
        }
        catch
        {
            return new(Id, AgentAuthenticationStates.Unknown);
        }
    }
}
