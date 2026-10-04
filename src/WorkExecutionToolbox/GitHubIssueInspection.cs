namespace WorkExecutionToolbox;

public sealed partial class GitHubIssueProvider
{
    public async Task<IssueGraph?> GetGraphAsync(IssueReference root, IssueGraphOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnsureReadOperation();
        ArgumentNullException.ThrowIfNull(root);
        options ??= new();
        options.Validate();
        var requests = 0;
        void BeforeRequest()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref requests) > options.MaxRequests) throw SafetyLimit("request count");
        }

        var resolvedRoot = await ResolveAsync(root, cancellationToken, BeforeRequest);
        if (resolvedRoot is null) return null;
        var nodes = new Dictionary<int, ResolvedIssue> { [root.Number] = resolvedRoot };
        var ids = new Dictionary<long, int> { [resolvedRoot.DatabaseId] = root.Number };
        var relationships = new Dictionary<int, Relations>();
        var pending = new Queue<(int Number, int Depth)>();
        pending.Enqueue((root.Number, 0));
        var truncated = false;
        while (pending.TryDequeue(out var entry))
        {
            var target = nodes[entry.Number];
            if (entry.Number != root.Number)
            {
                var verified = await ResolveAsync(target.Summary.Issue, cancellationToken, BeforeRequest);
                if (verified is null || verified.DatabaseId != target.DatabaseId)
                    throw InconsistentGraph();
                target = verified;
                nodes[entry.Number] = verified;
            }
            var relations = await ReadRelationsAsync(target, cancellationToken, options.MaxPagesPerRelation, BeforeRequest);
            relationships.Add(entry.Number, relations);
            foreach (var reference in relations.References())
            {
                var number = reference.Summary.Issue.Number;
                if (nodes.TryGetValue(number, out var known))
                {
                    if (known.DatabaseId != reference.DatabaseId) throw InconsistentGraph();
                    continue;
                }
                if (ids.TryGetValue(reference.DatabaseId, out var otherNumber) && otherNumber != number)
                    throw InconsistentGraph();
                if (entry.Depth >= options.MaxDepth)
                {
                    truncated = true;
                    continue;
                }
                if (nodes.Count >= options.MaxIssues) throw SafetyLimit("Issue count");
                nodes.Add(number, reference);
                ids.Add(reference.DatabaseId, number);
                pending.Enqueue((number, entry.Depth + 1));
            }
        }

        var edges = new HashSet<IssueGraphEdge>();
        void Connect(int from, int to, IssueGraphEdgeKind kind)
        {
            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to)) return;
            var edge = new IssueGraphEdge(from, to, kind);
            if (!edges.Contains(edge) && edges.Count >= options.MaxEdges) throw SafetyLimit("edge count");
            edges.Add(edge);
        }

        foreach (var (number, relations) in relationships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relations.Parent is { } parent)
                Connect(parent.Summary.Issue.Number, number, IssueGraphEdgeKind.ParentChild);
            foreach (var child in relations.Children)
                Connect(number, child.Summary.Issue.Number, IssueGraphEdgeKind.ParentChild);
            foreach (var blocker in relations.BlockedBy)
                Connect(number, blocker.Summary.Issue.Number, IssueGraphEdgeKind.BlockedBy);
            foreach (var dependent in relations.Blocking)
                Connect(dependent.Summary.Issue.Number, number, IssueGraphEdgeKind.BlockedBy);
        }

        // Both endpoints have been read from GitHub. Reject asymmetric results instead of
        // treating a missing reverse relation (or a changing snapshot) as a valid package.
        foreach (var edge in edges)
        {
            var from = relationships[edge.FromIssueNumber];
            var to = relationships[edge.ToIssueNumber];
            var consistent = edge.Kind == IssueGraphEdgeKind.ParentChild
                ? from.Children.Any(item => item.Summary.Issue.Number == edge.ToIssueNumber) &&
                    to.Parent?.Summary.Issue.Number == edge.FromIssueNumber
                : from.BlockedBy.Any(item => item.Summary.Issue.Number == edge.ToIssueNumber) &&
                    to.Blocking.Any(item => item.Summary.Issue.Number == edge.FromIssueNumber);
            if (!consistent) throw InconsistentGraph();
        }

        var cycleEdges = FindCycleEdges(edges, cancellationToken);
        return new(resolvedRoot.Summary.Issue,
            nodes.Values.OrderBy(item => item.Summary.Issue.Number).Select(item => item.Summary).ToArray(),
            edges.OrderBy(edge => edge.Kind).ThenBy(edge => edge.FromIssueNumber).ThenBy(edge => edge.ToIssueNumber)
                .Select(edge => edge with { IsCycle = cycleEdges.Contains(edge) }).ToArray(),
            truncated, cycleEdges.Count > 0);
    }

    private static HashSet<IssueGraphEdge> FindCycleEdges(HashSet<IssueGraphEdge> edges, CancellationToken cancellationToken)
    {
        var cycles = new HashSet<IssueGraphEdge>();
        // Separate directed graphs: a child blocked by its parent is not a cycle.
        foreach (var kind in Enum.GetValues<IssueGraphEdgeKind>())
        {
            var adjacency = edges.Where(edge => edge.Kind == kind).GroupBy(edge => edge.FromIssueNumber)
                .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.ToIssueNumber).ToArray());
            var visited = new HashSet<int>();
            var active = new HashSet<int>();
            var pending = new Stack<(int Number, bool Exit, IssueGraphEdge? Incoming)>();
            foreach (var number in adjacency.Keys.Order())
            {
                pending.Push((number, false, null));
                while (pending.TryPop(out var entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Exit) { active.Remove(entry.Number); continue; }
                    if (active.Contains(entry.Number))
                    {
                        if (entry.Incoming is { } edge) cycles.Add(edge);
                        continue;
                    }
                    if (!visited.Add(entry.Number)) continue;
                    active.Add(entry.Number);
                    pending.Push((entry.Number, true, null));
                    if (adjacency.TryGetValue(entry.Number, out var outgoing))
                        foreach (var edge in outgoing.Reverse()) pending.Push((edge.ToIssueNumber, false, edge));
                }
            }
        }
        return cycles;
    }

    private static GitHubIssueException InconsistentGraph() => new(GitHubIssueFailure.InvalidResponse,
        "GitHub returned inconsistent Issue identities or reciprocal relationships. Refresh before reviewing the graph.");
}
