namespace CodexWorker;

/// <summary>Shared human-readable Issue identity formatting.</summary>
public static class IssueFormatting
{
    public static string Display(GitHubIssue issue) => $"{issue.Title} #{issue.Number}";

    public static string ReportHeading(GitHubIssue issue) => $"# {Display(issue)}\n\n";
}
