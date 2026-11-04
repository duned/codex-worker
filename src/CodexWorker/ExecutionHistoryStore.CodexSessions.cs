using System.Security.Cryptography;
using System.Text;

namespace CodexWorker;

/// <summary>Identifies the node, service account and Codex store that own a local conversation.</summary>
internal static class CodexSessionOwnership
{
    internal static string Current => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(new { Environment.MachineName, Environment.UserName,
            Home = Path.GetFullPath(CodexProvisioning.CodexServiceEnvironment.Home) }))));
}

public sealed partial class ExecutionHistoryStore
{
    /// <summary>Finds a blocker's latest authoritative attempt, never falling back past an ineligible attempt.
    /// The caller must verify availability in the owning Codex session store before inheriting it.
    /// This lookup grants no scheduling or managed lease authority.</summary>
    public async Task<ExecutionHistoryEntry?> FindInheritedCodexSessionAsync(string project, string repository,
        int blockerIssueNumber, int parentIssueNumber, string sessionOwner,
        Func<string, CancellationToken, Task<bool>> sessionAvailable, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionOwner);
        ArgumentNullException.ThrowIfNull(sessionAvailable);
        if (blockerIssueNumber <= 0 || parentIssueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(blockerIssueNumber));
        var latest = (await ReadAsync(" WHERE project=$project AND repository=$repository COLLATE NOCASE AND issue_number=$issue " +
            "ORDER BY attempt_number DESC, started_at_utc DESC, execution_id DESC LIMIT 1", null, null, ct,
            ("$project", project), ("$repository", repository), ("$issue", blockerIssueNumber))).SingleOrDefault();
        if (latest is not { State: "Completed", CompletedAtUtc: not null, Engine: "codex", CodexThreadId: { } thread } ||
            latest.ParentIssueNumber != parentIssueNumber || latest.CodexSessionOwner != sessionOwner ||
            !Guid.TryParse(thread, out var id) || id == Guid.Empty ||
            !await sessionAvailable(thread, ct)) return null;
        // A completed blocker does not certify later turns on the same mutable conversation.
        // Retain this check across host restarts as well as the in-memory turn gate.
        var turns = await ReadAsync(" WHERE codex_thread_id=$thread COLLATE NOCASE AND codex_session_owner=$owner",
            null, null, ct, ("$thread", thread), ("$owner", sessionOwner));
        if (turns.Any(turn => turn.Project != project ||
            !string.Equals(turn.Repository, repository, StringComparison.OrdinalIgnoreCase) ||
            turn.ParentIssueNumber != parentIssueNumber || turn.Engine != "codex" ||
            turn.State is "Failed" or "Blocked" or "Cancelled" or "InfrastructureFailure" ||
            turn.CodexRecovery is { ImplementationSucceeded: false } ||
            // Older or incomplete records cannot certify that an active turn finished.
            // Completed executions remain valid without interruption-recovery metadata.
            (!(turn.State == "Completed" && turn.CompletedAtUtc is not null) &&
                turn.CodexRecovery?.ImplementationSucceeded != true))) return null;
        return latest;
    }
}
