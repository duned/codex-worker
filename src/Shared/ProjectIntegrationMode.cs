using System.Text.RegularExpressions;

namespace CodexProvisioning;

/// <summary>How a project delivers validated work. <see cref="Direct"/> (the default) fast-forwards the base
/// branch and pushes it; <see cref="PullRequest"/> pushes the rebased, validated branch and opens a pull
/// request into the base for review, leaving the merge to GitHub.</summary>
public static partial class ProjectIntegrationMode
{
    public const string Direct = "direct";
    public const string PullRequest = "pull-request";

    /// <summary>The Worker's done label. In pull-request mode a sub-issue delivered into its parent's integration
    /// branch stays open with it until the parent's pull request is merged.</summary>
    public const string DoneLabel = "codex-done";

    /// <summary>Whether an open prerequisite no longer blocks <paramref name="issueNumber"/>: in pull-request mode, a
    /// prerequisite already delivered (done label) into the integration branch the Issue starts from, that is a
    /// sub-issue of the Issue or a sibling under the same parent. Any other prerequisite must be closed.</summary>
    public static bool DeliveredToSharedBranch(string? mode, int issueNumber, int? issueParent,
        IReadOnlyCollection<string>? blockerLabels, int? blockerParent) =>
        mode == PullRequest && blockerLabels is not null && blockerLabels.Contains(DoneLabel, StringComparer.OrdinalIgnoreCase) &&
        blockerParent is > 0 && (blockerParent == issueNumber || issueParent is > 0 && blockerParent == issueParent);

    /// <summary>Null means the default, <see cref="Direct"/>.</summary>
    public static bool Valid(string? mode) => mode is null or Direct or PullRequest;

    /// <summary>The stored form: the default is kept as null so existing definitions and fingerprints do not change.</summary>
    public static string? Normalize(string? mode) => string.IsNullOrWhiteSpace(mode) || mode.Trim() == Direct ? null : mode.Trim();

    public static bool ValidParentBranch(string? branch) => branch is { Length: <= 40 } && ParentBranchPattern().IsMatch(branch);

    [GeneratedRegex("^integration/[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParentBranchPattern();

    public static bool ValidPullRequestUrl(string? url) => url is { Length: <= 300 } && PullRequestUrlPattern().IsMatch(url);

    [GeneratedRegex("^https://github\\.com/[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?/[A-Za-z0-9_.-]+/pull/[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex PullRequestUrlPattern();
}
