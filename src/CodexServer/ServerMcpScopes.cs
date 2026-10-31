namespace CodexServer;

/// <summary>Explicit tool policy. Scopes are independent; unknown tools never inherit authority.</summary>
public static class ServerMcpScopes
{
    public const string Read = "mcp:read";
    public const string Write = "mcp:write";
    public const string Maintain = "mcp:maintain";
    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly(new[] { Read, Write, Maintain });

    public static string? Required(string? tool) => tool switch
    {
        "server_health" or "list_workers" or "list_projects" or "list_executions" or "get_execution" or
        "get_execution_operation" or "get_worker_maintenance" or "get_worker_provisioning_operation" or "wet_inspect" => Read,
        "dispatch_execution" or "cancel_execution" or "retry_execution_report" or "create_project_issue" or
        "update_project_issue" or "set_project_issue_label" or "plan_project_work_package" or "wet_dependencies" => Write,
        "request_worker_maintenance" or "request_worker_provisioning" or "cancel_worker_provisioning" => Maintain,
        _ => null
    };

    public static bool Valid(string? scope) => scope is { Length: > 0 and <= 128 } &&
        scope.Split(' ').All(part => Supported.Contains(part, StringComparer.Ordinal));

    public static bool Same(string left, string right) =>
        left.Split(' ').ToHashSet(StringComparer.Ordinal).SetEquals(right.Split(' '));

    public static bool Includes(string granted, string required) => granted.Split(' ').Contains(required, StringComparer.Ordinal);
}
