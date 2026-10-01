using System.Diagnostics;
using System.Text;

namespace CodexWorker;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner
{
    private const int CaptureLimit = 160_000;

    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string?>? environment = null,
        string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;

        if (!Directory.Exists(workingDirectory))
            throw new InvalidOperationException($"Working directory is missing or inaccessible: '{workingDirectory}'. Check service-account permissions.");
        if (!OperatingSystem.IsWindows())
            start.FileName = ResolveExecutable(executable, workingDirectory, start.Environment.TryGetValue("PATH", out var childPath) ? childPath : null);

        // A Linux session gives the entire child tree a stable termination target even if
        // the parent exits before its descendants. setsid execs directly in this child.
        var isolatedSession = OperatingSystem.IsLinux() && File.Exists("/usr/bin/setsid") && File.Exists("/bin/kill");
        if (isolatedSession)
        {
            start.ArgumentList.Insert(0, start.FileName);
            start.FileName = "/usr/bin/setsid";
        }
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var reason = ex.NativeErrorCode switch
            {
                2 => "Executable resolved, but launch failed: check its symlink target, shebang interpreter or native loader, and working-directory lifetime",
                13 => "Permission denied: check executable/interpreter permissions and working-directory traversal access",
                _ => "Process launch failed"
            };
            throw new InvalidOperationException($"{reason}. Executable '{start.FileName}', working directory '{workingDirectory}': {ex.Message}", ex);
        }
        catch (Exception ex) { throw new InvalidOperationException($"Could not start '{start.FileName}' in '{workingDirectory}': {ex.Message}", ex); }

        var stdout = ReadLimitedAsync(process.StandardOutput, CaptureLimit);
        var stderr = ReadLimitedAsync(process.StandardError, CaptureLimit);
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), linked.Token);
                await process.StandardInput.FlushAsync(linked.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(linked.Token);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested && isolatedSession)
            {
                await SignalSessionAsync(process.Id, "-TERM");
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { /* Escalate after the bounded graceful interval. */ }
            }
            if (isolatedSession) await SignalSessionAsync(process.Id, "-KILL");
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* process may have exited */ }
            await process.WaitForExitAsync(CancellationToken.None);
            var output = await stdout;
            var error = await stderr;
            if (!cancellationToken.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true)
                throw new ProcessTimeoutException(executable, timeout!.Value, output, error);
            throw;
        }
        var standardOutput = await stdout;
        var standardError = await stderr;
        if (isolatedSession && process.ExitCode is 126 or 127 &&
            standardError.StartsWith("setsid: failed to execute", StringComparison.Ordinal))
            throw new InvalidOperationException($"Executable resolved, but launch failed: check its symlink target, shebang interpreter or native loader, and working-directory lifetime. Permission denied may indicate executable/interpreter permissions. Executable '{executable}', working directory '{workingDirectory}'.");
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static async Task SignalSessionAsync(int processId, string signal)
    {
        var start = new ProcessStartInfo("/bin/kill") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add(signal);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add($"-{processId}");
        using var signalProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not terminate child process session.");
        // Exit 1 simply means the session already exited. Do not expose kill diagnostics.
        await signalProcess.WaitForExitAsync(CancellationToken.None);
    }

    internal static string ResolveExecutable(string executable, string workingDirectory, string? path)
    {
        if (OperatingSystem.IsWindows()) return executable;
        if (executable.Contains('/'))
        {
            var fullPath = Path.GetFullPath(executable, workingDirectory);
            if (InspectExecutable(fullPath)) return fullPath;
            throw new InvalidOperationException($"Executable is not installed at '{fullPath}'. Check the configured executable path.");
        }
        string? unusable = null;
        foreach (var entry in (path ?? "").Split(Path.PathSeparator))
        {
            // Service resolution deliberately excludes current-directory and relative PATH entries.
            if (!Path.IsPathFullyQualified(entry)) continue;
            var candidate = Path.Combine(entry, executable);
            if (!InspectExecutable(candidate)) continue;
            if (!Directory.Exists(candidate) && (File.GetUnixFileMode(candidate) &
                (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) return candidate;
            unusable ??= candidate;
        }
        if (unusable is not null)
            throw new InvalidOperationException($"Executable is present but not executable: '{unusable}'. Check service-account permissions and file type.");
        var guidance = executable == "codex"
            ? "Set CODEX_WORKER_CODEX_EXECUTABLE to an absolute service-accessible path in worker.env if needed."
            : "Configure an absolute service-accessible executable path if needed.";
        throw new InvalidOperationException($"Executable '{executable}' is not installed/resolvable in the effective service PATH. {guidance} Interactive shell profiles are not consulted.");
    }

    private static bool InspectExecutable(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"Executable path is inaccessible: '{path}'. Check service-account directory traversal and file permissions.", ex);
        }
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

public sealed class CodexExecutionInfrastructureException(string category, string message, Exception? inner = null)
    : WorkerInfrastructureException(message, inner)
{
    public string Category { get; } = category;
}

internal sealed class WorkerShutdownException(CancellationToken token, Exception inner)
    : OperationCanceledException("Execution interrupted by Worker shutdown", inner, token);

internal static class WorkerShutdown
{
    // Git/GitHub deliberately wrap cancellations to flag uncertain mutation state.
    // Recognize only cancellation chains; an unrelated failure during shutdown still fails.
    internal static bool IsCancellation(Exception error)
    {
        while (error is WorkerInfrastructureException && error.InnerException is not null)
            error = error.InnerException;
        return error is OperationCanceledException;
    }
}

public sealed class WorkerStartupException(string message, Exception? inner = null) : WorkerInfrastructureException(message, inner);

public static class ProcessExitCodes
{
    public const int Success = 0;
    public const int RuntimeFailure = 1;
    public const int StartupFailure = 2;
}
