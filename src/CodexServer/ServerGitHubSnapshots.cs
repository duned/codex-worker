namespace CodexServer;

using System.Text.Json;

public sealed partial class ServerGitHubReadService
{
    private const string ReferenceFields = "id fullDatabaseId number title state url labels(first:100){nodes{name} pageInfo{hasNextPage}}";
    private const string SnapshotFields = "id fullDatabaseId number title body state createdAt updatedAt url labels(first:100){nodes{name} pageInfo{hasNextPage}}";

    public async Task<IReadOnlyList<int>> ListIssueNumbersAsync(CentralProject project, GitHubIssueQuery query,
        CancellationToken cancellationToken)
    {
        ValidateProject(project);
        if (GitHubIssueQueryValidation.Error(query) is { } error) throw new InvalidDataException(error);
        var arguments = new List<string> { "issue", "list", "--repo", project.Repository, "--state", query.State,
            "--limit", query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "number" };
        if (query.Label is not null) { arguments.Add("--label"); arguments.Add(query.Label); }
        var result = await RunReadAsync(project.Repository, arguments, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var numbers = document.RootElement.EnumerateArray().Select(item => item.GetProperty("number").GetInt32()).ToArray();
            if (numbers.Length > query.Limit || numbers.Any(number => number <= 0) || numbers.Distinct().Count() != numbers.Length)
                throw new JsonException("Invalid Issue list.");
            return numbers;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new GitHubReadUnavailableException(project.Repository, $"GitHub Issue list was invalid for '{project.Repository}'.", "invalid-response", ex);
        }
    }

    public async Task<IReadOnlyList<GitHubIssueSnapshot>> ReadSnapshotsAsync(CentralProject project,
        IReadOnlyDictionary<int, string?> identities, CancellationToken cancellationToken)
    {
        ValidateProject(project);
        if (identities.Count is < 1 or > 10 || identities.Keys.Any(number => number <= 0))
            throw new InvalidDataException("Snapshot batches require 1 to 10 positive Issue numbers.");
        var repositoryParts = project.Repository.Split('/');
        var connectionFields = $"nodes{{{ReferenceFields}}} pageInfo{{hasNextPage endCursor}}";
        var fields = $"{SnapshotFields} parent{{{ReferenceFields}}} subIssues(first:100){{{connectionFields}}} blockedBy(first:100){{{connectionFields}}} blocking(first:100){{{connectionFields}}}";
        var selections = identities.Select(pair => pair.Value is { } id
            ? $"i{pair.Key}:node(id:{JsonSerializer.Serialize(id)}){{... on Issue{{{fields}}}}}"
            : $"i{pair.Key}:repository(owner:{JsonSerializer.Serialize(repositoryParts[0])},name:{JsonSerializer.Serialize(repositoryParts[1])}){{issue(number:{pair.Key}){{{fields}}}}}");
        var data = await QueryAsync(project.Repository, "query{" + string.Join(" ", selections) + "}", cancellationToken);
        try
        {
            var snapshots = new List<GitHubIssueSnapshot>();
            foreach (var pair in identities)
            {
                var item = data.GetProperty($"i{pair.Key}");
                if (pair.Value is null && item.ValueKind != JsonValueKind.Null) item = item.GetProperty("issue");
                if (item.ValueKind == JsonValueKind.Null) continue;
                var known = new Dictionary<int, GitHubIssueIdentity>();
                var issue = await ReadGraphIssueAsync(project, item, known, cancellationToken);
                if (issue.Number != pair.Key || pair.Value is { } expected && RequiredString(item, "id") != expected)
                    throw new JsonException("Issue identity did not match.");
                var parent = item.GetProperty("parent");
                var parentIssue = parent.ValueKind == JsonValueKind.Null ? null :
                    await ReadGraphReferenceAsync(project, parent, known, cancellationToken);
                var children = await ReadGraphConnectionAsync(project, item, "subIssues", known, cancellationToken);
                var blockedBy = await ReadGraphConnectionAsync(project, item, "blockedBy", known, cancellationToken);
                var blocking = await ReadGraphConnectionAsync(project, item, "blocking", known, cancellationToken);
                var relationships = new GitHubIssueRelationships(1, project.Repository, issue.Number,
                    new(issue.Number, issue.Title, issue.State.ToLowerInvariant(), issue.Url) { Labels = issue.Labels },
                    parentIssue, children, blockedBy, blocking);
                issue = issue with { BlockedBy = blockedBy.Select(reference => new GitHubBlockingIssue(reference.Number,
                    reference.Title, reference.State, reference.Url)).ToArray() };
                snapshots.Add(new(known[issue.Number], issue, relationships, known));
            }
            return snapshots;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new GitHubReadUnavailableException(project.Repository,
                $"GitHub Issue snapshot results were invalid for '{project.Repository}'.", "invalid-response", ex);
        }
    }

