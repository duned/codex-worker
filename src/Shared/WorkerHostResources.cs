namespace CodexProvisioning;

/// <summary>Optional, read-only host facts. CPU percentages cover all logical CPUs over SampleSeconds.</summary>
public sealed record WorkerHostResources(DateTimeOffset MeasuredAtUtc, int? LogicalCpuCount = null,
    long? TotalMemoryBytes = null, long? UsedMemoryBytes = null, long? DiskTotalBytes = null,
    long? DiskAvailableBytes = null, double? CpuUsagePercent = null, double? MemoryUsagePercent = null,
    double? SampleSeconds = null)
{
    public static bool Valid(WorkerHostResources? value) => value is null ||
        (value.MeasuredAtUtc != default && value.LogicalCpuCount is null or >= 1 and <= 1048576 &&
        value.TotalMemoryBytes is null or > 0 && value.UsedMemoryBytes is null or >= 0 &&
        value.DiskTotalBytes is null or > 0 && value.DiskAvailableBytes is null or >= 0 &&
        (value.UsedMemoryBytes is null || value.TotalMemoryBytes is not null && value.UsedMemoryBytes <= value.TotalMemoryBytes) &&
        (value.DiskAvailableBytes is null || value.DiskTotalBytes is not null && value.DiskAvailableBytes <= value.DiskTotalBytes) &&
        Percent(value.CpuUsagePercent) && Percent(value.MemoryUsagePercent) &&
        (value.SampleSeconds is null || double.IsFinite(value.SampleSeconds.Value) && value.SampleSeconds >= 1 && value.SampleSeconds <= 3600) &&
        (value.CpuUsagePercent is null || value.SampleSeconds >= 1));

    private static bool Percent(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 100;
}
