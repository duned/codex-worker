using System.Diagnostics;

namespace CodexWorker;

/// <summary>Small presentation boundary for worker status and bounded in-place progress.</summary>
public sealed class WorkerConsole(TextWriter? writer = null, bool? interactive = null, TextWriter? errorWriter = null)
{
    private readonly TextWriter _writer = writer ?? Console.Out;
    private readonly TextWriter _errorWriter = errorWriter ?? Console.Error;
    private readonly bool _interactive = interactive ?? !Console.IsOutputRedirected;
    private bool _waiting;

    public void Startup(string project, string repository)
    {
        _waiting = false;
        WriteLine("────────────────────────────────────────────", ConsoleColor.Cyan);
        WriteLine("CODEX WORKER · " + project, ConsoleColor.Cyan);
        WriteLine(repository, null);
        WriteLine("────────────────────────────────────────────", ConsoleColor.Cyan);
    }

    public void PreflightStarted() => WriteLine("Codex preflight starting...", ConsoleColor.Cyan, "▶");
    public void PreflightSucceeded(TimeSpan elapsed) => WriteLine($"Codex preflight OK · {FormatDuration(elapsed)}", ConsoleColor.Green, "✓");
    public void PreflightFailed(string reason, TimeSpan elapsed) =>
        WriteLine($"Codex preflight failed · {FormatDuration(elapsed)}: {Compact(reason)}", ConsoleColor.Red, "✗");
    public void Started() => WriteLine("Worker started.", ConsoleColor.Green, "✓");
    public void Shutdown(string message = "Worker stopped.") { _waiting = false; WriteLine(message, null, "■"); }
    public void InfrastructureFailure(string message) { _waiting = false; WriteLine(message, ConsoleColor.Red, "✗", _errorWriter); }
    public void Warning(string message) => WriteLine(message, ConsoleColor.Yellow, "⚠");

    public void Waiting()
    {
        if (_waiting) return;
        _waiting = true;
        WriteLine("Waiting for work...", null, "○");
    }

    public void IssueStarted(GitHubIssue issue)
    {
        _waiting = false;
        WriteLine($"#{issue.Number} · {issue.Title}", ConsoleColor.Cyan, "▶");
    }

    public void IssueCompleted(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"#{issue.Number} completed · {FormatDuration(elapsed)}{(string.IsNullOrWhiteSpace(details) ? "" : "\n  " + details)}", ConsoleColor.Green, "✓");
    public void IssueBlocked(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"#{issue.Number} blocked · {FormatDuration(elapsed)}\n  {details}", ConsoleColor.Yellow, "⚠");
    public void IssueFailed(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"#{issue.Number} failed · {FormatDuration(elapsed)}\n  {details}", ConsoleColor.Red, "✗", _errorWriter);

    public async Task<T> RunProgressAsync<T>(string label, Func<Task<T>> operation, Func<T, string>? completion = null,
        Func<T, bool>? succeeded = null, Func<T, bool>? warning = null, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        using var spinnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? spinner = null;
        if (_interactive) spinner = SpinAsync(label, timer, spinnerCancellation.Token);
        else WriteLine($"{label}...", ConsoleColor.Cyan, "▶");
        try
        {
            var result = await operation();
            timer.Stop();
            await StopSpinnerAsync();
            var ok = succeeded?.Invoke(result) ?? true;
            FinishProgress(label, timer.Elapsed, ok, warning?.Invoke(result) ?? false, completion?.Invoke(result));
            return result;
        }
        catch
        {
            timer.Stop();
            await StopSpinnerAsync();
            FinishProgress(label, timer.Elapsed, false, false, "interrupted");
            throw;
        }

        async Task StopSpinnerAsync()
        {
            spinnerCancellation.Cancel();
            if (spinner is not null)
            {
                try { await spinner; } catch (OperationCanceledException) { }
            }
        }
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        if (duration.TotalMinutes >= 1) return $"{duration.Minutes:00}:{duration.Seconds:00}";
        return $"{duration.Seconds}s";
    }

    private async Task SpinAsync(string label, Stopwatch timer, CancellationToken ct)
    {
        const string frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        var index = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var text = $"{frames[index++ % frames.Length]} {label}... {FormatDuration(timer.Elapsed)}";
            lock (_writer) { _writer.Write('\r'); _writer.Write(text); _writer.Flush(); }
            await Task.Delay(120, ct);
        }
    }

    private void FinishProgress(string label, TimeSpan elapsed, bool success, bool warning, string? detail)
    {
        var symbol = warning ? "⚠" : success ? "✓" : "✗";
        var state = warning ? ConsoleColor.Yellow : success ? ConsoleColor.Green : ConsoleColor.Red;
        if (_interactive)
        {
            lock (_writer) { _writer.Write("\r\u001b[2K"); _writer.Flush(); }
        }
        var result = warning ? "blocked" : success ? "OK" : "failed";
        WriteLine($"{label} {result} · {FormatDuration(elapsed)}{(string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail)}", state, symbol);
    }

    private void WriteLine(string message, ConsoleColor? color, string? symbol = null, TextWriter? writer = null)
    {
        writer ??= _writer;
        var line = string.IsNullOrEmpty(symbol) ? message : $"{symbol} {message}";
        lock (writer)
        {
            if (_interactive && color.HasValue)
            {
                writer.Write(ColorCode(color.Value)); writer.Write(line); writer.Write("\u001b[0m");
            }
            else writer.Write(line);
            writer.WriteLine();
            writer.Flush();
        }
    }

    private static string ColorCode(ConsoleColor color) => color switch
    {
        ConsoleColor.Green => "\u001b[32m", ConsoleColor.Red => "\u001b[31m", ConsoleColor.Yellow => "\u001b[33m",
        ConsoleColor.Cyan => "\u001b[36m", _ => "\u001b[90m"
    };

    private static string Compact(string value)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 500 ? oneLine : oneLine[..480] + " … [truncated]";
    }
}
