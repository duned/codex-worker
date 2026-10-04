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

    /// <summary>Preflights all children, preserves individual outcomes, and stops on unsuccessful writes.</summary>
    Task<ParentBatchResult> SetParentsAsync(SetParentsRequest request, CancellationToken cancellationToken = default);

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

/// <summary>Assigns one parent to 1–50 distinct children in the same repository.</summary>
public sealed record SetParentsRequest
{
    public SetParentsRequest(IssueReference parent, IReadOnlyList<int> childIssueNumbers, bool previewOnly = false)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(childIssueNumbers);
        if (childIssueNumbers.Count is < 1 or > 50)
            throw new ArgumentException("A parent batch must contain 1 to 50 children.", nameof(childIssueNumbers));
        var numbers = childIssueNumbers.ToArray();
        foreach (var number in numbers)
            _ = new SetParentRequest(new(parent.Repository, number), parent.Number, previewOnly);
        if (numbers.Distinct().Count() != numbers.Length)
            throw new ArgumentException("A parent batch cannot contain duplicate children.", nameof(childIssueNumbers));
        Parent = parent;
        ChildIssueNumbers = Array.AsReadOnly(numbers);
        PreviewOnly = previewOnly;
    }

    public IssueReference Parent { get; }
    public IReadOnlyList<int> ChildIssueNumbers { get; }
    public bool PreviewOnly { get; }
}

public sealed record ParentChangeResult(int ChildIssueNumber, RelationshipChangeResult Result);

/// <summary>Ordered child outcomes; Partial requires relationship refresh before retrying.</summary>
public sealed record ParentBatchResult(int ParentIssueNumber, RelationshipChangeStatus Status, IReadOnlyList<ParentChangeResult> Relations);
