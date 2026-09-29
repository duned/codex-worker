namespace CodexWorker;

/// <summary>Shared human-readable formatting for persisted execution identities.</summary>
public static class ExecutionFormatting
{
    public static string ShortId(Guid executionId) => executionId.ToString("N")[..8];

    public static string Display(Guid executionId) => $"[{ShortId(executionId)}]";

    public static string OperationalIdentity(GitHubIssue issue, Guid executionId) =>
        $"Issue · {Display(executionId)} · {IssueFormatting.Display(issue)}";
}
