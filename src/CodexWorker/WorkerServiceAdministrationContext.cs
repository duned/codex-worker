namespace CodexWorker;

using System.Diagnostics;

/// <summary>Runs packaged node administration with the service's identity and protected environment.
/// systemd reads EnvironmentFile itself: secrets never become command arguments, and its quoting
/// rules remain identical to the installed service. This boundary is local, not a remote executor.</summary>
internal static class WorkerServiceAdministrationContext
{
    internal const string WorkerExecutable = "/opt/codex-worker/CodexWorker";
    internal const string EnvironmentFile = "/etc/codex-worker/worker.env";
    internal const string ServiceAccount = "codex-worker";
    internal static readonly IReadOnlyList<string> ServiceProperties = Array.AsReadOnly<string>([
        "User=codex-worker", "Group=codex-worker",
        "Environment=HOME=/var/lib/codex-worker",
        "Environment=PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        "Environment=TMPDIR=/run/codex-worker",
        "EnvironmentFile=" + EnvironmentFile,
        "WorkingDirectory=/var/lib/codex-worker",
        "RuntimeDirectory=codex-worker", "RuntimeDirectoryMode=0750",
        "RuntimeDirectoryPreserve=yes", "UMask=0077",
        "KillMode=mixed", "TimeoutStopSec=10", "RuntimeMaxSec=900"
    ]);

    internal static bool IsNodeAdministration(WorkerCommandLine command) =>
        command.Command is "provision" or "credential" or "capabilities" or "status" or "diagnostics";

    internal static bool RequiresTransition(WorkerCommandLine command, bool packaged, string user, bool serviceContext = false)
    {
        if (!packaged || !IsNodeAdministration(command) || user == ServiceAccount && serviceContext) return false;
        if (user != "root")
            throw new InvalidOperationException("Use sudo codex-worker for packaged node administration so the protected Worker service environment is loaded.");
        return true;
    }

    internal static ProcessStartInfo CreateStartInfo(WorkerCommandLine command)
    {
        if (!IsNodeAdministration(command)) throw new ArgumentException("Unsupported service administration command.", nameof(command));
        // Validate typed mutations before crossing the identity boundary. No executable, user,
        // shell text or systemd property can be supplied by a CLI caller.
        if (command.Command == "provision") _ = ProvisioningCli.Parse(command.Arguments);
        if (command.Command == "credential") _ = WorkerCredentialCli.Parse(command.Arguments);
        var start = new ProcessStartInfo("/usr/bin/systemd-run") { UseShellExecute = false };
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        foreach (var argument in new[] { "--quiet", "--pipe", "--wait", "--collect", "--service-type=exec" })
            start.ArgumentList.Add(argument);
        foreach (var property in ServiceProperties) start.ArgumentList.Add("--property=" + property);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(WorkerExecutable);
        start.ArgumentList.Add(command.Command);
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(Path.GetFullPath(command.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath));
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static async Task<int?> TryRunAsync(WorkerCommandLine command, CancellationToken token)
    {
        // Unpackaged/standalone installations retain their explicitly owned caller context.
        // Server-triggered operations already execute inside the running Worker service.
        // A missing/unreadable EnvironmentFile must fail in systemd, never fall back to
        // authenticating the administrator in a partially installed packaged deployment.
        if (!RequiresTransition(command, OperatingSystem.IsLinux() && File.Exists(WorkerExecutable), Environment.UserName,
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))) return null;
        using var process = new Process { StartInfo = CreateStartInfo(command) };
        var unit = "codex-worker-admin-" + Guid.NewGuid().ToString("N") + ".service";
        process.StartInfo.ArgumentList.Insert(0, "--unit=" + unit);
        token.ThrowIfCancellationRequested();
        process.Start();
        try
        {
            await process.WaitForExitAsync(token);
            return process.ExitCode;
        }
        finally
        {
            // The transient service is not a descendant of systemd-run. Stop its unit
            // explicitly so cancellation reaches login and its verification children.
            try
            {
                if (!process.HasExited)
                {
                    var stopped = await new ProcessRunner().RunAsync("/usr/bin/systemctl", ["stop", unit], "/",
                        TimeSpan.FromSeconds(15), CancellationToken.None);
                    if (stopped.ExitCode != 0 && !process.HasExited)
                        throw new InvalidOperationException("The Worker administration unit could not be stopped; inspect the unit before retrying the operation.");
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
