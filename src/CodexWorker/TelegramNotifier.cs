using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace CodexWorker;

public sealed class TelegramNotifier : IDisposable
{
    private static readonly JsonSerializerOptions TelegramJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record DeliverySettings(bool Enabled, string? Token, string? ChatId);
    private DeliverySettings _settings = new(false, null, null);

    internal void Configure(bool enabled, string? token, string? chatId)
    {
        ValidateConfiguration(enabled, token, chatId);
        Volatile.Write(ref _settings, new(enabled, token, chatId));
    }
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
        Configure(enabled, token, chatId);
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

    public Task NoWorkAsync(CancellationToken ct) =>
        SendAsync(Format($"🟡 CW {ApplicationVersion.Display} · SIN TRABAJO"), ct);

    public Task StartingAsync(string project, string repository, GitHubIssue issue, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, "▶", "TAREA INICIADA", null, ct);

    public Task StartingAsync(string project, string repository, GitHubIssue issue, WorkerExecution execution,
        CancellationToken ct, bool recovered = false)
    {
        var action = execution.Resumed ? "TAREA REANUDADA" : "REINTENTO INICIADO";
        var mode = execution.Resumed ? "resume" : "restart";
        var retry = execution.AttemptNumber <= 1 ? "" : $"\nIntento {execution.AttemptNumber} · {mode} · ejecución actual {ExecutionFormatting.Display(execution.ExecutionId)}" +
            (execution.RetryOfExecutionId is { } id ? $"\nejecución anterior {ExecutionFormatting.Display(id)}" : "");
        var profile = execution.CodexProfile is { } settings
            ? $"\nCodex · {settings.EffectiveModel ?? "unknown (CLI model unavailable)"} · {settings.Effort}" : "";
        return SendTaskAsync(project, repository, issue, "▶",
            recovered ? "TAREA RECUPERADA" : execution.AttemptNumber <= 1 ? "TAREA INICIADA" : action,
            $"Ejecución {ExecutionFormatting.Display(execution.ExecutionId)}{retry}{profile}", ct);
    }

