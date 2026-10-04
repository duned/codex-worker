using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace WorkExecutionToolbox;

public sealed partial class GitHubIssueProvider
{
    public async Task<IReadOnlyList<IssueSummary>?> GetBlockedByAsync(IssueReference issue,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnsureReadOperation();
        var target = await ResolveAsync(issue, cancellationToken);
        if (target is null) return null;
        return (await ReadBlockedByAsync(target.Summary.Issue, cancellationToken)).Select(item => item.Summary).ToArray();
    }

    public async Task<RelationshipChangeResult> SetDependencyAsync(SetDependencyRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnsureReadOperation();
        ArgumentNullException.ThrowIfNull(request);
        var batch = await SetDependenciesAsync(new(request.Issue, [request.BlockerIssueNumber],
            request.Applied, request.PreviewOnly), cancellationToken);
        return batch.Relations[0].Result;
    }

    public async Task<DependencyBatchResult> SetDependenciesAsync(SetDependenciesRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginReadOperation(refresh: true);
        ArgumentNullException.ThrowIfNull(request);
        // Validate scope before credentials, then preflight every relation before the first write.
        var repository = GitHubRepositoryContext.Create(request.Issue.Repository.Repository);
        var target = new IssueReference(repository, request.Issue.Number);
        var blockers = new Dictionary<int, ResolvedIssue>();
        HashSet<int> current;
        try
        {
            if (await ResolveAsync(target, cancellationToken) is null)
                return Rejected(request, "Target Issue is missing, invisible or a pull request.");
            foreach (var number in request.BlockerIssueNumbers)
            {
                var blocker = await ResolveAsync(new(repository, number), cancellationToken);
                if (blocker is null)
                    return Rejected(request, $"Blocker Issue #{number} is missing, invisible or a pull request; no writes performed.");
                blockers.Add(number, blocker);
            }
            current = (await ReadBlockedByAsync(target, cancellationToken)).Select(item => item.Summary.Issue.Number).ToHashSet();
            if (request.Applied)
            {
                var graph = new Dictionary<int, IReadOnlyList<ResolvedIssue>>();
                foreach (var number in request.BlockerIssueNumbers.Where(number => !current.Contains(number)))
                    if (await ReachesTargetAsync(new(repository, number), target.Number, graph, cancellationToken))
                        return Rejected(request, $"Blocker Issue #{number} would create a dependency cycle; no writes performed.");
            }
        }
        catch (GitHubIssueException error) { return Rejected(request, error.Message); }

        var results = new List<DependencyChangeResult>();
        foreach (var number in request.BlockerIssueNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Contains(number) == request.Applied)
            {
                results.Add(new(number, new(RelationshipChangeStatus.Unchanged)));
                continue;
            }
            if (request.PreviewOnly)
            {
                results.Add(new(number, new(RelationshipChangeStatus.Preview)));
                continue;
            }
            GitHubIssueException? writeError = null;
            var path = $"repos/{repository.Repository}/issues/{target.Number.ToString(CultureInfo.InvariantCulture)}/dependencies/blocked_by";
            try
            {
                var id = blockers[number].DatabaseId;
                using var content = request.Applied ? JsonContent.Create(new { issue_id = id }) : null;
                using var response = await SendMutationAsync(target, request.Applied ? HttpMethod.Post : HttpMethod.Delete,
                    request.Applied ? path : $"{path}/{id.ToString(CultureInfo.InvariantCulture)}", content, cancellationToken);
            }
            catch (GitHubIssueException error) { writeError = error; }

            // Even a transport failure can occur after GitHub has applied a mutation.
            try
            {
                var actual = await ReadBlockedByAsync(target, cancellationToken);
                var satisfied = actual.Any(item => item.Summary.Issue.Number == number) == request.Applied;
                results.Add(new(number, satisfied
                    ? new(RelationshipChangeStatus.Changed, writeError is null ? null : "Write reported an error, but the requested state was verified.")
                    : new(RelationshipChangeStatus.Failed, writeError?.Message ?? "GitHub did not retain the requested dependency; read-back verification failed.")));
            }
            catch (GitHubIssueException error)
            {
                results.Add(new(number, new(RelationshipChangeStatus.Partial,
                    $"Dependency write attempted but resulting state could not be verified. {error.Message} Refresh before retrying.")));
                // Uncertain state is not authority to keep writing the remainder.
                foreach (var remaining in request.BlockerIssueNumbers.Skip(results.Count))
                    results.Add(new(remaining, new(RelationshipChangeStatus.Failed, "Not attempted because a preceding write could not be verified.")));
                break;
            }
        }
        var failed = results.Any(item => item.Result.Status is RelationshipChangeStatus.Failed or RelationshipChangeStatus.Partial);
        var changed = results.Any(item => item.Result.Status == RelationshipChangeStatus.Changed);
        var uncertain = results.Any(item => item.Result.Status == RelationshipChangeStatus.Partial);
        var status = failed ? changed || uncertain ? RelationshipChangeStatus.Partial : RelationshipChangeStatus.Failed
            : changed ? RelationshipChangeStatus.Changed
            : results.Any(item => item.Result.Status == RelationshipChangeStatus.Preview) ? RelationshipChangeStatus.Preview
            : RelationshipChangeStatus.Unchanged;
        return new(status, results.AsReadOnly());
    }

    private static DependencyBatchResult Rejected(SetDependenciesRequest request, string diagnostic) =>
        new(RelationshipChangeStatus.Failed, request.BlockerIssueNumbers
            .Select(number => new DependencyChangeResult(number, new(RelationshipChangeStatus.Failed, diagnostic))).ToArray());

    private async Task<bool> ReachesTargetAsync(IssueReference start, int target,
        Dictionary<int, IReadOnlyList<ResolvedIssue>> graph, CancellationToken cancellationToken)
    {
        var pending = new Stack<(int Number, bool Exiting)>();
        var visited = new HashSet<int>();
        var active = new HashSet<int>();
        pending.Push((start.Number, false));
        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = entry.Number;
            if (entry.Exiting)
            {
                active.Remove(number);
                visited.Add(number);
                continue;
            }
            if (number == target) return true;
            if (visited.Contains(number)) continue;
            if (!active.Add(number))
                throw new GitHubIssueException(GitHubIssueFailure.Provider, "Blocker dependency graph contains a cycle; no writes performed.");
            if (!graph.TryGetValue(number, out var dependencies))
            {
                if (graph.Count >= 1000)
                    throw new GitHubIssueException(GitHubIssueFailure.Provider, "Dependency graph exceeds the 1000-Issue safety limit; no writes performed.");
                dependencies = await ReadBlockedByAsync(new(start.Repository, number), cancellationToken);
                graph.Add(number, dependencies);
            }
            pending.Push((number, true));
            foreach (var dependency in dependencies) pending.Push((dependency.Summary.Issue.Number, false));
        }
        return false;
    }

    private Task<IReadOnlyList<ResolvedIssue>> ReadBlockedByAsync(IssueReference issue, CancellationToken cancellationToken) =>
        ReadRelatedIssuesAsync(issue, "/dependencies/blocked_by", cancellationToken);
}
