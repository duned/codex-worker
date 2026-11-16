namespace CodexWorker;

using CodexProvisioning;
using System.Diagnostics;
using System.Globalization;

/// <summary>Linux host counters, never process/container limits. Unsupported counters remain absent.</summary>
internal static class WorkerHostResourceCollector
{
    private static readonly Lock CpuGate = new();
    private static ((long Total, long Idle, int Count) Sample, long Timestamp)? _previousCpu;

    public static async Task<WorkerHostResources> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int? cpus = null;
        long? total = null, used = null, diskTotal = null, diskAvailable = null;
        double? cpu = null, seconds = null;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                // CPU usage covers the interval since the previous collection (normally one heartbeat),
                // so a collection never waits for a sample. The first collection reports no CPU value.
                var second = await ReadCpuAsync(cancellationToken);
                var now = Stopwatch.GetTimestamp();
                ((long Total, long Idle, int Count) Sample, long Timestamp)? previous;
                lock (CpuGate)
                {
                    previous = _previousCpu;
                    _previousCpu = (second, now);
                }
                cpus = second.Count is > 0 and <= 1048576 ? second.Count : null;
                if (previous is { } baseline)
                {
                    var first = baseline.Sample;
                    seconds = Stopwatch.GetElapsedTime(baseline.Timestamp, now).TotalSeconds;
                    var elapsed = second.Total - first.Total;
                    var idle = second.Idle - first.Idle;
                    if (first.Count == second.Count && second.Count > 0 && elapsed > 0 && idle >= 0 && idle <= elapsed && seconds is >= 1 and <= 3600)
                        cpu = 100d * (elapsed - idle) / elapsed;
                }
                var memory = (await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken))
                    .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length >= 3 && parts[2] == "kB")
                    .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024);
                if (memory.TryGetValue("MemTotal:", out var capacity) && capacity > 0)
                {
                    total = capacity;
                    if (memory.TryGetValue("MemAvailable:", out var available) && available >= 0 && available <= capacity)
                        used = capacity - available;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException) { }
        }
        try
        {
            var drive = DriveInfo.GetDrives().Where(item => ContainsDirectory(item.RootDirectory.FullName, AppContext.BaseDirectory))
                .OrderByDescending(item => item.RootDirectory.FullName.Length).FirstOrDefault();
            if (drive is not null && drive.IsReady)
            {
                var capacity = drive.TotalSize;
                var available = drive.AvailableFreeSpace;
                if (capacity > 0)
                {
                    diskTotal = capacity;
                    if (available >= 0 && available <= capacity) diskAvailable = available;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new(DateTimeOffset.UtcNow, cpus, total, used, diskTotal, diskAvailable, cpu,
            total is > 0 && used.HasValue ? 100d * used.Value / total.Value : null, cpu.HasValue ? seconds : null);
    }

    private static async Task<(long Total, long Idle, int Count)> ReadCpuAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync("/proc/stat", cancellationToken);
        return ParseCpu(lines);
    }

    internal static bool ContainsDirectory(string mountRoot, string directory)
    {
        var relative = Path.GetRelativePath(mountRoot, directory);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static (long Total, long Idle, int Count) ParseCpu(string[] lines)
    {
        if (lines.Length == 0 || !lines[0].StartsWith("cpu ", StringComparison.Ordinal))
            throw new IOException("Host CPU counters unavailable.");
        var counters = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (counters.Length < 5 || counters.Any(value => value < 0)) throw new IOException("Host CPU counters unavailable.");
        return (counters.Sum(), counters[3] + counters[4], lines.Count(line => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsDigit(line[3])));
    }
}
