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
        SendAsync(Format($"🟢 CW {ApplicationVersion.Display} · INICIADO\n{project}"), ct);

    public Task StartedAsync(int projectCount, CancellationToken ct) =>
        SendAsync(Format($"🟢 CW {ApplicationVersion.Display} · INICIADO\n{projectCount} proyectos cargados"), ct);

    public Task StoppedAsync(string project, CancellationToken ct) =>
        SendAsync(Format($"⚫ CW {ApplicationVersion.Display} · DETENIDO\n{project}"), ct);

    public Task StartingAsync(string project, string repository, GitHubIssue issue, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, $"▶ CW {ApplicationVersion.Display} · {Project(project)} · TAREA INICIADA", null, ct);

    public Task SuccessAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, string summary, CancellationToken ct)
    {
        var lines = new List<string> { $"✅ CW {ApplicationVersion.Display} · {Project(project)} · TAREA COMPLETADA", IssueLink(repository, issue), "",
            $"Duración: {WorkerConsole.FormatDuration(duration)}" };
        AddMatch(lines, summary, @"Committed as `([^`]+)`", "Commit");
        AddMatch(lines, summary, @"Merged into `([^`]+)`", "Integrada en");
        AddMatch(lines, summary, @"Preserved on origin as `([^`]+)`", "Rama completada preservada");
        AddMatch(lines, summary, @"Local feature branch: `([^`]+)`", "Rama completada local");
        if (summary.Contains("No code changes", StringComparison.OrdinalIgnoreCase)) lines.Add("Completada sin cambios de código");
        var codexSummary = Regex.Match(summary, @"(?:^|\n\n)Codex summary:\s*(.*)$", RegexOptions.Singleline);
        if (codexSummary.Success && !string.IsNullOrWhiteSpace(codexSummary.Groups[1].Value))
        {
            lines.Add("");
            lines.Add($"Resumen de Codex:\n{codexSummary.Groups[1].Value.Trim()}");
        }
        return SendAsync(HtmlMessage(lines), ct, html: true);
    }

    public Task BlockedAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, string details, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, $"🟡 CW {ApplicationVersion.Display} · {Project(project)} · TAREA BLOQUEADA",
            $"{details}\nDuración: {WorkerConsole.FormatDuration(duration)}", ct);

    public Task FailedAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, string details, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, $"❌ CW {ApplicationVersion.Display} · {Project(project)} · TAREA FALLIDA",
            $"{details}\nDuración: {WorkerConsole.FormatDuration(duration)}", ct);

    public Task CriticalAsync(string? project, string details, CancellationToken ct) =>
        SendAsync(Format($"🚨 CW {ApplicationVersion.Display} · INFRAESTRUCTURA\n{(string.IsNullOrWhiteSpace(project) ? "" : $"Proyecto: {project}\n")}Worker detenido\n{Clean(details)}"), ct);

    public Task StoppedAsync(int projectCount, CancellationToken ct) =>
        SendAsync(Format($"⚫ CW {ApplicationVersion.Display} · DETENIDO"), ct);

    public static string Format(string message) => string.Join("\n", message.Split('\n').Select(Clean));

    private Task SendTaskAsync(string project, string repository, GitHubIssue issue, string header, string? details, CancellationToken ct)
    {
        var lines = new List<string> { header, IssueLink(repository, issue) };
        if (!string.IsNullOrWhiteSpace(details)) { lines.Add(""); lines.Add(details); }
        return SendAsync(HtmlMessage(lines), ct, html: true);
    }

    private static string IssueLink(string repository, GitHubIssue issue)
    {
        var url = $"https://github.com/{repository}/issues/{issue.Number}";
        return $"<a href=\"{EscapeHtml(url)}\">{EscapeHtml(IssueFormatting.Display(issue))}</a>";
    }

    private static string HtmlMessage(IEnumerable<string> lines) => string.Join("\n", lines.Select(line =>
        string.IsNullOrEmpty(line) || line.StartsWith("<a href=\"https://github.com/", StringComparison.Ordinal)
            ? line
            : string.Join("\n", line.Split('\n').Select(EscapeHtml))));

    private static string Project(string project) => EscapeHtml(project.ToUpperInvariant());
    private static string EscapeHtml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);
    private static void AddMatch(List<string> lines, string summary, string pattern, string label)
    {
        var match = Regex.Match(summary, pattern);
        if (match.Success) lines.Add($"{label}: {match.Groups[1].Value}");
    }

    private async Task SendAsync(string text, CancellationToken ct, bool html = false)
    {
        if (!_enabled) return;
        try
        {
            var payload = new Dictionary<string, object?> { ["chat_id"] = _chatId, ["text"] = text };
            if (html) payload["parse_mode"] = "HTML";
            using var response = await _client.PostAsJsonAsync($"https://api.telegram.org/bot{_token}/sendMessage", payload, ct);
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
