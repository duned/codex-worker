using System.Net;
using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class OperabilityTests
{
    [Fact]
    public void RedirectedConsoleUsesSemanticTextWithoutAnsi()
    {
        var writer = new StringWriter();
        var output = new WorkerConsole(writer, interactive: false, errorWriter: writer);
        output.IssueCompleted(new GitHubIssue(3, "Add config", "", DateTimeOffset.UtcNow), TimeSpan.FromSeconds(77), "Committed");
        output.IssueFailed(new GitHubIssue(4, "Fail", "", DateTimeOffset.UtcNow), TimeSpan.FromSeconds(3), "Check failed");
        output.IssueBlocked(new GitHubIssue(5, "Needs input", "", DateTimeOffset.UtcNow), TimeSpan.FromSeconds(1), "Choose API");
        var text = writer.ToString();
        Assert.Contains("✓ #3 completed · 01:17", text);
        Assert.Contains("✗ #4 failed", text);
        Assert.Contains("⚠ #5 blocked", text);
        Assert.DoesNotContain("\u001b[", text);
    }

    [Fact]
    public void RepeatedIdleStateIsPrintedOnlyOnceUntilIssueStarts()
    {
        var writer = new StringWriter();
        var output = new WorkerConsole(writer, interactive: false);
        output.Waiting();
        output.Waiting();
        output.IssueStarted(new GitHubIssue(1, "Work", "", DateTimeOffset.UtcNow));
        output.Waiting();
        Assert.Equal(2, writer.ToString().Split("Waiting for work...", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task InteractiveProgressFinalizesTheSpinnerWhenCancelled()
    {
        var writer = new StringWriter();
        var output = new WorkerConsole(writer, interactive: true, errorWriter: writer);
        using var cancellation = new CancellationTokenSource();
        var operation = output.RunProgressAsync("Codex working", async () =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            return true;
        }, ct: cancellation.Token);
        cancellation.CancelAfter(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Contains("\u001b[2K", writer.ToString());
        Assert.Contains("Codex working failed", writer.ToString());
    }

    [Fact]
    public void TelegramFormatUsesCentralizedCompactSpanishLayout()
    {
        var message = TelegramNotifier.Format("Example Project", "COMPLETADA", ["#3 · Add config", "Duración: 01:17"]);
        Assert.StartsWith("📥 CODEX WORKER · EXAMPLE PROJECT\n════════════\n>> COMPLETADA", message);
        Assert.Contains("│ #3 · Add config", message);
        Assert.Contains("│ Duración: 01:17", message);
    }

    [Fact]
    public async Task TelegramDisabledDoesNotAttemptDelivery()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(false, null, null, client, new WorkerConsole(new StringWriter(), false));
        await telegram.StartedAsync("Example", CancellationToken.None);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void EnabledTelegramRequiresBothEnvironmentValuesWithoutPrintingThem()
    {
        var error = Assert.Throws<InvalidDataException>(() => TelegramNotifier.ValidateConfiguration(true, null, ""));
        Assert.Contains("TELEGRAM_BOT_TOKEN", error.Message);
        Assert.Contains("TELEGRAM_CHAT_ID", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task TelegramDeliveryFailureIsWarningAndDoesNotThrow()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);
        var writer = new StringWriter();
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(writer, false));
        await telegram.StartedAsync("Example", CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("Telegram notification failed with HTTP 500", writer.ToString());
        Assert.DoesNotContain("token", writer.ToString());
    }

    [Fact]
    public async Task CompletionNotificationCarriesDurationAndCommitDetails()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(new StringWriter(), false));
        await telegram.SuccessAsync("Example", new GitHubIssue(3, "Add config", "", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(77), "Committed as `abcdef123456`. Merged into `main`.", CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        var message = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Duración: 01:17", message);
        Assert.Contains("Commit: abcdef123456", message);
        Assert.Contains("Integrada en: main", message);
    }

    [Fact]
    public async Task GracefulStopNotificationUsesStoppedStatus()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(new StringWriter(), false));
        await telegram.StoppedAsync("Example", "Cancellation received", CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Contains(">> DETENIDO", body.RootElement.GetProperty("text").GetString());
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }
}
