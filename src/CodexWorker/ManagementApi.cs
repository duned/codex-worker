namespace CodexWorker;

using CodexProvisioning;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed record RuntimeEvent(long Id, DateTimeOffset TimestampUtc, string Type, string Message, string? Project = null);
public sealed record WorkerCapability(string Name, string Kind, string? Version = null, IReadOnlyDictionary<string, string>? Attributes = null);
public sealed record WorkerStatus(string Version, string State, long UptimeSeconds, int MaxParallelTasks,
    int ActiveExecutionCount, int AvailableExecutionCapacity, int ConfiguredProjectCount, int EnabledProjectCount,
    string LifecycleState, bool DrainRequested, string? LastUpdateResult, string ReconnectReadinessResult,
    ManagedWorkerDiagnostics? ManagedDiagnostics = null);
public sealed record ProjectRuntimeInfo(string Name, string ConfigurationPath, string ProjectDirectory, string Repository, bool Enabled, string State,
    int MaxParallelTasks, int ActiveExecutionCount, int AvailableExecutionCapacity, int? ReadyWorkCount, string? UnavailableReason = null);
public sealed record ProjectLifecycleRequest(string Action);
public sealed record ExecutionRepairInfo(int Attempt, int MaximumAttempts, bool PassedAfterRepair);
public sealed record ExecutionRuntimeInfo(Guid ExecutionId, string Project, string Repository, int IssueNumber,
    string IssueTitle, string State, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc,
    long? DurationMilliseconds, string? ValidationOutcome, int RepairCount, IReadOnlyList<ExecutionRepairInfo> Repairs,
    string? Result, string? RecoveryState, string? RecoveryBaseCommit, string? RecoveryStatus,
    Guid? RetryOfExecutionId, int AttemptNumber, bool Resumed, DateTimeOffset? RecoveryExpiresAtUtc, string? EffectiveModel = null, string? EffectiveEffort = null,
    string? FailureReason = null, string? ReportingFailure = null, string? FeatureBranch = null,
    string? BaseBranch = null, string? CompletedBranch = null, string? CommitSha = null, string? IntegrationBranch = null);

/// <summary>Bounded, process-local event history with fan-out subscriptions for SSE consumers.</summary>
public sealed class RuntimeEventLog
{
    private readonly object _lock = new();
    private readonly Queue<RuntimeEvent> _history = new();
    private readonly HashSet<Channel<RuntimeEvent>> _subscribers = [];
    private readonly int _limit;
    private long _nextId;

    public RuntimeEventLog(int limit = 500)
    {
        if (limit < 1 || limit > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
    }

    public RuntimeEvent Publish(string type, string message, string? project = null)
    {
        lock (_lock)
        {
            var item = new RuntimeEvent(++_nextId, DateTimeOffset.UtcNow, type, message, project);
            _history.Enqueue(item);
            while (_history.Count > _limit) _history.Dequeue();
            foreach (var channel in _subscribers) channel.Writer.TryWrite(item);
            return item;
        }
    }

    public IReadOnlyList<RuntimeEvent> ReadRecent(int? limit = null)
    {
        lock (_lock)
        {
            var count = Math.Clamp(limit ?? _limit, 1, _limit);
            return _history.TakeLast(count).ToArray();
        }
    }

    public IAsyncEnumerable<RuntimeEvent> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<RuntimeEvent>(new BoundedChannelOptions(_limit)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        lock (_lock) _subscribers.Add(channel);
        return ReadSubscription(channel, ct);
    }

    private async IAsyncEnumerable<RuntimeEvent> ReadSubscription(Channel<RuntimeEvent> channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct)) yield return item;
        }
        finally
        {
            lock (_lock) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}

