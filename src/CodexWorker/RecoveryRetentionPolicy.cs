namespace CodexWorker;

/// <summary>Deterministic expiry rules for temporary recovery resources; execution history is never expired here.</summary>
public static class RecoveryRetentionPolicy
{
    public static DateTimeOffset ExpiresAt(ExecutionHistoryEntry entry, TimeSpan retention) =>
        entry.RecoveryExpiresAtUtc ?? (entry.CompletedAtUtc ?? entry.StartedAtUtc).Add(retention);

    public static bool IsExpired(ExecutionHistoryEntry entry, TimeSpan retention, DateTimeOffset now) =>
        ExpiresAt(entry, retention) <= now;
}
