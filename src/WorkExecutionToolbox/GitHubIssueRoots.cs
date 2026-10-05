using System.Globalization;
using System.Text.Json;

namespace WorkExecutionToolbox;

public sealed partial class GitHubIssueProvider : IIssueRepositoryProvider
{
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
            if (++requests > options.MaxRequests) throw SafetyLimit("request count");
        }

        var roots = new List<IssueSummary>();
        var numbers = new HashSet<int>();
        var ids = new HashSet<long>();
        // Listing stays fresh; parent reads use the existing operation/cache and refresh policy.
        // Construct pages locally rather than forwarding credentials to Link header URLs.
        for (var page = 1; page <= options.MaxPages; page++)
        {
            BeforeRequest();
            using var document = await SendAsync(HttpMethod.Get,
                $"repos/{repository.Repository}/issues?state={state.ToString().ToLowerInvariant()}&sort=created&direction=asc&per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                null, allowMissing: false, readBody: true, cancellationToken);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > 100) throw InvalidResponse();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = ParseIssue(item, repository, allowPullRequest: true);
                if (resolved is null) continue;
                if (!numbers.Add(resolved.Summary.Issue.Number) || !ids.Add(resolved.DatabaseId) ||
                    state == IssueListState.Open && resolved.Summary.State != IssueState.Open ||
                    state == IssueListState.Closed && resolved.Summary.State != IssueState.Closed)
                    throw InvalidResponse();
                await _cache.RememberIdentityAsync(resolved, cancellationToken);
                var parent = await ReadParentAsync(resolved.Summary.Issue, cancellationToken, BeforeRequest);
                if (parent is null) roots.Add(resolved.Summary);
                else if (parent.DatabaseId == resolved.DatabaseId || parent.Summary.Issue.Number == resolved.Summary.Issue.Number)
                    throw InvalidResponse();
            }
            // Count raw entries, including PRs, to decide whether another page is required.
            if (document.RootElement.GetArrayLength() < 100)
                return roots.OrderBy(item => item.Issue.Number).ToArray();
        }
        throw SafetyLimit("repository pagination");
    }
}
