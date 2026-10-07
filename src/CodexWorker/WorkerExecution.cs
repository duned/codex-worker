namespace CodexWorker;

/// <summary>Runtime identity and lifecycle for one attempt to process one Issue.</summary>
public sealed class WorkerExecution
{
    private static readonly IReadOnlyDictionary<ExecutionState, ExecutionState[]> AllowedTransitions =
        new Dictionary<ExecutionState, ExecutionState[]>
        {
            [ExecutionState.Created] = [ExecutionState.Claimed, ExecutionState.Cancelled, ExecutionState.InfrastructureFailure],
            [ExecutionState.Claimed] = [ExecutionState.Preparing, ExecutionState.InfrastructureFailure, ExecutionState.Cancelled],
            [ExecutionState.Preparing] = [ExecutionState.Implementing, ExecutionState.Integrating, ExecutionState.Reporting, ExecutionState.InfrastructureFailure, ExecutionState.Cancelled],
            [ExecutionState.Implementing] = [ExecutionState.Validating, ExecutionState.Reporting, ExecutionState.InfrastructureFailure, ExecutionState.Cancelled],
            [ExecutionState.Validating] = [ExecutionState.Repairing, ExecutionState.Integrating, ExecutionState.Reporting, ExecutionState.InfrastructureFailure, ExecutionState.Cancelled],
            [ExecutionState.Repairing] = [ExecutionState.Validating, ExecutionState.Reporting, ExecutionState.InfrastructureFailure, ExecutionState.Cancelled],
            [ExecutionState.Integrating] = [ExecutionState.Reporting, ExecutionState.InfrastructureFailure, ExecutionState.Cancelled],
            [ExecutionState.Reporting] = [ExecutionState.Completed, ExecutionState.Blocked, ExecutionState.Failed, ExecutionState.IntegrationConflict, ExecutionState.Superseded, ExecutionState.InfrastructureFailure, ExecutionState.Cancelled],
            [ExecutionState.Completed] = [],
            [ExecutionState.Blocked] = [],
            [ExecutionState.Failed] = [],
            [ExecutionState.IntegrationConflict] = [],
            [ExecutionState.InfrastructureFailure] = [],
            [ExecutionState.Cancelled] = [],
            [ExecutionState.Superseded] = []
        };

    private WorkerExecution(string project, string repository, GitHubIssue issue, string baseBranch,
        string featureBranch, DateTimeOffset startedAtUtc, Guid? retryOfExecutionId = null, int attemptNumber = 1,
        bool resumed = false, string? serverExecutionId = null, string? assignmentId = null, long? ownershipGeneration = null)
    {
        ExecutionId = Guid.NewGuid();
        Project = project;
        Repository = repository;
        IssueNumber = issue.Number;
        IssueTitle = issue.Title;
        IssueBody = issue.Body;
        BaseBranch = baseBranch;
        FeatureBranch = featureBranch;
        StartedAtUtc = startedAtUtc;
        RetryOfExecutionId = retryOfExecutionId;
        AttemptNumber = attemptNumber;
        Resumed = resumed;
        ServerExecutionId = serverExecutionId;
        AssignmentId = assignmentId;
        OwnershipGeneration = ownershipGeneration;
    }

    public Guid ExecutionId { get; }
    public string Project { get; }
    public string Repository { get; }
    public int IssueNumber { get; }
    public string IssueTitle { get; }
    public string IssueBody { get; }
    public string BaseBranch { get; }
    public string FeatureBranch { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public Guid? RetryOfExecutionId { get; }
    public int AttemptNumber { get; }
    public bool Resumed { get; }
    public string? ServerExecutionId { get; }
    public string? AssignmentId { get; }
    public long? OwnershipGeneration { get; }
    public CodexExecutionProfile? CodexProfile { get; private set; }
    public CodexInterruptionRecovery? CodexRecovery { get; internal set; }
    public string? MetadataError { get; private set; }
    public ExecutionState State { get; private set; } = ExecutionState.Created;
    public bool IsTerminal => AllowedTransitions[State].Length == 0;

    public static WorkerExecution Create(ProjectSettings project, GitSettings git, GitHubIssue issue,
        DateTimeOffset? startedAtUtc = null, Guid? retryOfExecutionId = null, int attemptNumber = 1, bool resumed = false,
        string? serverExecutionId = null, string? assignmentId = null, long? ownershipGeneration = null,
        string? featureBranchOverride = null, CodexSettings? codexSettings = null, ExecutionHistoryEntry? settingsSource = null)
    {
        var featureBranch = featureBranchOverride ?? GitRepository.FeatureBranchName(git, issue);
        if (featureBranchOverride is null && attemptNumber > 1) featureBranch = $"{featureBranch}-retry-{attemptNumber}";
        var execution = new WorkerExecution(project.Name, project.Repository, issue, git.BaseBranch, featureBranch, startedAtUtc ?? DateTimeOffset.UtcNow,
        retryOfExecutionId, attemptNumber, resumed, serverExecutionId, assignmentId, ownershipGeneration);
        if (codexSettings is not null)
        {
            try { execution.CodexProfile = CodexExecutionProfile.Resolve(issue.Body, codexSettings, settingsSource); }
            catch (InvalidDataException ex) { execution.MetadataError = ex.Message; }
        }
        return execution;
    }

    internal void RecordCliModel(string? model)
    {
        if (CodexProfile is { Model: null } profile)
            CodexProfile = profile with { CliModel = model };
    }

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
    IntegrationConflict,
    InfrastructureFailure,
    Cancelled,
    Superseded
}
