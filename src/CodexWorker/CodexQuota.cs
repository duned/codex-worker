using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexWorker;

public sealed record CodexQuotaWindow(int? DurationMinutes, double? RemainingPercent, DateTimeOffset? ResetsAtUtc);
public sealed record CodexQuotaObservation(DateTimeOffset ObservedAtUtc, string Status, IReadOnlyList<CodexQuotaWindow> Windows)
{
    public string Summary(DateTimeOffset now)
    {
        if (now - ObservedAtUtc > TimeSpan.FromMinutes(2)) return "unavailable (stale)";
        if (Status != "available") return $"unavailable ({Status})";
        return string.Join(" · ", new[] { (300, "5h"), (10080, "weekly") }.Select(pair =>
        {
            var window = Windows.SingleOrDefault(w => w.DurationMinutes == pair.Item1);
            return pair.Item2 + ": " + (window?.RemainingPercent is { } remaining
                ? remaining.ToString("0.#", CultureInfo.InvariantCulture) + "% remaining" +
                  (window.ResetsAtUtc is { } reset ? $" (reset {reset:yyyy-MM-dd HH:mm} UTC)" : "") : "unavailable");
        }));
    }
}

public interface ICodexQuotaReader
{
    Task<CodexQuotaObservation> ReadAsync(CancellationToken ct);
}

internal interface ICodexQuotaTransport
{
    Task<JsonElement> ReadAsync(CancellationToken ct);
}

/// <summary>One local credential context; no account identifiers or raw responses are retained.</summary>
internal sealed class CodexQuotaReader(ICodexQuotaTransport transport, TimeProvider? clock = null) : ICodexQuotaReader
{
    internal static ICodexQuotaReader Local { get; } = new CodexQuotaReader(new CodexQuotaTransport());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CodexQuotaObservation? _cached;

    public async Task<CodexQuotaObservation> ReadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token);
            entered = true;
            if (_cached is { } cached && _clock.GetUtcNow() - cached.ObservedAtUtc < TimeSpan.FromSeconds(2)) return cached;
            _cached = Parse(await transport.ReadAsync(deadline.Token), _clock.GetUtcNow());
            return _cached;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var observation = new CodexQuotaObservation(_clock.GetUtcNow(),
                ct.IsCancellationRequested ? "cancelled" : ex is OperationCanceledException ? "timeout" : "query failed", []);
            if (entered) _cached = observation;
            return observation;
        }
        finally { if (entered) _gate.Release(); }
    }

    internal static CodexQuotaObservation Parse(JsonElement result, DateTimeOffset now)
    {
        if (!result.TryGetProperty("rateLimits", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
            return new(now, "no quota snapshot", []);
        var windows = new List<CodexQuotaWindow>();
        foreach (var field in snapshot.EnumerateObject())
        {
            var value = field.Value;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("usedPercent", out var used)) continue;
            int? duration = value.TryGetProperty("windowDurationMins", out var mins) && mins.ValueKind == JsonValueKind.Number && mins.TryGetInt32(out var m) && m > 0 ? m : null;
            double? remaining = used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out var p) && double.IsFinite(p) && p is >= 0 and <= 100 ? 100 - p : null;
            DateTimeOffset? reset = null;
            if (value.TryGetProperty("resetsAt", out var resetValue) && resetValue.ValueKind == JsonValueKind.Number && resetValue.TryGetInt64(out var seconds))
            {
                try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
            }
            windows.Add(new(duration, remaining, reset));
        }
        // Ambiguous duplicate durations must not be presented as a single allowance.
        windows = windows.Select(w => w.DurationMinutes is { } d && windows.Count(x => x.DurationMinutes == d) > 1
            ? w with { DurationMinutes = null } : w).ToList();
        return new(now, "available", windows);
    }
}

internal sealed class CodexQuotaTransport : ICodexQuotaTransport
{
    public async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        using var environment = CodexEnvironment.Create();
        var start = new ProcessStartInfo(CodexProvisioning.CodexServiceEnvironment.Executable)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("app-server");
        foreach (var (key, value) in environment.Variables)
            if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("App-server unavailable.");
        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stderr = DrainAsync(process.StandardError, drainCancellation.Token);
        try
        {
            return await QueryAsync(process.StandardInput, process.StandardOutput, ct);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await process.WaitForExitAsync(termination.Token); } catch (OperationCanceledException) { }
            drainCancellation.Cancel();
            try { await stderr; } catch (OperationCanceledException) { }
        }
    }

    internal static async Task<JsonElement> QueryAsync(StreamWriter input, StreamReader output, CancellationToken ct)
    {
            input.AutoFlush = true;
            await input.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex_worker_quota\",\"version\":\"1\"}}}".AsMemory(), ct);
            await ResponseAsync(output, 1, ct);
            await input.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), ct);
            await input.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}".AsMemory(), ct);
            return await ResponseAsync(output, 2, ct);
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) != 0) { }
    }

    private static async Task<JsonElement> ResponseAsync(StreamReader reader, int id, CancellationToken ct)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        var total = 0;
        while (await reader.ReadAsync(buffer.AsMemory(), ct) != 0)
        {
            if (++total > 131072) throw new InvalidDataException("App-server output exceeds limit.");
            if (buffer[0] != '\n') { line.Append(buffer[0]); continue; }
            using var document = JsonDocument.Parse(line.ToString());
            line.Clear();
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var responseNumber) || responseNumber != id) continue;
            if (root.TryGetProperty("error", out _)) throw new IOException("App-server request unavailable.");
            return root.GetProperty("result").Clone();
        }
        throw new IOException("App-server closed output.");
    }
}

internal static class CodexQuotaFormatting
{
    internal static string Journal(WorkerExecution execution, string phase, CodexQuotaObservation observation, DateTimeOffset now) =>
        $"Codex quota {phase} · execution [{execution.ExecutionId}] · {execution.Project} #{execution.IssueNumber} · observed {observation.ObservedAtUtc:O} · {observation.Summary(now)}";
}
