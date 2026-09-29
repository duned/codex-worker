namespace CodexWorker;

public interface IGitHubClient
{
    Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken);
    Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct);
    Task CommentAsync(int issueNumber, string comment, CancellationToken ct);
    Task CloseAsync(int issueNumber, CancellationToken ct);
}

public interface IGitHubLabelClient
{
    Task<IReadOnlyList<RequiredGitHubLabel>> FindMissingLabelsAsync(IReadOnlyList<RequiredGitHubLabel> required, CancellationToken ct);
    Task CreateLabelAsync(RequiredGitHubLabel label, CancellationToken ct);
}

public interface IGitRepository
{
    string ExecutionDirectory { get; }
    /// <summary>Creates repository execution state owned by one execution; integration still targets the shared repository checkout.</summary>
    IGitRepository CreateExecutionRepository() => this;
    Task InitializeAsync(CancellationToken ct);
    Task StartIssueAsync(Guid executionId, GitHubIssue issue, CancellationToken ct);
    Task VerifyCodexStateAsync(CancellationToken ct);
    Task DiscardUncommittedIssueChangesAsync(CancellationToken ct);
    async Task<GitRecoveryInfo?> PreserveFailedIssueChangesAsync(CancellationToken ct)
    {
        await DiscardUncommittedIssueChangesAsync(ct);
        return null;
    }
    Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct);
}

public interface ICodexExecutor
{
    Task PreflightAsync(CancellationToken ct);
    Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct);
    Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
        ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct);
}

public interface IValidationRunner
{
    Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct);
}
