namespace CodexProvisioning;

public sealed record EngineQuotaWindow(int? DurationMinutes, double? RemainingPercent, DateTimeOffset? ResetsAtUtc);

/// <summary>A Worker's latest observation of an engine account's usage windows, sent with its heartbeat.
/// Contains no account identifiers. Status is "available" when the windows were read.</summary>
public sealed record EngineQuota(string Engine, DateTimeOffset ObservedAtUtc, string Status, IReadOnlyList<EngineQuotaWindow> Windows)
{
    /// <summary>Below this share of any window, new work for the engine is not assigned until the window resets.</summary>
    public const double ReservePercent = 5;
    /// <summary>Older observations say nothing about the current allowance.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    public static bool Valid(IReadOnlyList<EngineQuota>? quotas) => quotas is null || quotas.Count <= 4 && quotas.All(quota =>
        quota is not null && quota.Engine is "codex" or "ccc" && quota.ObservedAtUtc != default &&
        !string.IsNullOrWhiteSpace(quota.Status) && quota.Status.Length <= 64 && !quota.Status.Any(char.IsControl) &&
        quota.Windows is { Count: <= 8 } && quota.Windows.All(window => window is not null &&
            window.DurationMinutes is null or > 0 and <= 527040 &&
            (window.RemainingPercent is null || double.IsFinite(window.RemainingPercent.Value) && window.RemainingPercent is >= 0 and <= 100)));

    /// <summary>The window that blocks new work, when a fresh observation shows one at or below the reserve
    /// that has not reset yet. Unknown or stale observations never block: the engine reports its own limit.</summary>
    public static EngineQuotaWindow? ExhaustedWindow(IReadOnlyList<EngineQuota>? quotas, string engine, DateTimeOffset now)
    {
        var quota = quotas?.FirstOrDefault(item => string.Equals(item.Engine, engine, StringComparison.OrdinalIgnoreCase));
        if (quota is null || quota.Status != "available" || now - quota.ObservedAtUtc > Freshness) return null;
        return quota.Windows.Where(window => window.RemainingPercent <= ReservePercent && (window.ResetsAtUtc is null || window.ResetsAtUtc > now))
            .OrderByDescending(window => window.ResetsAtUtc ?? DateTimeOffset.MaxValue).FirstOrDefault();
    }
}
