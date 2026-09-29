using System.Net;
using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class OperabilityTests
{
    [Fact]
    public void ProcessExitCodesDistinguishGracefulStartupAndRuntimeOutcomes()
    {
        Assert.Equal(ProcessExitCodes.Success, Program.ExitCodeFor(null));
        Assert.Equal(ProcessExitCodes.StartupFailure, Program.ExitCodeFor(new WorkerStartupException("preflight failed")));
        Assert.Equal(ProcessExitCodes.RuntimeFailure, Program.ExitCodeFor(new WorkerInfrastructureException("runtime failed")));
    }

    [Fact]
    public void InteractiveStartupClearsTerminalBeforeHeader()
    {
        var writer = new StringWriter();
        new WorkerConsole(writer, interactive: true).Startup("Example", "owner/repository");
        Assert.StartsWith("\u001b[2J\u001b[H", writer.ToString());
        Assert.Contains($"CODEX WORKER v{ApplicationVersion.Display} · Example", writer.ToString());
    }

    [Fact]
    public void RedirectedStartupDoesNotEmitClearScreenOrAnsi()
    {
        var writer = new StringWriter();
        new WorkerConsole(writer, interactive: false).Startup("Example", "owner/repository");
        Assert.DoesNotContain("\u001b[", writer.ToString());
    }

    [Fact]
    public void MultiProjectStartupHeaderUsesAssemblyVersion()
    {
        var writer = new StringWriter();
        new WorkerConsole(writer, interactive: false).Startup(2);
        Assert.Contains($"CODEX WORKER v{ApplicationVersion.Display}", writer.ToString());
        var assemblyVersion = typeof(WorkerConsole).Assembly.GetName().Version!;
        Assert.Equal($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}", ApplicationVersion.Display);
    }

    [Fact]
    public async Task GracefulIdleFinalizationKeepsInteractiveLineAndPlacesShutdownBelowIt()
    {
        var writer = new StringWriter();
        var console = new WorkerConsole(writer, interactive: true);
        console.Waiting();
        await Task.Delay(30);
        await console.StopWaitingAsync(finalizeLine: true);
        console.Shutdown();

        var output = writer.ToString();
        var idleLine = output.LastIndexOf("Waiting for work...", StringComparison.Ordinal);
        var stopped = output.IndexOf("■ Worker stopped.", idleLine, StringComparison.Ordinal);
        Assert.True(idleLine >= 0 && stopped > idleLine, output);
        Assert.Contains("\n■ Worker stopped.", output[idleLine..]);
        Assert.DoesNotContain("\r\u001b[2K", output[idleLine..stopped]);
    }

    [Fact]
    public async Task RedirectedIdleFinalizationRemainsPlainLineBasedOutput()
    {
        var writer = new StringWriter();
        var console = new WorkerConsole(writer, interactive: false, errorWriter: writer);
        console.Waiting();
        await console.StopWaitingAsync(finalizeLine: true);
        console.Shutdown();
        var output = writer.ToString();
        Assert.Equal("○ Waiting for work..." + Environment.NewLine + "■ Worker stopped." + Environment.NewLine, output);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\r', output);
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
        Assert.Contains("✓ Issue · Add config #3 · completed · 01:17", text);
        Assert.Contains("✗ Issue · Fail #4 · failed", text);
        Assert.Contains("⚠ Issue · Needs input #5 · blocked", text);
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
        // Let the spinner cross the one-second display boundary without reading
        // StringWriter concurrently with its background writer task.
        await Task.Delay(1_300);
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
        await telegram.StartingAsync("Codex Worker Test", "duned/codex-worker", issue, CancellationToken.None);
        await telegram.BlockedAsync("Codex Worker Test", "duned/codex-worker", issue, TimeSpan.FromMinutes(1), "Which endpoint path?", CancellationToken.None);
        await telegram.FailedAsync("Codex Worker Test", "duned/codex-worker", issue, TimeSpan.FromMinutes(2), "Validation failed", CancellationToken.None);
        await telegram.CriticalAsync("Codex Worker Test", "Git integration state is uncertain", CancellationToken.None);
        var retry = WorkerExecution.Create(new ProjectSettings { Name = "Codex Worker Test", Repository = "duned/codex-worker" },
            new GitSettings(), issue, retryOfExecutionId: Guid.Parse("0b3fdff5-0000-0000-0000-000000000000"),
            attemptNumber: 2, resumed: true);
        await telegram.StartingAsync("Codex Worker Test", "duned/codex-worker", issue, retry, CancellationToken.None);

        Assert.StartsWith($"🟢 CW {ApplicationVersion.Display} · INICIADO\nCodex Worker Test", MessageAt(0));
        Assert.StartsWith($"▶ CW {ApplicationVersion.Display} · CODEX WORKER TEST · TAREA INICIADA\n<a href=\"https://github.com/duned/codex-worker/issues/5\">Add application uptime endpoint #5</a>", MessageAt(1));
        Assert.StartsWith($"🟡 CW {ApplicationVersion.Display} · CODEX WORKER TEST · TAREA BLOQUEADA\n<a href=\"https://github.com/duned/codex-worker/issues/5\">Add application uptime endpoint #5</a>", MessageAt(2));
        Assert.Contains("Which endpoint path?", MessageAt(2));
        Assert.StartsWith($"❌ CW {ApplicationVersion.Display} · CODEX WORKER TEST · TAREA FALLIDA\n<a href=\"https://github.com/duned/codex-worker/issues/5\">Add application uptime endpoint #5</a>", MessageAt(3));
        Assert.Contains("Duración: 02:00", MessageAt(3));
        Assert.StartsWith($"🚨 CW {ApplicationVersion.Display} · INFRAESTRUCTURA\nProyecto: Codex Worker Test\nWorker detenido", MessageAt(4));
        Assert.StartsWith($"▶ CW {ApplicationVersion.Display} · CODEX WORKER TEST · TAREA REANUDADA\n", MessageAt(5));
        Assert.Contains("Intento 2 · resume · ejecución", MessageAt(5));
        Assert.Contains("Ejecución anterior: 0b3fdff5", MessageAt(5));
        Assert.DoesNotContain(retry.ExecutionId.ToString(), MessageAt(5));
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
        await telegram.SuccessAsync("Example", "owner/repo", new GitHubIssue(3, "Add config", "", DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(77), "Committed as `abcdef123456`. Merged into `main`. Preserved on origin as `completed/3`.\n\nCodex summary: Added the uptime endpoint with focused coverage.", CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        var message = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Duración: 01:17", message);
        Assert.Contains("Commit: abcdef123456", message);
        Assert.Contains("Integrada en: main", message);
        Assert.Contains("Added the uptime endpoint with focused coverage.", message);
        Assert.Contains("Rama completada preservada: completed/3", message);
        Assert.StartsWith($"✅ CW {ApplicationVersion.Display} · EXAMPLE · TAREA COMPLETADA\n<a href=\"https://github.com/owner/repo/issues/3\">Add config #3</a>", message);
        Assert.DoesNotContain("════════════", message);
    }

    [Fact]
    public async Task TaskIssueLinkEscapesCompleteIssueTitleForTelegramHtml()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(new StringWriter(), false));
        const string title = "Add <uptime> & \"health\" endpoint with a complete descriptive title";

        await telegram.StartingAsync("Example", "owner/repo", new GitHubIssue(8, title, "", DateTimeOffset.UtcNow), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        var message = body.RootElement.GetProperty("text").GetString()!;
        Assert.Equal("HTML", body.RootElement.GetProperty("parse_mode").GetString());
        Assert.Contains("https://github.com/owner/repo/issues/8", message);
        Assert.Contains("Add &lt;uptime&gt; &amp; &quot;health&quot; endpoint with a complete descriptive title #8", message);
        Assert.DoesNotContain("#8 · Add <uptime>", message);
    }

    [Fact]
    public async Task ConsoleTaskLifecycleLinesUseIssueIdentityAndKeepMarkdownOutOfOperationalOutput()
    {
        var writer = new StringWriter();
        var issue = new GitHubIssue(7, "V0.5 · P1 · Introduce execution identity and lifecycle state", "", DateTimeOffset.UtcNow);
        var console = new WorkerConsole(writer, interactive: false, errorWriter: writer);
        const string summary = "## Implementation\n\nImplemented X.\n\n## Validation\n\nTests passed.";

        console.IssueStarted("Finance", issue);
        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(issue)} · Codex working", () => Task.FromResult(true), _ => "success");
        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(issue)} · Validation", () => Task.FromResult(true), _ => "passed");
        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(issue)} · Repair 1/2", () => Task.FromResult(true), _ => "success");
        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(issue)} · Integrating", () => Task.FromResult(true), _ => "complete");
        console.IssueCompleted("Finance", issue, TimeSpan.FromSeconds(12), summary);
        console.IssueBlocked("Finance", issue, TimeSpan.FromSeconds(13), summary);
        console.IssueFailed("Finance", issue, TimeSpan.FromSeconds(14), summary);

        var lines = writer.ToString();
        Assert.Contains($"▶ Issue · {issue.Title} #7", lines);
        Assert.Contains($"▶ Issue · {issue.Title} #7 · Codex working...", lines);
        Assert.Contains($"✓ Issue · {issue.Title} #7 · Codex working OK · 0s · success", lines);
        Assert.Contains($"▶ Issue · {issue.Title} #7 · Validation...", lines);
        Assert.Contains($"✓ Issue · {issue.Title} #7 · Validation OK · 0s · passed", lines);
        Assert.Contains($"▶ Issue · {issue.Title} #7 · Repair 1/2...", lines);
        Assert.Contains($"✓ Issue · {issue.Title} #7 · Repair 1/2 OK · 0s · success", lines);
        Assert.Contains($"▶ Issue · {issue.Title} #7 · Integrating...", lines);
        Assert.Contains($"✓ Issue · {issue.Title} #7 · Integrating OK · 0s · complete", lines);
        Assert.Contains($"✓ Issue · {issue.Title} #7 · completed · 12s", lines);
        Assert.Contains($"⚠ Issue · {issue.Title} #7 · blocked · 13s", lines);
        Assert.Contains($"✗ Issue · {issue.Title} #7 · failed · 14s", lines);
        Assert.DoesNotContain("## Implementation", lines);
        Assert.DoesNotContain("## Validation", lines);
        Assert.DoesNotContain("Implemented X.", lines);
        Assert.DoesNotContain("\u001b[", lines);
    }

    [Fact]
    public async Task InterleavedProgressEventsKeepEachIssueContext()
    {
        var writer = new StringWriter();
        var console = new WorkerConsole(writer, interactive: false);
        var first = new GitHubIssue(42, "Finance task", "", DateTimeOffset.UtcNow);
        var second = new GitHubIssue(31, "Worker task", "", DateTimeOffset.UtcNow);

        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(first)} · Validation", () => Task.FromResult(true));
        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(second)} · Codex working", () => Task.FromResult(true));
        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(first)} · Validation", () => Task.FromResult(true));
        await console.RunProgressAsync($"{IssueFormatting.OperationalIdentity(second)} · Codex working", () => Task.FromResult(true));

        var lines = writer.ToString();
        Assert.Contains("▶ Issue · Finance task #42 · Validation...", lines);
        Assert.Contains("✓ Issue · Finance task #42 · Validation OK", lines);
        Assert.Contains("▶ Issue · Worker task #31 · Codex working...", lines);
        Assert.Contains("✓ Issue · Worker task #31 · Codex working OK", lines);
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
        Assert.StartsWith($"⚫ CW {ApplicationVersion.Display} · DETENIDO\nExample", message);
        Assert.DoesNotContain("Cancellation received", message);
    }

    [Fact]
    public async Task GlobalLifecycleNotificationsUseProjectCountAndNoProjectIdentity()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var telegram = new TelegramNotifier(true, "token", "chat", client, new WorkerConsole(new StringWriter(), false));
        await telegram.StartedAsync(2, CancellationToken.None);
        await telegram.StoppedAsync(2, CancellationToken.None);
        using var start = JsonDocument.Parse(handler.Bodies[0]);
        using var stop = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal($"🟢 CW {ApplicationVersion.Display} · INICIADO\n2 proyectos cargados", start.RootElement.GetProperty("text").GetString());
        Assert.Equal($"⚫ CW {ApplicationVersion.Display} · DETENIDO", stop.RootElement.GetProperty("text").GetString());
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
