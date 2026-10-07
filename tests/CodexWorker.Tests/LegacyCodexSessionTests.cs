using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class LegacyCodexSessionTests
{
    [Theory]
    [InlineData("root", null)]
    [InlineData("child", null)]
    [InlineData("none", "no root")]
    [InlineData("ambiguous", "multiple root")]
    [InlineData("branch", "no root")]
    [InlineData("commit", "no root")]
    [InlineData("cwd", "no root")]
    [InlineData("time", "no root")]
    [InlineData("partial", "malformed")]
    [InlineData("json", "malformed")]
    [InlineData("oversized", "malformed")]
    [InlineData("duplicate", "malformed")]
    [InlineData("parent", "no root")]
    [InlineData("empty", "malformed")]
    [InlineData("repository", "no root")]
    public async Task DiscoveryCorrelatesOnlyUnambiguousStructuredRootMetadata(string scenario, string? rejection)
    {
        var home = Path.Combine(Path.GetTempPath(), "legacy-session-tests", Guid.NewGuid().ToString("N"));
        var date = Path.Combine(home, "sessions", "2026", "01", "02");
        Directory.CreateDirectory(date);
        try
        {
            var source = Source();
            var workspace = new LegacyWorkspaceEvidence(Path.Combine(home, "workspace"), new string('a', 40));
            var id = Guid.NewGuid();
            if (scenario != "none")
                await File.WriteAllTextAsync(Path.Combine(date, "rollout-root.jsonl"), scenario switch
                {
                    "json" => "{partial-private-value",
                    "empty" => "",
                    "oversized" => new string('x', 65537),
                    "duplicate" => "{\"type\":\"session_meta\",\"type\":\"session_meta\",\"payload\":{}}",
                    "parent" => Header(id, workspace.Directory, source.FeatureBranch, workspace.Head, source.StartedAtUtc.AddMinutes(1))
                        .Replace("\"source\":\"exec\"", "\"source\":\"exec\",\"parent_thread_id\":\"parent\"", StringComparison.Ordinal),
                    "repository" => Header(id, workspace.Directory, source.FeatureBranch, workspace.Head, source.StartedAtUtc.AddMinutes(1))
                        .Replace("\"commit_hash\":", "\"repository_url\":\"https://github.com/other/repo\",\"commit_hash\":", StringComparison.Ordinal),
                    _ => Header(id, scenario == "cwd" ? workspace.Directory + "-other" : workspace.Directory,
                        scenario == "branch" ? "feature/other" : source.FeatureBranch,
                        scenario == "commit" ? new string('b', 40) : workspace.Head,
                        scenario == "time" ? source.StartedAtUtc.AddMinutes(-1) : source.StartedAtUtc.AddMinutes(1),
                        partial: scenario == "partial") + "\n{\"secret\":\"private-value\",\"text\":\"prior work and tests completed\"}"
                });
            if (scenario is "child" or "ambiguous")
                await File.WriteAllTextAsync(Path.Combine(date, "rollout-second.jsonl"), Header(Guid.NewGuid(), workspace.Directory,
                    source.FeatureBranch, workspace.Head, source.StartedAtUtc.AddMinutes(2), child: scenario == "child"));
            var result = await new LegacyCodexSessionStore(home).FindAsync(source, workspace, CancellationToken.None);
            if (rejection is null)
            {
                Assert.Equal(id.ToString(), result.SessionId);
                Assert.Null(result.Rejection);
            }
            else
            {
                Assert.Null(result.SessionId);
                Assert.Contains(rejection, result.Rejection, StringComparison.Ordinal);
                Assert.DoesNotContain("private-value", result.Rejection, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(home, true); }
    }

    internal static ExecutionHistoryEntry Source() => new(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
        "feature/17-example-task", "main", new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 2, 11, 0, 0, TimeSpan.Zero), "InfrastructureFailure", null, null, null, 0, [],
        null, null, null, "Codex usage limit", RecoveryState: "uncertain", OriginalIssueBody: "Implement this request", EffectiveEffort: "medium");

    internal static string Header(Guid id, string cwd, string branch, string commit, DateTimeOffset timestamp,
        bool child = false, bool partial = false) => JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new
            {
                id, cwd, timestamp, originator = "codex_exec", cli_version = "0.123.0",
                source = child ? (object)new { subagent = new { guardian = true, parent_thread_id = Guid.NewGuid() } } : "exec",
                git = partial ? null : new { branch, commit_hash = commit }
            }
        });
}
