namespace CodexWorker;

using CodexProvisioning;
using System.Text.Json;

internal static class StandaloneReportingSnapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static ObserverStatusSnapshot Create(string observerId, DateTimeOffset now, WorkerStatus status,
        IReadOnlyList<ExecutionRuntimeInfo> active)
    {
        var executions = active.Where(item => item.CompletedAtUtc is null && item.Result is null).Take(ObserverProtocol.MaximumExecutions).Select(item => new ObserverExecution(
            item.ExecutionId.ToString("N"), item.Project, item.Repository, item.IssueNumber, item.IssueTitle, item.State,
            null, null, item.StartedAtUtc.ToUniversalTime())).Where(item => item.IsValid() && item.StartedAtUtc <= now).ToArray();
        var lifecycle = WorkerLifecycleStates.IsValid(status.State) ? status.State : status.State switch
        {
            "shutting-down" => WorkerLifecycleStates.Stopped,
            "failed" => WorkerLifecycleStates.NotReady,
            _ => WorkerLifecycleStates.IsValid(status.LifecycleState) ? status.LifecycleState : WorkerLifecycleStates.NotReady
        };
        var snapshot = new ObserverStatusSnapshot(1, observerId, Guid.NewGuid().ToString("N"), now, lifecycle, executions,
            Version: status.Version, MaximumCapacity: status.MaxParallelTasks,
            ActiveExecutions: status.ActiveExecutionCount >= 0 && status.ActiveExecutionCount <= status.MaxParallelTasks
                ? status.ActiveExecutionCount : null);
        while (snapshot.Executions.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(snapshot, Json).Length > ObserverProtocol.MaximumPayloadBytes)
            snapshot = snapshot with { Executions = snapshot.Executions.Take(snapshot.Executions.Count - 1).ToArray() };
        return snapshot;
    }
}

/// <summary>Passive observer loop. Failure never changes execution ownership, capacity or lifecycle.</summary>
internal sealed class StandaloneReporting : IAsyncDisposable
{
    private readonly StandaloneReportingStore store;
    private readonly StandaloneReportingClient client;
    private readonly WorkerRuntimeReadModel runtime;
    private readonly TimeProvider clock;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource stop;
    private Task? run;
    private readonly bool standalone;
    private string? lastDiagnostic;

    internal StandaloneReporting(GlobalWorkerConfiguration config, WorkerRuntimeReadModel runtime, TimeProvider clock, CancellationToken ct,
        StandaloneReportingClient? client = null)
    {
        store = new(config.Reporting.CredentialFile);
        this.client = client ?? new();
        this.runtime = runtime;
        this.clock = clock;
        interval = TimeSpan.FromSeconds(config.Reporting.IntervalSeconds);
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        standalone = config.Projects.Ownership == "standalone";
    }
    internal void Start() => run ??= standalone ? RunAsync() : Task.CompletedTask;
    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await ReportOnceAsync(stop.Token);
                await Task.Delay(interval, clock, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    internal async Task ReportOnceAsync(CancellationToken ct)
    {
        try
        {
            // Default off: no identity creation, lock file or Server request while unset.
            if (!standalone || !File.Exists(store.FilePath)) return;
            if (await store.LoadAsync(ct) is not { Enabled: true, Accepted: true }) return;
            await using var gate = await store.LockAsync(ct);
            var local = await store.LoadAsync(ct);
            if (local is not { Enabled: true, Accepted: true }) return;
            string result;
            try
            {
                var snapshot = await runtime.ObserverSnapshotAsync(local.ObserverId, clock.GetUtcNow(), ct);
                result = snapshot.IsValid() ? await client.ReportAsync(local, snapshot, ct) : "rejected";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or JsonException)
            {
                result = "unavailable";
            }
            await store.SaveAsync(local with { LastResult = result, LastReportAtUtc = result == "reporting" ? clock.GetUtcNow() : local.LastReportAtUtc }, ct);
            Diagnostic(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Diagnostic("local-state-unavailable");
        }
    }
    private void Diagnostic(string result)
    {
        if (result == lastDiagnostic) return;
        lastDiagnostic = result;
        runtime.Events.Publish("observer.reporting", result == "reporting" ? "Standalone reporting delivered." :
            $"Standalone reporting: {result}. Local execution continues. Inspect 'codex-worker reporting status'.");
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { if (run is not null) await run; }
        finally { client.Dispose(); stop.Dispose(); }
    }
}
