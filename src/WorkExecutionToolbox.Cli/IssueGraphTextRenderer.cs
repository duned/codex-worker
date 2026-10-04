using System.Globalization;

namespace WorkExecutionToolbox.Cli;

/// <summary>Terminal presentation of the bounded graph; relationships retain their provider direction.</summary>
internal static class IssueGraphTextRenderer
{
    public static IReadOnlyList<string> Render(IssueGraph graph)
    {
        var lines = new List<string>();
        var nodes = graph.Issues.ToDictionary(issue => issue.Issue.Number);
        var hierarchy = graph.Edges.Where(edge => edge.Kind == IssueGraphEdgeKind.ParentChild).ToArray();
        var children = hierarchy.GroupBy(edge => edge.FromIssueNumber)
            .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.ToIssueNumber).ToArray());
        var blockers = graph.Edges.Where(edge => edge.Kind == IssueGraphEdgeKind.BlockedBy)
            .GroupBy(edge => edge.FromIssueNumber)
            .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.ToIssueNumber).ToArray());
        var parents = hierarchy.GroupBy(edge => edge.ToIssueNumber)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.FromIssueNumber).ToArray());
        var shown = new HashSet<int>();
        var active = new HashSet<int>();

        void WriteNode(int number, string prefix, string continuation, bool cycleEdge = false)
        {
            var cycle = cycleEdge || active.Contains(number);
            if (!shown.Add(number))
            {
                lines.Add($"{prefix}#{Number(number)} ({(cycle ? "cycle reference" : "reference; already shown")})");
                return;
            }

            var node = nodes[number];
            lines.Add($"{prefix}#{Number(number)} [{node.State.ToString().ToLowerInvariant()}] {Safe(node.Title)}" +
                (number == graph.Root.Number ? " (root)" : "") + (cycle ? " (cycle edge)" : ""));
            active.Add(number);
            var descendants = children.GetValueOrDefault(number) ?? [];
            var prerequisites = blockers.GetValueOrDefault(number) ?? [];
            for (var i = 0; i < descendants.Length; i++)
            {
                var last = i == descendants.Length - 1 && prerequisites.Length == 0;
                var edge = descendants[i];
                WriteNode(edge.ToIssueNumber, continuation + (last ? "└── " : "├── "),
                    continuation + (last ? "    " : "│   "), edge.IsCycle);
            }
            if (prerequisites.Length > 0)
                lines.Add(continuation + "└── blocked by " + string.Join(", ", prerequisites.Select(edge =>
                    $"#{Number(edge.ToIssueNumber)}{(edge.IsCycle ? " (cycle)" : "")}")));
            active.Remove(number);
        }

        // Prefer the requested root's topmost ancestor so its parent edges remain structural.
        // For a root in a hierarchy cycle, start at the requested root and stop at references.
        var ancestors = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(graph.Root.Number);
        while (pending.TryPop(out var number))
        {
            if (!ancestors.Add(number)) continue;
            foreach (var parent in parents.GetValueOrDefault(number) ?? []) pending.Push(parent);
        }
        var anchor = ancestors.Where(number => !parents.ContainsKey(number)).Order()
            .DefaultIfEmpty(graph.Root.Number).First();
        WriteNode(anchor, "", "");

        // A forest includes dependency-only nodes and dependents, without inventing hierarchy edges.
        var relatedHeading = false;
        foreach (var number in nodes.Keys.OrderBy(number => parents.ContainsKey(number)).ThenBy(number => number))
        {
            if (shown.Contains(number)) continue;
            lines.Add("");
            if (!relatedHeading)
            {
                lines.Add("Related branches:");
                relatedHeading = true;
            }
            WriteNode(number, "", "");
        }
        if (graph.IsDepthTruncated) lines.Add("Depth truncated: additional relationships are outside the graph depth limit.");
        if (graph.CycleDetected) lines.Add("Cycles detected: cycle edges are marked; hierarchy references are not expanded again.");
        return lines;
    }

    private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);
    private static string Safe(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
