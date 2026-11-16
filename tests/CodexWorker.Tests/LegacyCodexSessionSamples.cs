using System.Text.Json;

namespace CodexWorker.Tests;

/// <summary>Sample legacy Codex session data shared by the test assemblies.</summary>
internal static class LegacyCodexSessionSamples
{
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
