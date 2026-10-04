using WorkExecutionToolbox;

namespace CodexWorker;

/// <summary>CLI host boundary: reuse node-local gh authentication without storing credentials.</summary>
public static class GitHubToolboxHost
{
    public static GitHubIssueProvider CreateProvider(HttpClient http, ProcessRunner runner, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return CreateProvider(http, token => runner.RunAsync("gh",
            ["auth", "token", "--hostname", "github.com"], workingDirectory,
            TimeSpan.FromSeconds(30), token));
    }

    public static GitHubIssueProvider CreateProvider(HttpClient http,
        Func<CancellationToken, Task<ProcessResult>> readGhToken)
    {
        ArgumentNullException.ThrowIfNull(readGhToken);
        return new GitHubIssueProvider(http, async cancellationToken =>
        {
            try
            {
                var result = await readGhToken(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.ExitCode != 0)
                    throw new GitHubIssueException(GitHubIssueFailure.Authorization,
                        "GitHub CLI authentication is unavailable; run gh auth login for github.com.");
                return result.StandardOutput.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // gh output and process exceptions may contain credentials. Never attach them.
                throw new GitHubIssueException(GitHubIssueFailure.Authorization,
                    "GitHub CLI authentication is unavailable; run gh auth login for github.com.");
            }
        });
    }
}
