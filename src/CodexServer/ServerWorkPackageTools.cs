namespace CodexServer;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

public sealed record WorkPackageIssue(string Key, string OperationId, string Title, string Body, int? IssueNumber = null);
public sealed record WorkPackageDependency(string ChildKey, string BlockerKey);
public sealed record WorkPackageStep(string Operation, string Key, object Result);

/// <summary>Composes existing control-plane operations; owns neither scheduling nor Git integration.</summary>
[McpServerToolType]
public sealed class ServerWorkPackageTools(IRegistryStore registry, IServerGitHubAdministrationService github,
    IssuePlanningOperations operations, ExecutionMaintenanceStore maintenance)
{
    [McpServerTool(Name = "plan_project_work_package", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [McpMeta("securitySchemes", JsonValue = """[{"type":"oauth2","scopes":["mcp:write"]}]""")]
    [Description("Create/update a parent and 1–20 children in one project, then assign and verify native parent and blocked-by relationships. Keys are unique within this request; dependencies refer to child keys. Reuse each Issue's stable GUID operationId on retries; never replace an uncertain creation identity. Existing issueNumber updates that Issue. Returns ordered step results and Issue/execution identities, stopping on failure. No readiness or dispatch unless explicitly requested with child keys. Dispatch uses Server eligibility and scheduling; inspect execution IDs with get_execution. No branch operations.")]
    public async Task<object> Plan(string projectId, WorkPackageIssue parent, WorkPackageIssue[] children,
        WorkPackageDependency[] dependencies, CancellationToken cancellationToken,
        string[]? readyChildren = null, string[]? dispatchChildren = null)
    {
        Validate(parent, children, dependencies, readyChildren ?? [], dispatchChildren ?? []);
        var steps = new List<WorkPackageStep>();
        var identities = new Dictionary<string, int>(StringComparer.Ordinal);
        var planning = new ServerIssuePlanningTools(registry, github, operations);
        var execution = new ServerExecutionTools(registry, github, maintenance);
        object Result(bool completed) => new { outcome = completed ? "accepted" : "partial", projectId, issues = identities, steps };
        var currentOperation = "project";
        var currentKey = parent.Key;
        try
        {
            var project = await registry.GetProjectAsync(projectId, cancellationToken);
            if (project is null)
            {
                steps.Add(new("project", parent.Key, new { outcome = "rejected", reason = "project-not-found" }));
                return Result(false);
            }
            foreach (var issue in new[] { parent }.Concat(children))
            {
                currentOperation = "issue";
                currentKey = issue.Key;
                var result = issue.IssueNumber is { } number
                    ? await planning.Update(projectId, number, cancellationToken, issue.Title, issue.Body)
                    : await planning.Create(projectId, issue.OperationId, issue.Title, issue.Body, cancellationToken);
                steps.Add(new("issue", issue.Key, result));
                var json = JsonSerializer.SerializeToElement(result);
                if (json.GetProperty("outcome").GetString() != "accepted") return Result(false);
                identities.Add(issue.Key, json.GetProperty("issue").GetProperty("Number").GetInt32());
            }
            if (identities.Values.Distinct().Count() != identities.Count)
            {
                steps.Add(new("relationships", parent.Key, new { outcome = "rejected", reason = "duplicate-issue-identities" }));
                return Result(false);
            }
            currentOperation = "parent";
            currentKey = parent.Key;
            var parents = await github.SetIssueParentForChildrenAsync(projectId, identities[parent.Key],
                new(children.Select(child => identities[child.Key]).ToArray()), cancellationToken);
            steps.Add(new("parent", parent.Key, Describe(parents)));
            if (parents.Items.Any(item => item.Status is not ("changed" or "unchanged"))) return Result(false);
            foreach (var group in dependencies.GroupBy(dependency => dependency.ChildKey))
            {
                currentOperation = "blocked-by";
                currentKey = group.Key;
                var result = await github.SetIssueBlockedByBatchAsync(projectId, identities[group.Key],
                    new(group.Select(dependency => identities[dependency.BlockerKey]).ToArray(), true), cancellationToken);
                steps.Add(new("blocked-by", group.Key, Describe(result)));
                if (result.Items.Any(item => item.Status is not ("changed" or "unchanged"))) return Result(false);
            }
            using (github.BeginReadOperation(refresh: true))
            {
                foreach (var child in children)
                {
                    currentOperation = "verify";
                    currentKey = child.Key;
                    var relationships = await github.GetIssueRelationshipsAsync(projectId, identities[child.Key], cancellationToken);
                    var verified = relationships is not null && relationships.Parent?.Number == identities[parent.Key] && dependencies
                        .Where(dependency => dependency.ChildKey == child.Key)
                        .All(dependency => relationships.BlockedBy.Any(blocker => blocker.Number == identities[dependency.BlockerKey]));
                    steps.Add(new("verify", child.Key, new { outcome = verified ? "accepted" : "rejected" }));
                    if (!verified) return Result(false);
                }
            }
            foreach (var key in readyChildren ?? [])
            {
                if (project.IssueReadyLabel is not { Length: > 0 } readyLabel)
                {
                    steps.Add(new("ready", key, new { outcome = "rejected", reason = "ready-label-not-configured" }));
                    return Result(false);
                }
                currentOperation = "ready";
                currentKey = key;
                var result = await planning.Label(projectId, identities[key], readyLabel, true, cancellationToken);
                steps.Add(new("ready", key, result));
                if (!Accepted(result)) return Result(false);
            }
            foreach (var key in dispatchChildren ?? [])
            {
                currentOperation = "dispatch";
                currentKey = key;
                var result = await execution.Dispatch(projectId, identities[key], cancellationToken);
                steps.Add(new("dispatch", key, result));
                if (!Accepted(result)) return Result(false);
            }
            return Result(true);
        }
        catch (Exception exception) when (exception is GitHubIssueNotFoundException or KeyNotFoundException or
            InvalidDataException or GitHubIssueWriteUnavailableException or GitHubReadUnavailableException)
        {
            // Preserve completed steps without exposing provider diagnostics or guessing whether a write applied.
            steps.Add(new(currentOperation, currentKey, new { outcome = "uncertain", reason = "refresh-before-retry" }));
            return Result(false);
        }
    }

    private static object Describe(GitHubIssueRelationshipBatchResult result) => new
    {
        result.Operation,
        items = result.Items.Select(item => new { item.IssueNumber, item.RelatedIssueNumber, item.Status, item.Changed })
    };

    private static bool Accepted(object result) =>
        JsonSerializer.SerializeToElement(result).GetProperty("outcome").GetString() == "accepted";

    private static void Validate(WorkPackageIssue parent, WorkPackageIssue[] children,
        WorkPackageDependency[] dependencies, string[] ready, string[] dispatch)
    {
        if (parent is null || children is null || dependencies is null || children.Length is < 1 or > 20 || dependencies.Length > 100)
            throw new McpException("A package requires a parent, 1–20 children and at most 100 dependencies.");
        var issues = new[] { parent }.Concat(children).ToArray();
        if (issues.Any(issue => issue is null || string.IsNullOrWhiteSpace(issue.Key) || issue.Key.Length > 80 ||
                !Guid.TryParseExact(issue.OperationId, "N", out _) || issue.IssueNumber is <= 0 ||
                GitHubIssueMutationValidation.CreateError(new(issue.Title, issue.Body)) is not null) ||
            issues.Select(issue => issue.Key).Distinct(StringComparer.Ordinal).Count() != issues.Length ||
            issues.Select(issue => issue.OperationId).Distinct(StringComparer.Ordinal).Count() != issues.Length)
            throw new McpException("Issues require unique keys and GUID operationIds, valid titles/bodies and positive existing numbers.");
        var existingNumbers = issues.Where(issue => issue.IssueNumber.HasValue).Select(issue => issue.IssueNumber).ToArray();
        if (existingNumbers.Distinct().Count() != existingNumbers.Length)
            throw new McpException("Existing Issue numbers must be distinct within a package.");
        var keys = children.Select(child => child.Key).ToHashSet(StringComparer.Ordinal);
        if (dependencies.Any(dependency => dependency is null || !keys.Contains(dependency.ChildKey) ||
                !keys.Contains(dependency.BlockerKey) || dependency.ChildKey == dependency.BlockerKey) ||
            dependencies.Distinct().Count() != dependencies.Length ||
            ready.Concat(dispatch).Any(key => !keys.Contains(key)) || ready.Distinct().Count() != ready.Length ||
            dispatch.Distinct().Count() != dispatch.Length)
            throw new McpException("Dependencies and explicit readiness/dispatch must reference distinct child keys without self-links.");
    }
}
