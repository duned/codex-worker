using System.Net;
using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class OperabilityTests
{
    [Fact]
    public void InteractiveStartupClearsTerminalBeforeHeader()
    {
        var writer = new StringWriter();
        new WorkerConsole(writer, interactive: true).Startup("Example", "owner/repository");
        Assert.StartsWith("\u001b[2J\u001b[H", writer.ToString());
        Assert.Contains("CODEX WORKER · Example", writer.ToString());
    }

    [Fact]
    public void RedirectedStartupDoesNotEmitClearScreenOrAnsi()
    {
        var writer = new StringWriter();
        new WorkerConsole(writer, interactive: false).Startup("Example", "owner/repository");
        Assert.DoesNotContain("\u001b[", writer.ToString());
    }

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
    public async Task InteractivePreflightProgressSpinsAndFinalizesWithElapsedTime()
    {
        var writer = new StringWriter();
        var output = new WorkerConsole(writer, interactive: true);
        await output.RunProgressAsync("Codex preflight", async () =>
        {
            await Task.Delay(240);
            return true;
        });
        var text = writer.ToString();
        Assert.Contains("Codex preflight...", text);
        Assert.Contains("\u001b[2K", text);
        Assert.Contains("✓ Codex preflight OK ·", text);
    }

    [Fact]
    public async Task IdleSpinnerShowsElapsedTimeAndRestartsForANewIdlePeriod()
    {
        var writer = new StringWriter();
        var output = new WorkerConsole(writer, interactive: true);
        output.Waiting();
        for (var i = 0; i < 30 && !writer.ToString().Contains("00:01", StringComparison.Ordinal); i++)
            await Task.Delay(100);
        await output.StopWaitingAsync();
        Assert.Contains("Waiting for work... 00:01", writer.ToString());

        writer.GetStringBuilder().Clear();
        output.Waiting();
        await output.StopWaitingAsync();
        Assert.Contains("Waiting for work... 00:00", writer.ToString());
        Assert.Contains("\u001b[2K", writer.ToString());
    }

    [Fact]
    public void TelegramFormatUsesCentralizedCompactSpanishLayout()
    {
        var message = TelegramNotifier.Format("✅ TAREA COMPLETADA · #3\nAdd config\n\nDuración: 01:17");
        Assert.StartsWith("✅ TAREA COMPLETADA · #3\nAdd config", message);
        Assert.Contains("Duración: 01:17", message);
        Assert.DoesNotContain("════════════", message);
        Assert.DoesNotContain(">>", message);
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
    public async Task TelegramLifecycleNotificationsUseSimpleSemanticFirstLines()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(new StringWriter(), false));
        var issue = new GitHubIssue(5, "Add application uptime endpoint", "", DateTimeOffset.UtcNow);
        await telegram.StartedAsync("Codex Worker Test", CancellationToken.None);
        await telegram.StartingAsync("Codex Worker Test", issue, CancellationToken.None);
        await telegram.BlockedAsync("Codex Worker Test", issue, TimeSpan.FromMinutes(1), "Which endpoint path?", CancellationToken.None);
        await telegram.FailedAsync("Codex Worker Test", issue, TimeSpan.FromMinutes(2), "Validation failed", CancellationToken.None);
        await telegram.CriticalAsync("Codex Worker Test", "Git integration state is uncertain", CancellationToken.None);

        Assert.StartsWith("🟢 CODEX WORKER · INICIADO\nCodex Worker Test", MessageAt(0));
        Assert.StartsWith("▶️ TAREA INICIADA · #5\nAdd application uptime endpoint\nCodex Worker Test", MessageAt(1));
        Assert.StartsWith("🟡 TAREA BLOQUEADA · #5\nAdd application uptime endpoint", MessageAt(2));
        Assert.Contains("Which endpoint path?", MessageAt(2));
        Assert.StartsWith("❌ TAREA FALLIDA · #5\nAdd application uptime endpoint", MessageAt(3));
        Assert.Contains("Duración: 02:00", MessageAt(3));
        Assert.StartsWith("🚨 CODEX WORKER · INFRAESTRUCTURA\nCodex Worker Test\n\nWorker detenido", MessageAt(4));
        Assert.All(handler.Bodies, body =>
        {
            Assert.DoesNotContain("════════════", body);
            Assert.DoesNotContain(">>", body);
        });

        string MessageAt(int index) => JsonDocument.Parse(handler.Bodies[index]).RootElement.GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task CompletionNotificationCarriesDurationAndCommitDetails()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(new StringWriter(), false));
        await telegram.SuccessAsync("Example", new GitHubIssue(3, "Add config", "", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(77), "Committed as `abcdef123456`. Merged into `main`.\n\nCodex summary: Added the uptime endpoint with focused coverage.", CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        var message = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Duración: 01:17", message);
        Assert.Contains("Commit: abcdef123456", message);
        Assert.Contains("Integrada en: main", message);
        Assert.Contains("Added the uptime endpoint with focused coverage.", message);
        Assert.StartsWith("✅ TAREA COMPLETADA · #3\nAdd config", message);
        Assert.DoesNotContain("════════════", message);
    }

    [Fact]
    public async Task GracefulStopNotificationUsesStoppedStatus()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(new StringWriter(), false));
        await telegram.StoppedAsync("Example", CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        var message = body.RootElement.GetProperty("text").GetString()!;
        Assert.StartsWith("⚫ CODEX WORKER · DETENIDO\nExample", message);
        Assert.DoesNotContain("Cancellation received", message);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(Body);
            return new HttpResponseMessage(status);
        }
    }
}
