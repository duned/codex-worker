namespace WorkExecutionToolbox;

public enum IssueListState
{
    Open,
    Closed,
    All
}

/// <summary>Repository inspection only; roots are defined solely by native parent relations.</summary>
public interface IIssueRepositoryProvider : IIssueProvider
{
    /// <summary>Returns every parentless Issue in the selected state, or fails on an exhausted bound.</summary>
    Task<IReadOnlyList<IssueSummary>> ListRootsAsync(RepositoryContext repository,
        IssueListState state = IssueListState.All, IssueRootOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record IssueRootOptions
{
    public int MaxPages { get; init; } = 100;
    public int MaxRequests { get; init; } = 5000;

    internal void Validate()
    {
        if (MaxPages is < 1 or > 100 || MaxRequests is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(IssueRootOptions),
                "Root listing limits must use pages 1–100 and requests 1–5000.");
    }
}
