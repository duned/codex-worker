namespace CodexProvisioning;

/// <summary>What the engine reported it used across every run of one execution (implementation and repairs).
/// Codex reports tokens only; Claude Code also reports its cost estimate.</summary>
public sealed record EngineUsage(long InputTokens = 0, long CachedInputTokens = 0, long OutputTokens = 0, decimal? CostUsd = null, int Runs = 1)
{
    private const long MaximumTokens = 1_000_000_000_000;

    public EngineUsage Add(EngineUsage? other) => other is null ? this : new(InputTokens + other.InputTokens,
        CachedInputTokens + other.CachedInputTokens, OutputTokens + other.OutputTokens,
        CostUsd is null && other.CostUsd is null ? null : (CostUsd ?? 0) + (other.CostUsd ?? 0), Runs + other.Runs);

    public static bool Valid(EngineUsage? usage) => usage is null ||
        usage.InputTokens is >= 0 and <= MaximumTokens && usage.CachedInputTokens is >= 0 and <= MaximumTokens &&
        usage.OutputTokens is >= 0 and <= MaximumTokens && usage.Runs is >= 1 and <= 1000 &&
        (usage.CostUsd is null || usage.CostUsd >= 0 && usage.CostUsd <= 1_000_000);
}

/// <summary>The size of the change an execution integrated: what <c>git diff --numstat</c> reports between
/// the base it started from and the integrated commit. File paths are repository-relative and bounded.</summary>
public sealed record ExecutionChanges(int FilesChanged, int Insertions, int Deletions, IReadOnlyList<string> Files,
    string? BaseCommit = null, string? Commit = null)
{
    public const int MaximumFiles = 50;
    public const int MaximumPathLength = 300;

    public static bool Valid(ExecutionChanges? changes) => changes is null ||
        changes.FilesChanged is >= 0 and <= 1_000_000 && changes.Insertions is >= 0 and <= 100_000_000 && changes.Deletions is >= 0 and <= 100_000_000 &&
        changes.Files is { Count: <= MaximumFiles } && changes.Files.Count <= changes.FilesChanged &&
        changes.Files.All(path => !string.IsNullOrWhiteSpace(path) && path.Length <= MaximumPathLength && !path.Any(char.IsControl)) &&
        CommitId(changes.BaseCommit) && CommitId(changes.Commit);

    private static bool CommitId(string? value) => value is null || value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit);
}

/// <summary>Optional facts a Worker adds to its final execution report. Invalid metrics are dropped, never the report.
/// <see cref="PullRequestUrl"/> is the pull request a project in pull-request integration mode opened for review;
/// <see cref="ParentBranch"/> is the parent's integration branch a sub-issue was delivered into instead.</summary>
public sealed record ExecutionMetrics(EngineUsage? Usage = null, ExecutionChanges? Changes = null, string? PullRequestUrl = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentBranch { get; init; }

    public static bool Valid(ExecutionMetrics? metrics) => metrics is null || EngineUsage.Valid(metrics.Usage) &&
        ExecutionChanges.Valid(metrics.Changes) && (metrics.PullRequestUrl is null || ProjectIntegrationMode.ValidPullRequestUrl(metrics.PullRequestUrl)) &&
        (metrics.ParentBranch is null || ProjectIntegrationMode.ValidParentBranch(metrics.ParentBranch));
}
