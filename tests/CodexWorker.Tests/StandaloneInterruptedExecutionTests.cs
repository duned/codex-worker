using System.Diagnostics;
using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class StandaloneInterruptedExecutionTests(GitWorktreeTests.RepositorySeed seed)
    : IClassFixture<GitWorktreeTests.RepositorySeed>
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    public async Task ShutdownDuringValidationContinuesSameAttemptWithoutCodex(int interruptAt)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        var github = new GitHub(fixture.Issue);
        using var telegram = new TelegramNotifier(false);
        using var shutdown = new CancellationTokenSource();
        var codex = new Codex(async path =>
        {
            await File.WriteAllTextAsync(Path.Combine(path, "implementation.txt"), "implemented once");
            await fixture.AdvanceBaseAsync();
        });
        var validation = new Validation(() =>
        {
            shutdown.Cancel();
            return Task.FromCanceled<ValidationResult>(shutdown.Token);
        }, interruptAt: interruptAt);
        using (var originalGit = fixture.CreateRepository(config.Git))
        {
            var worker = new Worker(config, github, originalGit, codex, validation, telegram, history: history, shutdownToken: shutdown.Token);
            await worker.PrepareForHostAsync(CancellationToken.None);
            await Assert.ThrowsAsync<WorkerShutdownException>(() => worker.ProcessOneAsync(shutdown.Token));
        }
        var interrupted = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("Cancelled", interrupted.State);
        Assert.Equal("uncertain", interrupted.RecoveryState);
        Assert.Equal(interruptAt == 2 ? "pending: validation after rebase" : null, interrupted.ValidationOutcome);
        Assert.Null(interrupted.CommitSha);
        Assert.Null(interrupted.IntegrationBranch);
        Assert.Null(interrupted.CompletionJson);
        Assert.NotNull(interrupted.CodexRecovery?.ProcessScope);
        Assert.True(interrupted.CodexRecovery?.ImplementationSucceeded);
        Assert.Equal(interruptAt, validation.Calls);
        using var restartedGit = fixture.CreateRepository(config.Git);
        var nextValidation = new Validation();
        var diagnostics = new List<string>();
        var restarted = new Worker(config, github, restartedGit, codex, nextValidation, telegram, history: history, operationalLog: diagnostics.Add);
        Assert.True(restarted.IsStandaloneInterruption(interrupted));
        await restarted.PrepareForHostAsync(CancellationToken.None);
        var result = await restarted.ProcessOneAsync(CancellationToken.None);
        Assert.True(result?.Kind == IssueOutcomeKind.Succeeded, string.Join("\n", diagnostics));
        var completed = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(interrupted.ExecutionId, completed.ExecutionId);
        Assert.Equal("Completed", completed.State);
        Assert.Contains("Worker shutdown", completed.FailureReason);
        Assert.Equal(1, codex.Calls);
        Assert.Equal(1, nextValidation.Calls);
        Assert.Single(github.Comments);
        Assert.Equal(1, github.CloseCalls);
        Assert.False(Directory.Exists(restartedGit.RecoveryWorkspacePath(completed)));
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Single(github.Comments);
        Assert.Equal(1, github.CloseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    public async Task ObservedExecution724ce14eResolvesWithRecoveryOrSafeRetry(bool originalIntent)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var source = await PreserveAsync(fixture, git, config, history, Guid.Parse("724ce14e-d3fa-4f33-b6ab-585bf63e4872"), originalIntent: originalIntent);
        var featureTip = await fixture.Git("rev-parse", source.FeatureBranch);
        var github = new GitHub(fixture.Issue) { Labels = ["ready", "working"] };
        var codex = new Codex(path => File.WriteAllTextAsync(Path.Combine(path, "fresh.txt"), "safe fresh attempt"));
        using var telegram = new TelegramNotifier(false);
        var worker = new Worker(config, github, git, codex, new Validation(), telegram, history: history);
        await worker.PrepareForHostAsync(CancellationToken.None);
        var saved = await history.ReadAllAsync();
        Assert.Equal("recovery-ambiguous", (await git.InspectCleanupAsync(Assert.Single(saved), saved, CancellationToken.None)).ReasonCode);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await worker.ProcessOneAsync(CancellationToken.None))?.Kind);
        var entries = await history.ReadAllAsync();
        if (originalIntent)
        {
            var completed = Assert.Single(entries);
            Assert.Equal(source.ExecutionId, completed.ExecutionId);
            Assert.Equal("Completed", completed.State);
            Assert.Equal(0, codex.Calls);
            Assert.Single(github.Comments);
            return;
        }
        var retired = Assert.Single(entries, entry => entry.ExecutionId == source.ExecutionId);
        var attempt = Assert.Single(entries, entry => entry.ExecutionId != source.ExecutionId);
        Assert.Equal("Superseded", retired.State);
        Assert.Equal("superseded", ExecutionMaintenanceClassifier.Outcome(retired.State));
        var inventory = new WorkerRuntimeReadModel(new(), [("project.yml", config)], history);
        var superseded = await inventory.ExecutionInventoryAsync(new(Outcome: "Superseded"), CancellationToken.None);
        Assert.Equal(retired.ExecutionId, Assert.Single(superseded.Items).Execution.ExecutionId);
        Assert.Equal("superseded", Assert.Single(superseded.Items).Execution.Result);
        Assert.Equal("standalone-retired", retired.RecoveryState);
        Assert.Equal(source.FailureReason, retired.FailureReason);
        Assert.Equal("passed", retired.ValidationOutcome);
        Assert.Equal(source.ExecutionId, attempt.RetryOfExecutionId);
        Assert.Equal(2, attempt.AttemptNumber);
        Assert.False(attempt.Resumed);
        Assert.False(Directory.Exists(git.RecoveryWorkspacePath(retired)));
        Assert.Equal(featureTip, await fixture.Git("rev-parse", retired.FeatureBranch));
        Assert.DoesNotContain("working", github.Labels);
        Assert.Equal(1, codex.Calls);
        Assert.Null(await worker.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
    }

    [Theory]
    [InlineData("remote-unavailable", "authoritative-remote-unavailable")]
    [InlineData("integrated-without-provenance", "integration-present-without-validated-provenance")]
    [InlineData("closed", "issue-no-longer-eligible")]
    [InlineData("not-ready", "issue-no-longer-eligible")]
    [InlineData("active-process", "workspace-process-active")]
    [InlineData("newer-attempt", "newer-or-completed-attempt-exists")]
    [InlineData("active-attempt", "another-attempt-or-recovery-active")]
    [InlineData("missing-ownership", "integration-or-ownership-metadata-unproven")]
    [InlineData("changed-head", "workspace-history-diverged")]
    [InlineData("process-scope-changed", "process-scope-changed")]
    [InlineData("uncompleted-implementation", "retry-requires-ready-label")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task AmbiguousOrIneligibleInterruptionRetainsEvidence(string scenario, string blocker)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var source = await PreserveAsync(fixture, git, config, history, Guid.NewGuid(), originalIntent: true,
            missingBase: scenario == "missing-ownership", unprovenProcesses: scenario == "process-scope-changed", uncompletedImplementation: scenario == "uncompleted-implementation");
        var github = new GitHub(fixture.Issue) { Labels = ["ready", "working"] };
        var messages = new List<string>();
        var codex = new Codex(_ => Task.CompletedTask);
        using var telegram = new TelegramNotifier(false);
        Process? child = null;
        try
        {
            if (scenario == "remote-unavailable") Directory.Move(Path.Combine(fixture._root, "origin.git"), Path.Combine(fixture._root, "offline.git"));
            if (scenario == "integrated-without-provenance")
            {
                await fixture.Git("merge", "--ff-only", source.FeatureBranch);
                await fixture.Git("push", "origin", "main");
            }
            if (scenario == "changed-head")
            {
                // Replace history with an unrelated root; a registered branch is not enough.
                var tree = await fixture.Git("rev-parse", "HEAD^{tree}");
                var unrelated = await fixture.Git("commit-tree", tree, "-m", "unrelated ownership");
                await fixture.GitAt(git.RecoveryWorkspacePath(source), "reset", "--hard", unrelated);
            }
            if (scenario == "closed") github.Open = false;
            if (scenario == "not-ready") github.Labels = [];
            if (scenario == "uncompleted-implementation") github.Labels = ["working"];
            if (scenario is "newer-attempt" or "active-attempt")
                await history.CreateAsync(source with { ExecutionId = Guid.NewGuid(), AttemptNumber = 2, RetryOfExecutionId = source.ExecutionId,
                    StartedAtUtc = source.StartedAtUtc.AddSeconds(1), State = scenario == "active-attempt" ? "Implementing" : "Failed",
                    CompletedAtUtc = scenario == "active-attempt" ? null : DateTimeOffset.UtcNow, RecoveryState = "uncertain" });
            if (scenario == "active-process")
            {
                child = Process.Start(new ProcessStartInfo("/bin/sleep", ["120"]) { WorkingDirectory = git.RecoveryWorkspacePath(source), UseShellExecute = false });
                Assert.NotNull(child);
            }
            var worker = new Worker(config, github, git, codex, new Validation(), telegram, history: history, operationalLog: messages.Add);
            Assert.Null(await worker.ProcessOneAsync(CancellationToken.None));
            Assert.True(messages.Any(message => message.Contains(blocker, StringComparison.Ordinal)), string.Join("\n", messages));
            Assert.True(Directory.Exists(git.RecoveryWorkspacePath(source)));
            var retained = await history.ReadExecutionAsync(source.ExecutionId);
            Assert.NotNull(retained);
            Assert.Equal(JsonSerializer.Serialize(source with { StandaloneRecoveryDiagnosticJson = retained.StandaloneRecoveryDiagnosticJson }),
                JsonSerializer.Serialize(retained));
            var diagnostic = JsonSerializer.Deserialize<StandaloneRecoveryDiagnostic>(retained.StandaloneRecoveryDiagnosticJson!);
            Assert.NotNull(diagnostic);
            Assert.Equal(blocker, diagnostic.ReasonCode);
            Assert.Equal(0, codex.Calls);
            Assert.Empty(github.Mutations);
        }
        finally
        {
            if (child is not null)
            {
                child.Kill();
                await child.WaitForExitAsync();
                child.Dispose();
            }
        }
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    public async Task RestartAfterRetirementRemovalFinishesReceiptAndCreatesExactlyOneFreshAttempt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var source = await PreserveAsync(fixture, git, config, history, Guid.NewGuid(), originalIntent: false);
        var inspection = await git.InspectInterruptedExecutionAsync(source, CancellationToken.None);
        Assert.Null(inspection.Blocker);
        Assert.True(await history.TryTransitionStandaloneAsync(source, "standalone-retiring", inspection, CancellationToken.None));
        var retiring = await history.ReadExecutionAsync(source.ExecutionId);
        Assert.NotNull(retiring);
        await git.RetireInterruptedExecutionAsync(retiring, CancellationToken.None);
        // Simulate losing the final history write after removing only the owned worktree.
        var github = new GitHub(fixture.Issue) { Labels = ["ready", "working"] };
        using var telegram = new TelegramNotifier(false);
        var codex = new Codex(path => File.WriteAllTextAsync(Path.Combine(path, "fresh.txt"), "retry"));
        var restarted = new Worker(config, github, git, codex, new Validation(), telegram, history: history);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await restarted.ProcessOneAsync(CancellationToken.None))?.Kind);
        Assert.Null(await restarted.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
        Assert.Equal(1, codex.Calls);
        Assert.DoesNotContain("working", github.Labels);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task ValidatedRemoteIntegrationFinishesOriginalCompletionWithoutDuplicateEffects()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        using var telegram = new TelegramNotifier(false);
        var github = new GitHub(fixture.Issue) { LoseCommentResponse = true };
        var codex = new Codex(path => File.WriteAllTextAsync(Path.Combine(path, "implementation.txt"), "once"));
        var validation = new Validation();
        var worker = new Worker(config, github, git, codex, validation, telegram, history: history);
        await worker.PrepareForHostAsync(CancellationToken.None);
        await Assert.ThrowsAsync<GitHubOperationException>(() => worker.ProcessOneAsync(CancellationToken.None));
        var pending = Assert.Single(await history.ReadAllAsync());
        Assert.NotNull(pending.CompletionJson);
        Assert.True(WorkerHost.NeedsCompletionReconciliation(pending));
        Assert.False(worker.IsStandaloneInterruption(pending));
        Assert.Single(github.Comments);
        await fixture.AdvanceBaseAsync();
        var validationCalls = validation.Calls;
        var restarted = new Worker(config, github, git, codex, validation, telegram, history: history);
        await restarted.PrepareForHostAsync(CancellationToken.None);
        Assert.True(restarted.HandlesStandaloneRecovery(pending));
        Assert.Null(await restarted.ProcessOneAsync(CancellationToken.None));
        var completed = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(pending.ExecutionId, completed.ExecutionId);
        Assert.Equal("Completed", completed.State);
        await restarted.ResumeCompletionAsync(completed, CancellationToken.None);
        Assert.Single(github.Comments);
        Assert.Equal(1, github.CloseCalls);
        Assert.Equal(1, codex.Calls);
        Assert.Equal(validationCalls, validation.Calls);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task ReadOnlyReconciliationIsBoundedAndRepeatedStartupDoesNotDuplicateAttempts()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var preserved = await PreserveAsync(fixture, git, config, history, Guid.NewGuid(), originalIntent: true);
        for (var index = 1; index < 21; index++)
        {
            var executionId = Guid.NewGuid();
            var recovery = preserved.CodexRecovery is { } snapshot
                ? snapshot with { WorkspaceExecutionId = executionId }
                : null;
            await history.CreateAsync(preserved with
            {
                ExecutionId = executionId,
                IssueNumber = fixture.Issue.Number + index,
                StartedAtUtc = preserved.StartedAtUtc.AddSeconds(index),
                CodexRecovery = recovery
            });
        }
        var github = new GitHub(fixture.Issue) { Labels = [] };
        using var telegram = new TelegramNotifier(false);
        var clock = new Clock();
        var codex = new Codex(_ => Task.CompletedTask);
        using var startupOutput = new StringWriter();
        var output = new WorkerConsole(startupOutput, interactive: false, errorWriter: startupOutput);
        var worker = new Worker(config, github, git, codex, new Validation(), telegram, history: history,
            output: output, timeProvider: clock);
        for (var check = 0; check < 5; check++)
        {
            Assert.Null(await worker.ProcessOneAsync(CancellationToken.None));
            clock.Now += TimeSpan.FromMinutes(1);
        }
        Assert.Equal(3, github.StateReads);
        Assert.Single(startupOutput.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Standalone recovery checks blocked", startupOutput.ToString());
        Assert.Contains("issue-no-longer-eligible (", startupOutput.ToString());
        Assert.Contains("codex-worker recovery blocked", startupOutput.ToString());
        var restarted = new Worker(config, github, git, codex, new Validation(), telegram, history: history, timeProvider: clock);
        Assert.Null(await restarted.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(6, github.StateReads);
        var retained = await history.ReadAllAsync();
        Assert.Equal(21, retained.Count);
        Assert.Equal(3, retained.Count(entry => entry.StandaloneRecoveryDiagnosticJson is not null));
        Assert.All(retained.Where(entry => entry.StandaloneRecoveryDiagnosticJson is not null), entry =>
            Assert.Contains("issue-no-longer-eligible", entry.StandaloneRecoveryDiagnosticJson));
        Assert.Equal(0, codex.Calls);
        Assert.Empty(github.Mutations);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task UnexpectedStandaloneRecoveryFailuresRemainVisible()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        await PreserveAsync(fixture, git, config, history, Guid.NewGuid(), originalIntent: true);
        var github = new GitHub(fixture.Issue) { Labels = [], StateReadFailure = new WorkerInfrastructureException("GitHub state inspection failed unexpectedly.") };
        using var telegram = new TelegramNotifier(false);
        using var startupOutput = new StringWriter();
        var worker = new Worker(config, github, git, new Codex(_ => Task.CompletedTask), new Validation(), telegram,
            output: new WorkerConsole(startupOutput, interactive: false, errorWriter: startupOutput), history: history);

        Assert.Null(await worker.ProcessOneAsync(CancellationToken.None));
        Assert.Contains("Standalone recovery check failed", startupOutput.ToString());
        Assert.Contains("GitHub state inspection failed unexpectedly", startupOutput.ToString());
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("uncertain", "recovery-ambiguous")]
    [InlineData("github-reconciliation-required", "recovery-ambiguous")]
    [InlineData("github-reconciled", "recovery-ambiguous")]
    [InlineData("integration-conflict-unavailable", "recovery-ambiguous")]
    [InlineData("integration-recovery-interrupted", "recovery-ambiguous")]
    [InlineData("standalone-continuing", "recovery-ambiguous")]
    [InlineData("standalone-retiring", "recovery-ambiguous")]
    [InlineData("standalone-retired", "retired-commits-retained")]
    [InlineData("preparation-failed", "preparation-resource-proof-missing")]
    [InlineData("managed-completion-quarantined", "managed-completion-quarantined")]
    [InlineData("operator-acknowledged", "operator-acknowledged")]
    [InlineData("unsupported-state", "unknown-recovery-state")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task InspectorClassifiesSupportedRecoveryStatesExplicitly(string state, string reason)
    {
        using var git = new GitRepository(new ProcessRunner(), "/unused", "owner/repo", new GitSettings(), new WorkerSettings(), "/owned-worktrees");
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task", "feature/example-task-17", "main",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Cancelled", 1, null, null, 0, [], null, null, null, null, RecoveryState: state);
        Assert.Equal(reason, (await git.InspectCleanupAsync(entry, [entry], CancellationToken.None)).ReasonCode);
    }

    [Theory]
    [InlineData("Created", "ready", true)]
    [InlineData("Created", "ready", false)]
    [InlineData("Claimed", "working", true)]
    [InlineData("Preparing", "working", true)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task RestartBetweenFreshAttemptCreationAndWorkspacePreparationKeepsOneAttempt(string stage, string label, bool hasChanges)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        var execution = WorkerExecution.Create(config.Project, config.Git, fixture.Issue, codexSettings: config.Codex);
        var saved = new ExecutionHistoryEntry(execution.ExecutionId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            execution.FeatureBranch, "main", execution.StartedAtUtc, null, stage, null, null, null, 0, [], null, null, null, null,
            EffectiveEffort: config.Codex.ReasoningEffort, OriginalIssueBody: fixture.Issue.Body);
        await history.CreateAsync(saved);
        var github = new GitHub(fixture.Issue) { Labels = [label] };
        using var telegram = new TelegramNotifier(false);
        var codex = new Codex(path => hasChanges ? File.WriteAllTextAsync(Path.Combine(path, "once.txt"), "one implementation") : Task.CompletedTask);
        var worker = new Worker(config, github, git, codex, new Validation(), telegram, history: history);
        await worker.PrepareForHostAsync(CancellationToken.None);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await worker.ProcessOneAsync(CancellationToken.None))?.Kind);
        var completed = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(saved.ExecutionId, completed.ExecutionId);
        Assert.Equal("Completed", completed.State);
        Assert.Equal(1, codex.Calls);
        Assert.Null(await worker.ProcessOneAsync(CancellationToken.None));
        Assert.Single(await history.ReadAllAsync());
        Assert.DoesNotContain("working", github.Labels);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task LostRecoveryClaimLabelResponseKeepsTheAttemptAndUsesTheHostFailureBoundary()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var source = await PreserveAsync(fixture, git, config, history, Guid.NewGuid(), originalIntent: true);
        var github = new GitHub(fixture.Issue) { Labels = ["ready", "working"], LoseLabelResponse = true };
        using var telegram = new TelegramNotifier(false);
        var codex = new Codex(_ => throw new InvalidOperationException("Implementation must not rerun."));
        var worker = new Worker(config, github, git, codex, new Validation(), telegram, history: history);
        var error = await Assert.ThrowsAsync<GitHubOperationException>(() => worker.ProcessOneAsync(CancellationToken.None));
        Assert.True(error.IsMutation);
        var retained = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(source.ExecutionId, retained.ExecutionId);
        Assert.Equal("standalone-continuing", retained.RecoveryState);
        Assert.True(Directory.Exists(git.RecoveryWorkspacePath(source)));
        var restarted = new Worker(config, github, git, codex, new Validation(), telegram, history: history);
        await restarted.PrepareForHostAsync(CancellationToken.None);
        Assert.Equal(IssueOutcomeKind.Succeeded, (await restarted.ProcessOneAsync(CancellationToken.None))?.Kind);
        Assert.Single(await history.ReadAllAsync());
        Assert.Equal(0, codex.Calls);
        Assert.DoesNotContain("working", github.Labels);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task UnconfirmedValidatedPushIsReadOnlyReconciledWithoutReplayOrRetirement()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        var config = Config(fixture);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture._root, "history.db"));
        using var git = fixture.CreateRepository(config.Git);
        using var telegram = new TelegramNotifier(false);
        var github = new GitHub(fixture.Issue);
        var codex = new Codex(path => File.WriteAllTextAsync(Path.Combine(path, "implementation.txt"), "once"));
        var hook = Path.Combine(fixture._root, "origin.git", "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var worker = new Worker(config, github, git, codex, new Validation(), telegram, history: history);
        await worker.PrepareForHostAsync(CancellationToken.None);
        await Assert.ThrowsAsync<CompletionReconciliationRequiredException>(() => worker.ProcessOneAsync(CancellationToken.None));
        var pending = Assert.Single(await history.ReadAllAsync());
        Assert.NotNull(pending.CompletionJson);
        github.Labels.Add("ready");
        var clock = new Clock();
        var diagnostics = new List<string>();
        var restarted = new Worker(config, github, git, codex, new Validation(), telegram, history: history,
            timeProvider: clock, operationalLog: diagnostics.Add);
        await restarted.PrepareForHostAsync(CancellationToken.None);
        using (var child = Process.Start(new ProcessStartInfo("/bin/sleep", ["120"])
            { WorkingDirectory = git.RecoveryWorkspacePath(pending), UseShellExecute = false }))
        {
            Assert.NotNull(child);
            try
            {
                Assert.Null(await restarted.ProcessOneAsync(CancellationToken.None));
                Assert.Contains(diagnostics, message => message.Contains("workspace-process-active", StringComparison.Ordinal));
            }
            finally { child.Kill(); await child.WaitForExitAsync(); }
        }
        clock.Now += TimeSpan.FromMinutes(1);
        for (var check = 0; check < 4; check++)
        {
            Assert.Null(await restarted.ProcessOneAsync(CancellationToken.None));
            clock.Now += TimeSpan.FromMinutes(1);
        }
        Assert.Contains(diagnostics, message => message.Contains("missing the exact validated integration commit", StringComparison.Ordinal));
        Assert.Single(await history.ReadAllAsync());
        Assert.True(Directory.Exists(git.RecoveryWorkspacePath(pending)));
        Assert.Empty(github.Comments);
        Assert.Equal(1, codex.Calls);
        File.Delete(hook);
        await fixture.Git("push", "origin", "main"); // Explicit operator retry of only the original push.
        github.Labels.Remove("ready");
        var confirmed = new Worker(config, github, git, codex, new Validation(), telegram, history: history);
        await confirmed.PrepareForHostAsync(CancellationToken.None);
        Assert.Null(await confirmed.ProcessOneAsync(CancellationToken.None));
        Assert.Equal("Completed", Assert.Single(await history.ReadAllAsync()).State);
        Assert.Single(github.Comments);
        Assert.Equal(1, codex.Calls);
    }

    private static WorkerConfiguration Config(GitWorktreeTests.RepositoryFixture fixture) => new()
    {
        Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
        Git = new() { PushCompletedBranch = false },
        GitHub = new() { ReadyLabel = "ready", WorkingLabel = "working", DoneLabel = "done", FailedLabel = "failed", BlockedLabel = "blocked" },
        Codex = new() { InstructionsFile = "unused", AllowMissingDefaultInstructions = true },
        Validation = new() { Commands = ["authoritative-check"], MaxFixAttempts = 0 }
    };

    private static async Task<ExecutionHistoryEntry> PreserveAsync(GitWorktreeTests.RepositoryFixture fixture, GitRepository git,
        WorkerConfiguration config, ExecutionHistoryStore history, Guid id, bool originalIntent, bool missingBase = false, bool unprovenProcesses = false, bool uncompletedImplementation = false)
    {
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var starting = await fixture.Git("rev-parse", "main");
        var path = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(path, "preserved.txt"), "useful original implementation");
        await fixture.GitAt(path, "add", "preserved.txt");
        await fixture.GitAt(path, "commit", "-m", "original implementation before interrupted validation");
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            GitRepository.FeatureBranchName(config.Git, fixture.Issue), "main", DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow,
            "Cancelled", 1, "Implementation succeeded", "passed", 0, [], null, null, null, "Execution interrupted by Worker shutdown",
            RecoveryState: "uncertain", RecoveryBaseCommit: missingBase ? null : starting, EffectiveEffort: config.Codex.ReasoningEffort,
            OriginalIssueBody: originalIntent ? fixture.Issue.Body : null,
            CodexRecovery: originalIntent ? new(id, 1, 0, CodexInterruptionRecovery.Fingerprint(config)) : null);
        if (uncompletedImplementation)
            entry = entry with { CodexRecovery = entry.CodexRecovery is { } snapshot
                ? snapshot with { ImplementationSucceeded = false } : null };
        if (unprovenProcesses)
            entry = entry with { State = "InfrastructureFailure", FailureReason = "Process teardown was not confirmed.",
                CodexRecovery = entry.CodexRecovery is { } recovery ? recovery with { ProcessScope = "changed-scope" } : null };
        await history.CreateAsync(entry);
        return entry;
    }

    private sealed class GitHub(GitHubIssue issue) : IGitHubClient
    {
        internal HashSet<string> Labels { get; set; } = ["ready"];
        internal bool Open { get; set; } = true;
        internal List<string> Mutations { get; } = [];
        internal Dictionary<Guid, string> Comments { get; } = [];
        internal int CloseCalls { get; private set; }
        internal int StateReads { get; private set; }
        internal bool LoseCommentResponse { get; set; }
        internal bool LoseLabelResponse { get; set; }
        internal Exception? StateReadFailure { get; set; }
        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubIssue?>(Open && Labels.Contains(label) ? issue with { Labels = Labels.ToArray() } : null);
        public Task<GitHubIssue?> FindOldestReadyAsync(string label, IReadOnlySet<int> excludedIssueNumbers, CancellationToken cancellationToken) =>
            excludedIssueNumbers.Contains(issue.Number) ? Task.FromResult<GitHubIssue?>(null) : FindOldestReadyAsync(label, cancellationToken);
        public Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken cancellationToken) => Task.FromResult<GitHubIssue?>(Open ? issue : null);
        public Task<GitHubIssue?> GetCompletionIssueAsync(int issueNumber, CancellationToken ct) => Task.FromResult<GitHubIssue?>(issue);
        public Task<bool> IsIssueOpenAsync(int issueNumber, CancellationToken cancellationToken) => Task.FromResult(Open);
        public Task<GitHubIssueState> ReadIssueStateAsync(int issueNumber, CancellationToken ct)
        {
            if (StateReadFailure is { } failure) throw failure;
            StateReads++; return Task.FromResult(new GitHubIssueState(Open, Labels.ToArray()));
        }
        public Task<string> GetIssueCommentContextAsync(int issueNumber, CancellationToken cancellationToken, IReadOnlyList<string>? secretValues = null) => Task.FromResult("");
        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        {
            Mutations.Add($"{remove}->{add}"); Labels.Remove(remove); Labels.Add(add);
            if (LoseLabelResponse)
            {
                LoseLabelResponse = false;
                throw new GitHubOperationException("replace labels", issueNumber, true, GitHubFailureKind.TransientProvider,
                    GitHubRemoteState.Uncertain, "Lost label response after successful mutation");
            }
            return Task.CompletedTask;
        }
        public Task RemoveLabelAsync(int issueNumber, string label, CancellationToken ct)
        {
            Mutations.Add($"remove:{label}"); Labels.Remove(label); return Task.CompletedTask;
        }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct)
        {
            Comments.TryAdd(Guid.Empty, comment); return Task.CompletedTask;
        }
        public Task EnsureSuccessCommentAsync(int issueNumber, Guid executionId, string comment, CancellationToken ct, bool allowCreate = true)
        {
            if (allowCreate) Comments.TryAdd(executionId, comment);
            if (LoseCommentResponse)
            {
                LoseCommentResponse = false;
                throw new GitHubOperationException("comment", issueNumber, true, GitHubFailureKind.TransientProvider,
                    GitHubRemoteState.Uncertain, "Lost comment response after successful mutation");
            }
            return Task.CompletedTask;
        }
        public Task CloseAsync(int issueNumber, CancellationToken ct) { Open = false; CloseCalls++; return Task.CompletedTask; }
    }

    private sealed class Codex(Func<string, Task> implement) : ICodexExecutor
    {
        internal int Calls { get; private set; }
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            Calls++; await implement(projectDirectory); return new("success", "Implementation succeeded", [], false, null);
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected repair.");
    }

    private sealed class Validation(Func<Task<ValidationResult>>? interrupt = null, int interruptAt = 0) : IValidationRunner
    {
        internal int Calls { get; private set; }
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct) =>
            ++Calls == interruptAt && interrupt is not null ? interrupt() : Task.FromResult(ValidationResult.Success);
    }
}
