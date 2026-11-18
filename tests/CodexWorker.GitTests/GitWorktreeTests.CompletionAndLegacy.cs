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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedIntegrationProvesExactRemoteCommitAndResumesOwnedCleanup(bool pushReachedRemote)
    {
        if (OperatingSystem.IsWindows()) return; // Deterministic local receive hook uses POSIX shell.
        using var fixture = await seed.CreateAsync();
        var settings = new GitSettings();
        var id = Guid.NewGuid();
        GitIntegrationResult? prepared = null;
        var confirmed = false;
        var hook = Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git/hooks/pre-receive"));
        if (!pushReachedRemote)
        {
            // Drop the connection mid-push: the outcome is uncertain, unlike an explicit remote rejection.
            await File.WriteAllTextAsync(hook, "#!/bin/sh\nkill -9 $PPID\nsleep 1\nexit 1\n");
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using (var integrating = fixture.CreateRepository(settings))
        {
            await integrating.InitializeAsync(CancellationToken.None);
            await integrating.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(integrating.ExecutionDirectory, "implemented.txt"), "validated source");
            integrating.WithIntegrationObserver((result, pushed, _) =>
            {
                prepared = result;
                confirmed = pushed;
                if (pushed) throw new WorkerInfrastructureException("Lost push response after remote accepted commit");
                return Task.CompletedTask;
            });
            await Assert.ThrowsAnyAsync<WorkerInfrastructureException>(() => integrating.CommitAndIntegrateAsync(fixture.Issue,
                _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));
        }
        var result = Assert.IsType<GitIntegrationResult>(prepared);
        Assert.Equal(pushReachedRemote, confirmed);
        Assert.Equal(40, result.CommitSha?.Length);
        var report = new IssueExecutionReport("Validated implementation", [], Integration: result, ExecutionId: id);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            GitRepository.FeatureBranchName(settings, fixture.Issue), "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "InfrastructureFailure", 1, report.ImplementationSummary, "passed", 0, [], result.CommitSha, "main", result.CompletedBranch,
            "uncertain push", CompletionJson: JsonSerializer.Serialize(new ExecutionCompletion(report, "intent", "policy")));
        using var restarted = fixture.CreateRepository(settings);
        Assert.Equal(pushReachedRemote, await restarted.VerifyRemoteIntegrationAsync(entry, CancellationToken.None));
        if (!pushReachedRemote)
        {
            Assert.True(Directory.Exists(restarted.RecoveryWorkspacePath(entry)));
            File.Delete(hook);
            // The operator retries only the exact original base push.
            await fixture.Git("push", "origin", $"{result.CommitSha}:refs/heads/main");
            await fixture.Git("merge", "--ff-only", result.CommitSha!);
        }
        Assert.True(await restarted.VerifyRemoteIntegrationAsync(entry, CancellationToken.None));
        // Independently advancing base HEAD still contains the exact validated ancestor.
        await fixture.AddAndPushAsync("later.txt", "later change");
        Assert.NotEqual(entry.CommitSha, await fixture.Git("rev-parse", "origin/main"));
        Assert.True(await restarted.VerifyRemoteIntegrationAsync(entry, CancellationToken.None));
        await restarted.FinishIntegratedExecutionAsync(entry, CancellationToken.None);
        await restarted.FinishIntegratedExecutionAsync(entry, CancellationToken.None);
        Assert.False(Directory.Exists(restarted.RecoveryWorkspacePath(entry)));
        Assert.Equal(entry.CommitSha, await fixture.Git("rev-parse", $"refs/heads/{result.CompletedBranch}"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("base")]
    [InlineData("repository")]
    public async Task RemoteCompletionProofRejectsInvalidProvenance(string invalid)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings());
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, fixture.Issue.Title,
            "feature/17-example-task", "main", DateTimeOffset.UtcNow, null, "InfrastructureFailure", null, "validated", "passed",
            0, [], await fixture.Git("rev-parse", "HEAD"), "main", null, "push failed");
        entry = invalid switch
        {
            "missing" => entry with { CommitSha = null },
            "malformed" => entry with { CommitSha = "--all" },
            "base" => entry with { IntegrationBranch = "different-base" },
            _ => entry with { Repository = "different/repo" }
        };
        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => git.VerifyRemoteIntegrationAsync(entry, CancellationToken.None));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task IndependentlyAdvancedRemoteWithoutExpectedCommitDoesNotAuthorizeCompletion()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { PushCompletedBranch = false });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "implemented.txt"), "validated feature");
        await fixture.GitAt(workspace, "add", "implemented.txt");
        await fixture.GitAt(workspace, "commit", "-m", "preserved local implementation");
        var expected = await fixture.GitAt(workspace, "rev-parse", "HEAD");
        await fixture.AddAndPushAsync("independent.txt", "independent base change");
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, fixture.Issue.Title,
            GitRepository.FeatureBranchName(new GitSettings(), fixture.Issue), "main", DateTimeOffset.UtcNow, null,
            "InfrastructureFailure", null, "validated", "passed", 0, [], expected, "main", null, "uncertain push");
        Assert.False(await git.VerifyRemoteIntegrationAsync(entry, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(workspace, "implemented.txt")));
        Assert.Equal(expected, await fixture.GitAt(workspace, "rev-parse", "HEAD"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData("execution")]
    [InlineData("issue")]
    [InlineData("base")]
    [InlineData("repository")]
    [InlineData("starting-commit")]
    public async Task LegacyInspectionRejectsConflictingOwnershipWithoutMutatingWorkspace(string conflict)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings());
        await git.InitializeAsync(CancellationToken.None);
        var source = LegacyCodexSessionSamples.Source() with { IssueTitle = fixture.Issue.Title };
        await git.StartIssueAsync(source.ExecutionId, fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "valuable.txt"), "retain this work");
        var info = Assert.IsType<GitRecoveryInfo>(await git.InspectExecutionWorkspaceAsync(CancellationToken.None));
        source = source with { FeatureBranch = info.Branch };
        source = conflict switch
        {
            "execution" => source with { ExecutionId = Guid.NewGuid() },
            "issue" => source with { IssueNumber = 99 },
            "base" => source with { BaseBranch = "other-base" },
            "repository" => source with { Repository = "other/repo" },
            _ => source with { RecoveryBaseCommit = new string('b', 40) }
        };
        if (conflict is "issue" or "starting-commit")
            await Assert.ThrowsAsync<IssuePreparationRejectedException>(() => git.InspectLegacyCodexWorkspaceAsync(source, CancellationToken.None));
        else Assert.Null(await git.InspectLegacyCodexWorkspaceAsync(source, CancellationToken.None));
        Assert.Equal("retain this work", await File.ReadAllTextAsync(Path.Combine(workspace, "valuable.txt")));
        Assert.Equal(info.BaseCommit, await fixture.GitAt(workspace, "rev-parse", "HEAD"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StandaloneBatchArchivesVerifiedCleanCompletionAndIsRepeatable(bool historicalAcknowledgement, bool reconciled)
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings());
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var info = Assert.IsType<GitRecoveryInfo>(await git.InspectExecutionWorkspaceAsync(CancellationToken.None));
        var started = DateTimeOffset.UtcNow.AddDays(-40);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            info.Branch, "main", started, started.AddMinutes(1), historicalAcknowledgement ? "InfrastructureFailure" : "Completed", 1, "retained", "passed", 0, [],
            info.BaseCommit, "main", null, null, "operator-cleaned", info.BaseCommit);
        if (reconciled)
        {
            var report = new IssueExecutionReport("retained", [], Integration: new(true, "integrated", info.BaseCommit, "main",
                ResourceExecutionId: id, ResourceAttemptNumber: 1), ExecutionId: id);
            entry = entry with { RecoveryState = "completion-reconciled", CompletionJson = JsonSerializer.Serialize(
                new ExecutionCompletion(report, "intent", "policy", RemoteConfirmed: true, LabelsReported: true,
                    CommentReported: true, IssueClosed: true, NotificationAttempted: true, CleanupCompleted: true, Finished: true)) };
        }
        await git.CleanupRecoveryWorkspaceAsync(entry with { RecoveryState = "cleanup-pending" }, CancellationToken.None);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../batch-history.db"));
        await history.CreateAsync(entry);
        var config = new WorkerConfiguration { Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        config.GitHub = new() { DoneLabel = "done", ReadyLabel = "ready", WorkingLabel = "working", FailedLabel = "failed", BlockedLabel = "blocked" };
        if (historicalAcknowledgement)
        {
            var proof = await ExecutionAdministrationCli.VerifyResolutionAsync(entry, config,
                new GitHubIssueState(false, [config.GitHub.DoneLabel]), git.VerifyRemoteIntegrationAsync, CancellationToken.None);
            await history.AcknowledgeAsync(entry, proof, false, CancellationToken.None);
        }
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)]);
        var cleanup = new ExecutionCleanupService(history, registry, c => fixture.CreateRepository(c.Git));
        var service = new StandaloneExecutionMaintenance(history, registry, cleanup, false);
        var preview = await service.RunAsync(new(), CancellationToken.None);
        Assert.Equal(0, preview.Archived);
        Assert.Null(await history.ReadArchiveAuditAsync(id));
        Assert.False(registry.WorkerDraining);
        var applied = await service.RunAsync(new(Apply: true), CancellationToken.None);
        Assert.Equal(1, applied.Archived);
        Assert.Equal(0, applied.NeedsReview);
        Assert.False(registry.WorkerDraining);
        Assert.Equal(entry.State, (await history.ReadExecutionAsync(id))?.State);
        var repeated = await service.RunAsync(new(Apply: true), CancellationToken.None);
        Assert.Equal(1, repeated.AlreadyResolved);
        Assert.Equal(0, repeated.Archived);
        Assert.NotNull(await history.ReadArchiveAuditAsync(id));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task LegacyReviewRestartAggregatesWarningsAndRetainsOriginalHistory()
    {
        using var fixture = await seed.CreateAsync();
        var database = Path.Combine(fixture.Checkout, "../legacy-review.db");
        var config = new WorkerConfiguration
        {
            Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout }
        };
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var missingIntent = LegacyCodexSessionSamples.Source() with { OriginalIssueBody = null };
        var progressed = LegacyCodexSessionSamples.Source() with { ImplementationSummary = "Retained implementation", IssueNumber = 18 };
        var missingWorkspace = LegacyCodexSessionSamples.Source() with { IssueNumber = 19 };
        using var gate = new SemaphoreSlim(1, 1);
        using (var history = new ExecutionHistoryStore(database))
        {
            await history.CreateAsync(missingIntent);
            await history.CreateAsync(progressed);
            await history.CreateAsync(missingWorkspace);
            using var logs = new StringWriter();
            await new CodexRecoveryCoordinator(config, git, history, gate, new WorkerConsole(logs, false), TimeProvider.System)
                .ReconcileAsync(CancellationToken.None);
            Assert.Contains("3 executions (3 new or changed diagnostics)", logs.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Legacy recovery unavailable", logs.ToString(), StringComparison.Ordinal);
        }
        using var reopened = new ExecutionHistoryStore(database);
        using var restartLogs = new StringWriter();
        await new CodexRecoveryCoordinator(config, git, reopened, gate, new WorkerConsole(restartLogs, false), TimeProvider.System)
            .ReconcileAsync(CancellationToken.None);
        Assert.Contains("3 executions (0 new or changed diagnostics)", restartLogs.ToString(), StringComparison.Ordinal);
        Assert.Equal("uncertain", (await reopened.ReadExecutionAsync(progressed.ExecutionId))?.RecoveryState);
        Assert.Contains("original Issue intent is missing", await reopened.ReadLegacyReviewAsync(missingIntent.ExecutionId, CancellationToken.None));
        Assert.Contains("progressed beyond", await reopened.ReadLegacyReviewAsync(progressed.ExecutionId, CancellationToken.None));
        Assert.Contains("worktree missing", await reopened.ReadLegacyReviewAsync(missingWorkspace.ExecutionId, CancellationToken.None));
        await reopened.CreateAsync(progressed with { ExecutionId = Guid.NewGuid(), State = "Failed", RecoveryState = null,
            AttemptNumber = 2, RetryOfExecutionId = progressed.ExecutionId });
        using var changedLogs = new StringWriter();
        await new CodexRecoveryCoordinator(config, git, reopened, gate, new WorkerConsole(changedLogs, false), TimeProvider.System)
            .ReconcileAsync(CancellationToken.None);
        Assert.Contains("3 executions (1 new or changed diagnostics)", changedLogs.ToString(), StringComparison.Ordinal);
        Assert.Null(await reopened.ReadArchiveAuditAsync(progressed.ExecutionId));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReconstructionPreservesDirtyDeltaAndResumesOnceWithAuthoritativeValidation(bool advanceMain)
    {
        using var fixture = await seed.CreateAsync();
        await fixture.AddAndPushAsync("deleted.txt", "removed by the preserved implementation");
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../legacy-history.db"));
        var config = new WorkerConfiguration
        {
            Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
            Git = new() { AutoMerge = true },
            GitHub = new() { ReadyLabel = "ready", WorkingLabel = "working", DoneLabel = "done", BlockedLabel = "blocked", FailedLabel = "failed" },
            Validation = new() { Commands = ["authoritative-check"] }
        };
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var source = LegacyCodexSessionSamples.Source() with
        {
            IssueNumber = fixture.Issue.Number, IssueTitle = fixture.Issue.Title, OriginalIssueBody = fixture.Issue.Body,
            State = "Cancelled", FailureReason = "Execution interrupted by Worker shutdown"
        };
        await git.StartIssueAsync(source.ExecutionId, fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "base.txt"), "already completed tracked work");
        await File.WriteAllTextAsync(Path.Combine(workspace, "added.txt"), "already completed untracked work");
        File.Delete(Path.Combine(workspace, "deleted.txt"));
        var info = Assert.IsType<GitRecoveryInfo>(await git.InspectExecutionWorkspaceAsync(CancellationToken.None));
        source = source with { FeatureBranch = info.Branch };
        await history.CreateAsync(source);
        if (advanceMain) await fixture.AdvanceBaseAsync();
        var home = Path.Combine(fixture.Checkout, "../session-fixture");
        var sessionDirectory = Path.Combine(home, "sessions", "2026", "01", "02");
        Directory.CreateDirectory(sessionDirectory);
        var session = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "rollout-root.jsonl"),
            LegacyCodexSessionSamples.Header(session, workspace, source.FeatureBranch, info.BaseCommit, source.StartedAtUtc.AddMinutes(1)));
        using var gate = new SemaphoreSlim(1, 1);
        using var logs = new StringWriter();
        var console = new WorkerConsole(logs, false);
        var clock = new FixedRecoveryClock((source.CompletedAtUtc ?? throw new InvalidOperationException("Fixture completion missing")).AddHours(1));
        var coordinator = new CodexRecoveryCoordinator(config, git, history, gate, console, clock,
            sessions: new LegacyCodexSessionStore(home));
        await coordinator.ReconcileAsync(CancellationToken.None);
        await coordinator.ReconcileAsync(CancellationToken.None);
        var reconstructed = Assert.Single(await history.ReadAllAsync());
        Assert.Equal("codex-interrupted", reconstructed.RecoveryState);
        Assert.Equal(session.ToString(), reconstructed.CodexRecovery?.SessionId);
        Assert.Null(reconstructed.CodexRecovery?.ImplementationSucceeded);
        Assert.Null(reconstructed.ValidationOutcome);
        Assert.Equal(info.BaseCommit, reconstructed.RecoveryBaseCommit);
        Assert.Equal(0, reconstructed.CodexRecovery?.ResumeCount);
        Assert.Equal(source.CompletedAtUtc, reconstructed.CompletedAtUtc);
        Assert.Equal(1, logs.ToString().Split("Legacy interruption reconstructed", StringSplitOptions.None).Length - 1);
        git.Dispose(); // Simulate the old Worker exiting and releasing checkout ownership.
        using var restartedGit = fixture.CreateRepository(config.Git);
        await restartedGit.InitializeAsync(CancellationToken.None);
        using var telegram = new TelegramNotifier(false, console);
        var codex = new AlreadyCompleteCodex(workspace, session.ToString());
        var validation = new CountingLegacyValidation();
        var readyClock = new FixedRecoveryClock(clock.GetUtcNow().AddMinutes(6));
        var github = new ConcurrentGitHub(fixture.Issue);
        github.Labels.Add((source.IssueNumber, config.GitHub.ReadyLabel));
        var worker = new Worker(config, github, restartedGit, codex, validation,
            telegram, console, history, timeProvider: readyClock);
        var result = await worker.ProcessOneAsync(CancellationToken.None);
        Assert.Equal(IssueOutcomeKind.Succeeded, result?.Kind);
        Assert.True(validation.Calls > 0);
        Assert.Equal("already completed tracked work", await fixture.Git("show", "main:base.txt"));
        Assert.Equal("already completed untracked work", await fixture.Git("show", "main:added.txt"));
        Assert.DoesNotContain("deleted.txt", await fixture.Git("ls-tree", "-r", "--name-only", "main"), StringComparison.Ordinal);
        if (advanceMain) Assert.Equal("independent base change", await fixture.Git("show", "main:base-advanced.txt"));
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
    }

    private sealed class CountingLegacyValidation : IValidationRunner
    {
        public int Calls { get; private set; }
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            Assert.Contains("authoritative-check", commands);
            Calls++;
            return Task.FromResult(ValidationResult.Success);
        }
    }

    private sealed class AlreadyCompleteCodex(string workspace, string session) : ICodexExecutor
    {
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct) =>
            throw new InvalidOperationException("Continuation must retain its source.");
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ExecutionHistoryEntry? retryOf, bool resume, int attemptNumber, CancellationToken ct)
        {
            Assert.Equal(workspace, projectDirectory);
            Assert.True(resume);
            Assert.Equal(session, retryOf?.CodexRecovery?.SessionId);
            return Task.FromResult(new CodexOutcome("success", "Existing work was already complete", [], false, null));
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("No repair expected.");
    }
}