    public Task SuccessAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, Guid executionId, string summary, CancellationToken ct) =>
        SuccessAsync(project, repository, issue, duration, (Guid?)executionId, summary, ct);

    public Task SuccessAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, string summary, CancellationToken ct)
        => SuccessAsync(project, repository, issue, duration, null, summary, ct);

    private Task SuccessAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, Guid? executionId, string summary, CancellationToken ct)
    {
        var lines = TaskHeader(project, repository, issue, "✅", "TAREA COMPLETADA");
        if (executionId is not null) lines.Add($"Ejecución {ExecutionFormatting.Display(executionId.Value)}");
        lines.AddRange(["", $"Duración: {WorkerConsole.FormatDuration(duration)}"]);
        AddMatch(lines, summary, @"Committed as `([^`]+)`", "Commit");
        AddMatch(lines, summary, @"Merged into `([^`]+)`", "Integrada en");
        AddMatch(lines, summary, @"Preserved on origin as `([^`]+)`", "Rama completada preservada");
        AddMatch(lines, summary, @"Local feature branch: `([^`]+)`", "Rama completada local");
        AddMatch(lines, summary, @"Integration repair succeeded after (\d+) attempt\(s\)", "Reparación de integración completada · intentos");
        if (summary.Contains("No code changes", StringComparison.OrdinalIgnoreCase)) lines.Add("Completada sin cambios de código");
        var quotaSummary = Regex.Match(summary, @"(?:^|\n\n)(Codex account quota:[^\r\n]*)");
        if (quotaSummary.Success)
        {
            lines.Add("");
            lines.Add(quotaSummary.Groups[1].Value);
        }
        var codexSummary = Regex.Match(summary, @"(?:^|\n\n)Codex summary:\s*(.*)$", RegexOptions.Singleline);
        if (codexSummary.Success && !string.IsNullOrWhiteSpace(codexSummary.Groups[1].Value))
        {
            lines.Add("");
            lines.Add($"Resumen de Codex:\n{codexSummary.Groups[1].Value.Trim()}");
        }
        return SendAsync(HtmlMessage(lines), ct, html: true);
    }

    public Task BlockedAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, string details, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, "🟡", "TAREA BLOQUEADA",
            $"{details}\nDuración: {WorkerConsole.FormatDuration(duration)}", ct);

    public Task BlockedAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, Guid executionId, string details, CancellationToken ct) =>
        BlockedAsync(project, repository, issue, duration, $"Ejecución {ExecutionFormatting.Display(executionId)}\n{details}", ct);

    public Task FailedAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, string details, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, "❌", "TAREA FALLIDA",
            $"{details}\nDuración: {WorkerConsole.FormatDuration(duration)}", ct);

    public Task FailedAsync(string project, string repository, GitHubIssue issue, TimeSpan duration, Guid executionId, string details, CancellationToken ct) =>
        FailedAsync(project, repository, issue, duration, $"Ejecución {ExecutionFormatting.Display(executionId)}\n{details}", ct);

    public Task IntegrationRecoveryRejectedAsync(string project, string repository, GitHubIssue issue, string reason, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, "⚠️", "RECUPERACIÓN DE INTEGRACIÓN RECHAZADA", reason, ct);

    public Task PreparationRejectedAsync(string project, string repository, GitHubIssue issue, Guid executionId,
        string reason, CancellationToken ct) =>
        SendTaskAsync(project, repository, issue, "🟡", "TAREA BLOQUEADA",
            $"Ejecución {ExecutionFormatting.Display(executionId)}\nPreparación rechazada: {Clean(reason)}", ct);

    public Task CriticalAsync(string? project, string details, CancellationToken ct) =>
        SendAsync(Format($"🚨 CW {ApplicationVersion.Display} · INFRAESTRUCTURA\n{(string.IsNullOrWhiteSpace(project) ? "" : $"Proyecto: {project}\n")}Worker detenido\n{Clean(details)}"), ct);

    public Task StoppedAsync(int projectCount, CancellationToken ct) =>
        SendAsync(Format($"⚫ CW {ApplicationVersion.Display} · DETENIDO"), ct);

    public static string Format(string message) => string.Join("\n", message.Split('\n').Select(Clean));

    private Task SendTaskAsync(string project, string repository, GitHubIssue issue, string icon, string status, string? details, CancellationToken ct)
    {
        var lines = TaskHeader(project, repository, issue, icon, status);
        if (!string.IsNullOrWhiteSpace(details)) { lines.Add(""); lines.Add(details); }
        return SendAsync(HtmlMessage(lines), ct, html: true);
    }

    private static List<string> TaskHeader(string project, string repository, GitHubIssue issue, string icon, string status) =>
        [$"{icon} {IssueLink(repository, issue)}", $"{Project(project)} · {status} · CW {ApplicationVersion.Display}"];

    private static string IssueLink(string repository, GitHubIssue issue)
    {
        var url = $"https://github.com/{repository}/issues/{issue.Number}";
        return $"<a href=\"{EscapeHtml(url)}\">{EscapeHtml(IssueFormatting.Display(issue))}</a>";
    }

    private static string HtmlMessage(IEnumerable<string> lines) => string.Join("\n", lines.Select(line =>
        string.IsNullOrEmpty(line) || IsIssueLinkLine(line)
            ? line
            : string.Join("\n", line.Split('\n').Select(EscapeHtml))));

    private static bool IsIssueLinkLine(string line) =>
        line.IndexOf("<a href=\"https://github.com/", StringComparison.Ordinal) > 0 &&
        line.EndsWith("</a>", StringComparison.Ordinal);

    private static string Project(string project) => EscapeHtml(project.ToUpperInvariant());
    private static string EscapeHtml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);
    private static void AddMatch(List<string> lines, string summary, string pattern, string label)
    {
        var match = Regex.Match(summary, pattern);
        if (match.Success) lines.Add($"{label}: {match.Groups[1].Value}");
    }

    internal async Task<string> TestAsync(CancellationToken ct) =>
        (await DeliverAsync("Codex Worker Telegram delivery test.", ct)).Diagnostic;

    private async Task SendAsync(string text, CancellationToken ct, bool html = false)
    {
        if (!Volatile.Read(ref _settings).Enabled) return;
        var result = await DeliverAsync(text, ct, html);
        if (result.HttpStatus is { } status) _console.Warning($"Telegram notification failed with HTTP {status}; worker continues.");
        else if (result.Diagnostic != "accepted") _console.Warning($"Telegram notification failed: {result.Diagnostic}; worker continues.");
    }

    private async Task<(string Diagnostic, int? HttpStatus)> DeliverAsync(string text, CancellationToken ct, bool html = false)
    {
        var settings = Volatile.Read(ref _settings);
        if (!settings.Enabled || settings.Token is null || settings.ChatId is null) return ("delivery-unavailable", null);
        try
        {
            var payload = new Dictionary<string, object?> { ["chat_id"] = settings.ChatId, ["text"] = text };
            if (html) payload["parse_mode"] = "HTML";
            using var response = await _client.PostAsJsonAsync($"https://api.telegram.org/bot{settings.Token}/sendMessage", payload,
                TelegramJsonOptions, ct);
            if (!response.IsSuccessStatusCode) return ("telegram-rejected", (int)response.StatusCode);
            // Read only a bounded response. Never surface Telegram descriptions or request URLs.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[65537];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length == buffer.Length) return ("delivery-unavailable", null);
            using var body = JsonDocument.Parse(buffer.AsMemory(0, length));
            return body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("ok", out var ok) &&
                ok.ValueKind == JsonValueKind.True ? ("accepted", null) : ("telegram-rejected", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return ("delivery-unavailable", null);
        }
    }

    private static string Clean(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= 300 ? clean : clean[..280] + " … [truncated]";
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
