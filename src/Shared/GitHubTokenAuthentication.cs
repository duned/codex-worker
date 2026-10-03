namespace CodexProvisioning;

/// <summary>
/// Node-local token authentication through gh. Hosts supply process execution context;
/// credential delivery authorization and repository readiness remain with their owners.
/// Only an exit code is accepted, so secret-bearing CLI output cannot enter the result.
/// </summary>
public sealed class GitHubTokenAuthentication(
    Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Task<int>> run)
{
    public async Task<ProvisioningCommandReport> AuthenticateAsync(string expectedCredentialId,
        CredentialDeliveryResponse credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.Id != expectedCredentialId || credential.Version < 1 ||
            string.IsNullOrWhiteSpace(credential.Secret) || credential.Secret.Length > 16_384 ||
            string.IsNullOrWhiteSpace(credential.Provider) || string.IsNullOrWhiteSpace(credential.Type) ||
            !credential.Provider.Equals("github", StringComparison.OrdinalIgnoreCase) ||
            credential.Type.ToLowerInvariant() is not ("api-token" or "personal-access-token" or "token"))
            return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.Unsupported);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var exitCode = await run("gh",
                ["auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--with-token"],
                credential.Secret + "\n", TimeSpan.FromSeconds(30), cancellationToken);
            return exitCode == 0
                ? new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed)
                : new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.ProcessFailed,
                    FailureDetail: new ProvisioningFailureDetail(ProvisioningFailureCode.ProcessExited,
                        exitCode >= 0 ? exitCode : null));
        }
        catch (TimeoutException)
        {
            return new(ProvisioningCommandStatus.TimedOut, ProvisioningDiagnostic.TimedOut,
                FailureDetail: new ProvisioningFailureDetail(ProvisioningFailureCode.TimedOut));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or
            InvalidOperationException or UnauthorizedAccessException)
        {
            return new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.ProcessFailed,
                FailureDetail: new ProvisioningFailureDetail(ProvisioningFailureCode.ProcessStartFailed));
        }
    }
}
