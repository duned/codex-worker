namespace CodexWorker;

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
    int ActiveExecutionCount, int AvailableExecutionCapacity, int ConfiguredProjectCount, int EnabledProjectCount);
public sealed record ProjectRuntimeInfo(string Name, string Repository, bool Enabled, string State,
    int MaxParallelTasks, int ActiveExecutionCount, int AvailableExecutionCapacity, int? ReadyWorkCount);
public sealed record ProjectLifecycleRequest(string Action);
public sealed record ExecutionRepairInfo(int Attempt, int MaximumAttempts, bool PassedAfterRepair);
public sealed record ExecutionRuntimeInfo(Guid ExecutionId, string Project, string Repository, int IssueNumber,
    string IssueTitle, string State, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc,
    long? DurationMilliseconds, string? ValidationOutcome, int RepairCount, IReadOnlyList<ExecutionRepairInfo> Repairs,
    string? Result, string? RecoveryState, string? RecoveryBaseCommit, string? RecoveryStatus,
    Guid? RetryOfExecutionId, int AttemptNumber, bool Resumed);

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
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private volatile string _state = "starting";

    public WorkerRuntimeReadModel(GlobalWorkerConfiguration global,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects, ExecutionHistoryStore history,
        ProjectConfigurationService? configurationService = null)
    {
        _global = global;
        _projects = projects;
        _history = history;
        _configurationService = configurationService;
        Events = new RuntimeEventLog(global.Api.EventHistoryLimit);
        _registry = new ProjectRuntimeRegistry(projects, Events);
    }

    public RuntimeEventLog Events { get; }
    public ProjectRuntimeRegistry Registry => _registry;
    public string State { get => _state; set => _state = value; }

    public async Task<WorkerStatus> StatusAsync(CancellationToken ct)
    {
        var active = (await _history.ReadActiveAsync(ct)).Count;
        var projectCount = _registry.Snapshot().Count;
        return new WorkerStatus(ApplicationVersion.Display, State,
            Math.Max(0, (long)(DateTimeOffset.UtcNow - _startedAtUtc).TotalSeconds),
            _global.Worker.MaxParallelTasks, active, Math.Max(0, _global.Worker.MaxParallelTasks - active),
            projectCount, _registry.Status().Count(project => project.State == ProjectLifecycleState.Enabled));
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
                return new ProjectRuntimeInfo(config.Project.Name, config.Project.Repository, state.State == ProjectLifecycleState.Enabled,
                    state.State.ToString(), config.Worker.MaxParallelTasks, active,
                    Math.Max(0, config.Worker.MaxParallelTasks - active), null);
            }).ToArray();
    }

    public async Task<IReadOnlyList<ExecutionRuntimeInfo>> ExecutionsAsync(int limit, CancellationToken ct)
    {
        var entries = await _history.ReadAllAsync(ct);
        return entries.OrderByDescending(e => e.StartedAtUtc).Take(Math.Clamp(limit, 1, 500)).Select(e =>
            new ExecutionRuntimeInfo(e.ExecutionId, e.Project, e.Repository, e.IssueNumber, e.IssueTitle, e.State,
                e.StartedAtUtc, e.CompletedAtUtc, e.DurationMilliseconds, e.ValidationOutcome, e.RepairCount,
                e.Repairs.Select(repair => new ExecutionRepairInfo(repair.Attempt, repair.MaximumAttempts, repair.PassedAfterRepair)).ToArray(),
                Outcome(e.State), e.RecoveryState, e.RecoveryBaseCommit, e.RecoveryStatus,
                e.RetryOfExecutionId, e.AttemptNumber, e.Resumed)).ToArray();
    }

    private static string? Outcome(string state) => state switch
    {
        "Completed" => "succeeded", "Blocked" => "blocked", "Failed" => "failed",
        "InfrastructureFailure" => "infrastructure-failure", "Cancelled" => "cancelled", _ => null
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
            return Results.Ok(new { draining = true, activeExecutionCount = model.Registry.WorkerActiveExecutionCount,
                drainComplete = model.Registry.WorkerDrainComplete });
        });
        app.MapGet("/api/worker/drain", (WorkerRuntimeReadModel model) =>
            Results.Ok(new { draining = model.Registry.WorkerDraining, activeExecutionCount = model.Registry.WorkerActiveExecutionCount,
                drainComplete = model.Registry.WorkerDrainComplete }));
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
        app.MapGet("/api/executions", async (int? limit, WorkerRuntimeReadModel model, HttpContext context) =>
            Results.Ok(await model.ExecutionsAsync(limit ?? 100, context.RequestAborted)));
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
