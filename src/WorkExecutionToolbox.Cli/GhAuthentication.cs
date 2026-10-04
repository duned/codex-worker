using System.Diagnostics;

namespace WorkExecutionToolbox.Cli;

/// <summary>Uses existing gh login in memory only; discards credential-bearing process diagnostics.</summary>
public static class GhAuthentication
{
    public static async Task<string> ReadTokenAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("gh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "auth", "token", "--hostname", "github.com" }
            }
        };
        try
        {
            if (!process.Start()) throw AuthenticationFailure();
            try
            {
                // Drain both pipes concurrently and bound the only retained output.
                var stdout = ReadBoundedAsync(process.StandardOutput, retain: true, deadline.Token);
                var stderr = ReadBoundedAsync(process.StandardError, retain: false, deadline.Token);
                try
                {
                    await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
                }
                catch
                {
                    deadline.Cancel();
                    throw;
                }
                if (process.ExitCode != 0) throw AuthenticationFailure();
                var token = (await stdout).Trim();
                if (string.IsNullOrWhiteSpace(token)) throw AuthenticationFailure();
                return token;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanup.Token);
                }
            }
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw AuthenticationFailure();
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, bool retain, CancellationToken token)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[1024];
        var count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            count += read;
            if (count > 16_384) throw AuthenticationFailure();
            if (retain) result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    private static GitHubIssueException AuthenticationFailure() => new(GitHubIssueFailure.Authorization,
        "GitHub CLI authentication unavailable; install gh and run gh auth login --hostname github.com.");
}
