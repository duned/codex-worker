namespace WorkExecutionToolbox;

/// <summary>Repository-scoped blocked-by operations; prerequisites block the target Issue.</summary>
public interface IIssueDependencyProvider : IIssueProvider
{
    /// <summary>Returns the complete direct list, or null when the target is missing or invisible.</summary>
    Task<IReadOnlyList<IssueSummary>?> GetBlockedByAsync(IssueReference issue, CancellationToken cancellationToken = default);

    /// <summary>Preflights visibility and cycles, skips satisfied requests and verifies writes. Preview never writes.</summary>
    Task<RelationshipChangeResult> SetDependencyAsync(SetDependencyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Preflights the entire batch before writing; returns each relation's outcome without rollback.</summary>
    Task<DependencyBatchResult> SetDependenciesAsync(SetDependenciesRequest request, CancellationToken cancellationToken = default);
}

public sealed record SetDependenciesRequest
{
    public SetDependenciesRequest(IssueReference issue, IReadOnlyList<int> blockerIssueNumbers, bool applied, bool previewOnly = false)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(blockerIssueNumbers);
        if (blockerIssueNumbers.Count is < 1 or > 50)
            throw new ArgumentException("A dependency batch must contain 1 to 50 Issue numbers.", nameof(blockerIssueNumbers));
        var numbers = blockerIssueNumbers.ToArray();
        foreach (var number in numbers)
            _ = new SetDependencyRequest(issue, number, applied, previewOnly);
        if (numbers.Distinct().Count() != numbers.Length)
            throw new ArgumentException("A dependency batch cannot contain duplicate Issue numbers.", nameof(blockerIssueNumbers));
        Issue = issue;
        BlockerIssueNumbers = Array.AsReadOnly(numbers);
        Applied = applied;
        PreviewOnly = previewOnly;
    }

    public IssueReference Issue { get; }
    public IReadOnlyList<int> BlockerIssueNumbers { get; }
    public bool Applied { get; }
    public bool PreviewOnly { get; }
}

public sealed record DependencyChangeResult(int BlockerIssueNumber, RelationshipChangeResult Result);

/// <summary>Partial includes verified successes alongside failures or uncertain mutations. Refresh before retrying.</summary>
public sealed record DependencyBatchResult(RelationshipChangeStatus Status, IReadOnlyList<DependencyChangeResult> Relations);
