using System.Diagnostics;

namespace WorkExecutionToolbox.Cli;

/// <summary>Reads only local remote configuration; never contacts a remote or reports its URLs.</summary>
public static class LocalRepositoryDiscovery
{
    public static Task<IReadOnlyList<string>> ReadRemoteUrlsAsync(CancellationToken cancellationToken) =>
        ReadRemoteUrlsAsync(Environment.CurrentDirectory, cancellationToken);

    public static async Task<IReadOnlyList<string>> ReadRemoteUrlsAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "remote", "-v" }
            }
        };
        try
        {
            if (!process.Start()) return [];
            try
            {
                var stdout = ReadBoundedAsync(process.StandardOutput, true, deadline.Token);
                var stderr = ReadBoundedAsync(process.StandardError, false, deadline.Token);
                try { await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)); }
                catch { deadline.Cancel(); throw; }
                if (process.ExitCode != 0) return [];
                var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var urls = new List<string>();
                foreach (var line in lines)
                {
                    var tab = line.IndexOf('\t');
                    var end = line.LastIndexOf(" (", StringComparison.Ordinal);
                    if (tab < 0 || end <= tab + 1) return [];
                    urls.Add(line[(tab + 1)..end]);
                }
                return urls.AsReadOnly();
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
            // Process errors and remote configuration can contain credentials; discard all diagnostics.
            return [];
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
            if (count > 65_536) throw new IOException("Local remote output exceeded its safety limit.");
            if (retain) result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
