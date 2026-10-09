namespace CodexWorker;

using CodexProvisioning;
using System.Diagnostics;
using System.Globalization;

/// <summary>Linux host counters, never process/container limits. Unsupported counters remain absent.</summary>
internal static class WorkerHostResourceCollector
{
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
                var first = await ReadCpuAsync(cancellationToken);
                var started = Stopwatch.GetTimestamp();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                var second = await ReadCpuAsync(cancellationToken);
                seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                cpus = second.Count is > 0 and <= 1048576 ? second.Count : null;
                var elapsed = second.Total - first.Total;
                var idle = second.Idle - first.Idle;
                if (first.Count == second.Count && second.Count > 0 && elapsed > 0 && idle >= 0 && idle <= elapsed && seconds is >= 1 and <= 3600)
                    cpu = 100d * (elapsed - idle) / elapsed;
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
            var drive = DriveInfo.GetDrives().Where(item => AppContext.BaseDirectory.StartsWith(item.RootDirectory.FullName, StringComparison.Ordinal))
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
        var counters = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (counters.Length < 5) throw new IOException("Host CPU counters unavailable.");
        return (counters.Sum(), counters[3] + counters[4], lines.Count(line => line.Length > 3 && line.StartsWith("cpu", StringComparison.Ordinal) && char.IsDigit(line[3])));
    }
}
