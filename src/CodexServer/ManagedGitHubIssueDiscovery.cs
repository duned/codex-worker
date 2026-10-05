namespace CodexServer;

using System.Text.Json;

public sealed record GitHubIssueDiscoveryQuery(int Limit = 50, string? After = null);
public sealed record GitHubIssueNumberPage(IReadOnlyList<int> Numbers, string? NextCursor);
public sealed record ManagedGitHubIssuePage(IReadOnlyList<ManagedGitHubIssue> Issues, string? NextCursor,
    IReadOnlyList<int>? MissingNumbers = null);
public sealed record ManagedGitHubIssueCandidate(WorkReference WorkReference, string Classification,
    IReadOnlyList<string> Reasons);
public sealed record ManagedGitHubIssueDiscovery(string ProjectId, string Repository,
    IReadOnlyList<ManagedGitHubIssueCandidate> Candidates, string? NextCursor, bool IsComplete);

public static class GitHubIssueDiscoveryValidation
{
    public static string? Error(GitHubIssueDiscoveryQuery query) => query.Limit is < 1 or > 100 ||
        query.After is { } cursor && (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 1024 || cursor.Any(char.IsControl))
        ? "Discovery requires limit 1 to 100 and an optional printable cursor up to 1024 characters." : null;
}

public sealed partial class ServerGitHubReadService
{
    public async Task<GitHubIssueNumberPage> ListDiscoveryIssueNumbersAsync(CentralProject project,
        GitHubIssueDiscoveryQuery query, CancellationToken cancellationToken)
    {
        ValidateProject(project);
        if (GitHubIssueDiscoveryValidation.Error(query) is { } error) throw new InvalidDataException(error);
        if (GitHubIssueQueryValidation.Error(new(Label: project.IssueReadyLabel)) is { } labelError)
            throw new InvalidDataException(labelError);
        var parts = project.Repository.Split('/');
        var after = query.After is null ? "" : $",after:{JsonSerializer.Serialize(query.After)}";
        var labels = project.IssueReadyLabel is null ? "" : $",labels:[{JsonSerializer.Serialize(project.IssueReadyLabel)}]";
        var data = await QueryAsync(project.Repository,
            $"query{{repository(owner:{JsonSerializer.Serialize(parts[0])},name:{JsonSerializer.Serialize(parts[1])}){{issues(first:{query.Limit},states:[OPEN],orderBy:{{field:CREATED_AT,direction:ASC}}{after}{labels}){{nodes{{number url}} pageInfo{{hasNextPage endCursor}}}}}}}}",
            cancellationToken);
        try
        {
            var connection = data.GetProperty("repository").GetProperty("issues");
            var nodes = connection.GetProperty("nodes");
            if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > query.Limit)
                throw new JsonException("Invalid discovery page size.");
            var numbers = new HashSet<int>();
            foreach (var node in nodes.EnumerateArray())
            {
                var number = node.GetProperty("number").GetInt32();
                if (number <= 0 || !ValidIssueUrl(RequiredString(node, "url"), project.Repository, number))
                    throw new JsonException("Invalid discovery Issue identity.");
                numbers.Add(number);
            }
            var page = connection.GetProperty("pageInfo");
            var next = page.GetProperty("hasNextPage").GetBoolean() ? RequiredString(page, "endCursor") : null;
            if (next is not null && (nodes.GetArrayLength() == 0 || next == query.After ||
                GitHubIssueDiscoveryValidation.Error(new(After: next)) is not null))
                throw new JsonException("Invalid discovery continuation.");
            return new(numbers.Order().ToArray(), next);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new GitHubReadUnavailableException(project.Repository,
                $"GitHub discovery page was invalid for '{project.Repository}'.", "invalid-response", ex);
        }
    }

    public async Task<ManagedGitHubIssuePage> ReadDiscoveryPageAsync(CentralProject project,
        GitHubIssueDiscoveryQuery query, CancellationToken cancellationToken = default)
    {
        var page = await ListDiscoveryIssueNumbersAsync(project, query, cancellationToken);
        var issues = new List<ManagedGitHubIssue>();
        var missing = new List<int>();
        foreach (var batch in page.Numbers.Chunk(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = await ReadSnapshotsAsync(project, batch.ToDictionary(number => number, _ => (string?)null), cancellationToken);
            missing.AddRange(batch.Where(number => !snapshots.Any(snapshot => snapshot.Issue.Number == number)));
            issues.AddRange(snapshots.Select(snapshot => ManagedGitHubIssueEligibility.Evaluate(project, snapshot.Issue)));
        }
        return new(issues, page.NextCursor, missing);
    }
}
