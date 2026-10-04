using System.Text.Json;

namespace WorkExecutionToolbox;

public sealed partial class GitHubIssueProvider
{
    /// <summary>Returns the native parent, or null when the Issue or parent is missing.</summary>
    public async Task<IssueSummary?> GetParentAsync(IssueReference child, CancellationToken cancellationToken = default)
    {
        using var operation = EnsureReadOperation();
        var resolved = await ResolveAsync(child, cancellationToken);
        return resolved is null ? null : (await ReadParentAsync(resolved.Summary.Issue, cancellationToken))?.Summary;
    }

    /// <summary>
    /// Lists all direct native children in the selected repository; null means the parent is missing.
    /// Cross-repository children and pull requests are rejected rather than returned as scoped Issues.
    /// </summary>
    public async Task<IReadOnlyList<IssueSummary>?> ListChildrenAsync(IssueReference parent, CancellationToken cancellationToken = default)
    {
        using var operation = EnsureReadOperation();
        var resolved = await ResolveAsync(parent, cancellationToken);
        if (resolved is null) return null;
        var children = await ReadRelatedIssuesAsync(resolved.Summary.Issue, "/sub_issues", cancellationToken);
        if (children.Any(item => item.DatabaseId == resolved.DatabaseId)) throw InvalidResponse();
        return children.Select(item => item.Summary).ToArray();
    }

    /// <summary>Explicitly clears the native parent. An absent parent returns Unchanged.</summary>
    public Task<RelationshipChangeResult> ClearParentAsync(IssueReference child, CancellationToken cancellationToken = default) =>
        SetParentAsync(new(child, null), cancellationToken);

    /// <summary>
    /// Sets or clears the native parent, without replacing a different parent. Previews run the same
    /// preflight checks without writing. Preflight failures return Failed and unverified writes return Partial; refresh
    /// GitHub state before retrying. Cancellation propagates, including after a write was sent.
    /// </summary>
    public async Task<RelationshipChangeResult> SetParentAsync(SetParentRequest request, CancellationToken cancellationToken = default)
    {
        using var operation = BeginReadOperation(refresh: true);
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
            GitHubIssueException? writeError = null;
            try
            {
                using var ignored = await SendAsync(child.Summary.Issue.Repository, mutationParent.Summary.Issue.Number,
                    desired is null ? "/sub_issue" : "/sub_issues", desired is null ? HttpMethod.Delete : HttpMethod.Post,
                    new { sub_issue_id = child.DatabaseId }, allowMissing: false, readBody: false, cancellationToken);
            }
            catch (GitHubIssueException ex) { writeError = ex; }

            // A lost response may follow a successful write; verify rather than replaying it.

            // A parent 404 alone cannot distinguish an absent relationship from a now-invisible child.
            var verifiedChild = await ResolveAsync(child.Summary.Issue, cancellationToken);
            if (verifiedChild?.DatabaseId != child.DatabaseId)
                return new(RelationshipChangeStatus.Partial, "Parent write attempted, but the child Issue could not be verified. Refresh before retrying.");
            var actual = await ReadParentAsync(child.Summary.Issue, cancellationToken);
            return actual?.DatabaseId == desired?.DatabaseId
                ? new(RelationshipChangeStatus.Changed, writeError is null ? null : "Write reported an error, but the requested state was verified.")
                : Failed(writeError is null
                    ? "GitHub's parent relationship did not match the requested state after the write. Refresh before retrying."
                    : $"{writeError.Message} Requested parent state was not retained; refresh before retrying.");
        }
        catch (GitHubIssueException ex)
        {
            return writeAttempted
                ? new(RelationshipChangeStatus.Partial, $"{ex.Message} The write may have taken effect; refresh the relationship before retrying.")
                : Failed(ex.Message);
        }
    }

    public async Task<ParentBatchResult> SetParentsAsync(SetParentsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<ParentChangeResult>();
        foreach (var number in request.ChildIssueNumbers)
        {
            var result = await SetParentAsync(new(new(request.Parent.Repository, number),
                request.Parent.Number, previewOnly: true), cancellationToken);
            results.Add(new(number, result));
        }
        if (results.Any(item => item.Result.Status is RelationshipChangeStatus.Failed or RelationshipChangeStatus.Conflict))
        {
            var rejected = results.Select(item => item.Result.Status == RelationshipChangeStatus.Preview
                ? item with { Result = Failed("Not attempted because batch preflight was rejected; no writes performed.") }
                : item).ToArray();
            return new(request.Parent.Number, results.Any(item => item.Result.Status == RelationshipChangeStatus.Conflict)
                ? RelationshipChangeStatus.Conflict : RelationshipChangeStatus.Failed, rejected);
        }
        if (request.PreviewOnly) return new(request.Parent.Number, results.Any(item => item.Result.Status == RelationshipChangeStatus.Preview)
            ? RelationshipChangeStatus.Preview : RelationshipChangeStatus.Unchanged, results.AsReadOnly());

        // Recheck each relationship at mutation time to preserve conflict/cycle safety if state changed.
        for (var i = 0; i < results.Count; i++)
        {
            var number = results[i].ChildIssueNumber;
            var result = await SetParentAsync(new(new(request.Parent.Repository, number), request.Parent.Number), cancellationToken);
            results[i] = new(number, result);
            if (result.Status is not (RelationshipChangeStatus.Changed or RelationshipChangeStatus.Unchanged))
            {
                for (var remaining = i + 1; remaining < results.Count; remaining++)
                    results[remaining] = results[remaining] with
                    {
                        Result = Failed("Not attempted because a preceding operation failed or could not be verified. Refresh relationships before retrying.")
                    };
                return new(request.Parent.Number, result.Status == RelationshipChangeStatus.Partial ||
                    results.Any(item => item.Result.Status == RelationshipChangeStatus.Changed)
                    ? RelationshipChangeStatus.Partial : result.Status, results.AsReadOnly());
            }
        }
        return new(request.Parent.Number, results.Any(item => item.Result.Status == RelationshipChangeStatus.Changed)
            ? RelationshipChangeStatus.Changed : RelationshipChangeStatus.Unchanged, results.AsReadOnly());
    }

    private async Task<ResolvedIssue?> ReadParentAsync(IssueReference child, CancellationToken cancellationToken, Action? beforeRequest = null)
    {
        var entry = await CachedReadAsync(child, "parent", async () =>
        {
            beforeRequest?.Invoke();
            using var document = await SendAsync(child.Repository, child.Number, "/parent", HttpMethod.Get, null,
                allowMissing: true, readBody: true, cancellationToken);
            var parent = document is null ? null : ParseIssue(document.RootElement, child.Repository) ?? throw InvalidResponse();
            return (parent is null ? Array.Empty<ResolvedIssue>() : new[] { parent }, 1);
        }, cancellationToken);
        return entry.Issues.SingleOrDefault();
    }

    private static RelationshipChangeResult Failed(string diagnostic) => new(RelationshipChangeStatus.Failed, diagnostic);
}
