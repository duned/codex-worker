namespace CodexWorker.Tests;

public sealed class ImplementationEngineTests
{
    private static readonly ImplementationRequest Request = new("isolated-worktree", "AGENTS.md",
        new GitHubIssue(42, "Task", "Implement task", DateTimeOffset.UnixEpoch), AttemptNumber: 3, Resumed: true);

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void DefaultIsCodexAndUnavailableEngineNeverFallsBack()
    {
        var selector = new ImplementationEngineSelector();
        Assert.True(selector.IsAvailable("codex"));
        Assert.False(selector.IsAvailable("ccc"));
        Assert.Equal("codex", selector.Select("codex", new Executor())?.Identity);
        Assert.Null(selector.Select("ccc", new Executor()));
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task InjectedEngineHandlesImplementationAndRepair()
    {
        var registered = new Engine();
        var selector = new ImplementationEngineSelector([registered]);
        var engine = Assert.IsType<Engine>(selector.Select("ccc", new Executor()));
        Assert.Same(registered, engine);
        Assert.True(selector.IsAvailable("ccc"));
        Assert.Equal("success", (await engine.RunAsync(Request, CancellationToken.None)).Status);
        Assert.Equal("failed", (await engine.RepairAsync(Request,
            new ValidationFailure(1, "test", 1, "failure", "", false), 1, 2, CancellationToken.None)).Status);
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CodexAdapterPreservesAttemptInputsResultAndCancellation()
    {
        var executor = new Executor();
        var engine = Assert.IsType<CodexImplementationEngine>(new ImplementationEngineSelector().Select("codex", executor));
        var result = await engine.RunAsync(Request, CancellationToken.None);
        Assert.Equal("failed", result.Status);
        Assert.Equal("task incomplete", result.Summary);
        Assert.Equal(Request, executor.Observed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(Request, cancellation.Token));
        var failure = new CodexExecutionInfrastructureException(CodexFailure.ModelCapacity, "capacity");
        executor.Failure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<CodexExecutionInfrastructureException>(() => engine.RunAsync(Request, CancellationToken.None)));
    }

    private sealed class Executor : ICodexExecutor
    {
        public ImplementationRequest? Observed { get; private set; }
        public Exception? Failure { get; set; }
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CodexOutcome> RunAsync(string directory, string instructions, GitHubIssue issue, CancellationToken ct) =>
            throw new InvalidOperationException("Attempt context must be forwarded.");
        public Task<CodexOutcome> RunAsync(string directory, string instructions, GitHubIssue issue,
            ExecutionHistoryEntry? retryOf, bool resumed, int attemptNumber, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Observed = new(directory, instructions, issue, retryOf, resumed, attemptNumber);
            return Failure is { } failure ? Task.FromException<CodexOutcome>(failure)
                : Task.FromResult(new CodexOutcome("failed", "task incomplete", [], false, null));
        }
        public Task<CodexOutcome> RepairAsync(string directory, string instructions, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            Task.FromResult(new CodexOutcome("success", "repaired", [], false, null));
    }

    internal sealed class Engine : IImplementationEngine
    {
        public string Identity => "ccc";
        public List<ImplementationRequest> Requests { get; } = [];
        public string RepairStatus { get; set; } = "failed";
        public Task<ImplementationResult> RunAsync(ImplementationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ImplementationResult("success", "done", [], false, null));
        }
        public Task<ImplementationResult> RepairAsync(ImplementationRequest request, ValidationFailure failure,
            int attempt, int maximumAttempts, CancellationToken ct) =>
            RepairCoreAsync(request, ct);
        private Task<ImplementationResult> RepairCoreAsync(ImplementationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ImplementationResult(RepairStatus, "repair outcome", [], false, null));
        }
        public Task<ImplementationResult> RepairIntegrationAsync(ImplementationRequest request, IntegrationRepairContext context,
            string? summary, IReadOnlyList<string> commands, int attempt, int maximumAttempts, CancellationToken ct) =>
            RunAsync(request, ct);
        public Task<ImplementationResult> ResolveIntegrationConflictAsync(ImplementationRequest request, string details, CancellationToken ct) =>
            RunAsync(request, ct);
    }
}
