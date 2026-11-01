namespace CodexWorker;

/// <summary>Fail-closed Linux check for surviving execution children before reuse/removal.</summary>
internal static class InterruptedWorkspaceProcesses
{
    internal static async Task<string?> ReadScopeAsync(CancellationToken ct, string procRoot = "/proc")
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var scope = await File.ReadAllTextAsync(Path.Combine(procRoot, "self", "cgroup"), ct);
            return scope.Length is > 0 and <= 4096 ? scope : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<string?> CheckAsync(string workspace, bool childrenJoined, string? processScope, CancellationToken ct, string procRoot = "/proc")
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) return "process-inspection-unavailable";
        if (!childrenJoined && processScope is null) return "process-scope-missing";
        try
        {
            if (!childrenJoined && processScope is not null && await ReadScopeAsync(ct, procRoot) != processScope)
                return "process-scope-changed";
            var ancestors = new HashSet<int>();
            var ancestor = Environment.ProcessId;
            while (ancestor > 0 && ancestors.Add(ancestor))
            {
                var stat = await File.ReadAllTextAsync(Path.Combine(procRoot, ancestor.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat"), ct);
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || !int.TryParse(fields[1], out ancestor)) return "process-inspection-unavailable";
            }
            static string? Uid(IEnumerable<string> lines) => lines.FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
            var ownUid = Uid(await File.ReadAllLinesAsync(Path.Combine(procRoot, "self", "status"), ct));
            if (ownUid is null) return "process-inspection-unavailable";
            foreach (var process in Directory.EnumerateDirectories(procRoot).Where(path => int.TryParse(Path.GetFileName(path), out _)))
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
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        if (!Directory.Exists(process)) continue;
                        // Children inherit the recorded Linux cgroup, including across a
                        // service restart. A protected process outside it is not our child.
                        if (ancestors.Contains(int.Parse(Path.GetFileName(process), System.Globalization.CultureInfo.InvariantCulture))) continue;
                        if (!scopeMatches && (childrenJoined || processScope is not null)) continue;
                        return "relevant-process-unreadable";
                    }
                    if (cwd is null)
                    {
                        if (!scopeMatches && (childrenJoined || processScope is not null)) continue;
                        return "relevant-process-unreadable";
                    }
                    if (cwd == workspace || cwd.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        return "workspace-process-active";
                }
                // A durable graceful-shutdown record is written only after ProcessRunner
                // has joined/killed its isolated child sessions. Protected unrelated login
                // and user-manager processes cannot then be surviving execution children.
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    if (!Directory.Exists(process)) continue;
                    if (processScope is null && childrenJoined) continue;
                    if (processScope is not null)
                    {
                        try
                        {
                            if (await File.ReadAllTextAsync(Path.Combine(process, "cgroup"), ct) != processScope) continue;
                        }
                        catch (Exception scopeError) when (scopeError is IOException or UnauthorizedAccessException)
                        {
                            if (!Directory.Exists(process)) continue;
                            return "process-scope-unreadable";
                        }
                    }
                    return "relevant-process-unreadable";
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "process-inspection-unavailable";
        }
    }
}
