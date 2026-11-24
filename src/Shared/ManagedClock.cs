namespace CodexProvisioning;

/// <summary>How far a Worker clock may drift from the Server's before Server-issued deadlines are distrusted.
/// The Server flags larger skew on the Worker; Workers accept deadlines within it instead of failing.</summary>
public static class ManagedClock
{
    public const int ToleranceSeconds = 60;
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(ToleranceSeconds);
}