/// <summary>Safe application-level read model shared by the management API and future telemetry consumers.</summary>
public sealed class WorkerRuntimeReadModel
{
    private readonly GlobalWorkerConfiguration _global;
    private readonly IReadOnlyList<(string Path, WorkerConfiguration Configuration)> _projects;
    private readonly ExecutionHistoryStore _history;
    private readonly ProjectConfigurationService? _configurationService;
    private readonly ProjectRuntimeRegistry _registry;
    private readonly ManagedConfigurationSynchronizer? _managedConfiguration;
    private readonly WorkerLifecycle _lifecycle;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _repositoryGates;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private volatile string _state = "starting";

    public WorkerRuntimeReadModel(GlobalWorkerConfiguration global,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects, ExecutionHistoryStore history,
        ProjectConfigurationService? configurationService = null,
        ManagedConfigurationSynchronizer? managedConfiguration = null, WorkerLifecycle? lifecycle = null,
        System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>? repositoryGates = null)
    {
        _global = global;
        _projects = projects;
        _history = history;
        _configurationService = configurationService;
        _managedConfiguration = managedConfiguration;
        _lifecycle = lifecycle ?? new WorkerLifecycle();
        _repositoryGates = repositoryGates ?? new(StringComparer.OrdinalIgnoreCase);
        Events = new RuntimeEventLog(global.Api.EventHistoryLimit);
        _registry = new ProjectRuntimeRegistry(projects, Events, _lifecycle);
        CompletedBranchMaintenance = new CompletedBranchMaintenanceService(_registry, history);
    }

    public CompletedBranchMaintenanceService CompletedBranchMaintenance { get; }
    public RuntimeEventLog Events { get; }
    public ProjectRuntimeRegistry Registry => _registry;
    public WorkerLifecycle Lifecycle => _lifecycle;
    public Task<string?> ArchiveAuditAsync(Guid id, CancellationToken ct) => _history.ReadArchiveAuditAsync(id, ct);

    public ExecutionCleanupService ExecutionCleanup => new(_history, _registry, repositoryGates: _repositoryGates);
    public string State
    {
        get => StateFor(_lifecycle.Snapshot);
        set => _state = value;
    }

    private string StateFor(WorkerLifecycleSnapshot lifecycle)
    {
        var state = _state;
        return lifecycle.DrainRequested && state is not ("shutting-down" or "stopped" or "failed")
            ? lifecycle.State : state;
    }

    public WorkerHeartbeatStatus HeartbeatStatus(WorkerHeartbeatStatus status)
    {
        var lifecycle = _lifecycle.Snapshot;
        return status with { ActiveExecutions = lifecycle.ActiveExecutions, State = StateFor(lifecycle) };
    }
    public WorkerConfigurationSyncStatus? ConfigurationSyncStatus => _managedConfiguration?.Status;

