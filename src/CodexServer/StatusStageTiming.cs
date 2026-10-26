namespace CodexServer;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

// Bounded stage names only: no database paths, SQL, resource IDs or request tags.
internal sealed class StatusStageTiming(string stage) : IDisposable
{
    private static readonly Histogram<double> Duration = ServerApplication.StatusMeter.CreateHistogram<double>(
        "codex.server.status.stage.duration", "ms", "Status registry and projection stages, including failed operations.");
    private readonly long _started = Stopwatch.GetTimestamp();
    private TimeSpan? _elapsed;

    internal static string FormatMilliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);

    internal string Header => $"registry_{stage};dur={FormatMilliseconds(_elapsed.GetValueOrDefault())}";

    public void Dispose()
    {
        if (_elapsed.HasValue) return;
        _elapsed = Stopwatch.GetElapsedTime(_started);
        Duration.Record(_elapsed.Value.TotalMilliseconds, new KeyValuePair<string, object?>("stage", stage));
    }
}
