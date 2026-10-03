namespace CodexServer;

using System.Text;

public sealed record GitHubIssueGraphOptions(int MaxDepth = 5, int MaxIssues = 100, int MaxEdges = 500)
{
    public static string? Error(GitHubIssueGraphOptions? options) => options is null ||
        options.MaxDepth is < 0 or > 20 || options.MaxIssues is < 1 or > 200 || options.MaxEdges is < 1 or > 2_000
        ? "GitHub Issue graph limits must use depth 0 to 20, issues 1 to 200, and edges 1 to 2000."
        : null;
}

public sealed record GitHubIssueGraphNode(int Number, string Title, string State, IReadOnlyList<string> Labels,
    string Availability, int? HierarchyDepth)
{
    public bool LabelsTruncated { get; init; }
}

public sealed record GitHubIssueGraphEdge(int FromIssueNumber, int ToIssueNumber, string Kind, bool IsCycle = false);

public sealed record GitHubIssueGraph(int ContractVersion, string Repository, int RootIssueNumber,
    int MaxDepth, int MaxIssues, int MaxEdges, bool IsTruncated, IReadOnlyList<string> TruncationReasons,
    bool CycleDetected, IReadOnlyList<int> RepeatedNodeNumbers, IReadOnlyList<GitHubIssueGraphNode> Nodes,
    IReadOnlyList<GitHubIssueGraphEdge> Edges);

/// <summary>Builds a bounded, read-only graph using the existing Server Issue relationship contract.</summary>
public static class GitHubIssueGraphBuilder
{
    public static async Task<GitHubIssueGraph?> BuildAsync(int rootIssueNumber,
        Func<int, CancellationToken, Task<GitHubIssueRelationships?>> getRelationships,
        GitHubIssueGraphOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getRelationships);
        if (rootIssueNumber <= 0) throw new InvalidDataException("Issue number must be positive.");
        options ??= new GitHubIssueGraphOptions();
        if (GitHubIssueGraphOptions.Error(options) is { } error) throw new InvalidDataException(error);

        var rootRelationships = await getRelationships(rootIssueNumber, cancellationToken);
        if (rootRelationships is null) return null;
        if (rootRelationships.ContractVersion != 1 || string.IsNullOrWhiteSpace(rootRelationships.Repository) ||
            rootRelationships.IssueNumber != rootIssueNumber ||
            rootRelationships.Issue.Number != rootIssueNumber)
            throw new InvalidDataException("GitHub root relationship results did not match the requested Issue.");

