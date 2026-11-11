using System.Globalization;
using System.Text.Json;
using CodexProvisioning;

namespace CodexWorker;

public sealed partial class GitHubClient
{
    public async Task<string?> GetParentProvenanceAsync(int issueNumber, DateTimeOffset startedAt,
        int? currentParent, CancellationToken ct)
    {
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        // Filter only native relationship events plus transfers (which invalidate repository continuity).
        const string query = """
            query($owner:String!,$name:String!,$number:Int!,$after:String){repository(owner:$owner,name:$name){nameWithOwner issue(number:$number){id number createdAt parent{number repository{nameWithOwner}} timelineItems(first:100,after:$after,itemTypes:[PARENT_ISSUE_ADDED_EVENT,PARENT_ISSUE_REMOVED_EVENT,TRANSFERRED_EVENT]){totalCount pageInfo{hasNextPage endCursor} nodes{__typename ... on ParentIssueAddedEvent{id createdAt parent{number repository{nameWithOwner}}} ... on ParentIssueRemovedEvent{id createdAt parent{number repository{nameWithOwner}}}}}}}
            """;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var parts = repository.Split('/');
            var events = new List<IssueParentEvent>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null, issueId = null;
            DateTimeOffset? created = null;
            int? total = null;
            for (var page = 0; page < 10; page++)
            {
                var args = new List<string> { "api", "graphql", "-f", $"query={query}", "-f", $"owner={parts[0]}",
                    "-f", $"name={parts[1]}", "-F", $"number={issueNumber.ToString(CultureInfo.InvariantCulture)}" };
                if (cursor is not null) args.AddRange(["-f", $"after={cursor}"]);
                // A read-only, bounded inspection: retain the failing stage before generic gh
                // wrapping/retries erase it. Never put provider output in the candidate.
                ProcessResult result;
                try { result = await runCommand(args, deadline.Token); }
                catch (InvalidOperationException) { throw ConflictIssueIntent.Refuse("issue-parent-history-process-start"); }
                if (result.ExitCode != 0)
                {
                    if (HasGraphQlErrors(result.StandardOutput))
                        throw ConflictIssueIntent.Refuse("issue-parent-history-graphql");
                    throw ConflictIssueIntent.Refuse(ClassifyFailure(result.StandardError) == GitHubFailureKind.AuthenticationOrAuthorization
                        ? "issue-parent-history-authentication" : "issue-parent-history-process-failed");
                }
                using var document = JsonDocument.Parse(result.StandardOutput);
                var root = document.RootElement;
                if (root.TryGetProperty("errors", out var errors) &&
                    (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
                    throw ConflictIssueIntent.Refuse("issue-parent-history-graphql");
                var repo = root.GetProperty("data").GetProperty("repository");
                if (!string.Equals(repo.GetProperty("nameWithOwner").GetString(), repository, StringComparison.OrdinalIgnoreCase))
                    throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                var issue = repo.GetProperty("issue");
                var id = issue.GetProperty("id").GetString();
                var createdAt = issue.GetProperty("createdAt").GetDateTimeOffset();
                if (issue.GetProperty("number").GetInt32() != issueNumber || string.IsNullOrWhiteSpace(id) ||
                    issueId is not null && issueId != id || created is not null && created != createdAt)
                    throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                issueId = id;
                created = createdAt;
                int? ReadParent(JsonElement parent)
                {
                    if (parent.ValueKind == JsonValueKind.Null) return null;
                    var number = parent.GetProperty("number").GetInt32();
                    if (number <= 0 || number == issueNumber || !string.Equals(repository,
                        parent.GetProperty("repository").GetProperty("nameWithOwner").GetString(), StringComparison.OrdinalIgnoreCase))
                        throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                    return number;
                }
                if (ReadParent(issue.GetProperty("parent")) != currentParent)
                    throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                var timeline = issue.GetProperty("timelineItems");
                var count = timeline.GetProperty("totalCount").GetInt32();
                if (count is < 0 or > 1000 || total is not null && total != count)
                    throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                total = count;
                var nodes = timeline.GetProperty("nodes");
                if (nodes.GetArrayLength() > 100) throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                foreach (var node in nodes.EnumerateArray())
                {
                    var kind = node.GetProperty("__typename").GetString();
                    if (kind is not ("ParentIssueAddedEvent" or "ParentIssueRemovedEvent"))
                        throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                    var parent = ReadParent(node.GetProperty("parent"));
                    if (parent is null) throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                    events.Add(new(node.GetProperty("id").GetString() ?? "", node.GetProperty("createdAt").GetDateTimeOffset(),
                        kind == "ParentIssueAddedEvent", parent.Value));
                }
                var info = timeline.GetProperty("pageInfo");
                if (!info.GetProperty("hasNextPage").GetBoolean())
                {
                    if (events.Count != total) throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
                    var proof = new IssueParentProvenance(repository, issueNumber, issueId, createdAt, startedAt, currentParent, [.. events]);
                    if (proof.Refusal(startedAt) is { } refusal) throw ConflictIssueIntent.Refuse(refusal);
                    return JsonSerializer.Serialize(proof);
                }
                cursor = info.GetProperty("endCursor").GetString();
                if (nodes.GetArrayLength() == 0 || string.IsNullOrWhiteSpace(cursor) || !cursors.Add(cursor))
                    throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
            }
            throw ConflictIssueIntent.Refuse("issue-parent-history-incomplete");
        }
        catch (ConflictIntentException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw ConflictIssueIntent.Refuse("issue-parent-history-timeout"); }
        catch (ProcessTimeoutException) when (!ct.IsCancellationRequested)
        { throw ConflictIssueIntent.Refuse("issue-parent-history-timeout"); }
        catch (GitHubEnvironmentException)
        { throw ConflictIssueIntent.Refuse("issue-parent-history-environment"); }
        catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested)
        { throw ConflictIssueIntent.Refuse("issue-parent-history-process-failed"); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        { throw ConflictIssueIntent.Refuse("issue-parent-history-process-start"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw ConflictIssueIntent.Refuse("issue-parent-history-result-invalid"); }
    }

    private static bool HasGraphQlErrors(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("errors", out var errors) &&
                (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0);
        }
        catch (JsonException) { return false; }
    }
}
