namespace CodexProvisioning;

using System.Diagnostics;
using System.Text.RegularExpressions;

internal static class GitHubDeviceLogin
{
    private const string VerificationUri = "https://github.com/login/device";

    public static async Task<int> RunAsync(string configurationDirectory,
        Func<CodexLoginInstructions, CancellationToken, Task> publish, CancellationToken token)
        => await RunAsync(CreateStartInfo(configurationDirectory), publish, token);

    internal static ProcessStartInfo CreateStartInfo(string configurationDirectory)
    {
        var start = new ProcessStartInfo("/usr/bin/gh")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https", "--insecure-storage" })
            start.ArgumentList.Add(argument);
        start.Environment["GH_CONFIG_DIR"] = configurationDirectory;
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_BROWSER"] = "/usr/bin/true";
        start.Environment.Remove("GH_TOKEN");
        start.Environment.Remove("GITHUB_TOKEN");
        start.Environment.Remove("GH_ENTERPRISE_TOKEN");
        // A private product directory isolates credentials from the service account keyring.
        start.Environment["LC_ALL"] = "C";
        start.Environment.Remove("GH_DEBUG");
        return start;
    }

    internal static async Task<int> RunAsync(ProcessStartInfo start,
        Func<CodexLoginInstructions, CancellationToken, Task> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        using var drains = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var gate = new SemaphoreSlim(1, 1);
        var recent = string.Empty;
        var published = false;
        async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[256];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), drains.Token)) != 0)
            {
                await gate.WaitAsync(drains.Token);
                try
                {
                    if (published) continue;
                    recent += new string(buffer, 0, count);
                    if (recent.Length > 4096) recent = recent[^4096..];
                    if (Parse(recent) is not { } instructions) continue;
                    await publish(instructions, drains.Token);
                    published = true;
                    recent = string.Empty;
                }
                finally { gate.Release(); }
            }
        }
        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        try
        {
            var exited = process.WaitForExitAsync(token);
            var pending = new List<Task> { exited, output, error };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
                if (completed == exited) drains.CancelAfter(TimeSpan.FromSeconds(3));
            }
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            drains.Cancel();
            try { await Task.WhenAll(output, error); }
            catch (OperationCanceledException) when (drains.IsCancellationRequested) { }
        }
    }

    internal static CodexLoginInstructions? Parse(string output)
    {
        var plain = Regex.Replace(output, @"\x1B\[[0-9;]*m", "");
        if (!Regex.IsMatch(plain, @"(?<!\S)https://github\.com/login/device(?=\s)")) return null;
        // Capture only gh's explicit challenge prompt, not an arbitrary code-shaped token.
        var code = Regex.Match(plain, @"one-time code: ([A-Z0-9]{4}-[A-Z0-9]{4})(?=\s)");
        return code.Success ? new(VerificationUri, code.Groups[1].Value) : null;
    }
}