        var builder = new Builder(rootRelationships.Repository, rootIssueNumber, options, getRelationships, cancellationToken);
        await builder.TraverseAsync(rootRelationships);
        return builder.CreateGraph();
    }

    public static string RenderText(GitHubIssueGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var output = new StringBuilder();
        var nodes = graph.Nodes.ToDictionary(node => node.Number);
        var hierarchy = graph.Edges.Where(edge => edge.Kind == "parent-child")
            .GroupBy(edge => edge.FromIssueNumber)
            .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.ToIssueNumber).ToArray());
        var dependencies = graph.Edges.Where(edge => edge.Kind == "blocked-by").ToArray();
        var shown = new HashSet<int>();

        void WriteNode(int issueNumber, string indentation, bool root, bool isLast = true, bool repeatedReference = false,
            bool cycleReference = false)
        {
            if (!nodes.TryGetValue(issueNumber, out var node)) return;
            if (!root) output.Append(indentation).Append(isLast ? "└── " : "├── ");
            output.Append(FormatNode(node));
            if (repeatedReference) output.Append(cycleReference ? " (cycle reference)" : " (already shown)");
            output.AppendLine();
            if (repeatedReference) return;

            shown.Add(issueNumber);
            var annotationPrefix = root ? "    " : indentation + (isLast ? "    " : "│   ");
            if (root)
            {
                var parent = graph.Edges.FirstOrDefault(edge => edge.Kind == "parent-child" && edge.ToIssueNumber == issueNumber);
                if (parent is not null && nodes.TryGetValue(parent.FromIssueNumber, out var parentNode))
                    output.Append(annotationPrefix).Append("Parent: ").Append(FormatReference(parentNode)).AppendLine();
            }

            foreach (var edge in dependencies.Where(edge => edge.FromIssueNumber == issueNumber)
                         .OrderBy(edge => edge.ToIssueNumber))
                if (nodes.TryGetValue(edge.ToIssueNumber, out var blocker))
                    output.Append(annotationPrefix).Append("Blocked by: ").Append(FormatReference(blocker)).AppendLine();
            foreach (var edge in dependencies.Where(edge => edge.ToIssueNumber == issueNumber && edge.FromIssueNumber != issueNumber)
                         .OrderBy(edge => edge.FromIssueNumber))
                if (nodes.TryGetValue(edge.FromIssueNumber, out var blocked))
                    output.Append(annotationPrefix).Append("Blocking: ").Append(FormatReference(blocked)).AppendLine();

            if (!hierarchy.TryGetValue(issueNumber, out var children)) return;
            for (var index = 0; index < children.Length; index++)
            {
                var child = children[index];
                var childIsLast = index == children.Length - 1;
                var childSeen = shown.Contains(child.ToIssueNumber);
                var childIndentation = root ? string.Empty : annotationPrefix;
                WriteNode(child.ToIssueNumber, childIndentation, false, childIsLast, childSeen, child.IsCycle);
            }
        }

        WriteNode(graph.RootIssueNumber, string.Empty, true);
        var related = graph.Nodes.Where(node => node.Availability == "available" && !shown.Contains(node.Number)).ToArray();
        if (related.Length > 0)
        {
            output.AppendLine("Related Issues:");
            foreach (var node in related)
                if (!shown.Contains(node.Number)) WriteNode(node.Number, string.Empty, true);
        }
        if (graph.IsTruncated)
            output.Append("Truncated: ").Append(string.Join(", ", graph.TruncationReasons)).AppendLine();
        if (graph.CycleDetected) output.AppendLine("Relationship cycles were detected and stopped.");
        return output.ToString();
    }

    private static string FormatNode(GitHubIssueGraphNode node)
    {
        var title = SafeTerminalText(node.Title);
        var availability = node.Availability is "available" or "reference" ? string.Empty : $" ({node.Availability})";
        return $"#{node.Number} [{node.State}] {title}{FormatLabels(node)}{availability}";
    }

    private static string FormatReference(GitHubIssueGraphNode node) =>
        $"#{node.Number} [{node.State}] {SafeTerminalText(node.Title)}{FormatLabels(node)}";

    private static string FormatLabels(GitHubIssueGraphNode node) => node.Labels.Count == 0 ? string.Empty :
        $" (labels: {string.Join(", ", node.Labels.Select(SafeTerminalText))}{(node.LabelsTruncated ? ", …" : string.Empty)})";

    private static string SafeTerminalText(string value) =>
        new(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private sealed class Builder(string repository, int rootIssueNumber, GitHubIssueGraphOptions options,
        Func<int, CancellationToken, Task<GitHubIssueRelationships?>> getRelationships, CancellationToken cancellationToken)
    {
        private readonly Dictionary<int, GitHubIssueGraphNode> _nodes = [];
        private readonly HashSet<GitHubIssueGraphEdge> _edges = [];
        private readonly HashSet<int> _repeated = [];
        private readonly HashSet<string> _truncationReasons = new(StringComparer.Ordinal);
        private bool _cycleDetected;

        public async Task TraverseAsync(GitHubIssueRelationships root)
        {
            // Breadth-first expansion gives every Issue its shortest traversal distance,
            // even when it is reached through both hierarchy and dependency paths.
            var pending = new Queue<(GitHubRelationshipIssue Issue, int Depth)>();
            var queued = new HashSet<int> { rootIssueNumber };
            SetNode(root.Issue, "available", 0);
            pending.Enqueue((root.Issue, 0));
            while (pending.TryDequeue(out var entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relationships = entry.Issue.Number == rootIssueNumber ? root :
                    await getRelationships(entry.Issue.Number, cancellationToken);
                if (relationships is null)
                {
                    SetNode(entry.Issue, "missing", _nodes[entry.Issue.Number].HierarchyDepth);
                    continue;
                }
                var number = entry.Issue.Number;
                if (relationships.ContractVersion != 1 ||
                    !string.Equals(relationships.Repository, repository, StringComparison.OrdinalIgnoreCase) ||
                    relationships.IssueNumber != number || relationships.Issue.Number != number)
                    throw new InvalidDataException("GitHub relationship results did not match the requested repository Issue.");
                SetNode(relationships.Issue, "available", _nodes[number].HierarchyDepth);

                void Connect(GitHubRelationshipIssue reference, int from, int to, string kind, int? hierarchyDepth,
                    bool expand)
                {
                    if (!_edges.Any(edge => edge.FromIssueNumber == from && edge.ToIssueNumber == to && edge.Kind == kind) &&
                        _edges.Count >= options.MaxEdges)
                    {
                        _truncationReasons.Add("max-edges");
                        return;
                    }
                    if (!EnsureNode(reference, hierarchyDepth)) return;
                    _edges.Add(new(from, to, kind));
                    if (!expand) return;
                    if (!queued.Add(reference.Number))
                    {
                        // Inverse endpoints describing the same edge are normal, not duplicate nodes.
                        return;
                    }
                    pending.Enqueue((reference, entry.Depth + 1));
                }

                if (relationships.Parent is { } parent)
                    Connect(parent, parent.Number, number, "parent-child", null, false);

                if (entry.Depth >= options.MaxDepth && relationships.SubIssues.Count > 0)
                    _truncationReasons.Add("max-depth");
                else
                    foreach (var child in relationships.SubIssues.OrderBy(issue => issue.Number))
                        Connect(child, number, child.Number, "parent-child",
                            _nodes[number].HierarchyDepth is { } hierarchyDepth ? hierarchyDepth + 1 : null, true);

                foreach (var relationship in GetDependencyReferences(relationships))
                {
                    var reference = relationship.Reference;
                    var expand = entry.Depth < options.MaxDepth;
                    Connect(reference, relationship.IsBlockedBy ? number : reference.Number,
                        relationship.IsBlockedBy ? reference.Number : number, "blocked-by", null, expand);
                    if (!expand && !queued.Contains(reference.Number)) _truncationReasons.Add("max-depth");
                }
            }
        }

        public GitHubIssueGraph CreateGraph()
        {
            var cycleEdges = new HashSet<(int From, int To, string Kind)>();
            // Detect cycles separately in the directed hierarchy and dependency graphs.
            // Mixing them would flag ordinary child-blocked-by-parent relationships.
            foreach (var kind in new[] { "parent-child", "blocked-by" })
            {
                var adjacency = _edges.Where(edge => edge.Kind == kind).GroupBy(edge => edge.FromIssueNumber)
                    .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.ToIssueNumber).ToArray());
                var visited = new HashSet<int>();
                var active = new HashSet<int>();
                void DetectCycles(int number)
                {
                    visited.Add(number);
                    active.Add(number);
                    if (adjacency.TryGetValue(number, out var outgoing))
                        foreach (var edge in outgoing)
                        {
                            if (active.Contains(edge.ToIssueNumber))
                            {
                                cycleEdges.Add((edge.FromIssueNumber, edge.ToIssueNumber, kind));
                                _cycleDetected = true;
                                _repeated.Add(edge.ToIssueNumber);
                            }
                            else if (!visited.Contains(edge.ToIssueNumber)) DetectCycles(edge.ToIssueNumber);
                        }
                    active.Remove(number);
                }
                foreach (var number in _nodes.Keys.Order())
                    if (!visited.Contains(number)) DetectCycles(number);
                foreach (var group in _edges.Where(edge => edge.Kind == kind).GroupBy(edge => edge.ToIssueNumber))
                    if (group.Count() > 1) _repeated.Add(group.Key);
            }
            var edges = _edges.Select(edge => cycleEdges.Contains((edge.FromIssueNumber, edge.ToIssueNumber, edge.Kind))
                    ? edge with { IsCycle = true } : edge)
                .OrderBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.FromIssueNumber)
                .ThenBy(edge => edge.ToIssueNumber).ToArray();
            return new(1, repository, rootIssueNumber, options.MaxDepth, options.MaxIssues, options.MaxEdges,
                _truncationReasons.Count > 0, _truncationReasons.Order(StringComparer.Ordinal).ToArray(), _cycleDetected,
                _repeated.Order().ToArray(), _nodes.Values.OrderBy(node => node.Number).ToArray(), edges);
        }

        private bool EnsureNode(GitHubRelationshipIssue issue, int? hierarchyDepth)
        {
            if (_nodes.TryGetValue(issue.Number, out var existing))
            {
                if (hierarchyDepth is not null && (existing.HierarchyDepth is null || hierarchyDepth < existing.HierarchyDepth))
                    _nodes[issue.Number] = existing with { HierarchyDepth = hierarchyDepth };
                return true;
            }
            if (_nodes.Count >= options.MaxIssues)
            {
                _truncationReasons.Add("max-issues");
                return false;
            }
            SetNode(issue, "reference", hierarchyDepth);
            return true;
        }

        private void SetNode(GitHubRelationshipIssue issue, string availability, int? hierarchyDepth)
        {
            var state = issue.State.ToLowerInvariant();
            if (issue.Number <= 0 || state is not ("open" or "closed"))
                throw new InvalidDataException("GitHub Issue graph received an invalid Issue reference.");
            var allLabels = issue.Labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var labels = allLabels.Take(20).ToArray();
            _nodes[issue.Number] = new(issue.Number, issue.Title, state, labels,
                availability, hierarchyDepth) { LabelsTruncated = allLabels.Length > labels.Length };
        }

        private static IEnumerable<(GitHubRelationshipIssue Reference, bool IsBlockedBy)> GetDependencyReferences(
            GitHubIssueRelationships relationships)
        {
            foreach (var issue in relationships.BlockedBy.OrderBy(issue => issue.Number))
                yield return (issue, true);
            foreach (var issue in relationships.Blocking.OrderBy(issue => issue.Number))
                yield return (issue, false);
        }
    }
}
