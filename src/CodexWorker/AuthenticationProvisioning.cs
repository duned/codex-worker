namespace CodexWorker;

/// <summary>Materializes an assigned GitHub token through gh and optional repository-local Git configuration.</summary>
public sealed class GitHubAuthenticationProvisioner : IAuthenticationActionExecutor
{
    private readonly Func<string, IEnumerable<string>, string, TimeSpan, CancellationToken, string?, Task<ProcessResult>> _run;
    private readonly Func<string, GitHubClient?> _githubForRepository;
    private readonly Func<string, GitRepository?> _gitForRepository;
    private readonly Func<string, CancellationToken, Task<WorkerCredentialContract?>> _retrieveCredential;

    public GitHubAuthenticationProvisioner(ProcessRunner runner, Func<string, GitHubClient?> githubForRepository,
        Func<string, GitRepository?> gitForRepository,
        Func<string, CancellationToken, Task<WorkerCredentialContract?>> retrieveCredential)
        : this((executable, arguments, directory, timeout, cancellationToken, input) =>
                runner.RunAsync(executable, arguments, directory, timeout, cancellationToken, standardInput: input),
            githubForRepository, gitForRepository, retrieveCredential) { }

    internal GitHubAuthenticationProvisioner(
        Func<string, IEnumerable<string>, string, TimeSpan, CancellationToken, string?, Task<ProcessResult>> run,
        Func<string, GitHubClient?> githubForRepository,
        Func<string, GitRepository?> gitForRepository,
        Func<string, CancellationToken, Task<WorkerCredentialContract?>> retrieveCredential)
    {
        _run = run;
        _githubForRepository = githubForRepository;
        _gitForRepository = gitForRepository;
        _retrieveCredential = retrieveCredential;
    }

    public async Task<DependencyInstallResult> ExecuteAsync(ProvisioningActionContract action, CancellationToken cancellationToken)
    {
        if (action.Name is not ("github-api" or "git-https") || action.Scope is null || action.CredentialId is null)
            return Failure("supported actions are github-api and git-https with a repository scope and assigned credential");

        var github = _githubForRepository(action.Scope);
        if (github is null) return Failure("repository scope is not configured on this Worker");
        if (action.Name == "git-https" && _gitForRepository(action.Scope) is null)
            return Failure("repository scope is not configured on this Worker");

        WorkerCredentialContract? credential;
        try { credential = await _retrieveCredential(action.CredentialId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Failure("assigned credential could not be retrieved"); }
        if (credential is null) return Failure("assigned credential is unavailable");
        if (credential.Id != action.CredentialId || credential.Version < 1 || string.IsNullOrEmpty(credential.Secret) ||
            string.IsNullOrWhiteSpace(credential.Provider) || string.IsNullOrWhiteSpace(credential.Type) ||
            !credential.Provider.Equals("github", StringComparison.OrdinalIgnoreCase) ||
            credential.Type.ToLowerInvariant() is not ("api-token" or "personal-access-token" or "token"))
            return Failure("credential provider or type is not supported by the GitHub authentication handler");

        try
        {
            var login = await _run("gh", ["auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--with-token"],
                Environment.CurrentDirectory, TimeSpan.FromSeconds(30), cancellationToken, credential.Secret + "\n");
            if (login.ExitCode != ProcessExitCodes.Success)
                return Failure("GitHub CLI rejected the assigned credential");

            if (action.Name == "github-api")
            {
                await github.ValidateCapabilitiesAsync(cancellationToken);
                return new DependencyInstallResult(true, true, false, Message: "GitHub API authentication was provisioned and verified for the scoped repository.");
            }

            await _gitForRepository(action.Scope)!.ConfigureHttpsCredentialHelperAsync(cancellationToken);
            return new DependencyInstallResult(true, true, false, Message: "Repository-local Git HTTPS authentication was provisioned and verified.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Failure(action.Name == "github-api"
                ? "GitHub API authentication did not pass repository readiness checks"
                : "Git HTTPS authentication did not pass remote read and dry-run write checks");
        }
    }

    private static DependencyInstallResult Failure(string message) => new(true, false, false, Message: message);
}
