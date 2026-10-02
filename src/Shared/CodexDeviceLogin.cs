namespace CodexProvisioning;

using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>Only the one-time, user-facing challenge crosses the node boundary. Never raw CLI output.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CodexLoginInstructions(string VerificationUri, string UserCode);

public static class CodexDeviceLogin
{
    public const string VerificationUri = "https://auth.openai.com/codex/device";

    public static bool Valid(CodexLoginInstructions? instructions) => instructions is not null &&
        instructions.VerificationUri is VerificationUri or "https://github.com/login/device" && instructions.UserCode is not null &&
        Regex.IsMatch(instructions.UserCode, @"\A[A-Z0-9]{4}-[A-Z0-9]{4,5}\z");

    internal static CodexLoginInstructions? Parse(string output)
    {
        var plain = Regex.Replace(output, @"\x1B\[[0-9;]*m", "");
        if (!Regex.IsMatch(plain, @"(?<!\S)https://auth\.openai\.com/codex/device(?=\s|$)")) return null;
        var code = Regex.Match(plain, @"(?<![A-Z0-9-])[A-Z0-9]{4}-[A-Z0-9]{4,5}(?![A-Z0-9-])");
        return code.Success ? new(VerificationUri, code.Value) : null;
    }

    internal static async Task<int> RunAsync(Func<CodexLoginInstructions, CancellationToken, Task> publish,
        CancellationToken token)
    {
        using var process = new Process { StartInfo = new(CodexServiceEnvironment.Executable)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add("login");
        process.StartInfo.ArgumentList.Add("--device-auth");
        CodexServiceEnvironment.Apply(process.StartInfo);
        process.Start();
        process.StandardInput.Close();
        using var drains = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var gate = new SemaphoreSlim(1, 1);
        var buffer = "";
        var published = false;
        async Task ReadAsync(StreamReader reader)
        {
            var chars = new char[256];
            int count;
            while ((count = await reader.ReadAsync(chars.AsMemory(), drains.Token)) != 0)
            {
                await gate.WaitAsync(drains.Token);
                try
                {
                    if (published) continue;
                    buffer += new string(chars, 0, count);
                    if (buffer.Length > 4096) buffer = buffer[^4096..];
                    if (Parse(buffer) is { } instructions)
                    {
                        await publish(instructions, drains.Token);
                        published = true;
                        buffer = "";
                    }
                }
                finally { gate.Release(); }
            }
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        try
        {
            // If publishing fails, stop login rather than leaving an invisible challenge active.
            var exited = process.WaitForExitAsync(token);
            var pending = new List<Task> { exited, stdout, stderr };
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
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (drains.IsCancellationRequested) { }
        }
    }
}

/// <summary>Detection, login and execution resolve the same service identity and local Codex home.</summary>
public static class CodexServiceEnvironment
{
    public static string Executable
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
            if (string.IsNullOrWhiteSpace(configured)) return "codex";
            if (!Path.IsPathFullyQualified(configured)) throw new InvalidOperationException("Codex service executable must be an absolute path.");
            return configured;
        }
    }

    public static string Home => Environment.GetEnvironmentVariable("CODEX_HOME") is { } home && !string.IsNullOrWhiteSpace(home)
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    internal static void Apply(ProcessStartInfo start) => start.Environment["CODEX_HOME"] = Home;
}
