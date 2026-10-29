namespace CodexServer;

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class ServerIssuePlanningTools(IRegistryStore registry, IServerGitHubAdministrationService github,
    IssuePlanningOperations operations)
{
    [McpServerTool(Name = "create_project_issue", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Create a planning Issue in a configured project, without making it ready. Supply a stable 32-character GUID operationId and reuse it for retries. An uncertain result must be reconciled in GitHub, never retried with a new identity. Use set_project_issue_label explicitly to mark ready.")]
    public async Task<object> Create(string projectId, string operationId, string title, string body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new McpException("operationId must be a 32-character GUID.");
        if (GitHubIssueMutationValidation.CreateError(new(title, body)) is { } error) throw new McpException(error);
        return await Run(async () =>
        {
            await RequireProject(projectId, cancellationToken);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { projectId, title, body }))));
            if (!await operations.ReserveAsync(operationId, fingerprint, cancellationToken))
            {
                var prior = await operations.GetAsync(operationId, cancellationToken);
                if (prior.Fingerprint != fingerprint) return Rejected("operation-scope-conflict");
                return prior.IssueNumber is { } number ? await Current(projectId, number, cancellationToken) : Rejected("creation-uncertain-reconcile-before-retry");
            }
            var result = await github.CreateIssueAsync(projectId, new(title, body), cancellationToken);
            if (result.IssueNumber is not { } issueNumber) return Rejected("creation-uncertain-reconcile-before-retry");
            await operations.CompleteAsync(operationId, issueNumber, cancellationToken);
            return await Current(projectId, issueNumber, cancellationToken);
        });
    }

    [McpServerTool(Name = "update_project_issue", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Update an existing project Issue title and/or body. Does not change readiness or other labels. Repeating the same values is safe.")]
    public async Task<object> Update(string projectId, int issueNumber, CancellationToken cancellationToken,
        string? title = null, string? body = null)
    {
        if (GitHubIssueMutationValidation.UpdateError(new(title, body)) is { } error) throw new McpException(error);
        Number(issueNumber);
        return await Run(async () =>
        {
            await github.UpdateIssueAsync(projectId, issueNumber, new(title, body), cancellationToken);
            return await Current(projectId, issueNumber, cancellationToken);
        });
    }

    [McpServerTool(Name = "set_project_issue_label", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Explicitly apply or remove a project's configured ready or blocked label. No other lifecycle labels are supported. Repeating the desired state is safe.")]
    public async Task<object> Label(string projectId, int issueNumber, string label, bool applied, CancellationToken cancellationToken)
    {
        Number(issueNumber);
        return await Run(async () =>
        {
            await github.SetIssueLabelAsync(projectId, issueNumber, new(label, applied), cancellationToken);
            return await Current(projectId, issueNumber, cancellationToken);
        });
    }

    private async Task RequireProject(string projectId, CancellationToken ct)
    {
        var project = await registry.GetProjectAsync(projectId, ct) ?? throw new KeyNotFoundException();
        if (GitHubRepositoryValidation.Error(project) is { } error) throw new InvalidDataException(error);
    }

    private async Task<object> Current(string projectId, int issueNumber, CancellationToken ct)
    {
        using var refresh = github.BeginReadOperation(refresh: true);
        var issue = await github.GetIssueAsync(projectId, issueNumber, ct);
        return issue is null ? Rejected("issue-not-found-refresh-required") : new
        {
            outcome = "accepted", projectId,
            issue = new
            {
                issue.Number, issue.Url, Title = ServerReadOnlyTools.Text(issue.Title, 256),
                Body = ServerReadOnlyTools.Text(issue.Body, 4000), issue.State,
                Labels = issue.Labels.Take(32).Select(label => ServerReadOnlyTools.Text(label, 100)),
                issue.IsEligible,
                EligibilityReasons = issue.EligibilityReasons.Take(32).Select(reason => ServerReadOnlyTools.Text(reason)),
                contentMayBeTruncated = issue.Body.Length > 4000 || issue.Labels.Count > 32 || issue.EligibilityReasons.Count > 32
            }
        };
    }

    private static void Number(int number)
    {
        if (number <= 0) throw new McpException("Issue number must be positive.");
    }

    private static object Rejected(string reason) => new { outcome = "rejected", reason };
    private static async Task<object> Run(Func<Task<object>> action)
    {
        try { return await action(); }
        catch (GitHubIssueNotFoundException) { return Rejected("issue-not-found"); }
        catch (KeyNotFoundException) { return Rejected("project-not-found"); }
        catch (InvalidDataException) { return Rejected("invalid-project-or-fields"); }
        catch (GitHubIssueWriteUnavailableException) { return Rejected("github-write-uncertain-refresh-before-retry"); }
        catch (GitHubReadUnavailableException) { return Rejected("github-read-unavailable-refresh-required"); }
    }
}
