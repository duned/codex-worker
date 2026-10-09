using CodexWorker;
using System.Text;
using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class CodexQuotaTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("primary", "secondary")]
    [InlineData("secondary", "primary")]
    public void WindowsAreIdentifiedByDuration(string five, string week)
    {
        var now = DateTimeOffset.UtcNow;
        var observation = CodexQuotaReader.Parse(Json($$$$"""
            {"rateLimits":{"{{{{five}}}}":{"usedPercent":25,"windowDurationMins":300,"resetsAt":2000000000},"{{{{week}}}}":{"usedPercent":60,"windowDurationMins":10080}}}
            """), now);
        Assert.Contains("5h: 75% remaining", observation.Summary(now));
        Assert.Contains("weekly: 40% remaining", observation.Summary(now));
        Assert.Contains("reset", observation.Summary(now));
        Assert.Equal(now, observation.ObservedAtUtc);
        Assert.Equal("unavailable (stale)", observation.Summary(now.AddMinutes(3)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void MissingSnapshotIsUnavailable(string snapshot)
    {
        var result = CodexQuotaReader.Parse(Json("{\"rateLimits\":" + snapshot + "}"), DateTimeOffset.UtcNow);
        Assert.Empty(result.Windows);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("null")]
    [InlineData("\"bad\"")]
    public void InvalidPercentNeverInventsRemaining(string percent)
    {
        var now = DateTimeOffset.UtcNow;
        var result = CodexQuotaReader.Parse(Json($$$$"""{"rateLimits":{"primary":{"usedPercent":{{{{percent}}}},"windowDurationMins":300},"secondary":null}}"""), now);
        Assert.Null(Assert.Single(result.Windows).RemainingPercent);
        Assert.Equal("5h: unavailable · weekly: unavailable", result.Summary(now));
    }

    [Theory]
    [InlineData(300, "5h: 90% remaining", "weekly: unavailable")]
    [InlineData(10080, "weekly: 90% remaining", "5h: unavailable")]
    [InlineData(60, "5h: unavailable", "weekly: unavailable")]
    public void MissingAndNewWindowsRemainDistinct(int duration, string present, string absent)
    {
        var now = DateTimeOffset.UtcNow;
        var result = CodexQuotaReader.Parse(Json($$$$"""{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":{{{{duration}}}}}}}"""), now);
        Assert.Equal(duration, Assert.Single(result.Windows).DurationMinutes);
        Assert.Contains(present, result.Summary(now));
        Assert.Contains(absent, result.Summary(now));
    }

    [Fact]
    public async Task ProtocolInitializesBeforeReadAndOmitsSensitiveFields()
    {
        using var responses = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{}}\n{\"id\":2,\"result\":{\"rateLimits\":null}}\n"));
        using var output = new StreamReader(responses);
        using var requests = new MemoryStream();
        using var input = new StreamWriter(requests, leaveOpen: true);
        await CodexQuotaTransport.QueryAsync(input, output, CancellationToken.None);
        var sent = Encoding.UTF8.GetString(requests.ToArray());
        Assert.True(sent.IndexOf("initialize", StringComparison.Ordinal) < sent.IndexOf("account/rateLimits/read", StringComparison.Ordinal));
        Assert.Contains("\"method\":\"initialized\"", sent);
    }

    [Fact]
    public async Task ConcurrentReadsReuseLocalObservation()
    {
        var transport = new ControlledTransport();
        var reader = new CodexQuotaReader(transport);
        var reads = Enumerable.Range(0, 20).Select(_ => reader.ReadAsync(CancellationToken.None)).ToArray();
        await transport.Entered.Task;
        transport.Release.SetResult();
        var results = await Task.WhenAll(reads);
        Assert.Equal(1, transport.Calls);
        Assert.All(results, observation => Assert.Same(results[0], observation));
    }

    [Theory]
    [InlineData(false, "query failed")]
    [InlineData(true, "timeout")]
    public async Task LookupFailuresReturnSanitizedUnavailable(bool timeout, string status)
    {
        var reader = new CodexQuotaReader(new FakeTransport { Failure = timeout ? new OperationCanceledException() : new IOException("private raw response") });
        var result = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(status, result.Status);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public async Task CancellationDoesNotThrow()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeTransport();
        var result = await new CodexQuotaReader(transport).ReadAsync(cancellation.Token);
        Assert.Equal("cancelled", result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public void JournalAndNotificationIncludeContextWithoutAccountFields()
    {
        var now = DateTimeOffset.UtcNow;
        var observation = CodexQuotaReader.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300},"planType":"private"}}"""), now);
        var execution = WorkerExecution.Create(new ProjectSettings { Name = "sample", Repository = "owner/repo" },
            new GitSettings { BaseBranch = "main", FeaturePrefix = "feature/" }, new GitHubIssue(17, "task", "", now));
        using var writer = new StringWriter();
        new WorkerConsole(writer, interactive: false).Quota(execution, "end", observation);
        Assert.Contains($"execution [{execution.ExecutionId}]", writer.ToString());
        Assert.Contains(observation.ObservedAtUtc.ToString("O"), writer.ToString());
        Assert.Contains("sample #17", writer.ToString());
        Assert.DoesNotContain("private", writer.ToString());
        var report = new IssueExecutionReport("done", [], QuotaAtEnd: observation);
        Assert.Contains("5h: 80% remaining", Worker.QuotaNotification(report));
        Assert.Equal("", Worker.QuotaNotification(report with { QuotaAtEnd = new(now, "query failed", []) }));
    }

    [Theory]
    [InlineData("{\"id\":1,\"error\":{\"message\":\"auth private\"}}\n")]
    [InlineData("{\"id\":1,\"result\":{}}\n{\"id\":2,\"error\":{\"code\":-32601}}\n")]
    public async Task ProtocolRejectsAuthAndUnsupportedWithoutRawDiagnostics(string response)
    {
        using var responses = new MemoryStream(Encoding.UTF8.GetBytes(response));
        using var output = new StreamReader(responses);
        using var requests = new MemoryStream();
        using var input = new StreamWriter(requests);
        var error = await Assert.ThrowsAsync<IOException>(() => CodexQuotaTransport.QueryAsync(input, output, CancellationToken.None));
        Assert.DoesNotContain("private", error.Message);
    }

    private sealed class ControlledTransport : ICodexQuotaTransport
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JsonElement> ReadAsync(CancellationToken ct)
        {
            Calls++;
            Entered.SetResult();
            await Release.Task.WaitAsync(ct);
            return Json("{\"rateLimits\":null}");
        }
    }

    private sealed class FakeTransport : ICodexQuotaTransport
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; init; }
        public Task<JsonElement> ReadAsync(CancellationToken ct)
        {
            Calls++;
            return Failure is { } failure ? Task.FromException<JsonElement>(failure) : Task.FromResult(Json("{\"rateLimits\":null}"));
        }
    }
}
