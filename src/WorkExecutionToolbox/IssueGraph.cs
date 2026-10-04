namespace WorkExecutionToolbox;

/// <summary>Read-only, bounded relationship inspection; it conveys no scheduling authority.</summary>
public interface IIssueGraphProvider : IIssueProvider
{
    /// <summary>
    /// Returns null for a missing root. Depth truncation is explicit; other exhausted safety limits
    /// fail the read. Cycles are reported separately per edge kind. Validation covers expanded nodes
    /// only; GitHub reads are not an atomic snapshot.
    /// </summary>
    Task<IssueGraph?> GetGraphAsync(IssueReference root, IssueGraphOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record IssueGraphOptions
{
    public int MaxDepth { get; init; } = 5;
    public int MaxIssues { get; init; } = 100;
    public int MaxEdges { get; init; } = 500;
    public int MaxRequests { get; init; } = 1000;
    public int MaxPagesPerRelation { get; init; } = 10;

    internal void Validate()
    {
        if (MaxDepth is < 0 or > 20 || MaxIssues is < 1 or > 200 || MaxEdges is < 1 or > 2000 ||
            MaxRequests is < 1 or > 5000 || MaxPagesPerRelation is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(IssueGraphOptions),
                "Graph limits must use depth 0–20, Issues 1–200, edges 1–2000, requests 1–5000 and pages 1–100.");
    }
}

public enum IssueGraphEdgeKind
{
    ParentChild,
    BlockedBy
}

/// <summary>ParentChild points from parent to child; BlockedBy points from Issue to prerequisite.</summary>
public sealed record IssueGraphEdge(int FromIssueNumber, int ToIssueNumber, IssueGraphEdgeKind Kind,
    bool IsCycle = false);

public sealed record IssueGraph(IssueReference Root, IReadOnlyList<IssueSummary> Issues,
    IReadOnlyList<IssueGraphEdge> Edges, bool IsDepthTruncated, bool CycleDetected);