    public async Task<WorkerStatus> StatusAsync(CancellationToken ct)
    {
        var active = (await _history.ReadActiveAsync(ct)).Count;
        var projectCount = _registry.Snapshot().Count;
        var lifecycle = _lifecycle.Snapshot;
        return new WorkerStatus(ApplicationVersion.Display, StateFor(lifecycle),
            Math.Max(0, (long)(DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds),
            _global.Worker.MaxParallelTasks, active, Math.Max(0, _global.Worker.MaxParallelTasks - active),
            projectCount, _registry.Status().Count(project => project.State == ProjectLifecycleState.Enabled),
            lifecycle.State, lifecycle.DrainRequested, lifecycle.LastUpdateResult, lifecycle.ReconnectReadinessResult,
            ConfigurationSyncStatus?.Diagnostics);
    }

    public IReadOnlyList<WorkerCapability> Capabilities =>
    [
        new("codex-cli", "executor", Attributes: new Dictionary<string, string> { ["managedBy"] = "worker" }),
        new("git", "source-control", Attributes: new Dictionary<string, string> { ["platform"] = RuntimeInformation.OSDescription }),
        new("validation-commands", "validation", Attributes: new Dictionary<string, string> { ["sequential"] = "true" }),
        new("platform", "runtime", RuntimeInformation.ProcessArchitecture.ToString(),
            new Dictionary<string, string> { ["os"] = RuntimeInformation.OSDescription })
    ];

    public async Task<IReadOnlyList<ProjectRuntimeInfo>> ProjectsAsync(CancellationToken ct)
    {
        var entries = await _history.ReadActiveAsync(ct);
        var lifecycle = _registry.Status().ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        return _registry.Snapshot().Select(item =>
            {
                var config = item.Configuration;
                var state = lifecycle[config.Project.Name];
                var active = entries.Count(e => string.Equals(e.Project, config.Project.Name, StringComparison.OrdinalIgnoreCase));
                return new ProjectRuntimeInfo(config.Project.Name, item.Path, config.Project.Directory, config.Project.Repository, state.State == ProjectLifecycleState.Enabled,
                    state.State.ToString(), config.Worker.MaxParallelTasks, active,
                    Math.Max(0, config.Worker.MaxParallelTasks - active), null, state.UnavailableReason);
            }).ToArray();
    }

    public async Task<IReadOnlyList<ExecutionRuntimeInfo>> ExecutionsAsync(int limit, CancellationToken ct) =>
        (await _history.ReadRecentAsync(limit, ct)).Select(ProjectExecution).ToArray();

    public async Task<ExecutionInventoryPage> ExecutionInventoryAsync(ExecutionInventoryQuery query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > 200 || query.Offset is < 0 or > 10000 ||
            query.Attention is { } attention && attention is not ("healthy-active" or "healthy-terminal" or "recoverable" or "reconciliation-required" or "stale" or "orphaned" or "retained-review") ||
            query.Outcome is { } outcome && outcome is not ("succeeded" or "blocked" or "failed" or "infrastructure-failure" or "cancelled" or "integration-conflict" or "active"))
            throw new ArgumentException("Inventory filters, limit or offset are invalid.");
        var candidates = await _history.ReadInventoryAsync(query with { Limit = 5001, Offset = 0 }, DateTimeOffset.UtcNow, ct);
        var scanned = Math.Min(candidates.Count, 5000);
        var hasUnscanned = candidates.Count > 5000;
        var configured = _registry.Snapshot();
        var items = candidates.Take(5000).Select(entry =>
        {
            var projectConfigured = configured.Any(p => string.Equals(p.Configuration.Project.Name, entry.Project, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Configuration.Project.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase));
            var info = ProjectExecution(entry);
            return new ExecutionInventoryItem(info, ExecutionMaintenanceClassifier.Classify(entry, DateTimeOffset.UtcNow, projectConfigured));
        });
        var filtered = items.Where(item => query.Attention is null || item.Maintenance.Status == query.Attention)
            .Skip(query.Offset).Take(query.Limit + 1).ToArray();
        return new ExecutionInventoryPage(filtered.Take(query.Limit).ToArray(), query.Limit, query.Offset,
            filtered.Length > query.Limit || hasUnscanned, scanned);
    }

    public async Task<ExecutionRuntimeInfo?> ExecutionAsync(Guid executionId, CancellationToken ct) =>
        await _history.ReadExecutionAsync(executionId, ct) is { } entry ? ProjectExecution(entry) : null;

    public async Task<IReadOnlyList<ExecutionRuntimeInfo>> IssueExecutionsAsync(int issueNumber, CancellationToken ct) =>
        (await _history.ReadIssueAsync(issueNumber, ct)).Select(ProjectExecution).ToArray();

