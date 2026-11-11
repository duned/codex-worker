using System.Text.Json;

namespace CodexProvisioning;

public sealed record IssueParentEvent(string Id, DateTimeOffset AtUtc, bool Added, int ParentNumber);

/// <summary>Complete bounded native GitHub parent history. This supplements, never replaces, original execution evidence.</summary>
public sealed record IssueParentProvenance(string Repository, int IssueNumber, string IssueNodeId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExecutionStartedAtUtc, int? ParentNumber, IssueParentEvent[] Events)
{
    public static IssueParentProvenance? Parse(string? json)
    {
        if (json is null || json.Length > 262144) return null;
        try { return JsonSerializer.Deserialize<IssueParentProvenance>(json); }
        catch (JsonException) { return null; }
    }

    // A missing/contradictory event, equal timestamp, transfer, or inaccessible parent is never proof.
    public bool IsConsistent(DateTimeOffset boundary) => Refusal(boundary) is null;

    public string? Refusal(DateTimeOffset boundary)
    {
        if (string.IsNullOrWhiteSpace(Repository) || string.IsNullOrWhiteSpace(IssueNodeId) || IssueNumber <= 0 ||
            Events is null || Events.Length > 1000 || CreatedAtUtc >= boundary || boundary > ExecutionStartedAtUtc)
            return "issue-parent-history-incomplete";
        int? parent = null;
        int? original = null;
        var changed = false;
        var previous = CreatedAtUtc;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Events)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || item.AtUtc <= previous ||
                item.AtUtc.ToUnixTimeSeconds() == boundary.ToUnixTimeSeconds() || item.ParentNumber <= 0 || item.ParentNumber == IssueNumber)
                return "issue-parent-history-incomplete";
            if (item.Added ? parent is not null : parent != item.ParentNumber) return "issue-parent-history-incomplete";
            parent = item.Added ? item.ParentNumber : null;
            if (item.AtUtc < boundary) original = parent;
            else changed = true;
            previous = item.AtUtc;
        }
        if (parent != ParentNumber) return "issue-parent-history-incomplete";
        if (original is null && ParentNumber is not null) return "issue-original-parent-absent";
        return changed || original != ParentNumber ? "issue-parent-changed" : null;
    }
}
