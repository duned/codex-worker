using System.Diagnostics;

namespace CodexWorker;

/// <summary>Small presentation boundary for worker status and bounded in-place progress.</summary>
public sealed class WorkerConsole(TextWriter? writer = null, bool? interactive = null, TextWriter? errorWriter = null)
{
    private readonly TextWriter _writer = writer ?? Console.Out;
    private readonly TextWriter _errorWriter = errorWriter ?? Console.Error;
    private readonly bool _interactive = interactive ?? !Console.IsOutputRedirected;
    private bool _waiting;
    private Stopwatch? _idleTimer;
    private CancellationTokenSource? _idleCancellation;
    private Task? _idleSpinner;
    private bool _idleLineDrawn;

    public void Startup(string project, string repository)
    {
        if (_interactive)
        {
            lock (_writer) { _writer.Write("\u001b[2J\u001b[H"); _writer.Flush(); }
        }
        _waiting = false;
        WriteLine("────────────────────────────────────────────", ConsoleColor.Cyan);
        WriteLine($"CODEX WORKER v{ApplicationVersion.Display} · {project}", ConsoleColor.Cyan);
        WriteLine(repository, null);
        WriteLine("────────────────────────────────────────────", ConsoleColor.Cyan);
    }

    public void Startup(int projectCount)
    {
        if (_interactive) { lock (_writer) { _writer.Write("\u001b[2J\u001b[H"); _writer.Flush(); } }
        _waiting = false;
        WriteLine("────────────────────────────────────────────", ConsoleColor.Cyan);
        WriteLine($"CODEX WORKER v{ApplicationVersion.Display}", ConsoleColor.Cyan);
        WriteLine($"{projectCount} project{(projectCount == 1 ? "" : "s")}", null);
        WriteLine("────────────────────────────────────────────", ConsoleColor.Cyan);
    }

    public void ProjectLoaded(string name) => WriteLine($"Loaded · {name}", ConsoleColor.Green, "✓");
    public void GitHubCliReady() => WriteLine("GitHub CLI ready", ConsoleColor.Green, "✓");
    public void GitHubAuthenticationReady() => WriteLine("GitHub authentication ready", ConsoleColor.Green, "✓");
    public void GitHubLabelsReady(int projectCount, int createdCount) =>
        WriteLine($"GitHub labels ready · {projectCount} project{(projectCount == 1 ? "" : "s")}{(createdCount == 0 ? "" : $" · {createdCount} created")}", ConsoleColor.Green, "✓");
    public void GitHubDependenciesReady() => WriteLine("GitHub dependency API ready", ConsoleColor.Green, "✓");
    public void GlobalPreflight() => WriteLine("Global Codex preflight", ConsoleColor.Cyan, "▶");

    public void Started() => WriteLine("Worker started.", ConsoleColor.Green, "✓");
    public void Shutdown(string message = "Worker stopped.") { _waiting = false; WriteLine(message, null, "■"); }
    public void InfrastructureFailure(string message) { _waiting = false; WriteLine(message, ConsoleColor.Red, "✗", _errorWriter); }
    public void Warning(string message) => WriteLine(message, ConsoleColor.Yellow, "⚠");
    public void RecoveryCleanupCompleted(Guid executionId) =>
        WriteLine($"Recovery resources cleaned · execution {executionId}", ConsoleColor.Green, "✓");

    public void Waiting()
    {
        if (_waiting) return;
        _waiting = true;
        _idleTimer = Stopwatch.StartNew();
        if (_interactive)
        {
            _idleCancellation = new CancellationTokenSource();
            _idleSpinner = SpinAsync("Waiting for work", _idleTimer, _idleCancellation.Token);
        }
        else WriteLine("Waiting for work...", null, "○");
    }

    public async Task StopWaitingAsync(bool finalizeLine = false)
    {
        if (!_waiting) return;
        _waiting = false;
        _idleCancellation?.Cancel();
        if (_idleSpinner is not null)
        {
            try { await _idleSpinner; } catch (OperationCanceledException) { }
        }
        _idleTimer?.Stop();
        if (_interactive && finalizeLine)
        {
            if (_idleLineDrawn) { lock (_writer) { _writer.WriteLine(); _writer.Flush(); } }
        }
        else if (_interactive)
        {
            lock (_writer) { _writer.Write("\r\u001b[2K"); _writer.Flush(); }
        }
        _idleLineDrawn = false;
        _idleCancellation?.Dispose();
        _idleCancellation = null;
        _idleSpinner = null;
        _idleTimer = null;
    }

    public void IssueStarted(GitHubIssue issue)
    {
        _waiting = false;
        WriteLine(IssueFormatting.Display(issue), ConsoleColor.Cyan, "▶");
    }

    public void IssueStarted(string project, GitHubIssue issue)
    {
        _waiting = false;
        WriteLine($"{project.ToUpperInvariant()} · {IssueFormatting.Display(issue)}", ConsoleColor.Cyan, "▶");
    }

    private static string TaskIdentity(string project, GitHubIssue issue) =>
        $"{project.ToUpperInvariant()} · {IssueFormatting.Display(issue)}";

    public void IssueCompleted(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{IssueFormatting.Display(issue)} · completed · {FormatDuration(elapsed)}{(string.IsNullOrWhiteSpace(details) ? "" : "\n  " + details)}", ConsoleColor.Green, "✓");
    public void IssueBlocked(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{IssueFormatting.Display(issue)} · blocked · {FormatDuration(elapsed)}\n  {details}", ConsoleColor.Yellow, "⚠");
    public void IssueFailed(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{IssueFormatting.Display(issue)} · failed · {FormatDuration(elapsed)}\n  {details}", ConsoleColor.Red, "✗", _errorWriter);

    public void IssueCompleted(string project, GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{TaskIdentity(project, issue)} · completed · {FormatDuration(elapsed)}{(string.IsNullOrWhiteSpace(details) ? "" : "\n  " + details)}", ConsoleColor.Green, "✓");
    public void IssueBlocked(string project, GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{TaskIdentity(project, issue)} · blocked · {FormatDuration(elapsed)}\n  {details}", ConsoleColor.Yellow, "⚠");
    public void IssueFailed(string project, GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{TaskIdentity(project, issue)} · failed · {FormatDuration(elapsed)}\n  {details}", ConsoleColor.Red, "✗", _errorWriter);

    public async Task<T> RunProgressAsync<T>(string label, Func<Task<T>> operation, Func<T, string>? completion = null,
        Func<T, bool>? succeeded = null, Func<T, bool>? warning = null,
        Func<Exception, string>? failureDetail = null, CancellationToken ct = default)
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
        catch (Exception ex)
        {
            timer.Stop();
            await StopSpinnerAsync();
            FinishProgress(label, timer.Elapsed, false, false, failureDetail?.Invoke(ex) ?? "interrupted");
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
            var text = $"{frames[index++ % frames.Length]} {label}... {FormatElapsedClock(timer.Elapsed)}";
            lock (_writer) { _writer.Write('\r'); _writer.Write(text); _writer.Flush(); _idleLineDrawn = true; }
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
        WriteLine($"{label} {result} · {FormatDuration(elapsed)}{(string.IsNullOrWhiteSpace(detail) ? "" : " · " + Compact(detail))}", state, symbol);
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

    private static string FormatElapsedClock(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
