namespace CodexWorker;

/// <summary>Small presentation boundary for worker status and bounded in-place progress.</summary>
public sealed class WorkerConsole(TextWriter? writer = null, bool? interactive = null, TextWriter? errorWriter = null,
    TimeProvider? timeProvider = null)
{
    private readonly TextWriter _writer = writer ?? Console.Out;
    private readonly TextWriter _errorWriter = errorWriter ?? Console.Error;
    private readonly bool _hasExplicitErrorWriter = errorWriter is not null;
    private readonly bool _interactive = interactive ?? !Console.IsOutputRedirected;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private bool _waiting;
    private string? _readinessBlocker;
    private long? _idleStarted;
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
    public void GitHubAuthenticationReady() => WriteLine("GitHub API authentication ready", ConsoleColor.Green, "✓");
    public void GitRepositoryAuthenticationReady(int projectCount) =>
        WriteLine($"Git repository authentication ready · {projectCount} project{(projectCount == 1 ? "" : "s")}", ConsoleColor.Green, "✓");
    public void GitHubLabelsReady(int projectCount, int createdCount) =>
        WriteLine($"GitHub labels ready · {projectCount} project{(projectCount == 1 ? "" : "s")}{(createdCount == 0 ? "" : $" · {createdCount} created")}", ConsoleColor.Green, "✓");
    public void GitHubDependenciesReady() => WriteLine("GitHub dependency API ready", ConsoleColor.Green, "✓");
    public void GlobalPreflight() => WriteLine("Global Codex preflight", ConsoleColor.Cyan, "▶");

    public void Started() => WriteLine("Worker started.", ConsoleColor.Green, "✓");
    public void NoProjectsConfigured() => WriteLine("Managed Worker is healthy and idle · no projects configured", ConsoleColor.Cyan, "○");
    public void Shutdown(string message = "Worker stopped.") { _waiting = false; WriteLine(message, null, "■"); }
    public void InfrastructureFailure(string message) { _waiting = false; WriteLine(message, ConsoleColor.Red, "✗", _errorWriter); }
    public void Warning(string message) => WriteLine(message, ConsoleColor.Yellow, "⚠");
    public void RecoveryCleanupCompleted(Guid executionId) =>
        WriteLine($"Recovery resources cleaned · execution {ExecutionFormatting.Display(executionId)}", ConsoleColor.Green, "✓");

    public void Waiting()
    {
        _readinessBlocker = null;
        if (_waiting) return;
        _waiting = true;
        _idleStarted = _timeProvider.GetTimestamp();
        if (_interactive)
        {
            _idleCancellation = new CancellationTokenSource();
            _idleSpinner = SpinAsync("Waiting for work", _idleStarted.Value, _idleCancellation.Token);
        }
        else WriteLine("Waiting for work...", null, "○");
    }

    public async Task WaitingForPrerequisitesAsync(IReadOnlyList<string> blockingReasons)
    {
        ArgumentNullException.ThrowIfNull(blockingReasons);
        var reasonText = blockingReasons.Count == 0 ? "execution-readiness-unavailable" : string.Join(", ", blockingReasons);
        if (_waiting) await StopWaitingAsync(finalizeLine: true);
        _waiting = false;
        if (_readinessBlocker == reasonText) return;
        _readinessBlocker = reasonText;
        WriteLine("Worker online but not execution-ready", ConsoleColor.Yellow, "⚠");
        WriteLine($"Waiting for prerequisites... · {reasonText}", ConsoleColor.Yellow, "○");
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
        if (_interactive && _idleStarted is not null)
        {
            var elapsedText = $"⠋ Waiting for work... {FormatElapsedClock(_timeProvider.GetElapsedTime(_idleStarted.Value))}";
            lock (_writer) { _writer.Write('\r'); _writer.Write(elapsedText); _writer.Flush(); _idleLineDrawn = true; }
        }
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
        _idleStarted = null;
    }

    public void IssueStarted(GitHubIssue issue)
    {
        _waiting = false;
        WriteLine(IssueFormatting.OperationalIdentity(issue), ConsoleColor.Cyan, "▶");
    }

    public void IssueStarted(string project, GitHubIssue issue)
    {
        _waiting = false;
        WriteLine(IssueFormatting.OperationalIdentity(issue), ConsoleColor.Cyan, "▶");
    }

    public void IssueStarted(string project, GitHubIssue issue, WorkerExecution execution)
    {
        _waiting = false;
        WriteLine(ExecutionFormatting.OperationalIdentity(issue, execution.ExecutionId), ConsoleColor.Cyan, "▶");
        if (execution.CodexProfile is { } profile)
            WriteLine($"Codex · model {profile.Model ?? "CLI default"} · effort {profile.Effort}", ConsoleColor.DarkGray, "↳");
        if (execution.AttemptNumber > 1 && execution.RetryOfExecutionId is { } previousId)
        {
            var mode = execution.Resumed ? "resume" : "restart";
            WriteLine($"Attempt {execution.AttemptNumber} · {mode} from [{ExecutionFormatting.ShortId(previousId)}]",
                ConsoleColor.DarkGray, "↳");
        }
    }

    public void IssueCompleted(GitHubIssue issue, TimeSpan elapsed) =>
        IssueCompletedCore(issue, elapsed);
    public void IssueCompleted(GitHubIssue issue, TimeSpan elapsed, Guid executionId) =>
        IssueCompleted(issue, elapsed, executionId, 1, null);
    public void IssueCompleted(GitHubIssue issue, TimeSpan elapsed, Guid executionId, int attemptNumber, Guid? retryOfExecutionId)
    {
        var lineage = attemptNumber > 1 && retryOfExecutionId is { } previousId
            ? $" (Attempt {attemptNumber} · from [{ExecutionFormatting.ShortId(previousId)}])"
            : "";
        WriteLine($"{ExecutionFormatting.OperationalIdentity(issue, executionId)} · completed · {FormatDuration(elapsed)}{lineage}", ConsoleColor.Green, "✓");
    }
    public void IssueCompleted(GitHubIssue issue, TimeSpan elapsed, string details) => IssueCompleted(issue, elapsed);
    public void IssueBlocked(GitHubIssue issue, TimeSpan elapsed) =>
        IssueBlockedCore(issue, elapsed);
    public void IssueBlocked(GitHubIssue issue, TimeSpan elapsed, Guid executionId, string details) =>
        WriteLine($"{ExecutionFormatting.OperationalIdentity(issue, executionId)} · blocked · {FormatDuration(elapsed)}" +
            (details.Contains("## ", StringComparison.Ordinal) ? "" : $" · {Compact(FailureDiagnosticRedactor.Redact(details))}"),
            ConsoleColor.Yellow, "⚠");
    public void IssueBlocked(GitHubIssue issue, TimeSpan elapsed, Guid executionId) =>
        WriteLine($"{ExecutionFormatting.OperationalIdentity(issue, executionId)} · blocked · {FormatDuration(elapsed)}", ConsoleColor.Yellow, "⚠");
    public void IssueBlocked(GitHubIssue issue, TimeSpan elapsed, string details) => IssueBlockedCore(issue, elapsed, details);
    public void IssueFailed(GitHubIssue issue, TimeSpan elapsed) =>
        IssueFailedCore(issue, elapsed);
    public void IssueFailed(GitHubIssue issue, TimeSpan elapsed, Guid executionId, string details) =>
        WriteLine($"{ExecutionFormatting.OperationalIdentity(issue, executionId)} · failed · {FormatDuration(elapsed)}" +
            (details.Contains("## ", StringComparison.Ordinal) ? "" : $" · {details}"),
            ConsoleColor.Red, "✗", _errorWriter);
    public void FailureReason(Guid executionId, string category, string reason, IReadOnlyList<string>? secretValues = null)
    {
        var safeReason = FailureDiagnosticRedactor.Redact(reason, secretValues);
        WriteLine($"Reason · execution {ExecutionFormatting.Display(executionId)} · {category} · {Compact(safeReason, 180)}",
            ConsoleColor.DarkGray, writer: _hasExplicitErrorWriter ? _errorWriter : _writer);
    }
    public void IssueFailed(GitHubIssue issue, TimeSpan elapsed, string details) =>
        IssueFailedCore(issue, elapsed, details);

    public void IssueCompleted(string project, GitHubIssue issue, TimeSpan elapsed) => IssueCompleted(issue, elapsed);
    public void IssueCompleted(string project, GitHubIssue issue, TimeSpan elapsed, string details) =>
        IssueCompleted(issue, elapsed);
    public void IssueBlocked(string project, GitHubIssue issue, TimeSpan elapsed) => IssueBlocked(issue, elapsed);
    public void IssueBlocked(string project, GitHubIssue issue, TimeSpan elapsed, string details) =>
        IssueBlockedCore(issue, elapsed, details);
    public void IssueFailed(string project, GitHubIssue issue, TimeSpan elapsed) => IssueFailed(issue, elapsed);
    public void IssueFailed(string project, GitHubIssue issue, TimeSpan elapsed, string details) =>
        IssueFailed(issue, elapsed, details);

    private void IssueCompletedCore(GitHubIssue issue, TimeSpan elapsed) =>
        WriteLine($"{IssueFormatting.OperationalIdentity(issue)} · completed · {FormatDuration(elapsed)}", ConsoleColor.Green, "✓");
    private void IssueBlockedCore(GitHubIssue issue, TimeSpan elapsed) =>
        WriteLine($"{IssueFormatting.OperationalIdentity(issue)} · blocked · {FormatDuration(elapsed)}", ConsoleColor.Yellow, "⚠");
    private void IssueBlockedCore(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{IssueFormatting.OperationalIdentity(issue)} · blocked · {FormatDuration(elapsed)}" +
            (details.Contains("## ", StringComparison.Ordinal) ? "" : $" · {Compact(FailureDiagnosticRedactor.Redact(details))}"),
            ConsoleColor.Yellow, "⚠");
    private void IssueFailedCore(GitHubIssue issue, TimeSpan elapsed) =>
        WriteLine($"{IssueFormatting.OperationalIdentity(issue)} · failed · {FormatDuration(elapsed)}", ConsoleColor.Red, "✗", _errorWriter);
    private void IssueFailedCore(GitHubIssue issue, TimeSpan elapsed, string details) =>
        WriteLine($"{IssueFormatting.OperationalIdentity(issue)} · failed · {FormatDuration(elapsed)}" +
            (details.Contains("## ", StringComparison.Ordinal) ? "" : $" · {details}"),
            ConsoleColor.Red, "✗", _errorWriter);

    public async Task<T> RunProgressAsync<T>(string label, Func<Task<T>> operation, Func<T, string>? completion = null,
        Func<T, bool>? succeeded = null, Func<T, bool>? warning = null,
        Func<Exception, string>? failureDetail = null, CancellationToken ct = default)
    {
        var started = _timeProvider.GetTimestamp();
        using var spinnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? spinner = null;
        if (_interactive) spinner = SpinAsync(label, started, spinnerCancellation.Token);
        else WriteLine($"{label}...", ConsoleColor.Cyan, "▶");
        try
        {
            var result = await operation();
            var elapsed = _timeProvider.GetElapsedTime(started);
            await StopSpinnerAsync();
            var ok = succeeded?.Invoke(result) ?? true;
            FinishProgress(label, elapsed, ok, warning?.Invoke(result) ?? false, completion?.Invoke(result));
            return result;
        }
        catch (Exception ex)
        {
            var elapsed = _timeProvider.GetElapsedTime(started);
            await StopSpinnerAsync();
            FinishProgress(label, elapsed, false, false, failureDetail?.Invoke(ex) ?? "interrupted",
                cancelled: ct.IsCancellationRequested && WorkerShutdown.IsCancellation(ex));
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

    private async Task SpinAsync(string label, long started, CancellationToken ct)
    {
        const string frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        var index = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var text = $"{frames[index++ % frames.Length]} {label}... {FormatElapsedClock(_timeProvider.GetElapsedTime(started))}";
            lock (_writer) { _writer.Write('\r'); _writer.Write(text); _writer.Flush(); _idleLineDrawn = true; }
            await Task.Delay(TimeSpan.FromMilliseconds(120), _timeProvider, ct);
        }
    }

    private void FinishProgress(string label, TimeSpan elapsed, bool success, bool warning, string? detail, bool cancelled = false)
    {
        var symbol = cancelled ? "↳" : warning ? "⚠" : success ? "✓" : "✗";
        var state = cancelled ? ConsoleColor.DarkGray : warning ? ConsoleColor.Yellow : success ? ConsoleColor.Green : ConsoleColor.Red;
        if (_interactive)
        {
            lock (_writer) { _writer.Write("\r\u001b[2K"); _writer.Flush(); }
        }
        var result = cancelled ? "cancelled" : warning ? "blocked" : success ? "OK" : "failed";
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

    private static string Compact(string value, int maximumLength = 500)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const string suffix = " [truncated]";
        return oneLine.Length <= maximumLength ? oneLine : oneLine[..(maximumLength - suffix.Length)] + suffix;
    }

    private static string FormatElapsedClock(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
