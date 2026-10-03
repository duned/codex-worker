namespace CodexWorker;

/// <summary>Issue preparation was safely rejected before an execution workspace was mutated.</summary>
public sealed class PreExecutionInfrastructureException(string project, int issueNumber, Guid executionId, string reason, Exception? inner = null)
    : WorkerInfrastructureException(reason, inner)
{
    public string Project { get; } = project;
    public int IssueNumber { get; } = issueNumber;
    public Guid ExecutionId { get; } = executionId;
}

public sealed class ProjectCheckoutDirtyException(string message) : WorkerInfrastructureException(message);

/// <summary>A deterministic Issue preparation rejection; no execution workspace was mutated.</summary>
public sealed class IssuePreparationRejectedException(string message) : WorkerInfrastructureException(message);

public interface IGitHubClient
{
    Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken);
    Task<GitHubIssue?> FindOldestReadyAsync(string label, IReadOnlySet<int> excludedIssueNumbers, CancellationToken cancellationToken) =>
        FindOldestReadyAsync(label, cancellationToken);
    Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) =>
        throw new WorkerInfrastructureException("This GitHub client cannot load a specifically assigned Issue.");
    Task<bool> IsIssueOpenAsync(int issueNumber, CancellationToken cancellationToken) => Task.FromResult(true);
    Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct);
    Task RemoveLabelAsync(int issueNumber, string label, CancellationToken ct) => Task.CompletedTask;
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
    Task StartIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
        throw new WorkerInfrastructureException("This Git repository does not support integration recovery.");
    Task<string?> ValidateIntegrationRecoveryAsync(ExecutionHistoryEntry source, CancellationToken ct) =>
        Task.FromResult<string?>("recovery state invalid: this Git repository cannot verify preserved integration resources");
    Task VerifyCodexStateAsync(CancellationToken ct);
    Task DiscardUncommittedIssueChangesAsync(CancellationToken ct);
    async Task<GitRecoveryInfo?> PreserveFailedIssueChangesAsync(CancellationToken ct)
    {
        await DiscardUncommittedIssueChangesAsync(ct);
        return null;
    }
    Task<GitRecoveryInfo?> PreserveIntegrationConflictAsync(CancellationToken ct) => Task.FromResult<GitRecoveryInfo?>(null);
    Task CleanupRecoveryWorkspaceAsync(ExecutionHistoryEntry recovery, CancellationToken ct) => Task.CompletedTask;
    Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase, CancellationToken ct);
    Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase,
        Func<string, CancellationToken, Task<bool>> resolveConflict, CancellationToken ct) =>
        CommitAndIntegrateAsync(issue, validateAfterRebase, ct);
    Task<GitIntegrationResult> CommitAndIntegrateAsync(GitHubIssue issue,
        Func<CancellationToken, Task<ValidationResult>> validateAfterRebase,
        Func<string, CancellationToken, Task<bool>> resolveConflict,
        Func<IntegrationRepairContext, CancellationToken, Task<IntegrationRepairResult>> repairIntegration,
        CancellationToken ct, Func<CancellationToken, Task>? ensureAuthority = null) =>
        CommitAndIntegrateAsync(issue, validateAfterRebase, resolveConflict, ct);
}

public interface ICodexExecutor
{
    ICodexExecutor WithProfile(CodexExecutionProfile profile) => this;
    Task PreflightAsync(CancellationToken ct);
    Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct);
    Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct) => RunAsync(projectDirectory, instructionsFile, issue, ct);
    Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
        ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct);
    Task<CodexOutcome> RepairIntegrationAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
        IntegrationRepairContext context, string? implementationSummary, IReadOnlyList<string> validationCommands,
        int attempt, int maximumAttempts, CancellationToken ct) =>
        Task.FromResult(new CodexOutcome("failed", "Integration repair is unavailable.", [], false, null));
    Task<CodexOutcome> ResolveIntegrationConflictAsync(string projectDirectory, string instructionsFile,
        GitHubIssue issue, string conflictDetails, CancellationToken ct) =>
        Task.FromResult(new CodexOutcome("failed", "Integration conflict resolution is unavailable.", [], false, null));
}

public interface IValidationRunner
{
    Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct);
}

public interface IAuthenticationActionExecutor
{
    Task<DependencyInstallResult> ExecuteAsync(ProvisioningActionContract action, CancellationToken cancellationToken);
}
