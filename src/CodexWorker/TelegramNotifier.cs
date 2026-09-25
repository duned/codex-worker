using System.Net.Http.Json;

namespace CodexWorker;

public sealed class TelegramNotifier : IDisposable
{
    private readonly bool _enabled;
    private readonly string? _token;
    private readonly string? _chatId;
    private readonly HttpClient _client = new();

    public TelegramNotifier(bool enabled)
    {
        _enabled = enabled;
        _client.Timeout = TimeSpan.FromSeconds(10);
        _token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        _chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
        if (_enabled && (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_chatId)))
            Console.Error.WriteLine("Telegram is enabled, but TELEGRAM_BOT_TOKEN or TELEGRAM_CHAT_ID is missing; notifications are disabled.");
    }

    public Task SuccessAsync(string project, int number, string summary, CancellationToken ct) =>
        SendAsync($"✅ {project}: Issue #{number} completed. {summary}", ct);

    public Task BlockedAsync(string project, int number, string details, CancellationToken ct) =>
        SendAsync($"⏸️ {project}: Issue #{number} is blocked. {details}", ct);

    public Task FailedAsync(string project, int number, string details, CancellationToken ct) =>
        SendAsync($"❌ {project}: Issue #{number} failed. {details}", ct);

    public Task StartingAsync(string project, int number, string title, CancellationToken ct) =>
        SendAsync($"▶️ {project}: starting Issue #{number}: {title}", ct);

    private async Task SendAsync(string text, CancellationToken ct)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_chatId)) return;
        try
        {
            using var response = await _client.PostAsJsonAsync($"https://api.telegram.org/bot{_token}/sendMessage",
                new { chat_id = _chatId, text }, ct);
            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"Telegram notification failed with HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var detail = _token is null ? ex.Message : ex.Message.Replace(_token, "[redacted]", StringComparison.Ordinal);
            Console.Error.WriteLine($"Telegram notification failed: {detail}");
        }
    }

    public void Dispose() => _client.Dispose();
}
