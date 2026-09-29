namespace CodexWorker;

/// <summary>Shared human-readable Issue identity formatting.</summary>
public static class IssueFormatting
{
    public static string Display(GitHubIssue issue) => $"{issue.Title} #{issue.Number}";

    /// <summary>Compact identity used on every operational execution event.</summary>
    public static string OperationalIdentity(GitHubIssue issue) => $"Issue · {Display(issue)}";

    public static string ReportHeading(GitHubIssue issue) => $"# {Display(issue)}\n\n";
}
