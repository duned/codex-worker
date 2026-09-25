using System.Diagnostics;
using System.Text;

namespace CodexWorker;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner
{
    private const int CaptureLimit = 160_000;

    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        }
        catch (Exception ex) { throw new InvalidOperationException($"Could not start '{executable}': {ex.Message}", ex); }

        var stdout = ReadLimitedAsync(process.StandardOutput, CaptureLimit);
        var stderr = ReadLimitedAsync(process.StandardError, CaptureLimit);
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* process may have exited */ }
            await process.WaitForExitAsync(CancellationToken.None);
            var output = await stdout;
            var error = await stderr;
            if (!cancellationToken.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true)
                throw new ProcessTimeoutException(executable, timeout!.Value, output, error);
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int limit)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(limit, 8192));
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var remaining = limit - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) output.Append("\n[output truncated]");
        return output.ToString();
    }
}

public sealed class ProcessTimeoutException(string executable, TimeSpan timeout, string standardOutput, string standardError)
    : TimeoutException($"'{executable}' exceeded timeout {timeout}.{Diagnostic(standardOutput, standardError)}")
{
    public string Executable { get; } = executable;
    public TimeSpan Timeout { get; } = timeout;
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
    private static string Diagnostic(string stdout, string stderr) =>
        $"\nstdout: {Tail(stdout)}\nstderr: {Tail(stderr)}";
    private static string Tail(string value) => value.Length <= 3000 ? value : value[^3000..];
}

public class WorkerInfrastructureException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class TaskFailureException(string message, Exception? inner = null) : Exception(message, inner);
