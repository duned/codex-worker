namespace CodexServer;

using System.ComponentModel;
using System.Text.Json.Nodes;
using CodexProvisioning;
using Microsoft.AspNetCore.Http.Features;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class ServerMcp
{
    private static readonly object AuthorizationKey = new();

    public static void AddServerMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services
        .AddMcpServer(options => options.ServerInfo = new Implementation
        {
            Name = "Codex Server", Version = ServerApplication.DisplayVersion
        })
        .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
        .WithTools<ServerReadOnlyTools>()
        .WithTools<ServerExecutionTools>()
        .WithTools<ServerIssuePlanningTools>()
        .WithTools<ServerWorkPackageTools>()
        .WithTools<ServerToolboxTools>()
        .WithTools<ServerWorkerTools>()
        .WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (request, ct) =>
            {
                var result = await next(request, ct);
                foreach (var tool in result.Tools)
                {
                    if (ServerMcpScopes.Required(tool.Name) is null)
                        throw new McpException("Tool authorization policy unavailable.");
                }
                return result;
            });
            filters.AddCallToolFilter(next => async (request, ct) =>
            {
                var required = ServerMcpScopes.Required(request.Params?.Name);
                if (required is null) throw new McpException("Tool authorization policy unavailable.");
                var http = request.Services?.GetRequiredService<IHttpContextAccessor>().HttpContext;
                if (http?.Items[AuthorizationKey] is not string granted)
                    throw new McpException("Tool authorization required.");
                // Empty scope marks the existing management/session authority, established only by middleware.
                if (granted.Length != 0 && !ServerMcpScopes.Includes(granted, required))
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = "Insufficient scope. Required: " + required }],
                        Meta = new JsonObject
                        {
                            ["mcp/www_authenticate"] = new JsonArray(Challenge(http.RequestServices.GetRequiredService<ServerConfiguration>()) +
                                $", error=\"insufficient_scope\", scope=\"{required}\"")
                        }
                    };
                return await next(request, ct);
            });
        });
    }

    private static string Challenge(ServerConfiguration configuration) => McpOAuthEndpoints.Issuer(configuration) is { } issuer
        ? $"Bearer realm=\"Codex Server\", resource_metadata=\"{issuer}/.well-known/oauth-protected-resource/mcp\""
        : "Bearer realm=\"Codex Server\"";

    public static void UseServerMcpAuthorization(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }
        var sessions = context.RequestServices.GetRequiredService<AdministrationSessions>();
        // Browser-origin requests obey the existing exact origin/host policy, even with a bearer token.
        if (context.Request.Headers.ContainsKey("Origin") && !sessions.OriginAllowed(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var configuration = context.RequestServices.GetRequiredService<ServerConfiguration>();
        var supplied = context.Request.Headers.Authorization.ToString();
        var existingAuthorized = sessions.Authorized(context);
        var granted = !existingAuthorized && McpOAuthEndpoints.Issuer(configuration) is not null &&
            supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? await context.RequestServices.GetRequiredService<McpOAuthStore>().GrantedScopesAsync(supplied[7..], context.RequestAborted)
            : null;
        if (!existingAuthorized && granted is null)
        {
            context.Response.Headers.WWWAuthenticate = Challenge(configuration);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        context.Items[AuthorizationKey] = existingAuthorized ? "" : granted;
        if (context.Request.ContentLength > 16 * 1024)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = 16 * 1024;
        await next(context);
    });
}

