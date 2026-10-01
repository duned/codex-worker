namespace CodexProvisioning;

/// <summary>Lifecycle values accepted by the managed Worker heartbeat contract.</summary>
public static class WorkerLifecycleStates
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string NotReady = "not-ready";
    public const string Draining = "draining";
    public const string DrainRequested = "drain-requested";
    public const string Drained = "drained";
    public const string Updating = "updating";
    public const string UpdateFailed = "update-failed";
    public const string Updated = "updated";
    public const string Restarting = "restarting";
    public const string RestartFailed = "restart-failed";
    public const string Reconnecting = "reconnecting";
    public const string CapabilityRegression = "capability-regression";
    public const string ConfigurationIncompatible = "configuration-incompatible";
    public const string Ready = "ready";
    public const string Stopped = "stopped";

    public static bool IsValid(string? value) => value is Starting or Running or NotReady or Draining or
        DrainRequested or Drained or Updating or UpdateFailed or Updated or Restarting or RestartFailed or
        Reconnecting or CapabilityRegression or ConfigurationIncompatible or Ready or Stopped;
}
