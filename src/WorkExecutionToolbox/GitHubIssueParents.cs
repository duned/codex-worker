using System.Text.Json;

namespace WorkExecutionToolbox;

public sealed partial class GitHubIssueProvider
{
    /// <summary>Returns the native parent, or null when the Issue or parent is missing.</summary>
    public async Task<IssueSummary?> GetParentAsync(IssueReference child, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(child, cancellationToken);
        return resolved is null ? null : (await ReadParentAsync(resolved.Summary.Issue, cancellationToken))?.Summary;
    }

    /// <summary>
    /// Lists all direct native children in the selected repository; null means the parent is missing.
    /// Cross-repository children and pull requests are rejected rather than returned as scoped Issues.
    /// </summary>
    public async Task<IReadOnlyList<IssueSummary>?> ListChildrenAsync(IssueReference parent, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(parent, cancellationToken);
        if (resolved is null) return null;
        var children = new List<IssueSummary>();
        var seen = new HashSet<long>();
        // Bound total requests as well as individual payloads. Never return a silently truncated list.
        for (var page = 1; page <= 100; page++)
        {
            using var document = await SendAsync(resolved.Summary.Issue.Repository, parent.Number,
                $"/sub_issues?per_page=100&page={page.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                HttpMethod.Get, null, allowMissing: false, readBody: true, cancellationToken);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() > 100) throw InvalidResponse();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var child = ParseIssue(element, resolved.Summary.Issue.Repository) ?? throw InvalidResponse();
                if (child.DatabaseId == resolved.DatabaseId || !seen.Add(child.DatabaseId)) throw InvalidResponse();
                children.Add(child.Summary);
            }
            if (document.RootElement.GetArrayLength() < 100) return children;
        }
        throw new GitHubIssueException(GitHubIssueFailure.InvalidResponse, "GitHub child listing exceeded the supported pagination limit.");
    }

    /// <summary>Explicitly clears the native parent. An absent parent returns Unchanged.</summary>
    public Task<RelationshipChangeResult> ClearParentAsync(IssueReference child, CancellationToken cancellationToken = default) =>
        SetParentAsync(new(child, null), cancellationToken);

    /// <summary>
    /// Sets or clears the native parent, without replacing a different parent. Previews run the same
    /// preflight checks without writing. API failures and unverified writes return Failed; refresh
    /// GitHub state before retrying. Cancellation propagates, including after a write was sent.
    /// </summary>
    public async Task<RelationshipChangeResult> SetParentAsync(SetParentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var writeAttempted = false;
        try
        {
            var child = await ResolveAsync(request.Child, cancellationToken);
            if (child is null) return Failed("The child Issue is missing, inaccessible, or a pull request.");
            ResolvedIssue? desired = null;
            if (request.ParentIssueNumber is { } number)
            {
                desired = await ResolveAsync(new(child.Summary.Issue.Repository, number), cancellationToken);
                if (desired is null) return Failed("The parent Issue is missing, inaccessible, or a pull request.");
                if (desired.DatabaseId == child.DatabaseId) return Failed("An Issue cannot be its own parent.");
            }
            var current = await ReadParentAsync(child.Summary.Issue, cancellationToken);
            if (current?.DatabaseId == desired?.DatabaseId) return new(RelationshipChangeStatus.Unchanged);
            if (current is not null && desired is not null)
                return new(RelationshipChangeStatus.Conflict, "The Issue already has a different parent. Explicitly clear it before setting a new parent.");

            if (desired is not null)
            {
                var seen = new HashSet<long> { child.DatabaseId };
                var ancestor = desired;
                for (var depth = 0; ancestor is not null; depth++)
                {
                    if (!seen.Add(ancestor.DatabaseId)) return Failed("The requested parent would create or traverse a parent cycle.");
                    if (depth >= 1000) return Failed("The parent ancestry exceeded the supported safety limit.");
                    ancestor = await ReadParentAsync(ancestor.Summary.Issue, cancellationToken);
                }
            }
            if (request.PreviewOnly) return new(RelationshipChangeStatus.Preview);

            var mutationParent = desired ?? current;
            if (mutationParent is null) throw new InvalidOperationException("A parent mutation requires a resolved parent.");
            writeAttempted = true;
            using var ignored = await SendAsync(child.Summary.Issue.Repository, mutationParent.Summary.Issue.Number,
                desired is null ? "/sub_issue" : "/sub_issues", desired is null ? HttpMethod.Delete : HttpMethod.Post,
                new { sub_issue_id = child.DatabaseId }, allowMissing: false, readBody: false, cancellationToken);

            // A parent 404 alone cannot distinguish an absent relationship from a now-invisible child.
            var verifiedChild = await ResolveAsync(child.Summary.Issue, cancellationToken);
            if (verifiedChild?.DatabaseId != child.DatabaseId)
                return Failed("GitHub accepted the write, but the child Issue could not be verified. Refresh before retrying.");
            var actual = await ReadParentAsync(child.Summary.Issue, cancellationToken);
            return actual?.DatabaseId == desired?.DatabaseId
                ? new(RelationshipChangeStatus.Changed)
                : Failed("GitHub's parent relationship did not match the requested state after the write. Refresh before retrying.");
        }
        catch (GitHubIssueException ex)
        {
            return Failed(writeAttempted
                ? $"{ex.Message} The write may have taken effect; refresh the relationship before retrying."
                : ex.Message);
        }
    }

    private async Task<ResolvedIssue?> ReadParentAsync(IssueReference child, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(child.Repository, child.Number, "/parent", HttpMethod.Get, null,
            allowMissing: true, readBody: true, cancellationToken);
        return document is null ? null : ParseIssue(document.RootElement, child.Repository) ?? throw InvalidResponse();
    }

    private static RelationshipChangeResult Failed(string diagnostic) => new(RelationshipChangeStatus.Failed, diagnostic);
}
