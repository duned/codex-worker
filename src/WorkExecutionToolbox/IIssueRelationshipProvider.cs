namespace WorkExecutionToolbox;

/// <summary>
/// Provider-neutral Issue reads. Hosts construct providers
/// with their own HTTP and authentication dependencies. Implementations resolve internal IDs,
/// validate provider-specific repository rules, and propagate cancellation; no CLI is required.
/// </summary>
public interface IIssueProvider
{
    /// <summary>Returns null when the Issue is missing or not visible.</summary>
    Task<IssueSummary?> GetIssueAsync(IssueReference issue, CancellationToken cancellationToken = default);
}

/// <summary>Issue reads and initial relationship operations using repository-scoped human numbers.</summary>
public interface IIssueRelationshipProvider : IIssueDependencyProvider
{
    /// <summary>Returns complete relationships, or null when the Issue is missing or not visible.</summary>
    Task<IssueRelationships?> GetRelationshipsAsync(IssueReference issue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a child's parent; null clears it. Numbers are in the child's repository.
    /// Reject self-links and cycles and preflight visibility before writing. Preview performs no writes.
    /// An already satisfied request returns Unchanged. A different existing parent returns Conflict;
    /// callers must explicitly clear it before setting a new parent. Verify writes by reading back.
    /// </summary>
    Task<RelationshipChangeResult> SetParentAsync(SetParentRequest request, CancellationToken cancellationToken = default);
}

public sealed record SetParentRequest
{
    public SetParentRequest(IssueReference child, int? parentIssueNumber, bool previewOnly = false)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (parentIssueNumber is { } number)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number, nameof(parentIssueNumber));
            if (number == child.Number)
                throw new ArgumentException("An Issue cannot be its own parent.", nameof(parentIssueNumber));
        }
        Child = child;
        ParentIssueNumber = parentIssueNumber;
        PreviewOnly = previewOnly;
    }

    public IssueReference Child { get; }
    public int? ParentIssueNumber { get; }
    public bool PreviewOnly { get; }
}

public sealed record SetDependencyRequest
{
    public SetDependencyRequest(IssueReference issue, int blockerIssueNumber, bool applied, bool previewOnly = false)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockerIssueNumber);
        if (blockerIssueNumber == issue.Number)
            throw new ArgumentException("An Issue cannot depend on itself.", nameof(blockerIssueNumber));
        Issue = issue;
        BlockerIssueNumber = blockerIssueNumber;
        Applied = applied;
        PreviewOnly = previewOnly;
    }

    public IssueReference Issue { get; }
    public int BlockerIssueNumber { get; }
    public bool Applied { get; }
    public bool PreviewOnly { get; }
}
