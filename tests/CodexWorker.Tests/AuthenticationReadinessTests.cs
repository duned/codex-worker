using CodexWorker;

namespace CodexWorker.Tests;

public sealed class AuthenticationReadinessTests
{
    [Fact]
    public async Task GitReadinessChecksRemoteReadAndDryRunWriteWithoutMutation()
    {
        var commands = new List<string[]>();

        await GitRemoteAuthenticationProbe.ValidateAsync((arguments, _) =>
        {
            commands.Add(arguments.ToArray());
            return Task.CompletedTask;
        }, "team/repo", "codex/", CancellationToken.None);

        Assert.Equal(2, commands.Count);
        Assert.Equal(new[] { "ls-remote", "--exit-code", "origin", "HEAD" }, commands[0]);
        Assert.Contains("--dry-run", commands[1]);
        Assert.Contains("--porcelain", commands[1]);
        Assert.Contains("codex/auth-check-", commands[1][^1]);
    }

    [Fact]
    public async Task MissingGitRemoteAuthenticationStopsReadinessWithoutLeakingProcessDiagnostics()
    {
        var attempts = 0;

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            GitRemoteAuthenticationProbe.ValidateAsync((_, _) =>
            {
                attempts++;
                throw new WorkerInfrastructureException("credential token=fake-secret");
            }, "team/repo", "codex/", CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.DoesNotContain("fake-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerReportsSeparateRepositoryScopedApiAndGitCapabilities()
    {
        var capabilities = WorkerAuthenticationCapabilities.ForRepository("team/repo");

        Assert.Contains(capabilities, capability => capability.Type == "authentication" && capability.Name == "github-api" && capability.Scope == "team/repo");
        Assert.Contains(capabilities, capability => capability.Type == "authentication" && capability.Name == "git-repository" && capability.Scope == "team/repo");
        Assert.Equal(new WorkerCapabilityContract("agent-provider", "codex"), WorkerAgentCapabilities.AuthenticatedProvider("Codex"));
    }

    [Fact]
    public void GitHubReadinessRejectsMissingOrInsufficientIssueWritePermission()
    {
        Assert.Throws<WorkerInfrastructureException>(() => GitHubClient.ValidateIssueWritePermission("{}", "team/repo"));
        Assert.Throws<WorkerInfrastructureException>(() => GitHubClient.ValidateIssueWritePermission("{\"pull\":true}", "team/repo"));
        GitHubClient.ValidateIssueWritePermission("{\"triage\":true}", "team/repo");
        GitHubClient.ValidateIssueWritePermission("{\"push\":true}", "team/repo");
    }

    [Fact]
    public async Task AssignedGitHubCredentialIsPassedOverStdinAndOnlySafeResultIsReturned()
    {
        const string secret = "fake-authentication-token-value";
        var commands = new List<(string Executable, string[] Arguments, string? StandardInput)>();
        var github = new GitHubClient("team/repo", (arguments, _) =>
        {
            var command = arguments.ToArray();
            var output = command.Contains("--jq") ? "{\"push\":true}" : command.Contains("number") ? "[]" : "";
            return Task.FromResult(new ProcessResult(0, output, ""));
        });
        var provisioner = new GitHubAuthenticationProvisioner(
            (executable, arguments, _, _, _, standardInput) =>
            {
                commands.Add((executable, arguments.ToArray(), standardInput));
                return Task.FromResult(new ProcessResult(0, "", ""));
            },
            repository => repository == "team/repo" ? github : null,
            _ => null,
            (_, _) => Task.FromResult<WorkerCredentialContract?>(new WorkerCredentialContract(
                "credential-id", "github", "api-token", 1, secret)));
        var action = new ProvisioningActionContract("api-auth", "authentication", "github-api", Operation: "provision",
            CredentialId: "credential-id", Scope: "team/repo");

        var result = await provisioner.ExecuteAsync(action, CancellationToken.None);

        Assert.True(result.Supported);
        Assert.True(result.Succeeded);
        var login = Assert.Single(commands);
        Assert.Equal("gh", login.Executable);
        Assert.DoesNotContain(secret, string.Join(" ", login.Arguments), StringComparison.Ordinal);
        Assert.Equal(secret + "\n", login.StandardInput);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
    }
}
