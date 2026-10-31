namespace CodexWorker;

/// <summary>Fail-closed Linux check for surviving execution children before reuse/removal.</summary>
internal static class InterruptedWorkspaceProcesses
{
    internal static async Task<string?> ReadScopeAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var scope = await File.ReadAllTextAsync("/proc/self/cgroup", ct);
            return scope.Length is > 0 and <= 4096 ? scope : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<string?> CheckAsync(string workspace, bool childrenJoined, string? processScope, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return "process-inspection-unavailable";
        try
        {
            if (!childrenJoined && processScope is not null && await ReadScopeAsync(ct) != processScope)
                return "process-scope-changed";
            var ancestors = new HashSet<int>();
            var ancestor = Environment.ProcessId;
            while (ancestor > 0 && ancestors.Add(ancestor))
            {
                var stat = await File.ReadAllTextAsync($"/proc/{ancestor}/stat", ct);
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || !int.TryParse(fields[1], out ancestor)) return "process-inspection-unavailable";
            }
            static string? Uid(IEnumerable<string> lines) => lines.FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
            var ownUid = Uid(await File.ReadAllLinesAsync("/proc/self/status", ct));
            if (ownUid is null) return "process-inspection-unavailable";
            foreach (var process in Directory.EnumerateDirectories("/proc").Where(path => int.TryParse(Path.GetFileName(path), out _)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var status = await File.ReadAllLinesAsync(Path.Combine(process, "status"), ct);
                    if (status.Any(line => line.StartsWith("State:\tZ", StringComparison.Ordinal))) continue;
                    var scopeMatches = processScope is not null &&
                        await File.ReadAllTextAsync(Path.Combine(process, "cgroup"), ct) == processScope;
                    if (Uid(status) != ownUid && !scopeMatches) continue;
                    string? cwd;
                    try { cwd = new DirectoryInfo(Path.Combine(process, "cwd")).ResolveLinkTarget(true)?.FullName; }
                    catch (UnauthorizedAccessException)
                    {
                        // Children inherit the recorded Linux cgroup, including across a
                        // service restart. A protected process outside it is not our child.
                        if (ancestors.Contains(int.Parse(Path.GetFileName(process), System.Globalization.CultureInfo.InvariantCulture))) continue;
                        if (!scopeMatches && (childrenJoined || processScope is not null)) continue;
                        return "process-inspection-unavailable";
                    }
                    if (cwd is null)
                    {
                        if (!scopeMatches && childrenJoined) continue;
                        return "process-inspection-unavailable";
                    }
                    if (cwd == workspace || cwd.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        return "workspace-process-active";
                }
                // A durable graceful-shutdown record is written only after ProcessRunner
                // has joined/killed its isolated child sessions. Protected unrelated login
                // and user-manager processes cannot then be surviving execution children.
                catch (UnauthorizedAccessException)
                {
                    if (processScope is not null && await File.ReadAllTextAsync(Path.Combine(process, "cgroup"), ct) != processScope) continue;
                    if (processScope is null && childrenJoined) continue;
                    return "process-inspection-unavailable";
                }
                catch (IOException) when (!Directory.Exists(process)) { /* Process exited while inspected. */ }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "process-inspection-unavailable";
        }
    }
}
