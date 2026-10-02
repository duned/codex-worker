namespace CodexWorker;

public enum GitHubFailureKind
{
    TransientProvider,
    AuthenticationOrAuthorization,
    DeterministicRequest,
    Cancellation,
    Unknown
}

public enum GitHubRemoteState
{
    NotChanged,
    Uncertain,
    NotApplicable
}

/// <summary>A bounded GitHub CLI failure with the effect on remote Issue state made explicit.</summary>
public sealed class GitHubOperationException : WorkerInfrastructureException
{
    public const string ReconciliationRequiredState = "github-reconciliation-required";

    public GitHubOperationException(string operation, int? issueNumber, bool isMutation, GitHubFailureKind failureKind,
        GitHubRemoteState remoteState, string message, Exception? innerException = null,
        Guid? executionId = null, string? primaryFailure = null)
        : base(message, innerException)
    {
        Operation = operation;
        IssueNumber = issueNumber;
        IsMutation = isMutation;
        FailureKind = failureKind;
        RemoteState = remoteState;
        ExecutionId = executionId;
        PrimaryFailure = primaryFailure;
    }

    public string Operation { get; }
    public int? IssueNumber { get; }
    public bool IsMutation { get; }
    public GitHubFailureKind FailureKind { get; }
    public GitHubRemoteState RemoteState { get; }
    public Guid? ExecutionId { get; }
    public string? PrimaryFailure { get; }
    public bool RemoteStateUncertain => RemoteState == GitHubRemoteState.Uncertain;
    public bool IsSecondaryReportingFailure => ExecutionId is not null && PrimaryFailure is not null;

    public static GitHubOperationException? Find(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is GitHubOperationException githubFailure) return githubFailure;
        return null;
    }

    public GitHubOperationException DuringReporting(Guid executionId, string primaryFailure) =>
        new(Operation, IssueNumber, IsMutation, FailureKind, RemoteState,
            $"Execution {executionId} primary failure: {primaryFailure} Secondary GitHub reporting failure during {Operation}; " +
            $"remote Issue state is {(RemoteStateUncertain ? "uncertain" : RemoteState == GitHubRemoteState.NotChanged ? "known unchanged" : "read only")}. {Message}",
            this, executionId, primaryFailure);

    public GitHubOperationException ForExecution(Guid executionId) => ExecutionId == executionId ? this :
        new(Operation, IssueNumber, IsMutation, FailureKind, RemoteState,
            $"Execution {executionId} · {Message}", this, executionId, PrimaryFailure);
}
