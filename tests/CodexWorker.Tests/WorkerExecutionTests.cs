using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerExecutionTests
{
    private static readonly ProjectSettings Project = new() { Name = "sample", Repository = "owner/repo" };
    private static readonly GitSettings Git = new() { BaseBranch = "main", FeaturePrefix = "feature/" };
    private static readonly GitHubIssue Issue = new(17, "Add execution context", "", DateTimeOffset.UtcNow);

    [Fact]
    public void NewExecutionsHaveUniqueIdentityAndCompleteInitialContext()
    {
        var first = WorkerExecution.Create(Project, Git, Issue);
        var second = WorkerExecution.Create(Project, Git, Issue);

        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
        Assert.NotEqual(Issue.Number.ToString(), first.ExecutionId.ToString());
        Assert.Equal(ExecutionState.Created, first.State);
        Assert.Equal("sample", first.Project);
        Assert.Equal("owner/repo", first.Repository);
        Assert.Equal(17, first.IssueNumber);
        Assert.Equal(Issue.Title, first.IssueTitle);
        Assert.Equal("main", first.BaseBranch);
        Assert.Equal("feature/add-execution-context-17", first.FeatureBranch);
        var retry = WorkerExecution.Create(Project, Git, Issue, retryOfExecutionId: first.ExecutionId, attemptNumber: 2, resumed: true);
        Assert.Equal("feature/add-execution-context-17-retry-2", retry.FeatureBranch);
        Assert.Equal(first.ExecutionId, retry.RetryOfExecutionId);
        Assert.True(retry.Resumed);
        Assert.True(first.StartedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ServerAssignmentIsLinkedToTheNormalWorkerExecutionIdentity()
    {
        var execution = WorkerExecution.Create(Project, Git, Issue,
            serverExecutionId: "server-request-123", assignmentId: "assignment-456");

        Assert.NotEqual(Guid.Empty, execution.ExecutionId);
        Assert.Equal("server-request-123", execution.ServerExecutionId);
        Assert.Equal("assignment-456", execution.AssignmentId);
        Assert.Equal("feature/add-execution-context-17", execution.FeatureBranch);
    }

    [Fact]
    public void GitRepositoryCreatesSeparateMutableRepositoryStatePerExecution()
    {
        using var repository = new GitRepository(new ProcessRunner(), Path.GetTempPath(), "owner/repo",
            Git, new WorkerSettings(), Path.Combine(Path.GetTempPath(), "codex-worker-test-worktrees"));

        var first = repository.CreateExecutionRepository();
        var second = repository.CreateExecutionRepository();

        Assert.NotSame(repository, first);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void DefaultExecutionWorktreeRootsAreScopedByRepository()
    {
        var first = GitRepository.DefaultWorktreeRoot("owner-one/app");
        var second = GitRepository.DefaultWorktreeRoot("owner-two/app");

        Assert.NotEqual(first, second);
        Assert.EndsWith(Path.Combine("worktrees", "owner-one", "app"), first);
        Assert.EndsWith(Path.Combine("worktrees", "owner-two", "app"), second);
    }

    [Fact]
    public void NormalLifecycleTransitionsToCompleted()
    {
        var execution = WorkerExecution.Create(Project, Git, Issue);

        execution.TransitionTo(ExecutionState.Claimed);
        execution.TransitionTo(ExecutionState.Preparing);
        execution.TransitionTo(ExecutionState.Implementing);
        execution.TransitionTo(ExecutionState.Validating);
        execution.TransitionTo(ExecutionState.Repairing);
        execution.TransitionTo(ExecutionState.Validating);
        execution.TransitionTo(ExecutionState.Integrating);
        execution.TransitionTo(ExecutionState.Reporting);
        execution.TransitionTo(ExecutionState.Completed);

        Assert.True(execution.IsTerminal);
        Assert.Equal(ExecutionState.Completed, execution.State);
    }

    [Theory]
    [InlineData(ExecutionState.Blocked)]
    [InlineData(ExecutionState.Failed)]
    [InlineData(ExecutionState.InfrastructureFailure)]
    [InlineData(ExecutionState.Cancelled)]
    public void TerminalOutcomesAreExplicitAndCannotContinue(ExecutionState terminal)
    {
        var execution = WorkerExecution.Create(Project, Git, Issue);
        if (terminal == ExecutionState.Cancelled)
            execution.TransitionTo(terminal);
        else
        {
            execution.TransitionTo(ExecutionState.Claimed);
            if (terminal == ExecutionState.InfrastructureFailure)
                execution.TransitionTo(terminal);
            else
            {
                execution.TransitionTo(ExecutionState.Preparing);
                execution.TransitionTo(ExecutionState.Implementing);
                execution.TransitionTo(ExecutionState.Reporting);
                execution.TransitionTo(terminal);
            }
        }

        Assert.True(execution.IsTerminal);
        Assert.Equal(terminal, execution.State);
        Assert.Throws<InvalidOperationException>(() => execution.TransitionTo(ExecutionState.Claimed));
    }

    [Fact]
    public void InvalidLifecycleTransitionDoesNotSilentlySucceed()
    {
        var execution = WorkerExecution.Create(Project, Git, Issue);

        Assert.Throws<InvalidOperationException>(() => execution.TransitionTo(ExecutionState.Integrating));
        Assert.Equal(ExecutionState.Created, execution.State);
    }
}