    private ExecutionRuntimeInfo ProjectExecution(ExecutionHistoryEntry e)
    {
        var project = _registry.Snapshot().FirstOrDefault(project =>
            string.Equals(project.Configuration.Project.Name, e.Project, StringComparison.OrdinalIgnoreCase));
        var retentionDays = project.Configuration?.Worker.RecoveryRetentionDays ?? 7;
        var secrets = project.Configuration?.Environment.Variables.Values.ToArray();
        string? Diagnostic(string? value)
        {
            if (value is null) return null;
            var safe = FailureDiagnosticRedactor.Redact(value.Split('\n')[0], secrets);
            return safe.Length <= 1000 ? safe : safe[..1000] + "…";
        }
        // Task failure details can include validation/process output; retain the safe state/validation
        // summary instead. Infrastructure and reporting diagnostics are operational metadata.
        return new ExecutionRuntimeInfo(e.ExecutionId, e.Project, e.Repository, e.IssueNumber, e.IssueTitle, e.State,
            e.StartedAtUtc, e.CompletedAtUtc, e.DurationMilliseconds, Diagnostic(e.ValidationOutcome), e.RepairCount,
            e.Repairs.Select(repair => new ExecutionRepairInfo(repair.Attempt, repair.MaximumAttempts, repair.PassedAfterRepair)).ToArray(),
            Outcome(e.State), e.RecoveryState, e.RecoveryBaseCommit, Diagnostic(e.RecoveryStatus),
            e.RetryOfExecutionId, e.AttemptNumber, e.Resumed,
            e.RecoveryState is "recoverable" or "cleanup-pending"
                ? RecoveryRetentionPolicy.ExpiresAt(e, TimeSpan.FromDays(retentionDays)) : e.RecoveryExpiresAtUtc,
            e.EffectiveModel, e.EffectiveEffort,
            e.State is "InfrastructureFailure" or "Cancelled" ? Diagnostic(e.FailureReason) : null,
            Diagnostic(e.ReportingFailure), e.FeatureBranch, e.BaseBranch, e.CompletedBranch, e.CommitSha, e.IntegrationBranch);
    }

    public async Task<ExecutionCleanupInspection?> InspectExecutionCleanupAsync(Guid executionId, CancellationToken ct)
    {
        if (executionId == Guid.Empty) return null;
        var results = await ExecutionCleanup.RunAsync(new ExecutionCleanupRequest(ExecutionId: executionId), ct);
        return results.Count == 0 ? null : results[0].Inspection;
    }

    private static string? Outcome(string state) => state switch
    {
        "Completed" => "succeeded", "Blocked" => "blocked", "Failed" => "failed",
        "InfrastructureFailure" => "infrastructure-failure", "Cancelled" => "cancelled",
        "IntegrationConflict" => "integration-conflict", _ => null
    };
}

