using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace WorkExecutionToolbox;

public sealed partial class GitHubIssueProvider : IIssueRepositoryProvider
{
    // Keep HTTP fan-out small, including on cold repositories. No child read outlives the scan.
    private const int RootReadConcurrency = 4;

    public async Task<IReadOnlyList<IssueSummary>> ListRootsAsync(RepositoryContext repository,
        IssueListState state = IssueListState.All, IssueRootOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        repository = GitHubRepositoryContext.Create(repository.Repository);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        options ??= new();
        options.Validate();
        using var operation = EnsureReadOperation();
        var requests = 0;
        void BeforeRequest()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref requests) > options.MaxRequests) throw SafetyLimit("request count");
        }

        // The repository kind separates this key from every Issue/relationship read.
        var scope = new IssueReference(repository, 1);
        var kind = "repository-" + state.ToString().ToLowerInvariant();
        DateTimeOffset? snapshotAt = null;
        DateTimeOffset? fetchedAt = null;
        var listing = await CachedReadAsync(scope, kind, async () =>
        {
            var previous = _operation.Value?.Refresh == true ? null :
                await _cache.ReadAsync(scope, kind, cancellationToken, allowStaleListing: true);
            if (previous is not null && previous.Pages > options.MaxPages) throw SafetyLimit("repository pagination");
            var started = _cache.Now;
            fetchedAt = started;
            snapshotAt = previous?.SnapshotAt ?? previous?.FetchedAt ?? started;
            var issues = previous?.Issues.ToDictionary(item => item.Summary.Issue.Number) ?? [];
            var changed = new HashSet<int>();
            var ids = new HashSet<long>();
            var generation = await _cache.CaptureAsync(scope, cancellationToken);
            // A short-lived listing advances via updated Issues, with a full reconciliation every
            // seven days. Overlap the timestamp by a second to avoid timestamp precision gaps.
            var since = previous is null ? "" : "&since=" + Uri.EscapeDataString(
                previous.FetchedAt.AddSeconds(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            for (var page = 1; page <= options.MaxPages; page++)
            {
                BeforeRequest();
                using var document = await SendAsync(HttpMethod.Get,
                    $"repos/{repository.Repository}/issues?state={state.ToString().ToLowerInvariant()}&sort=created&direction=asc&per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}{since}",
                    null, allowMissing: false, readBody: true, cancellationToken);
                if (document is null || document.RootElement.ValueKind != JsonValueKind.Array ||
                    document.RootElement.GetArrayLength() > 100) throw InvalidResponse();
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resolved = ParseIssue(item, repository, allowPullRequest: true);
                    if (resolved is null) continue;
                    if (!changed.Add(resolved.Summary.Issue.Number) || !ids.Add(resolved.DatabaseId) || !Matches(resolved, state))
                        throw InvalidResponse();
                    await _cache.RememberIdentityAsync(resolved, cancellationToken);
                    issues[resolved.Summary.Issue.Number] = resolved;
                    // Listing metadata is authoritative; do not GET it again for identity/state.
                    var metadata = new GitHubIssueCache.Entry(1, repository.Repository,
                        resolved.Summary.Issue.Number, "issue", started, [resolved]);
                    await _cache.StoreAsync(generation, metadata, cancellationToken);
                    _operation.Value?.Reads.TryAdd((repository.Repository.ToLowerInvariant(), metadata.Number, "issue"),
                        new Lazy<Task<GitHubIssueCache.Entry>>(() => Task.FromResult(metadata)));
                }
                if (document.RootElement.GetArrayLength() == 100) continue;
                // Unchanged open metadata cannot inherit the long-lived snapshot. Recheck it to
                // detect closures, disappearance or state changes omitted by a filtered delta.
                if (previous is not null)
                {
                    foreach (var old in previous.Issues.Where(item => item.Summary.State == IssueState.Open &&
                                 !changed.Contains(item.Summary.Issue.Number)))
                    {
                        var current = await ResolveAsync(old.Summary.Issue, cancellationToken, BeforeRequest);
                        if (current is null || !Matches(current, state)) issues.Remove(old.Summary.Issue.Number);
                        else issues[old.Summary.Issue.Number] = current;
                    }
                }
                var result = issues.Values.ToArray();
                if (result.Select(item => item.DatabaseId).Distinct().Count() != result.Length) throw InvalidResponse();
                // Incremental growth must not evade the repository-size bound represented by
                // the original scan. Exact hundreds require an empty terminal page.
                var requiredPages = Math.Max(Math.Max(previous?.Pages ?? 0, page), result.Length / 100 + 1);
                if (requiredPages > options.MaxPages) throw SafetyLimit("repository pagination");
                return (result, requiredPages);
            }
            throw SafetyLimit("repository pagination");
        }, cancellationToken, () => snapshotAt, () => fetchedAt);
        if (listing.Pages > options.MaxPages) throw SafetyLimit("repository pagination");

        var roots = new ConcurrentBag<IssueSummary>();
        await Parallel.ForEachAsync(listing.Issues, new ParallelOptions
        {
            MaxDegreeOfParallelism = RootReadConcurrency, CancellationToken = cancellationToken
        }, async (resolved, token) =>
        {
            var parent = await ReadParentAsync(resolved.Summary.Issue, token, BeforeRequest);
            if (parent is null) roots.Add(resolved.Summary);
            else if (parent.DatabaseId == resolved.DatabaseId || parent.Summary.Issue.Number == resolved.Summary.Issue.Number)
                throw InvalidResponse();
        });
        return roots.OrderBy(item => item.Issue.Number).ToArray();
    }

    private static bool Matches(ResolvedIssue issue, IssueListState state) => state == IssueListState.All ||
        state == IssueListState.Open && issue.Summary.State == IssueState.Open ||
        state == IssueListState.Closed && issue.Summary.State == IssueState.Closed;
}
