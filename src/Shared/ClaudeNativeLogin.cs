namespace CodexProvisioning;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>No OAuth URL parameters or browser-returned codes cross the node boundary.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaudeLoginProgress(string Provider, string State, string InstructionsUri,
    bool RequiresBrowserAction, bool RequiresLocalCliCodeInput);

public static partial class ClaudeNativeLogin
{
    public const int ManualCompletionExitCode = -2;
    public const string InstructionsUri = "https://code.claude.com/docs/en/troubleshoot-install";
    public static ClaudeLoginProgress ManualProgress => new("anthropic", "manual-vendor-cli-completion-required", InstructionsUri, true, true);
    public static bool Valid(ClaudeLoginProgress? progress) => progress == ManualProgress;

    // Verified against the unmodified 2.1.309 binary. The complete URL contains code,
    // state and PKCE parameters. It must never be returned, persisted or logged.
    internal static bool HasTrustedChallenge(string output)
    {
        foreach (Match match in Regex.Matches(output, @"https://[^\s<>""']{1,4096}"))
        {
            if (match.Length > 4096 || !Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                uri.Host != "claude.com" || uri.AbsolutePath != "/cai/oauth/authorize") continue;
            // Require the observed native OAuth challenge, not a bare provider link.
            var fields = uri.Query.TrimStart('?').Split('&');
            if (new[] { "code", "state", "code_challenge", "redirect_uri" }.All(name =>
                fields.Any(field => field.StartsWith(name + "=", StringComparison.Ordinal) && field.Length > name.Length + 1)))
                return true;
        }
        return false;
    }

    internal static async Task<int> RunAsync(Func<ClaudeLoginProgress, CancellationToken, Task> publish, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Native login requires Linux service identity verification.");
        var uid = GetEffectiveUserId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var accounts = await File.ReadAllLinesAsync("/etc/passwd", token);
        var account = accounts.Select(line => line.Split(':')).SingleOrDefault(fields => fields.Length == 7 && fields[2] == uid);
        var home = ClaudeServiceEnvironment.Home;
        if (account is null || Path.GetFullPath(account[5]) != Path.GetFullPath(home) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")))
            throw new InvalidOperationException("Native login requires the service account HOME and default vendor configuration directory.");

        var start = new ProcessStartInfo(ClaudeServiceEnvironment.Executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = home
        };
        start.Environment["HOME"] = home;
        start.Environment["BROWSER"] = "/bin/true";
        return await RunProcessAsync(start, publish, token);
    }

    internal static async Task<int> RunProcessAsync(ProcessStartInfo start,
        Func<ClaudeLoginProgress, CancellationToken, Task> publish, CancellationToken token)
    {
        using var process = new Process { StartInfo = start };
        process.StartInfo.ArgumentList.Add("auth");
        process.StartInfo.ArgumentList.Add("login");
        process.Start();
        using var drains = CancellationTokenSource.CreateLinkedTokenSource(token);
        var challenge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var challengeObserved = false;
        async Task ReadAsync(StreamReader reader)
        {
            var buffer = "";
            var chars = new char[256];
            int count;
            while ((count = await reader.ReadAsync(chars.AsMemory(), drains.Token)) != 0)
            {
                if (challenge.Task.IsCompleted) continue;
                if (buffer.Length + count > 8192) throw new InvalidOperationException("Native challenge exceeded output bounds.");
                buffer += new string(chars, 0, count);
                if (HasTrustedChallenge(buffer)) challengeObserved = true;
                if (buffer.Contains("Paste code here", StringComparison.Ordinal) && HasTrustedChallenge(buffer))
                {
                    challenge.TrySetResult();
                    buffer = "";
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        var exited = process.WaitForExitAsync(token);
        try
        {
            var pending = new List<Task> { exited, stdout, stderr, challenge.Task };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                if (challenge.Task.IsCompletedSuccessfully)
                {
                    await publish(ManualProgress, token);
                    // No remote code receiver: stop this CLI before directing a fresh local flow.
                    return ManualCompletionExitCode;
                }
                pending.Remove(completed);
                if (completed == exited)
                {
                    pending.Remove(challenge.Task);
                    drains.CancelAfter(TimeSpan.FromSeconds(3));
                }
            }
            // Missing/failed challenge cannot be represented as a successful login.
            return process.ExitCode == 0 && !challengeObserved ? 1 : process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            drains.Cancel();
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (drains.IsCancellationRequested) { }
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
