namespace CodexWorker.Tests;

using System.Text.Json;
using CodexProvisioning;
using CodexWorker;

public sealed class ExecutionMetricsTests
{
    [Fact]
    public void CodexTurnUsageIsSummedAndOlderCumulativeEventsAreUsedAlone()
    {
        const string current = """
            [output truncated; showing tail]
            {"type":"thread.started","thread_id":"t"}
            {"type":"turn.completed","usage":{"input_tokens":1000,"cached_input_tokens":600,"output_tokens":50}}
            {"type":"turn.completed","usage":{"input_tokens":500,"cached_input_tokens":100,"output_tokens":20}}
            """;
        Assert.Equal(new EngineUsage(1500, 700, 70), EngineUsageParser.FromCodexEvents(current));
        const string older = """
            {"id":"0","msg":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":1}}}}
            {"id":"0","msg":{"type":"token_count","info":{"total_token_usage":{"input_tokens":300,"cached_input_tokens":200,"output_tokens":9}}}}
            """;
        Assert.Equal(new EngineUsage(300, 200, 9), EngineUsageParser.FromCodexEvents(older));
        Assert.Null(EngineUsageParser.FromCodexEvents("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":-1}}\nnot json"));
        Assert.Null(EngineUsageParser.FromCodexEvents(null));
    }

    [Fact]
    public void ClaudeUsageCountsCacheAsInputAndKeepsItsCostEstimate()
    {
        using var document = JsonDocument.Parse("""
            {"type":"result","total_cost_usd":0.1234567,"usage":{"input_tokens":20,"cache_read_input_tokens":3000,"cache_creation_input_tokens":500,"output_tokens":400}}
            """);
        Assert.Equal(new EngineUsage(3520, 3000, 400, 0.123457m), EngineUsageParser.FromClaudeResult(document.RootElement));
        using var empty = JsonDocument.Parse("""{"type":"result"}""");
        Assert.Null(EngineUsageParser.FromClaudeResult(empty.RootElement));
    }

    [Fact]
    public void UsageAddsAcrossRunsAndCostStaysUnknownWhenNoRunReportsIt()
    {
        var codex = new EngineUsage(100, 10, 5).Add(new EngineUsage(50, 5, 1));
        Assert.Equal(new EngineUsage(150, 15, 6, null, 2), codex);
        Assert.Equal(0.3m, new EngineUsage(CostUsd: 0.1m).Add(new EngineUsage(CostUsd: 0.2m)).CostUsd);
        Assert.False(ExecutionMetrics.Valid(new(new EngineUsage(-1))));
        Assert.False(ExecutionChanges.Valid(new(1, 1, 0, ["a", "b"])));
    }

    [Fact]
    public async Task HistoryKeepsMetricsThroughCompletion()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "history.db");
        var started = DateTimeOffset.UtcNow;
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "p", "o/r", 1, "issue", "feature/1", "main",
            started, null, "Codex", null, null, null, 0, [], null, null, null, null);
        var metrics = new ExecutionMetrics(new EngineUsage(10, 0, 2), new ExecutionChanges(1, 3, 1, ["a.txt"], "abcdef1", "1234567"));
        using (var store = new ExecutionHistoryStore(path))
        {
            await store.CreateAsync(entry);
            await store.UpdateAsync(entry with { State = "Completed", CompletedAtUtc = started.AddMinutes(1), Metrics = metrics });
        }
        using var reopened = new ExecutionHistoryStore(path);
        Assert.Equivalent(metrics, (await reopened.ReadExecutionAsync(entry.ExecutionId))!.Metrics);
    }

    [Fact]
    public void NumstatCountsTextAndBinaryFilesAndBoundsTheFileList()
    {
        var output = "3\t1\tsrc/a.cs\n-\t-\timage.png\n10\t0\tdocs/{old => new}.md\n";
        var changes = GitRepository.ParseNumstat(output, "abcdef1", "1234567")!;
        Assert.Equal((3, 13, 1), (changes.FilesChanged, changes.Insertions, changes.Deletions));
        Assert.Equal(["src/a.cs", "image.png", "docs/{old => new}.md"], changes.Files);
        var many = string.Concat(Enumerable.Range(0, 80).Select(index => $"1\t0\tf{index}.txt\n"));
        var bounded = GitRepository.ParseNumstat(many, null, null)!;
        Assert.Equal((80, ExecutionChanges.MaximumFiles), (bounded.FilesChanged, bounded.Files.Count));
        Assert.True(ExecutionChanges.Valid(bounded));
    }
}
