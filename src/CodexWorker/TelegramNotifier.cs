using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace CodexWorker;

public sealed class TelegramNotifier : IDisposable
{
    private readonly bool _enabled;
    private readonly string? _token;
    private readonly string? _chatId;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly WorkerConsole _console;

    public TelegramNotifier(bool enabled, WorkerConsole? console = null)
        : this(enabled, Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"),
            Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"), new HttpClient(), console, true) { }

    internal TelegramNotifier(bool enabled, string? token, string? chatId, HttpClient client, WorkerConsole? console = null)
        : this(enabled, token, chatId, client, console, false) { }

    private TelegramNotifier(bool enabled, string? token, string? chatId, HttpClient client, WorkerConsole? console, bool ownsClient)
    {
        _enabled = enabled;
        _token = token;
        _chatId = chatId;
        _client = client;
        _ownsClient = ownsClient;
        _console = console ?? new WorkerConsole();
        _client.Timeout = TimeSpan.FromSeconds(10);
        ValidateConfiguration(enabled, token, chatId);
    }

    public static void ValidateConfiguration(bool enabled, string? token, string? chatId)
    {
        if (!enabled) return;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(token)) missing.Add("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(chatId)) missing.Add("TELEGRAM_CHAT_ID");
        if (missing.Count > 0) throw new InvalidDataException("Telegram is enabled but required environment variable(s) are missing: " + string.Join(", ", missing));
    }

    public Task StartedAsync(string project, CancellationToken ct) =>
        SendAsync(Format($"🟢 CODEX WORKER · INICIADO\n{project}"), ct);

    public Task StartedAsync(int projectCount, CancellationToken ct) =>
        SendAsync(Format($"🟢 CODEX WORKER · INICIADO\n{projectCount} proyectos cargados"), ct);

    public Task StoppedAsync(string project, CancellationToken ct) =>
        SendAsync(Format($"⚫ CODEX WORKER · DETENIDO\n{project}"), ct);

    public Task StartingAsync(string project, GitHubIssue issue, CancellationToken ct) =>
        SendAsync(Format($"▶️ TAREA INICIADA · {project.ToUpperInvariant()} · #{issue.Number}\n{Clean(issue.Title)}"), ct);

    public Task SuccessAsync(string project, GitHubIssue issue, TimeSpan duration, string summary, CancellationToken ct)
    {
        var lines = new List<string>
        {
            $"✅ TAREA COMPLETADA · {project.ToUpperInvariant()} · #{issue.Number}", Clean(issue.Title), "",
            $"Duración: {WorkerConsole.FormatDuration(duration)}"
        };
        var commit = Regex.Match(summary, @"Committed as `([^`]+)`");
        if (commit.Success) lines.Add($"Commit: {commit.Groups[1].Value}");
        var branch = Regex.Match(summary, @"Merged into `([^`]+)`");
        if (branch.Success) lines.Add($"Integrada en: {branch.Groups[1].Value}");
        else if (summary.Contains("No code changes", StringComparison.OrdinalIgnoreCase)) lines.Add("Completada sin cambios de código");
        var codexSummary = Regex.Match(summary, @"(?:^|\n\n)Codex summary:\s*(.*)$", RegexOptions.Singleline);
        if (codexSummary.Success && !string.IsNullOrWhiteSpace(codexSummary.Groups[1].Value))
        {
            lines.Add("");
            lines.Add(Clean(codexSummary.Groups[1].Value));
        }
        return SendAsync(Format(string.Join("\n", lines)), ct);
    }

    public Task BlockedAsync(string project, GitHubIssue issue, TimeSpan duration, string details, CancellationToken ct) =>
        SendAsync(Format($"🟡 TAREA BLOQUEADA · {project.ToUpperInvariant()} · #{issue.Number}\n{Clean(issue.Title)}\n\n{Clean(details)}"), ct);

    public Task FailedAsync(string project, GitHubIssue issue, TimeSpan duration, string details, CancellationToken ct) =>
        SendAsync(Format($"❌ TAREA FALLIDA · {project.ToUpperInvariant()} · #{issue.Number}\n{Clean(issue.Title)}\n\n{Clean(details)}\nDuración: {WorkerConsole.FormatDuration(duration)}"), ct);

    public Task CriticalAsync(string? project, string details, CancellationToken ct) =>
        SendAsync(Format($"🚨 CODEX WORKER · INFRAESTRUCTURA\n{(string.IsNullOrWhiteSpace(project) ? "" : $"Proyecto: {project}\n")}Worker detenido\n{Clean(details)}"), ct);

    public Task StoppedAsync(int projectCount, CancellationToken ct) =>
        SendAsync(Format("⚫ CODEX WORKER · DETENIDO"), ct);

    public static string Format(string message) => string.Join("\n", message.Split('\n').Select(line =>
        string.IsNullOrWhiteSpace(line) ? "" : Clean(line)));

    private async Task SendAsync(string text, CancellationToken ct)
    {
        if (!_enabled) return;
        try
        {
            using var response = await _client.PostAsJsonAsync($"https://api.telegram.org/bot{_token}/sendMessage",
                new { chat_id = _chatId, text }, ct);
            if (!response.IsSuccessStatusCode) _console.Warning($"Telegram notification failed with HTTP {(int)response.StatusCode}; worker continues.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var detail = _token is null ? ex.Message : ex.Message.Replace(_token, "[redacted]", StringComparison.Ordinal);
            _console.Warning($"Telegram notification failed: {detail}; worker continues.");
        }
    }

    private static string Clean(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 300 ? clean : clean[..280] + " … [truncated]";
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
