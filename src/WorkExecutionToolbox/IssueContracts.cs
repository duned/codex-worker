namespace WorkExecutionToolbox;

/// <summary>A provider's repository identifier, without host configuration or credentials.</summary>
public sealed record RepositoryContext
{
    public RepositoryContext(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        if (repository.Length > 512 || repository.Any(char.IsControl))
            throw new ArgumentException("Repository identifiers must be printable and at most 512 characters.", nameof(repository));
        Repository = repository;
    }

    public string Repository { get; }
}

/// <summary>A repository-scoped human Issue number, never a provider's internal ID.</summary>
public sealed record IssueReference
{
    public IssueReference(RepositoryContext repository, int number)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        Repository = repository;
        Number = number;
    }

    public RepositoryContext Repository { get; }
    public int Number { get; }
}

public enum IssueState
{
    Open,
    Closed
}

public sealed record IssueSummary(IssueReference Issue, string Title, IssueState State, Uri? Url = null);

/// <summary>
/// Organizational parent/child links are independent of execution dependencies.
/// BlockedBy lists prerequisites; Blocking lists Issues that depend on this Issue.
/// References retain repository scope even when a provider permits cross-repository reads.
/// </summary>
public sealed record IssueRelationships(
    IssueSummary Issue,
    IssueSummary? Parent,
    IReadOnlyList<IssueSummary> Children,
    IReadOnlyList<IssueSummary> BlockedBy,
    IReadOnlyList<IssueSummary> Blocking);

public enum RelationshipChangeStatus
{
    Changed,
    Unchanged,
    Preview,
    Failed,
    Partial,
    Conflict
}

/// <summary>
/// Partial means a mutation occurred before a remaining operation failed, or an attempted
/// mutation could not be verified. Refresh before retrying.
/// Conflict requires an explicit caller decision; parent operations never replace a different parent.
/// Diagnostics must be bounded, actionable and free of credentials or raw HTTP responses.
/// </summary>
public sealed record RelationshipChangeResult(RelationshipChangeStatus Status, string? Diagnostic = null);