/// <summary>Read-only projections of the same registry queries used by the administration API.</summary>
[McpServerToolType]
public sealed class ServerReadOnlyTools(IRegistryStore store, IServerHealthService health)
{
    [McpServerTool(Name = "server_health", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["mcp:read"]}]""")]
    [Description("Get Server health, persistence readiness and product version.")]
    public async Task<object> ServerHealth(CancellationToken cancellationToken)
    {
        var result = await health.GetHealthAsync(cancellationToken);
        return new { version = ServerApplication.DisplayVersion, result.Status, result.PersistenceAvailable, result.ControlPlaneInitialized };
    }

    [McpServerTool(Name = "list_workers", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["mcp:read"]}]""")]
    [Description("List managed Worker status, version, capabilities and current execution references. Current references are capped at 100 per active state; counts remain authoritative.")]
    public async Task<object> Workers(CancellationToken cancellationToken, int limit = 50, int offset = 0)
    {
        PageBounds(limit, offset);
        var workers = (await store.GetWorkersAsync(cancellationToken)).OrderBy(worker => worker.WorkerId, StringComparer.Ordinal).ToArray();
        var page = workers.Skip(offset).Take(limit).ToArray();
        var assigned = await store.ListExecutionsAsync(new(State: "Assigned", Limit: 100), cancellationToken);
        var running = await store.ListExecutionsAsync(new(State: "Running", Limit: 100), cancellationToken);
        return new
        {
            limit, offset, hasMore = workers.Length > offset + limit,
            currentExecutionsMayBeIncomplete = assigned.Count == 100 || running.Count == 100,
            items = page.Select(worker => new
            {
                worker.WorkerId, displayName = Text(worker.DisplayName), version = Text(worker.WorkerVersion),
                availability = Text(worker.Availability), lifecycleState = Text(worker.LifecycleState), worker.SchedulingPolicy,
                worker.ActiveExecutions, worker.ActiveAssignments, worker.AvailableCapacity, worker.MaximumCapacity, worker.LastSeenAtUtc,
                capabilities = worker.Capabilities.Take(32).Select(capability => new
                {
                    type = Text(capability.Type, 40), name = Text(capability.Name, 100), version = Text(capability.Version, 100)
                }),
                currentExecutions = assigned.Concat(running).Where(execution => execution.AssignedWorkerId == worker.WorkerId)
                    .Select(execution => new { execution.Id, execution.ProjectId, state = Text(execution.State), stage = Text(execution.CurrentStage) })
            })
        };
    }

    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["mcp:read"]}]""")]
    [Description("List configured project summaries without validation commands, local configuration or credentials.")]
    public async Task<object> Projects(CancellationToken cancellationToken, int limit = 50, int offset = 0)
    {
        PageBounds(limit, offset);
        var projects = (await store.GetProjectsAsync(cancellationToken)).OrderBy(project => project.Id, StringComparer.Ordinal).ToArray();
        return new
        {
            limit, offset, hasMore = projects.Length > offset + limit,
            items = projects.Skip(offset).Take(limit).Select(project => new
            {
                project.Id, name = Text(project.Name), repository = Text(project.Repository),
                defaultBranch = Text(project.DefaultBranch), project.Enabled, project.Revision, project.MaxParallelTasks,
                project.CreatedAtUtc, project.UpdatedAtUtc
            })
        };
    }

    [McpServerTool(Name = "list_executions", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["mcp:read"]}]""")]
    [Description("List execution history, newest first. State: Queued, Assigned, Running, Completed, Failed or Cancelled. Limit 1–100, offset 0–10000.")]
    public async Task<object> Executions(CancellationToken cancellationToken, string? projectId = null, string? state = null,
        string? workType = null, string? workId = null, int limit = 50, int offset = 0)
    {
        var query = new ExecutionQuery(projectId, state, workType, workId, limit, offset);
        if (ExecutionAdministrationValidation.QueryError(query) is { } error) throw new McpException(error);
        var executions = await store.ListExecutionsAsync(query, cancellationToken);
        return new { limit, offset, items = executions.Select(DescribeExecution) };
    }

    [McpServerTool(Name = "get_execution", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["mcp:read"]}]""")]
    [Description("Get execution state and bounded structured diagnostics/history: stage, timing, validation, integration, recovery and retry lineage. No raw logs or free-form completion reports.")]
    public async Task<object> Execution(string executionRequestId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(executionRequestId, "N", out _)) throw new McpException("executionRequestId must be a 32-character execution identity.");
        var execution = await store.GetExecutionAsync(executionRequestId, cancellationToken);
        return execution is null ? new { found = false } : DescribeExecution(execution);
    }

    private static object DescribeExecution(ExecutionRequest execution) => new
    {
        execution.Id, execution.ProjectId, state = Text(execution.State), execution.AssignedWorkerId,
        work = new { type = Text(execution.WorkReference.Type, 40), id = Text(execution.WorkReference.Id, 80) },
        execution.CreatedAtUtc, execution.AssignedAtUtc, execution.StartedAtUtc, execution.CompletedAtUtc,
        execution.DurationMilliseconds, stage = Text(execution.CurrentStage),
        validationResult = Text(execution.ValidationResult), integrationResult = Text(execution.IntegrationResult),
        failureClassification = Text(execution.FailureClassification), execution.Recoverable,
        recoveryState = Text(execution.RecoveryState), execution.RetryOfExecutionId, execution.AttemptNumber,
        stageHistory = execution.StageStartedAtUtc?.OrderBy(stage => stage.Value).Take(32)
            .Select(stage => new { stage = Text(stage.Key), startedAtUtc = stage.Value })
    };

    private static void PageBounds(int limit, int offset)
    {
        if (ExecutionAdministrationValidation.QueryError(new(Limit: limit, Offset: offset)) is { } error)
            throw new McpException(error);
    }

    // Defense in depth for persisted/display strings. Never expose paths, scopes, raw diagnostics or summaries.
    internal static string? Text(string? value, int maximum = 200)
    {
        if (value is null) return null;
        var redacted = SecretSanitizer.Redact(value);
        redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
            @"(?:github_pat_|gh[pousr]_)[A-Za-z0-9_]+|-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z]+ )?PRIVATE KEY-----|$)",
            "[redacted]", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        redacted = System.Text.RegularExpressions.Regex.Replace(redacted,
            @"(?<![\w/])(?:/[^\s,;]+|[A-Za-z]:\\[^\s,;]+)", "[redacted-path]");
        return SecretSanitizer.Sanitize(redacted, maximum);
    }
}
