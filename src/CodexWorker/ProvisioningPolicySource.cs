namespace CodexWorker;

/// <summary>The Worker's local provisioning policy, read again from its configuration file when the file changes,
/// so <c>codex-worker config add</c> takes effect without a restart. An invalid file keeps the last valid policy.</summary>
internal sealed class ProvisioningPolicySource(GlobalWorkerConfiguration configuration, Action<string> log)
{
    private DateTime? _observedWriteUtc = configuration.SourcePath is { } path && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    private DateTime? _rejectedWriteUtc;
    // Read by the heartbeat loop and the scheduler loop.
    private readonly Lock _gate = new();

    public ProvisioningPolicy Current
    {
        get
        {
            lock (_gate) return Refresh();
        }
    }

    private ProvisioningPolicy Refresh()
    {
        if (configuration.SourcePath is not { } path || !File.Exists(path)) return configuration.Worker.Provisioning;
        var written = File.GetLastWriteTimeUtc(path);
        if (written == _observedWriteUtc || written == _rejectedWriteUtc) return configuration.Worker.Provisioning;
        try
        {
            var reloaded = GlobalWorkerConfiguration.Load(path).Worker.Provisioning;
            _observedWriteUtc = written;
            if (!SamePolicy(reloaded, configuration.Worker.Provisioning))
            {
                configuration.Worker.Provisioning = reloaded;
                log("Provisioning policy reloaded from the Worker configuration file; it applies to the next provisioning or maintenance request.");
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _rejectedWriteUtc = written;
            log($"The changed Worker configuration file is invalid; the previous provisioning policy stays in effect. {error.Message}");
        }
        return configuration.Worker.Provisioning;
    }

    private static bool SamePolicy(ProvisioningPolicy left, ProvisioningPolicy right) =>
        left.Enabled == right.Enabled && left.AllowNonPrivileged == right.AllowNonPrivileged && left.AllowCredentials == right.AllowCredentials &&
        left.AllowedPrivilegedActions.SequenceEqual(right.AllowedPrivilegedActions, StringComparer.OrdinalIgnoreCase) &&
        left.DeniedActions.SequenceEqual(right.DeniedActions, StringComparer.OrdinalIgnoreCase);
}
