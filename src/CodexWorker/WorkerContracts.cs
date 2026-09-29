namespace CodexWorker;

public interface IGitHubClient
{
    Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken);
    Task<GitHubIssue?> FindOldestReadyAsync(string label, IReadOnlySet<int> excludedIssueNumbers, CancellationToken cancellationToken) =>
        FindOldestReadyAsync(label, cancellationToken);
    Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) =>
        throw new WorkerInfrastructureException("This GitHub client cannot load a specifically assigned Issue.");
    Task<bool> IsIssueOpenAsync(int issueNumber, CancellationToken cancellationToken) => Task.FromResult(true);
    Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct);
    Task CommentAsync(int issueNumber, string comment, CancellationToken ct);
    Task CloseAsync(int issueNumber, CancellationToken ct);
}

public interface IGitHubLabelClient
{
    Task<IReadOnlyList<RequiredGitHubLabel>> FindMissingLabelsAsync(IReadOnlyList<RequiredGitHubLabel> required, CancellationToken ct);
    Task CreateLabelAsync(RequiredGitHubLabel label, CancellationToken ct);
}

public interface IGitRepository : IDisposable
{
    // Implementations that own resources should override this default. Keeping it here
    // lets lightweight repository fakes remain resource-free while callers can dispose
    // execution repositories through the interface.
    void IDisposable.Dispose() { }

    string ExecutionDirectory { get; }
    /// <summary>Creates repository execution state owned by one execution; integration still targets the shared repository checkout.</summary>
    IGitRepository CreateExecutionRepository() => this;
    Task InitializeAsync(CancellationToken ct);
    Task StartIssueAsync(Guid executionId, GitHubIssue issue, CancellationToken ct);
    Task StartIssueAsync(Guid executionId, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resume, int attemptNumber, CancellationToken ct) => StartIssueAsync(executionId, issue, ct);
    Task VerifyCodexStateAsync(CancellationToken ct);
    Task DiscardUncommittedIssueChangesAsync(CancellationToken ct);
    async Task<GitRecoveryInfo?> PreserveFailedIssueChangesAsync(CancellationToken ct)
    {
        await DiscardUncommittedIssueChangesAsync(ct);
        return null;
    }
    Task CleanupRecoveryWorkspaceAsync(ExecutionHistoryEntry recovery, CancellationToken ct) => Task.CompletedTask;
    Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct);
}

public interface ICodexExecutor
{
    Task PreflightAsync(CancellationToken ct);
    Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct);
    Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct) => RunAsync(projectDirectory, instructionsFile, issue, ct);
    Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
        ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct);
}

public interface IValidationRunner
{
    Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct);
}

public interface IAuthenticationActionExecutor
{
    Task<DependencyInstallResult> ExecuteAsync(ProvisioningActionContract action, CancellationToken cancellationToken);
}