public static class ManagementApi
{
    public static async Task<WebApplication?> StartAsync(WorkerRuntimeReadModel runtime, ManagementApiSettings settings,
        CancellationToken ct, ProjectConfigurationService? projectConfigurations = null)
    {
        if (!settings.Enabled) return null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(settings.ListenUrl);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(runtime);
        if (projectConfigurations is not null) builder.Services.AddSingleton(projectConfigurations);
        var app = builder.Build();
        app.MapGet("/", () => Results.Content(DashboardHtml.Content, "text/html; charset=utf-8"));
        app.MapGet("/api/status", async (WorkerRuntimeReadModel model, HttpContext context) => Results.Ok(await model.StatusAsync(context.RequestAborted)));
        app.MapGet("/api/configuration-sync", (WorkerRuntimeReadModel model) =>
            model.ConfigurationSyncStatus is { } status ? Results.Ok(status) : Results.NoContent());
        app.MapGet("/api/capabilities", (WorkerRuntimeReadModel model) => Results.Ok(model.Capabilities));
        app.MapGet("/api/projects", async (WorkerRuntimeReadModel model, HttpContext context) => Results.Ok(await model.ProjectsAsync(context.RequestAborted)));
        app.MapPost("/api/projects/{name}/lifecycle", (string name, ProjectLifecycleRequest request, WorkerRuntimeReadModel model) =>
        {
            var action = request.Action?.ToLowerInvariant();
            if (action is not ("enable" or "disable" or "drain")) return (IResult)Results.BadRequest(new { error = "action must be enable, disable, or drain." });
            var result = action switch
            {
                "enable" => model.Registry.Enable(name),
                "disable" => model.Registry.Disable(name),
                "drain" => model.Registry.Drain(name),
                _ => null
            };
            return result is null ? (IResult)Results.NotFound() : Results.Ok(result);
        });
        app.MapPost("/api/worker/drain", (WorkerRuntimeReadModel model) =>
        {
            model.Registry.DrainWorker();
            var lifecycle = model.Lifecycle.Snapshot;
            return Results.Ok(new { draining = lifecycle.DrainRequested, state = lifecycle.State,
                activeExecutionCount = lifecycle.ActiveExecutions,
                drainComplete = lifecycle.DrainRequested && lifecycle.ActiveExecutions == 0 });
        });
        app.MapPost("/api/worker/drain/cancel", (WorkerRuntimeReadModel model) =>
        {
            if (!model.Registry.CancelWorkerDrain())
                return Results.Conflict(new { error = "Worker drain cannot be cancelled while executions are active or no drain is pending." });
            return Results.Ok(new { draining = false, state = model.Lifecycle.Snapshot.State });
        });
        app.MapGet("/api/worker/drain", (WorkerRuntimeReadModel model) =>
        {
            var lifecycle = model.Lifecycle.Snapshot;
            return Results.Ok(new { draining = lifecycle.DrainRequested, state = lifecycle.State,
                activeExecutionCount = lifecycle.ActiveExecutions,
                drainComplete = lifecycle.DrainRequested && lifecycle.ActiveExecutions == 0 });
        });
        if (projectConfigurations is not null)
        {
            app.MapPost("/api/project-configurations/reload", async (ProjectConfigurationService service, HttpContext context) =>
            {
                try { return Results.Ok(await service.ReloadAsync(context.RequestAborted)); }
                catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
                catch (IOException ex) { return Results.BadRequest(new { error = ex.Message }); }
                catch (ProjectConfigurationConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
            });
            app.MapGet("/api/project-configurations", async (ProjectConfigurationService service, HttpContext context) => Results.Ok(await service.ListAsync(context.RequestAborted)));
            app.MapGet("/api/project-configurations/{name}", async (string name, ProjectConfigurationService service, HttpContext context) =>
                await service.GetAsync(name, context.RequestAborted) is { } found ? Results.Ok(found) : Results.NotFound());
            app.MapPost("/api/project-configurations", async (WorkerConfiguration configuration, ProjectConfigurationService service, HttpContext context) =>
            {
                try { return (IResult)Results.Created($"/api/project-configurations/{Uri.EscapeDataString(configuration.Project.Name)}", await service.CreateAsync(configuration, context.RequestAborted)); }
                catch (ProjectConfigurationConflictException ex) { return (IResult)Results.Conflict(new { error = ex.Message }); }
                catch (InvalidDataException ex) { return (IResult)Results.BadRequest(new { error = ex.Message }); }
            });
            app.MapPut("/api/project-configurations/{name}", async (string name, WorkerConfiguration configuration, ProjectConfigurationService service, HttpContext context) =>
            {
                try { return (IResult)Results.Ok(await service.UpdateAsync(name, configuration, context.RequestAborted)); }
                catch (KeyNotFoundException) { return (IResult)Results.NotFound(); }
                catch (ProjectConfigurationConflictException ex) { return (IResult)Results.Conflict(new { error = ex.Message }); }
                catch (InvalidDataException ex) { return (IResult)Results.BadRequest(new { error = ex.Message }); }
            });
            app.MapDelete("/api/project-configurations/{name}", async (string name, ProjectConfigurationService service, HttpContext context) =>
            {
                try { return await service.RemoveAsync(name, context.RequestAborted) ? (IResult)Results.NoContent() : Results.NotFound(); }
                catch (ProjectConfigurationConflictException ex) { return (IResult)Results.Conflict(new { error = ex.Message }); }
                catch (InvalidDataException ex) { return (IResult)Results.BadRequest(new { error = ex.Message }); }
            });
        }
        app.MapPost("/api/maintenance/completed-branches", async (CompletedBranchCleanupRequest request, WorkerRuntimeReadModel model, HttpContext context) =>
        {
            if (request.Apply && !model.Registry.WorkerDrainComplete)
                return (IResult)Results.Conflict(new { error = "Wait for the running Worker to complete its drain before applying cleanup." });
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                deadline.CancelAfter(TimeSpan.FromMinutes(10));
                return await model.CompletedBranchMaintenance.CleanupAsync(request, deadline.Token) is { } result
                    ? (IResult)Results.Ok(result) : Results.NotFound(new { error = "No unique current Worker project matches this checkout." });
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            { return Results.BadRequest(new { error = "Invalid maintenance request or unverifiable Git refs; cleanup refused." }); }
            catch (InvalidOperationException ex)
            { return Results.Conflict(new { error = ex.Message }); }
            catch (WorkerInfrastructureException)
            { return Results.Conflict(new { error = "Repository refresh or verification failed; cleanup could not complete. Inspect repository state before retrying." }); }
        });
        app.MapGet("/api/executions", async (int? limit, WorkerRuntimeReadModel model, HttpContext context) =>
            Results.Ok(await model.ExecutionsAsync(limit ?? 100, context.RequestAborted)));
        app.MapGet("/api/executions/inventory", async (string? project, Guid? executionId, int? issueNumber,
            string? outcome, int? olderThanDays, string? attention, string? origin, int? limit, int? offset, bool? includeArchived,
            WorkerRuntimeReadModel model, HttpContext context) =>
        {
            try
            {
                var query = new ExecutionInventoryQuery(project, executionId, issueNumber, outcome, olderThanDays,
                    attention, origin, limit ?? 50, offset ?? 0, includeArchived ?? false);
                return (IResult)Results.Ok(await model.ExecutionInventoryAsync(query, context.RequestAborted));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/api/executions/{executionId:guid}", async (Guid executionId, WorkerRuntimeReadModel model, HttpContext context) =>
            await model.ExecutionAsync(executionId, context.RequestAborted) is { } entry ? Results.Ok(entry) : Results.NotFound());
        app.MapGet("/api/executions/issue/{issueNumber:int}", async (int issueNumber, WorkerRuntimeReadModel model, HttpContext context) =>
        {
            if (issueNumber <= 0) return (IResult)Results.BadRequest();
            var entries = await model.IssueExecutionsAsync(issueNumber, context.RequestAborted);
            return entries.Count == 0 ? Results.NotFound() : Results.Ok(entries);
        });
        app.MapGet("/api/executions/{executionId:guid}/archive-audit", async (Guid executionId, WorkerRuntimeReadModel model, HttpContext context) =>
            await model.ArchiveAuditAsync(executionId, context.RequestAborted) is { } audit ? Results.Ok(new { audit }) : Results.NotFound());
        app.MapGet("/api/executions/{executionId:guid}/cleanup-inspection", async (Guid executionId, WorkerRuntimeReadModel model, HttpContext context) =>
        {
            var inspection = await model.InspectExecutionCleanupAsync(executionId, context.RequestAborted);
            return inspection is null ? (IResult)Results.NotFound() : Results.Ok(inspection);
        });
        app.MapPost("/api/executions/cleanup", async (ExecutionCleanupRequest request, WorkerRuntimeReadModel model, HttpContext context) =>
        {
            if (request.Apply && !model.Registry.WorkerDrainComplete)
                return (IResult)Results.Conflict(new { error = "Wait for the running Worker to complete its drain before applying cleanup." });
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                return Results.Ok(await model.ExecutionCleanup.RunAsync(request, deadline.Token));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapGet("/api/events", (int? limit, WorkerRuntimeReadModel model) => Results.Ok(model.Events.ReadRecent(limit)));
        app.MapGet("/api/events/stream", async (HttpContext context, WorkerRuntimeReadModel model) =>
        {
            // Register before committing the response so the first event cannot race the connection setup.
            var subscription = model.Events.Subscribe(context.RequestAborted);
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await foreach (var item in subscription)
            {
                await context.Response.WriteAsync($"id: {item.Id}\nevent: {item.Type}\ndata: {System.Text.Json.JsonSerializer.Serialize(item)}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });
        await app.StartAsync(ct);
        return app;
    }
}
