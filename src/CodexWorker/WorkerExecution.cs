namespace CodexWorker;

/// <summary>Runtime identity and lifecycle for one attempt to process one Issue.</summary>
public sealed class WorkerExecution
{
    private static readonly IReadOnlyDictionary<ExecutionState, ExecutionState[]> AllowedTransitions =
        new Dictionary<ExecutionState, ExecutionState[]>
        {
            [ExecutionState.Created] = [ExecutionState.Claimed, ExecutionState.Cancelled, ExecutionState.InfrastructureFailure],
            [ExecutionState.Claimed] = [ExecutionState.Preparing, ExecutionState.InfrastructureFailure],
            [ExecutionState.Preparing] = [ExecutionState.Implementing, ExecutionState.InfrastructureFailure],
            [ExecutionState.Implementing] = [ExecutionState.Validating, ExecutionState.Reporting, ExecutionState.InfrastructureFailure],
            [ExecutionState.Validating] = [ExecutionState.Repairing, ExecutionState.Integrating, ExecutionState.Reporting, ExecutionState.InfrastructureFailure],
            [ExecutionState.Repairing] = [ExecutionState.Validating, ExecutionState.Reporting, ExecutionState.InfrastructureFailure],
            [ExecutionState.Integrating] = [ExecutionState.Reporting, ExecutionState.InfrastructureFailure],
            [ExecutionState.Reporting] = [ExecutionState.Completed, ExecutionState.Blocked, ExecutionState.Failed, ExecutionState.InfrastructureFailure],
            [ExecutionState.Completed] = [],
            [ExecutionState.Blocked] = [],
            [ExecutionState.Failed] = [],
            [ExecutionState.InfrastructureFailure] = [],
            [ExecutionState.Cancelled] = []
        };

    private WorkerExecution(string project, string repository, GitHubIssue issue, string baseBranch,
        string featureBranch, DateTimeOffset startedAtUtc)
    {
        ExecutionId = Guid.NewGuid();
        Project = project;
        Repository = repository;
        IssueNumber = issue.Number;
        IssueTitle = issue.Title;
        BaseBranch = baseBranch;
        FeatureBranch = featureBranch;
        StartedAtUtc = startedAtUtc;
    }

    public Guid ExecutionId { get; }
    public string Project { get; }
    public string Repository { get; }
    public int IssueNumber { get; }
    public string IssueTitle { get; }
    public string BaseBranch { get; }
    public string FeatureBranch { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public ExecutionState State { get; private set; } = ExecutionState.Created;
    public bool IsTerminal => AllowedTransitions[State].Length == 0;

    public static WorkerExecution Create(ProjectSettings project, GitSettings git, GitHubIssue issue,
        DateTimeOffset? startedAtUtc = null) => new(project.Name, project.Repository, issue,
        git.BaseBranch, GitRepository.FeatureBranchName(git, issue), startedAtUtc ?? DateTimeOffset.UtcNow);

    public void TransitionTo(ExecutionState next)
    {
        if (!AllowedTransitions[State].Contains(next))
            throw new InvalidOperationException($"Invalid execution transition: {State} -> {next}.");
        State = next;
    }
}

public enum ExecutionState
{
    Created,
    Claimed,
    Preparing,
    Implementing,
    Validating,
    Repairing,
    Integrating,
    Reporting,
    Completed,
    Blocked,
    Failed,
    InfrastructureFailure,
    Cancelled
}
