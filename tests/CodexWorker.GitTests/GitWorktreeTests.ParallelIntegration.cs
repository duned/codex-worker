using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed partial class GitWorktreeTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task PostRebaseValidationDoesNotRetryWhenSourceChanges()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        await fixture.AdvanceBaseAsync();
        var attempts = 0;
        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() => git.CommitAndIntegrateAsync(fixture.Issue,
            async _ =>
            {
                attempts++;
                await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "changed during validation");
                return new ValidationResult(new ValidationFailure(1, "local-check", 1, "", "failed test", false));
            }, CancellationToken.None));
        Assert.Equal(1, attempts);
        Assert.Contains("source changed", failure.Message);
        Assert.DoesNotContain("implemented.txt", await fixture.Git("ls-tree", "-r", "--name-only", "main"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreservedValidationFailureCanRetryIntegrationWithoutAnotherRebase(bool retrySucceeds)
    {
        using var fixture = await seed.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(original.ExecutionDirectory, "implemented.txt"), "implementation");
        await fixture.AdvanceBaseAsync();
        var attempts = 0;
        await Assert.ThrowsAsync<PostRebaseValidationException>(() => original.CommitAndIntegrateAsync(fixture.Issue, _ =>
        {
            attempts++;
            return Task.FromResult(new ValidationResult(new ValidationFailure(1, "local-check", 1, "", "failed test", false)));
        }, CancellationToken.None));
        Assert.Equal(2, attempts);
        var preserved = await original.PreserveIntegrationConflictAsync(CancellationToken.None);
        Assert.NotNull(preserved);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            preserved.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "IntegrationConflict", null,
            "Implemented", "failed: post-rebase validation", 0, [], preserved.BaseCommit, "main", null, null,
            RecoveryState: "integration-conflict", RecoveryBaseCommit: preserved.BaseCommit);
        using var recovery = original.CreateExecutionRepository();
        await recovery.StartIntegrationRecoveryAsync(entry, CancellationToken.None);
        attempts = 0;
        async Task<ValidationResult> Validate(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(preserved.BaseCommit, await fixture.GitAt(recovery.ExecutionDirectory, "rev-parse", "HEAD"));
            return ++attempts == 2 && retrySucceeds ? ValidationResult.Success :
                new ValidationResult(new ValidationFailure(1, "local-check", 1, "", $"failed test: attempt {attempts}", false));
        }
        if (retrySucceeds)
            Assert.True((await recovery.CommitAndIntegrateAsync(fixture.Issue, Validate, CancellationToken.None)).HasChanges);
        else
        {
            await Assert.ThrowsAsync<PostRebaseValidationException>(() => recovery.CommitAndIntegrateAsync(fixture.Issue, Validate, CancellationToken.None));
            Assert.Equal(preserved.BaseCommit, (await recovery.PreserveIntegrationConflictAsync(CancellationToken.None))!.BaseCommit);
        }
        Assert.Equal(2, attempts);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentExecutionsRetryRebasedValidationAndReleaseCapacity(bool retrySucceeds)
    {
        using var fixture = await seed.CreateAsync();
        var settings = new GitSettings { AutoMerge = true };
        using var git = fixture.CreateRepository(settings);
        await git.InitializeAsync(CancellationToken.None);
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
            Git = settings,
            GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", DoneLabel = "done", FailedLabel = "failed", BlockedLabel = "blocked" },
            Codex = new CodexSettings { InstructionsFile = "unused" },
            Validation = new ValidationSettings { Commands = ["local-check"], MaxFixAttempts = 0 },
            Worker = new WorkerSettings { MaxParallelTasks = 2 }
        };
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../history.db"));
        var github = new ConcurrentGitHub(fixture.Issue);
        var validation = new RebaseValidation(fixture, retrySucceeds);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var console = new WorkerConsole(output, false, errors);
        using var telegram = new TelegramNotifier(false, console);
        var worker = new Worker(config, github, git, new FileCodex(), validation, telegram, console, history);
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)], new RuntimeEventLog());

        Assert.True(registry.TryReserve("sample"));
        var first = await worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(first);
        await validation.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(registry.TryReserve("sample"));
        var second = await worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(second);
        await validation.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, registry.WorkerActiveExecutionCount);
        validation.ReleaseFirst.SetResult();
        Assert.Equal(IssueOutcomeKind.Succeeded, (await first)!.Kind);
        registry.Release("sample");

        // Independent work remains active throughout B's integration and failed retry.
        Assert.True(registry.TryReserve("sample"));
        var independent = await worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(independent);
        await validation.IndependentStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        validation.ReleaseSecond.SetResult();
        var result = await second;
        Assert.NotNull(result);
        registry.Release("sample");
        Assert.False(independent.IsCompleted);
        Assert.Equal(retrySucceeds ? IssueOutcomeKind.Succeeded : IssueOutcomeKind.IntegrationConflict, result.Kind);
        Assert.Equal(2, validation.RebasedHeads.Count);
        Assert.Equal(validation.RebasedHeads[0], validation.RebasedHeads[1]);
        Assert.All(validation.RebasedParents, parent => Assert.Equal(validation.FirstIntegratedHead, parent));
        Assert.Contains("Validation after rebase", output.ToString());
        if (!retrySucceeds)
        {
            Assert.True(result.Report.WorkspacePreserved);
            Assert.Equal("Post-rebase validation failed", result.Report.FailureCategory);
            Assert.Contains("Attempt 1/2", result.Summary);
            Assert.Contains("Attempt 2/2", result.Summary);
            var entry = (await history.ReadAllAsync()).Single(row => row.IssueNumber == 18);
            Assert.Equal("IntegrationConflict", entry.State);
            Assert.Equal("integration-conflict", entry.RecoveryState);
            Assert.Equal("failed: post-rebase validation (2 attempts)", entry.ValidationOutcome);
            Assert.Equal(validation.RebasedHeads[0], entry.RecoveryBaseCommit);
            Assert.Contains((18, config.GitHub.IntegrationConflictLabel), github.Labels);
            Assert.DoesNotContain("issue-18.txt", await fixture.Git("ls-tree", "-r", "--name-only", "main"));
        }
        Assert.DoesNotContain("infrastructure failure", errors.ToString(), StringComparison.OrdinalIgnoreCase);

        // The same worker can poll and claim again while unrelated work is still running.
        Assert.True(registry.TryReserve("sample"));
        var next = await worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(next);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await next)!.Kind);
        registry.Release("sample");
        validation.ReleaseIndependent.SetResult();
        Assert.Equal(IssueOutcomeKind.Succeeded, (await independent)!.Kind);
        registry.Release("sample");
        Assert.Equal(0, registry.Get("sample")!.ActiveExecutionCount);
    }

    [Theory]
    [InlineData(true, true, "success")]
    [InlineData(false, true, "success")]
    [InlineData(true, false, "success")]
    [InlineData(false, false, "success")]
    [InlineData(true, true, "standalone")]
    [InlineData(false, true, "standalone")]
    [InlineData(true, false, "standalone")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task FirstAttemptsIntegrateFromSameBaseInEitherOrder(bool firstFinishesFirst, bool overlap, string scenario)
    {
        using var fixture = await seed.CreateAsync();
        // Exercise a configured branch other than the fixture's default.
        await fixture.Git("branch", "-m", "main", "integration");
        await fixture.Git("push", "-u", "origin", "integration");
        var config = new WorkerConfiguration
        {
            Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
            Git = new() { AutoMerge = true, BaseBranch = "integration" },
            GitHub = new() { WorkingLabel = "working" },
            Validation = new() { Commands = ["combined source"], MaxFixAttempts = scenario == "budget" ? 0 : 1 }
        };
        using var root = fixture.CreateRepository(config.Git);
        await root.InitializeAsync(CancellationToken.None);
        using var left = root.CreateExecutionRepository();
        using var right = root.CreateExecutionRepository();
        using var gate = new SemaphoreSlim(1, 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "parallel.db"));
        using var writer = new StringWriter();
        var console = new WorkerConsole(writer, false);
        var issues = new[] { fixture.Issue with { ParentIssueCaptured = true },
            fixture.Issue with { Number = 18, ParentIssueCaptured = true } };
        var github = new ConcurrentGitHub(issues[0]);
        foreach (var issue in issues) github.Labels.Add((issue.Number, "working"));
        var engines = new[] { new ParallelIntegrationCodex(fixture, overlap, scenario, github),
            new ParallelIntegrationCodex(fixture, overlap, scenario, github) };
        var validation = new ParallelSourceValidation(overlap, scenario == "validation");
        var executions = issues.Select(issue => WorkerExecution.Create(config.Project, config.Git, issue,
            serverExecutionId: scenario == "standalone" ? null : $"server-{issue.Number}",
            assignmentId: scenario == "standalone" ? null : $"assignment-{issue.Number}",
            ownershipGeneration: scenario == "standalone" ? null : 1)).ToArray();
        foreach (var execution in executions)
        {
            execution.TransitionTo(ExecutionState.Claimed);
            await history.CreateAsync(new ExecutionHistoryEntry(execution.ExecutionId, execution.Project, execution.Repository,
                execution.IssueNumber, execution.IssueTitle, execution.FeatureBranch, execution.BaseBranch, execution.StartedAtUtc,
                null, "Claimed", null, null, null, 0, [], null, null, null, null,
                ServerExecutionId: execution.ServerExecutionId, AssignmentId: execution.AssignmentId,
                OwnershipGeneration: execution.OwnershipGeneration, OriginalIssueBody: execution.IssueBody,
                ParentIssueCaptured: true));
        }
        var runners = new[] { left, right }.Select((repository, index) => new ExecutionRunner(config, repository,
            engines[index], validation, console, history, gate,
            isAuthoritative: scenario == "standalone" ? null : (_, _) => Task.FromResult(!engines[index].LeaseLost),
            github: github)).ToArray();
        var tasks = runners.Select((runner, index) => runner.RunAsync(
            new CodexWorker.ExecutionContext(executions[index], issues[index]), deadline.Token)).ToArray();
        try
        {
            await Task.WhenAll(engines.Select(engine => engine.Started.Task)).WaitAsync(deadline.Token);
            Assert.NotEqual(left.ExecutionDirectory, right.ExecutionDirectory);
            Assert.Equal(engines[0].StartingHead, engines[1].StartingHead);
            Assert.NotEqual(engines[0].Branch, engines[1].Branch);
            Assert.All(engines, engine => Assert.Equal("integration", engine.BaseBranch));
            var winner = firstFinishesFirst ? 0 : 1;
            var follower = 1 - winner;
            engines[winner].Release.SetResult();
            Assert.Equal(IssueOutcomeKind.Succeeded, (await tasks[winner]).Kind);
            engines[follower].Release.SetResult();
            var result = await tasks[follower];
            // A first attempt never asks the engine to resolve a conflict: disjoint work integrates
            // after the rebase, overlapping work is preserved and reported without stopping the Worker.
            Assert.Equal(0, engines[follower].ResolutionCalls);
            if (!overlap)
            {
                Assert.Equal(IssueOutcomeKind.Succeeded, result.Kind);
                foreach (var issue in issues)
                    Assert.Equal($"task {issue.Number}", await fixture.Git("show", $"integration:issue-{issue.Number}.txt"));
                Assert.Equal("3", await fixture.Git("rev-list", "--count", "integration"));
                Assert.Equal(1, validation.CombinedCalls);
                var remote = await fixture.Git("ls-remote", "origin", "refs/heads/integration");
                Assert.Equal(await fixture.Git("rev-parse", "integration"), remote.Split('\t')[0]);
            }
            else
            {
                Assert.Equal(IssueOutcomeKind.IntegrationConflict, result.Kind);
                Assert.True(result.Report.RebaseConflict);
                Assert.True(result.Report.WorkspacePreserved);
                Assert.True(Directory.Exists(engines[follower].Workspace));
                Assert.Equal("2", await fixture.Git("rev-list", "--count", "integration"));
                Assert.Equal("integration-conflict", (await history.ReadExecutionAsync(executions[follower].ExecutionId))?.RecoveryState);
            }
            Assert.DoesNotContain("infrastructure failure", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var engine in engines) engine.Release.TrySetResult();
            await Task.WhenAll(tasks.Select(async task => { try { await task; } catch (WorkerInfrastructureException) { } }));
        }
    }

    private sealed class ParallelIntegrationCodex(RepositoryFixture fixture, bool overlap, string scenario, ConcurrentGitHub github) : ICodexExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? StartingHead { get; private set; }
        public string? Branch { get; private set; }
        public string? BaseBranch { get; private set; }
        public string Workspace { get; private set; } = "";
        public bool LeaseLost { get; private set; }
        public int ResolutionCalls { get; private set; }
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            Workspace = projectDirectory;
            StartingHead = await fixture.GitAt(projectDirectory, "rev-parse", "HEAD");
            Branch = await fixture.GitAt(projectDirectory, "branch", "--show-current");
            BaseBranch = await fixture.Git("branch", "--show-current");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, overlap ? "base.txt" : $"issue-{issue.Number}.txt"),
                overlap ? $"base + task {issue.Number}" : $"task {issue.Number}", ct);
            Started.SetResult();
            await Release.Task.WaitAsync(ct);
            return new("success", "implemented current issue", [], false, null);
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("Initial validation must pass.");
        public async Task<CodexOutcome> ResolveIntegrationConflictAsync(string projectDirectory, string instructionsFile,
            GitHubIssue issue, string conflictDetails, CancellationToken ct)
        {
            ResolutionCalls++;
            Assert.Equal(Workspace, projectDirectory);
            Assert.Contains("Base branch: integration", conflictDetails, StringComparison.Ordinal);
            Assert.Equal(fixture.Issue.Body, issue.Body);
            var source = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "base.txt"), ct);
            Assert.Contains("task 17", source, StringComparison.Ordinal);
            Assert.Contains("task 18", source, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "base.txt"), "base + task 17 + task 18", ct);
            if (scenario == "changed-intent") github.ChangedBody = "changed task";
            if (scenario == "lease-loss") LeaseLost = true;
            if (scenario == "uncertain-git") await fixture.GitAt(projectDirectory, "update-ref", "HEAD", StartingHead ?? "invalid");
            return new(scenario == "failed" ? "failed" : "success", "combined both tasks", [], false, null);
        }
    }

    private sealed class ParallelSourceValidation(bool overlap, bool failCombined) : IValidationRunner
    {
        public int CombinedCalls { get; private set; }
        public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            var combined = overlap
                ? (await File.ReadAllTextAsync(Path.Combine(directory, "base.txt"), ct)) == "base + task 17 + task 18"
                : File.Exists(Path.Combine(directory, "issue-17.txt")) && File.Exists(Path.Combine(directory, "issue-18.txt"));
            if (!combined) return ValidationResult.Success;
            CombinedCalls++;
            return failCombined ? new(new ValidationFailure(1, "combined source", 1, "", "combined check failed", false))
                : ValidationResult.Success;
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ShutdownDuringCodexPreservesRealWorkspaceAndRestartContinuesIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../shutdown-history.db"));
        using var shutdown = new CancellationTokenSource();
        using var writer = new StringWriter();
        var console = new WorkerConsole(writer, false);
        using var telegram = new TelegramNotifier(false, console);
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
            Git = new GitSettings { AutoMerge = true },
            GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", DoneLabel = "done" },
            Codex = new CodexSettings { InstructionsFile = "unused" },
            Validation = new ValidationSettings { Commands = ["local-check"] },
            Worker = new WorkerSettings()
        };
        var codex = new ShutdownCodex();
        var github = new ConcurrentGitHub(fixture.Issue);
        ExecutionHistoryEntry interrupted;
        using (var git = fixture.CreateRepository(config.Git))
        {
            await git.InitializeAsync(CancellationToken.None);
            var worker = new Worker(config, github, git, codex,
                new ImmediateValidation(), telegram, console, history, shutdownToken: shutdown.Token);
            var task = (await worker.ClaimNextAsync(shutdown.Token))!;
            var workspace = await codex.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(workspace, "child-started"))) await Task.Delay(20, deadline.Token);
            shutdown.Cancel();
            await WorkerHost.AwaitShutdownExecutionsAsync([task]);
            interrupted = Assert.Single(await history.ReadAllAsync());
            Assert.True(WorkerHost.IsShutdownInterruption(interrupted));
            Assert.Equal("codex-interrupted", interrupted.RecoveryState);
            Assert.Contains(workspace, interrupted.FailureReason);
            Assert.Equal("useful partial implementation", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
            Assert.DoesNotContain("infrastructure failure", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        using var restartedGit = fixture.CreateRepository(config.Git);
        await restartedGit.InitializeAsync(CancellationToken.None);
        var continuation = new FileCodex();
        var clock = new FixedRecoveryClock((interrupted.CodexRecovery?.RetryAfterUtc ?? DateTimeOffset.UtcNow).AddSeconds(1));
        // GitHub state survives Worker restarts, including the interrupted claim's working label.
        Assert.Equal(config.GitHub.WorkingLabel,
            Assert.Single((await github.ReadIssueStateAsync(interrupted.IssueNumber, CancellationToken.None)).Labels));
        var restarted = new Worker(config, github, restartedGit,
            continuation, new ImmediateValidation(), telegram, console, history, timeProvider: clock);
        // Preserved interruption is discovered before any new ready-label implementation.
        var retry = await restarted.ProcessOneAsync(CancellationToken.None);
        Assert.Equal(IssueOutcomeKind.Succeeded, retry!.Kind);
        Assert.Equal(2, retry.Report.AttemptNumber);
        var preserved = Path.Combine(fixture.WorktreeRoot, interrupted.ExecutionId.ToString("N"));
        Assert.Equal(preserved, continuation.LastDirectory);
        Assert.False(Directory.Exists(preserved));
        Assert.Equal("useful partial implementation", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "partial.txt")));
        Assert.Equal(interrupted.ExecutionId, retry.Report.RetryOfExecutionId);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ParallelSemanticIncompatibilityUsesBoundedRepairAndPreservesCurrentBase(
        bool obsoleteExpectation, bool exhausted, bool advanceDuringRepair)
    {
        // Model the provisioning/status concurrency pattern: A adds an installed provider;
        // B separates installation status from authentication/readiness. Both start on B0.
        using var fixture = await seed.CreateAsync();
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
            Git = new GitSettings { AutoMerge = true },
            GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", DoneLabel = "done", BlockedLabel = "blocked", FailedLabel = "failed" },
            Codex = new CodexSettings { InstructionsFile = "unused", Model = "configured-model", ReasoningEffort = "high" },
            Validation = new ValidationSettings { Commands = ["verify status contract"], MaxFixAttempts = 2 }
        };
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../semantic-history.db"));
        using var logs = new StringWriter();
        using var errors = new StringWriter();
        var console = new WorkerConsole(logs, false, errors);
        using var telegram = new TelegramNotifier(false, console);
        var github = new ConcurrentGitHub(fixture.Issue);
        var codex = new SemanticCodex(fixture, obsoleteExpectation, exhausted, advanceDuringRepair);
        var validation = new SemanticValidation(fixture, obsoleteExpectation);
        var worker = new Worker(config, github, git, codex, validation, telegram, console, history);
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)], new RuntimeEventLog());

        Assert.True(registry.TryReserve("sample"));
        var executionA = await worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(executionA);
        await validation.InitialPassed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(registry.TryReserve("sample"));
        var executionB = await worker.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(executionB);
        var integratedB = await executionB;
        Assert.NotNull(integratedB);
        Assert.Equal(IssueOutcomeKind.Succeeded, integratedB.Kind);
        registry.Release("sample");
        Assert.Equal(codex.StartingHeads[17], codex.StartingHeads[18]);
        var baseAfterB = await fixture.Git("rev-parse", "main");
        validation.AllowIntegration.SetResult();
        var result = await executionA;
        Assert.NotNull(result);
        registry.Release("sample");
        Assert.Equal(0, registry.WorkerActiveExecutionCount);
        Assert.Equal(exhausted ? IssueOutcomeKind.IntegrationConflict : IssueOutcomeKind.Succeeded, result.Kind);
        Assert.Equal(exhausted ? 2 : 1, codex.RepairContexts.Count);
        Assert.All(codex.RepairContexts, details =>
        {
            Assert.Equal(codex.StartingHeads[17], details.OriginalBaseCommit);
            Assert.Equal(baseAfterB, details.IntegratedBaseCommit);
            Assert.Contains("Expected:", details.Failure.StandardOutput);
            Assert.Contains("Actual:", details.Failure.StandardOutput);
        });
        Assert.Equal(2 + codex.RepairContexts.Count + (advanceDuringRepair ? 1 : 0), validation.Failures + validation.PassedAfterRebase);
        Assert.Contains("Validation after rebase", logs.ToString());
        Assert.Contains("Integration repair 1/2", logs.ToString());
        Assert.Contains("Validation after integration repair", logs.ToString());
        Assert.DoesNotContain("infrastructure failure", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Report.ValidationRepairs, repair => Assert.True(repair.IntegrationRepair));
        Assert.Contains("### Integration repairs", result.Summary);
        Assert.Contains("Initial validation passed before integration", result.Summary);
        Assert.Contains("installation", await fixture.Git("show", "main:status-contract.txt"));
        Assert.Equal("B's unrelated work", await fixture.Git("show", "main:unrelated.txt"));
        var entry = (await history.ReadAllAsync()).Single(row => row.IssueNumber == 17);
        Assert.Equal(codex.RepairContexts.Count, entry.RepairCount);
        Assert.All(entry.Repairs, repair => Assert.True(repair.IntegrationRepair));
        Assert.Contains(github.Comments, comment => comment.Issue == 17 && comment.Body.Contains("### Integration repairs", StringComparison.Ordinal));

        if (exhausted)
        {
            Assert.Equal(baseAfterB, await fixture.Git("rev-parse", "main"));
            Assert.Equal("IntegrationConflict", entry.State);
            Assert.Equal("integration-conflict", entry.RecoveryState);
            Assert.Contains("2 integration repair attempt(s)", entry.ValidationOutcome);
            Assert.NotEqual(codex.RepairContexts[0].RebasedCommit, codex.RepairContexts[1].RebasedCommit);
            Assert.Contains("preserved repair attempt 1", codex.RepairContexts[1].Failure.StandardError);
            Assert.Contains("preserved repair attempt 2", result.Report.FinalValidationDiagnostics);
            Assert.True(result.Report.WorkspacePreserved);
            Assert.True(result.Report.RetryAvailable);
            Assert.Contains("Expected: Installed", result.Summary);
            Assert.Contains("Actual: Missing", result.Summary);
            Assert.Contains((17, config.GitHub.IntegrationConflictLabel), github.Labels);
            var workspace = Path.Combine(fixture.WorktreeRoot, entry.ExecutionId.ToString("N"));
            Assert.Equal("Installed", await File.ReadAllTextAsync(Path.Combine(workspace, "provider.txt")));
            Assert.Equal("preserved repair attempt 2", await File.ReadAllTextAsync(Path.Combine(workspace, "repair-note.txt")));
            Assert.Equal(string.Empty, await fixture.GitAt(workspace, "status", "--porcelain"));
            Assert.Null(await git.ValidateIntegrationRecoveryAsync(entry, CancellationToken.None));

            // Restart discovery uses only the conflict label. Later main work must survive
            // recovery of the preserved implementation and semantic repair.
            await fixture.AdvanceBaseAsync("later.txt", "later main work");
            codex.Exhausted = false;
            github.AutomaticConflictIssue = fixture.Issue;
            var restartedWorker = new Worker(config, github, git, codex, validation, telegram, console, history);
            var recovered = await restartedWorker.ProcessOneAsync(CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(IssueOutcomeKind.Succeeded, recovered.Kind);
            Assert.Single(recovered.Report.ValidationRepairs);
            Assert.Equal(2, codex.ImplementationCalls);
            Assert.Equal("Installed", await fixture.Git("show", "main:provider.txt"));
            Assert.Equal("installation", await fixture.Git("show", "main:status-implementation.txt"));
            Assert.Equal("B's unrelated work", await fixture.Git("show", "main:unrelated.txt"));
            Assert.Equal("later main work", await fixture.Git("show", "main:later.txt"));
            Assert.Contains("Validation after integration recovery", logs.ToString());
            Assert.Contains(entry.ExecutionId.ToString(), recovered.Summary);
            Assert.Equal("integration-recovered", (await history.ReadAllAsync()).Single(row => row.ExecutionId == entry.ExecutionId).RecoveryState);
        }
        else
        {
            Assert.True(result.Report.ValidationRepairs[0].PassedAfterRepair);
            Assert.Equal("Installed", await fixture.Git("show", "main:provider.txt"));
            Assert.Equal(obsoleteExpectation ? "Installed" : "specification", await fixture.Git("show", "main:expected-status.txt"));
            Assert.Equal(obsoleteExpectation ? "contract" : "installation", await fixture.Git("show", "main:status-implementation.txt"));
            Assert.NotNull(result.Report.Integration);
            Assert.Equal(await fixture.Git("rev-parse", "main"), result.Report.Integration.CommitSha);
            Assert.Equal("passed", entry.ValidationOutcome);
            if (advanceDuringRepair)
            {
                Assert.Equal("new concurrent work", await fixture.Git("show", "main:during-repair.txt"));
                Assert.Equal(2, validation.PassedAfterRebase);
                Assert.NotNull(codex.AdvancedHead);
                Assert.Contains(codex.AdvancedHead, await fixture.Git("rev-list", "main"));
            }
        }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteIntegrationRepairIsRevalidatedAndRemainsRecoverable(bool validationPasses)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "implementation.txt"), "original valuable implementation");
        await fixture.AdvanceBaseAsync();
        var baseBefore = await fixture.Git("rev-parse", "main");
        var validations = 0;
        var repairs = 0;
        var error = await Assert.ThrowsAnyAsync<GitIntegrationConflictException>(() => git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(++validations == 3 && validationPasses ? ValidationResult.Success :
                new ValidationResult(new ValidationFailure(1, "check", 1, "Expected: compatible", "Actual: incompatible", false))),
            (_, _) => Task.FromResult(false), async (_, ct) =>
            {
                repairs++;
                await File.WriteAllTextAsync(Path.Combine(workspace, "partial-repair.txt"), "useful incomplete repair", ct);
                return new IntegrationRepairResult(true, false);
            }, CancellationToken.None));

        Assert.Equal(3, validations);
        Assert.Equal(1, repairs);
        Assert.Contains(validationPasses ? "although validation passed" : "incomplete or exhausted", error.Message);
        Assert.Equal(baseBefore, await fixture.Git("rev-parse", "main"));
        var recovery = await git.PreserveIntegrationConflictAsync(CancellationToken.None);
        Assert.NotNull(recovery);
        Assert.Equal("original valuable implementation", await fixture.GitAt(workspace, "show", "HEAD:implementation.txt"));
        Assert.Equal("useful incomplete repair", await fixture.GitAt(workspace, "show", "HEAD:partial-repair.txt"));
        Assert.Equal(string.Empty, await fixture.GitAt(workspace, "status", "--porcelain"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("history")]
    [InlineData("validation")]
    [InlineData("authority")]
    [InlineData("infrastructure")]
    public async Task IntegrationRepairDoesNotRelaxHistoryValidationAuthorityOrInfrastructureSafety(string violation)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "implementation.txt"), "implementation");
        await fixture.AdvanceBaseAsync();
        var baseBefore = await fixture.Git("rev-parse", "main");
        var repaired = false;
        var authoritative = true;
        var error = await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => git.CommitAndIntegrateAsync(fixture.Issue,
            async ct =>
            {
                if (!repaired) return new ValidationResult(new ValidationFailure(1, "check", 1, "failed test", "", false));
                if (violation == "validation") await File.WriteAllTextAsync(Path.Combine(workspace, "mutation.txt"), "validation changed source", ct);
                return ValidationResult.Success;
            }, (_, _) => Task.FromResult(false), async (_, ct) =>
            {
                if (violation == "infrastructure")
                    throw new CodexExecutionInfrastructureException("Codex process failure", "Codex service failed");
                await File.WriteAllTextAsync(Path.Combine(workspace, "repair.txt"), "repair", ct);
                if (violation == "history")
                {
                    await fixture.GitAt(workspace, "add", "--all");
                    await fixture.GitAt(workspace, "commit", "-m", "unauthorized Codex history mutation");
                }
                authoritative = violation != "authority";
                repaired = true;
                return new IntegrationRepairResult(true, true);
            }, CancellationToken.None, _ => authoritative ? Task.CompletedTask :
                throw new WorkerInfrastructureException("Integration authority was lost")));

        Assert.Equal(baseBefore, await fixture.Git("rev-parse", "main"));
        Assert.True(Directory.Exists(workspace));
        if (violation == "infrastructure") Assert.IsType<CodexExecutionInfrastructureException>(error);
        else Assert.Contains(violation == "history" ? "changed Git history" : violation == "validation" ? "source changed" : "authority was lost", error.Message);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CancellationDuringIntegrationRepairPreservesWorkAndNeverIntegrates()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "implementation.txt"), "original implementation");
        await fixture.AdvanceBaseAsync();
        var baseBefore = await fixture.Git("rev-parse", "main");
        using var cancellation = new CancellationTokenSource();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(new ValidationResult(new ValidationFailure(1, "check", 1, "failed test", "", false))),
            (_, _) => Task.FromResult(false), async (_, ct) =>
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, "partial-repair.txt"), "interrupted useful repair", ct);
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return new IntegrationRepairResult(true, true);
            }, cancellation.Token));

        Assert.True(WorkerShutdown.IsCancellation(error));
        Assert.Equal(baseBefore, await fixture.Git("rev-parse", "main"));
        Assert.Equal("original implementation", await fixture.GitAt(workspace, "show", "HEAD:implementation.txt"));
        Assert.Equal("interrupted useful repair", await File.ReadAllTextAsync(Path.Combine(workspace, "partial-repair.txt")));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterRepairCommitConflictPreservesImplementationAndHonorsResolutionAuthorization(bool preservePartial)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        if (preservePartial) git.PreservePartialConflictResolution();
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "first.txt"), "original implementation");
        await fixture.AdvanceBaseAsync();
        var repaired = false;
        var advanced = false;
        string? preservedHead = null;
        string? currentBase = null;
        var resolutions = 0;
        Task<GitIntegrationResult> IntegrateAsync() => git.CommitAndIntegrateAsync(fixture.Issue,
            async _ =>
            {
                if (!repaired) return new ValidationResult(new ValidationFailure(1, "check", 1, "failed test", "", false));
                if (!advanced)
                {
                    advanced = true;
                    preservedHead = await fixture.GitAt(workspace, "rev-parse", "HEAD");
                    await fixture.AdvanceBaseAsync("first.txt", "new base implementation");
                    await fixture.AdvanceBaseAsync("second.txt", "new base behavior");
                    currentBase = await fixture.Git("rev-parse", "main");
                }
                return ValidationResult.Success;
            }, async (_, ct) =>
            {
                resolutions++;
                await File.WriteAllTextAsync(Path.Combine(workspace, "first.txt"), "combined implementation", ct);
                return true;
            }, async (_, ct) =>
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, "second.txt"), "semantic repair", ct);
                repaired = true;
                return new IntegrationRepairResult(true, true);
            }, CancellationToken.None);
        GitIntegrationConflictException error = preservePartial
            ? await Assert.ThrowsAsync<GitConflictResolutionIncompleteException>(IntegrateAsync)
            : await Assert.ThrowsAsync<GitIntegrationConflictException>(IntegrateAsync);

        Assert.Equal(1, resolutions);
        if (preservePartial)
        {
            Assert.Contains("partial resolution", error.Message, StringComparison.Ordinal);
            Assert.Equal(currentBase, await fixture.Git("rev-parse", "main"));
            Assert.Equal("combined implementation", await File.ReadAllTextAsync(Path.Combine(workspace, "first.txt")));
            Assert.NotEmpty(await fixture.GitAt(workspace, "ls-files", "-u"));
            return;
        }
        Assert.Contains("rebase was aborted", error.Message);
        Assert.Contains("conflicts unresolved", error.Message);
        Assert.Equal(currentBase, await fixture.Git("rev-parse", "main"));
        Assert.Equal(preservedHead, await fixture.GitAt(workspace, "rev-parse", "HEAD"));
        Assert.Equal("original implementation", await File.ReadAllTextAsync(Path.Combine(workspace, "first.txt")));
        Assert.Equal("semantic repair", await File.ReadAllTextAsync(Path.Combine(workspace, "second.txt")));
        Assert.NotNull(await git.PreserveIntegrationConflictAsync(CancellationToken.None));
    }
}