    private async Task<JsonElement> QueryAsync(string repository, string query, CancellationToken cancellationToken)
    {
        var response = await RunReadAsync(repository, ["api", "graphql", "-f", $"query={query}"], cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(response.StandardOutput);
            if (document.RootElement.TryGetProperty("errors", out var errors) &&
                (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0)) throw new JsonException("GraphQL errors.");
            return document.RootElement.GetProperty("data").Clone();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new GitHubReadUnavailableException(repository, $"GitHub snapshot query failed for '{repository}'.", "invalid-response", ex);
        }
    }

    private async Task<IReadOnlyList<GitHubRelationshipIssue>> ReadGraphConnectionAsync(CentralProject project,
        JsonElement issue, string kind, Dictionary<int, GitHubIssueIdentity> known, CancellationToken cancellationToken)
    {
        var connection = issue.GetProperty(kind);
        var references = new List<GitHubRelationshipIssue>();
        var cursors = new HashSet<string>();
        while (true)
        {
            foreach (var node in connection.GetProperty("nodes").EnumerateArray())
                references.Add(await ReadGraphReferenceAsync(project, node, known, cancellationToken));
            var page = connection.GetProperty("pageInfo");
            if (!page.GetProperty("hasNextPage").GetBoolean()) return references;
            var cursor = RequiredString(page, "endCursor");
            if (references.Count >= 2500 || !cursors.Add(cursor)) throw new JsonException("Too many relationship pages.");
            var data = await QueryAsync(project.Repository,
                $"query{{node(id:{JsonSerializer.Serialize(RequiredString(issue, "id"))}){{... on Issue{{{kind}(first:100,after:{JsonSerializer.Serialize(cursor)}){{nodes{{{ReferenceFields}}} pageInfo{{hasNextPage endCursor}}}}}}}}}}", cancellationToken);
            connection = data.GetProperty("node").GetProperty(kind);
        }
    }

    private async Task<GitHubRelationshipIssue> ReadGraphReferenceAsync(CentralProject project, JsonElement item,
        Dictionary<int, GitHubIssueIdentity> known, CancellationToken cancellationToken)
    {
        var number = item.GetProperty("number").GetInt32();
        var state = RequiredString(item, "state").ToLowerInvariant();
        var url = RequiredString(item, "url");
        var id = RequiredString(item, "id");
        if (number <= 0 || state is not ("open" or "closed") || !ValidIssueUrl(url, project.Repository, number) || string.IsNullOrWhiteSpace(id))
            throw new JsonException("Invalid repository Issue reference.");
        var databaseIdValue = item.GetProperty("fullDatabaseId");
        var databaseId = databaseIdValue.ValueKind == JsonValueKind.String
            ? long.Parse(RequiredString(item, "fullDatabaseId"), System.Globalization.CultureInfo.InvariantCulture)
            : databaseIdValue.GetInt64();
        if (databaseId <= 0) throw new JsonException("Invalid Issue database identity.");
        known[number] = new(id, databaseId);
        var labels = item.GetProperty("labels");
        IReadOnlyList<string> names = labels.GetProperty("nodes").EnumerateArray().Select(label => RequiredString(label, "name")).ToArray();
        if (labels.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
            names = (await ReadIssueFieldsAsync(project, number, cancellationToken)
                ?? throw new JsonException("Issue labels were unavailable.")).Labels;
        return new(number, RequiredString(item, "title"), state, url) { Labels = names };
    }

    private async Task<ManagedGitHubIssue> ReadGraphIssueAsync(CentralProject project, JsonElement item,
        Dictionary<int, GitHubIssueIdentity> known, CancellationToken cancellationToken)
    {
        var reference = await ReadGraphReferenceAsync(project, item, known, cancellationToken);
        return new(reference.Number, reference.Title, RequiredString(item, "body"), reference.State.ToUpperInvariant(),
            item.GetProperty("createdAt").GetDateTimeOffset(), item.GetProperty("updatedAt").GetDateTimeOffset(),
            reference.Url, reference.Labels, [], false, []);
    }

}
