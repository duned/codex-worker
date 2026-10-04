using System.Globalization;
using System.Text.Json;

namespace WorkExecutionToolbox;

public sealed partial class GitHubIssueProvider
{
    public async Task<IssueRelationships?> GetRelationshipsAsync(IssueReference issue,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(issue, cancellationToken);
        if (target is null) return null;
        var relations = await ReadRelationsAsync(target, cancellationToken);
        return new(target.Summary, relations.Parent?.Summary,
            relations.Children.Select(item => item.Summary).ToArray(),
            relations.BlockedBy.Select(item => item.Summary).ToArray(),
            relations.Blocking.Select(item => item.Summary).ToArray());
    }

    private async Task<Relations> ReadRelationsAsync(ResolvedIssue target, CancellationToken cancellationToken,
        int maxPages = 100, Action? beforeRequest = null)
    {
        var issue = target.Summary.Issue;
        var parent = await ReadParentAsync(issue, cancellationToken, beforeRequest);
        var children = await ReadRelatedIssuesAsync(issue, "/sub_issues", cancellationToken, maxPages, beforeRequest);
        var blockedBy = await ReadRelatedIssuesAsync(issue, "/dependencies/blocked_by", cancellationToken, maxPages, beforeRequest);
        var blocking = await ReadRelatedIssuesAsync(issue, "/dependencies/blocking", cancellationToken, maxPages, beforeRequest);
        var relations = new Relations(parent, children, blockedBy, blocking);
        var identities = new Dictionary<int, long> { [issue.Number] = target.DatabaseId };
        var numbers = new Dictionary<long, int> { [target.DatabaseId] = issue.Number };
        foreach (var reference in relations.References())
        {
            var number = reference.Summary.Issue.Number;
            if (number == issue.Number || reference.DatabaseId == target.DatabaseId ||
                identities.TryGetValue(number, out var id) && id != reference.DatabaseId ||
                numbers.TryGetValue(reference.DatabaseId, out var otherNumber) && otherNumber != number)
                throw InvalidResponse();
            identities[number] = reference.DatabaseId;
            numbers[reference.DatabaseId] = number;
        }
        return relations;
    }

    private async Task<IReadOnlyList<ResolvedIssue>> ReadRelatedIssuesAsync(IssueReference issue, string suffix,
        CancellationToken cancellationToken, int maxPages = 100, Action? beforeRequest = null)
    {
        var results = new List<ResolvedIssue>();
        var numbers = new HashSet<int>();
        var ids = new HashSet<long>();
        // Construct pages locally; never follow untrusted Link URLs with credentials.
        for (var page = 1; page <= maxPages; page++)
        {
            beforeRequest?.Invoke();
            using var document = await SendAsync(issue.Repository, issue.Number,
                $"{suffix}?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                HttpMethod.Get, null, allowMissing: false, readBody: true, cancellationToken);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > 100) throw InvalidResponse();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var resolved = ParseIssue(item, issue.Repository) ?? throw InvalidResponse();
                if (resolved.Summary.Issue.Number == issue.Number || !numbers.Add(resolved.Summary.Issue.Number) ||
                    !ids.Add(resolved.DatabaseId)) throw InvalidResponse();
                results.Add(resolved);
            }
            if (document.RootElement.GetArrayLength() < 100) return results;
        }
        throw SafetyLimit("relationship pagination");
    }

    private static GitHubIssueException SafetyLimit(string limit) =>
        new(GitHubIssueFailure.LimitExceeded, $"GitHub Issue inspection exceeded the {limit} safety limit.");

    private sealed record Relations(ResolvedIssue? Parent, IReadOnlyList<ResolvedIssue> Children,
        IReadOnlyList<ResolvedIssue> BlockedBy, IReadOnlyList<ResolvedIssue> Blocking)
    {
        public IEnumerable<ResolvedIssue> References() =>
            (Parent is { } parent ? new[] { parent } : []).Concat(Children).Concat(BlockedBy).Concat(Blocking);
    }
}
