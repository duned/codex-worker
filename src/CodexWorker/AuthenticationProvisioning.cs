namespace CodexWorker;

using CodexProvisioning;

/// <summary>Materializes an assigned GitHub token through gh and optional repository-local Git configuration.</summary>
public sealed class GitHubAuthenticationProvisioner : IAuthenticationActionExecutor
{
    private readonly GitHubTokenAuthentication _authentication;
    private readonly Func<string, GitHubClient?> _githubForRepository;
    private readonly Func<string, GitRepository?> _gitForRepository;
    private readonly Func<string, CancellationToken, Task<CredentialDeliveryResponse?>> _retrieveCredential;

    public GitHubAuthenticationProvisioner(ProcessRunner runner, Func<string, GitHubClient?> githubForRepository,
        Func<string, GitRepository?> gitForRepository,
        Func<string, CancellationToken, Task<CredentialDeliveryResponse?>> retrieveCredential)
        : this(async (executable, arguments, directory, timeout, cancellationToken, input) =>
                await runner.RunAsync(executable, arguments, directory, timeout, cancellationToken,
                    environment: await CodexProvisioning.NodeGitHubSetup.GitHubEnvironmentAsync(cancellationToken), standardInput: input),
            githubForRepository, gitForRepository, retrieveCredential) { }

    internal GitHubAuthenticationProvisioner(
        Func<string, IEnumerable<string>, string, TimeSpan, CancellationToken, string?, Task<ProcessResult>> run,
        Func<string, GitHubClient?> githubForRepository,
        Func<string, GitRepository?> gitForRepository,
        Func<string, CancellationToken, Task<CredentialDeliveryResponse?>> retrieveCredential)
    {
        _authentication = new GitHubTokenAuthentication(async (executable, arguments, input, timeout, token) =>
            (await run(executable, arguments, Environment.CurrentDirectory, timeout, token, input)).ExitCode);
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
        var git = action.Name == "git-https" ? _gitForRepository(action.Scope) : null;
        if (action.Name == "git-https" && git is null)
            return Failure("repository scope is not configured on this Worker");

        CredentialDeliveryResponse? credential;
        try { credential = await _retrieveCredential(action.CredentialId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Failure("assigned credential could not be retrieved"); }
        if (credential is null) return Failure("assigned credential is unavailable");
        try
        {
            var login = await _authentication.AuthenticateAsync(action.CredentialId, credential, cancellationToken);
            if (login.Diagnostic == ProvisioningDiagnostic.Unsupported)
                return Failure("credential provider or type is not supported by the GitHub authentication handler");
            if (login.Status != ProvisioningCommandStatus.Succeeded)
                return Failure("GitHub CLI rejected the assigned credential");

            if (action.Name == "github-api")
            {
                await github.ValidateCapabilitiesAsync(cancellationToken);
                return new DependencyInstallResult(true, true, false, Message: "GitHub API authentication was provisioned and verified for the scoped repository.");
            }

            await (git ?? throw new InvalidOperationException("Repository scope is unavailable.")).ConfigureHttpsCredentialHelperAsync(cancellationToken);
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
