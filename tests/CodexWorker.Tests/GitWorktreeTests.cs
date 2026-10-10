using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class GitWorktreeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedIntegrationProvesExactRemoteCommitAndResumesOwnedCleanup(bool pushReachedRemote)
    {
        if (OperatingSystem.IsWindows()) return; // Deterministic local receive hook uses POSIX shell.
        using var fixture = await RepositoryFixture.CreateAsync();
        var settings = new GitSettings();
        var id = Guid.NewGuid();
        GitIntegrationResult? prepared = null;
        var confirmed = false;
        var hook = Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git/hooks/pre-receive"));
        if (!pushReachedRemote)
        {
            await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 1\n");
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
            await fixture.Git("push", "origin", "main"); // Operator retries only the exact original base push.
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

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("base")]
    [InlineData("repository")]
    public async Task RemoteCompletionProofRejectsInvalidProvenance(string invalid)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Fact]
    public async Task IndependentlyAdvancedRemoteWithoutExpectedCommitDoesNotAuthorizeCompletion()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Theory]
    [InlineData("execution")]
    [InlineData("issue")]
    [InlineData("base")]
    [InlineData("repository")]
    [InlineData("starting-commit")]
    public async Task LegacyInspectionRejectsConflictingOwnershipWithoutMutatingWorkspace(string conflict)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings());
        await git.InitializeAsync(CancellationToken.None);
        var source = LegacyCodexSessionTests.Source() with { IssueTitle = fixture.Issue.Title };
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StandaloneBatchArchivesVerifiedCleanCompletionAndIsRepeatable(bool historicalAcknowledgement, bool reconciled)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Fact]
    public async Task LegacyReviewRestartAggregatesWarningsAndRetainsOriginalHistory()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        var database = Path.Combine(fixture.Checkout, "../legacy-review.db");
        var config = new WorkerConfiguration
        {
            Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout }
        };
        using var git = fixture.CreateRepository(config.Git);
        await git.InitializeAsync(CancellationToken.None);
        var missingIntent = LegacyCodexSessionTests.Source() with { OriginalIssueBody = null };
        var progressed = LegacyCodexSessionTests.Source() with { ImplementationSummary = "Retained implementation", IssueNumber = 18 };
        var missingWorkspace = LegacyCodexSessionTests.Source() with { IssueNumber = 19 };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReconstructionPreservesDirtyDeltaAndResumesOnceWithAuthoritativeValidation(bool advanceMain)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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
        var source = LegacyCodexSessionTests.Source() with
        {
            IssueNumber = fixture.Issue.Number, IssueTitle = fixture.Issue.Title, OriginalIssueBody = fixture.Issue.Body
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
            LegacyCodexSessionTests.Header(session, workspace, source.FeatureBranch, info.BaseCommit, source.StartedAtUtc.AddMinutes(1)));
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
        var worker = new Worker(config, new ConcurrentGitHub(fixture.Issue), restartedGit, codex, validation,
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

    [Fact]
    public async Task RestartReconcilesIncompleteImplementationWithoutDiscardingChangesOrResettingBudget()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../restart-history.db"));
        var config = new WorkerConfiguration { Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var id = Guid.NewGuid();
        string workspace;
        using (var original = fixture.CreateRepository(config.Git))
        {
            await original.InitializeAsync(CancellationToken.None);
            await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
            workspace = original.ExecutionDirectory;
            await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "preserved after abrupt exit");
            var snapshot = Assert.IsType<GitRecoveryInfo>(await original.InspectExecutionWorkspaceAsync(CancellationToken.None));
            await history.CreateAsync(new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
                snapshot.Branch, "main", DateTimeOffset.UnixEpoch, null, "Implementing", null, null, null, 0, [], null, null, null, null,
                RecoveryBaseCommit: snapshot.BaseCommit, OriginalIssueBody: fixture.Issue.Body, EffectiveEffort: "medium",
                CodexRecovery: new(id, 1, 2, CodexInterruptionRecovery.Fingerprint(config), Guid.NewGuid().ToString())));
        }
        using var restarted = fixture.CreateRepository(config.Git);
        await restarted.InitializeAsync(CancellationToken.None);
        using var gate = new SemaphoreSlim(1, 1);
        using var messages = new StringWriter();
        var clock = new FixedRecoveryClock(DateTimeOffset.UnixEpoch.AddHours(1));
        await new CodexRecoveryCoordinator(config, restarted, history, gate, new WorkerConsole(messages, false), clock)
            .ReconcileAsync(CancellationToken.None);
        using var reopened = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../restart-history.db"));
        var interrupted = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", interrupted.State);
        Assert.Equal("codex-interrupted", interrupted.RecoveryState);
        Assert.Equal(2, interrupted.CodexRecovery?.ResumeCount);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), interrupted.CodexRecovery?.RetryAfterUtc);
        Assert.Equal("preserved after abrupt exit", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
    }

    [Fact]
    public async Task HostStaysOnlineAfterCodexInterruptionAndPreservesActiveSiblingOnShutdown()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.GitAt(Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git")), "symbolic-ref", "HEAD", "refs/heads/main");
        var bin = Path.Combine(fixture.Checkout, "../bin");
        Directory.CreateDirectory(bin);
        var instructions = Path.Combine(fixture.Checkout, "../instructions.md");
        await File.WriteAllTextAsync(instructions, "Project instructions");
        var executable = Path.Combine(bin, "codex");
        await File.WriteAllTextAsync(executable, """
            #!/bin/sh
            if [ "$1" = --version ]; then printf 'codex-cli 1.0.0'; exit 0; fi
            printf 'partial implementation' > partial.txt
            printf '%s\n' '{"type":"thread.started","thread_id":"11111111-1111-1111-1111-111111111111"}'
            for argument do prompt=$argument; done
            case "$prompt" in
              *'Number: 17'*) IFS= read -r ready < "$0.second-started"; printf "error: You've hit your usage limit token=private-value\n" >&2; exit 1;;
              *'Number: 18'*) printf 'ready\n' > "$0.second-started"; IFS= read -r release < "$0.release-second";;
              *) exit 2;;
            esac
            """);
        await File.WriteAllTextAsync(Path.Combine(bin, "gh"), """
            #!/bin/sh
            case "$1 $2" in
              'api repos/owner/repo') printf '%s' '{"push":true}';;
              'api --paginate') printf '%s' '[]';;
              'api '*'/issues/'*) printf '0';;
              'issue view') printf '%s' '{"state":"OPEN","labels":[{"name":"codex-ready"}]}';;
              'issue list')
                case " $* " in
                  *' --label codex-ready '*) printf '%s' '[{"number":17,"title":"Example task","body":"Original intent","createdAt":"2026-01-01T00:00:00Z"},{"number":18,"title":"Sibling task","body":"Original sibling intent","createdAt":"2026-01-02T00:00:00Z"}]';;
                  *) printf '[]';;
                esac;;
              'label list') printf '[]';;
            esac
            exit 0
            """);
        foreach (var tool in new[] { executable, Path.Combine(bin, "gh") })
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new ProcessRunner();
        await runner.RunAsync("mkfifo", [executable + ".second-started", executable + ".release-second"], bin);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousExecutable = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        using var stop = new CancellationTokenSource();
        using var output = new StringWriter();
        var releasedCapacity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = false;
        Task? run = null;
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previousPath);
            Directory.CreateDirectory(Path.Combine(bin, "home"));
            Environment.SetEnvironmentVariable("HOME", Path.Combine(bin, "home"));
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            var database = Path.Combine(bin, "history.db");
            var config = new WorkerConfiguration
            {
                Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
                GitHub = new ManagedProjectRuntimeSettings().GitHub,
                Codex = new() { InstructionsFile = instructions, Model = "original-model" },
                Worker = new() { MaxParallelTasks = 2 }
            };
            var host = new WorkerHost(new GlobalWorkerConfiguration
            {
                Api = new() { Enabled = false }, Projects = new() { Ownership = "standalone", Directory = bin },
                Worker = new() { MaxParallelTasks = 2 }
            }, [("project.yml", config)], new WorkerConsole(output, false),
                executionHistoryPath: database, agentAuthentication: new HealthyAgent(),
                registrationClient: new WorkerRegistrationClient(provisioningDiscovery:
                    new CodexProvisioning.NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0")))),
                operationalLog: message =>
                {
                    if (message.Contains("Codex usage limit", StringComparison.Ordinal)) interrupted = true;
                    if (interrupted && message.Contains("global 1/2", StringComparison.Ordinal)) releasedCapacity.TrySetResult();
                });
            run = host.RunAsync(stop.Token);
            try { await Task.WhenAny(releasedCapacity.Task, run).WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { Assert.Fail(output.ToString()); }
            if (run.IsCompleted) await run;
            Assert.True(releasedCapacity.Task.IsCompleted, output.ToString());
            Assert.False(run.IsCompleted);
            using var history = new ExecutionHistoryStore(database);
            var entries = await history.ReadAllAsync();
            Assert.Equal(2, entries.Count);
            var failed = entries.Single(entry => entry.IssueNumber == 17);
            var sibling = entries.Single(entry => entry.IssueNumber == 18);
            Assert.Equal("codex-interrupted", failed.RecoveryState);
            Assert.Equal("Implementing", sibling.State);
            Assert.DoesNotContain("private-value", failed.FailureReason, StringComparison.Ordinal);
            var siblingWorkspace = Path.Combine(GitRepository.DefaultWorktreeRoot("owner/repo"), sibling.ExecutionId.ToString("N"));
            Assert.Equal("partial implementation", await File.ReadAllTextAsync(Path.Combine(siblingWorkspace, "partial.txt")));
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            var preserved = (await history.ReadAllAsync()).Single(entry => entry.ExecutionId == sibling.ExecutionId);
            Assert.Equal("Cancelled", preserved.State);
            Assert.Equal("codex-interrupted", preserved.RecoveryState);
            Assert.True(Directory.Exists(siblingWorkspace));
        }
        finally
        {
            stop.Cancel();
            try { if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", previousPath);
                Environment.SetEnvironmentVariable("HOME", previousHome);
                Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previousExecutable);
            }
        }
    }

    private sealed class HealthyAgent : IAgentAuthenticationProvider
    {
        public string Provider => "codex";
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task CodexInterruptionUsesSameWorkspaceAfterRestartAndPreservesAdvancedMain()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        var settings = new GitSettings { AutoMerge = true, PushCompletedBranch = false };
        var id = Guid.NewGuid();
        string workspace;
        GitRecoveryInfo snapshot;
        using (var original = fixture.CreateRepository(settings))
        {
            await original.InitializeAsync(CancellationToken.None);
            await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
            workspace = original.ExecutionDirectory;
            await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "original useful implementation");
            snapshot = Assert.IsType<GitRecoveryInfo>(await original.InspectExecutionWorkspaceAsync(CancellationToken.None));
        }
        var now = DateTimeOffset.UtcNow;
        var source = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            snapshot.Branch, "main", now, now, "InfrastructureFailure", 1, null, null, 0, [], null, null, null,
            "Codex usage limit", "codex-interrupted", snapshot.BaseCommit, snapshot.StatusSummary,
            OriginalIssueBody: fixture.Issue.Body, CodexRecovery: new(id, 1, 0, "configuration"));
        await fixture.AdvanceBaseAsync();
        using var resumed = fixture.CreateRepository(settings);
        await resumed.InitializeAsync(CancellationToken.None);
        Assert.Null(await resumed.ValidateCodexRecoveryAsync(source, CancellationToken.None));
        await resumed.StartCodexRecoveryAsync(source, CancellationToken.None);
        Assert.Equal(workspace, resumed.ExecutionDirectory);
        Assert.Equal("original useful implementation", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
        await File.WriteAllTextAsync(Path.Combine(workspace, "finished.txt"), "continued implementation");
        var validations = 0;
        var result = await resumed.CommitAndIntegrateAsync(fixture.Issue, _ =>
        {
            validations++;
            Assert.True(File.Exists(Path.Combine(workspace, "base-advanced.txt")));
            Assert.True(File.Exists(Path.Combine(workspace, "partial.txt")));
            return Task.FromResult(ValidationResult.Success);
        }, CancellationToken.None);
        Assert.True(result.HasChanges);
        Assert.True(validations > 0);
        Assert.Equal("original useful implementation", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "partial.txt")));
        Assert.Equal("independent base change", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "base-advanced.txt")));
        Assert.Equal("continued implementation", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "finished.txt")));
    }

    [Fact]
    public async Task ExecutionWorktreeIsIsolatedAndRemovedAfterTaskFailureCleanup()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);

        Assert.Equal(Path.Combine(fixture.WorktreeRoot, id.ToString("N")), git.ExecutionDirectory);
        Assert.NotEqual(fixture.Checkout, git.ExecutionDirectory);
        Assert.True(Directory.Exists(git.ExecutionDirectory));
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "generated.txt"), "task output");
        await git.VerifyCodexStateAsync(CancellationToken.None);
        await git.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreeRoot, id.ToString("N"))));
        Assert.False(File.Exists(Path.Combine(fixture.Checkout, "generated.txt")));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
    }

    [Fact]
    public async Task FailedExecutionWithChangesRetainsOwnedWorktreeAndNeverIntegratesOrPushes()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true, PushCompletedBranch = true });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var directory = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "partial.txt"), "useful partial work");

        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);

        Assert.NotNull(recovery);
        Assert.Equal("feature/example-task-17", recovery.Branch);
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.True(Directory.Exists(directory));
        Assert.Equal("useful partial work", await File.ReadAllTextAsync(Path.Combine(directory, "partial.txt")));
        Assert.DoesNotContain("partial.txt", await fixture.Git("ls-tree", "-r", "--name-only", "main"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "completed/example-task-17"));
        Assert.Contains($"worktree {directory}", await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task FailedExecutionWithoutChangesRemovesWorktreeAndBranch()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var directory = git.ExecutionDirectory;

        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);

        Assert.Null(recovery);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
    }

    [Fact]
    public async Task RetryResumeCopiesVerifiedPartialFilesIntoNewOwnedWorktree()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        var previousDirectory = first.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(previousDirectory, "partial.txt"), "useful partial work");
        File.Delete(Path.Combine(previousDirectory, "base.txt"));
        var recovery = await first.PreserveFailedIssueChangesAsync(CancellationToken.None);
        Assert.NotNull(recovery);
        var previous = new ExecutionHistoryEntry(firstId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, "partial implementation",
            null, 0, [], null, null, null, "validation failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);

        using var retry = first.CreateExecutionRepository();
        var retryId = Guid.NewGuid();
        await retry.StartIssueAsync(retryId, fixture.Issue, previous, true, 2, CancellationToken.None);

        Assert.NotEqual(previousDirectory, retry.ExecutionDirectory);
        Assert.Equal("useful partial work", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "partial.txt")));
        Assert.False(File.Exists(Path.Combine(retry.ExecutionDirectory, "base.txt")));
        Assert.Equal("feature/example-task-17-retry-2", await fixture.GitAt(retry.ExecutionDirectory, "branch", "--show-current"));
        Assert.True(Directory.Exists(previousDirectory));
        await retry.DiscardUncommittedIssueChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RetryResumeReappliesOnlyPreservedDeltaOnAdvancedBaseBeforeIntegration()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var originalId = Guid.NewGuid();
        await original.StartIssueAsync(originalId, fixture.Issue, CancellationToken.None);
        var originalDirectory = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(originalDirectory, "task.txt"), "recovered task change");
        var recovery = await original.PreserveFailedIssueChangesAsync(CancellationToken.None);
        Assert.NotNull(recovery);
        var previous = new ExecutionHistoryEntry(originalId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, "partial implementation",
            null, 0, [], null, null, null, "validation failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);

        await fixture.AddAndPushAsync("cw", "#!/bin/sh\nprintf 'new executable\\n'\n", executable: true);
        await fixture.AddAndPushAsync("base.txt", "newer source modification");
        await fixture.AddAndPushAsync("src/example.cs", "newer source change");
        await fixture.AddAndPushAsync("tests/example.cs", "newer test change");
        var baseBeforeRetry = await fixture.Git("rev-parse", "main");

        using var retry = original.CreateExecutionRepository();
        await retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, true, 2, CancellationToken.None);
        Assert.Equal("newer source change", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "src", "example.cs")));
        Assert.Equal("newer test change", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "tests", "example.cs")));
        Assert.Equal("newer source modification", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "base.txt")));
        Assert.Equal("recovered task change", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "task.txt")));
        Assert.True(File.Exists(Path.Combine(retry.ExecutionDirectory, "cw")));
        Assert.Equal(baseBeforeRetry, await fixture.GitAt(retry.ExecutionDirectory, "rev-parse", "HEAD"));

        // Normal execution validation runs against the prepared source before integration.
        var validatedHead = await fixture.GitAt(retry.ExecutionDirectory, "rev-parse", "HEAD");
        Assert.Equal(baseBeforeRetry, validatedHead);
        var result = await retry.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        Assert.True(result.HasChanges);
        Assert.Equal("newer source change", await fixture.Git("show", "main:src/example.cs"));
        Assert.Equal("newer test change", await fixture.Git("show", "main:tests/example.cs"));
        Assert.Equal("newer source modification", await fixture.Git("show", "main:base.txt"));
        Assert.Equal("recovered task change", await fixture.Git("show", "main:task.txt"));
        Assert.Equal("#!/bin/sh\nprintf 'new executable\\n'\n", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "cw")));
        Assert.StartsWith("100755", await fixture.Git("ls-tree", "main", "cw"));
        Assert.Equal(baseBeforeRetry, await fixture.Git("rev-parse", "main^"));
        Assert.Equal("task.txt", await fixture.Git("diff", "--name-only", baseBeforeRetry, "main"));
        Assert.NotEmpty(validatedHead);
        Assert.True(Directory.Exists(originalDirectory));
        Assert.Equal("recovered task change", await File.ReadAllTextAsync(Path.Combine(originalDirectory, "task.txt")));
    }

    [Fact]
    public async Task RetryResumeConflictStopsAndPreservesOriginalRecoveryWorkspace()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var originalId = Guid.NewGuid();
        await original.StartIssueAsync(originalId, fixture.Issue, CancellationToken.None);
        var originalDirectory = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(originalDirectory, "base.txt"), "task replacement");
        var recovery = await original.PreserveFailedIssueChangesAsync(CancellationToken.None);
        Assert.NotNull(recovery);
        var previous = new ExecutionHistoryEntry(originalId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, "partial implementation",
            null, 0, [], null, null, null, "validation failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);
        await fixture.AddAndPushAsync("base.txt", "incompatible newer base change");

        using var retry = original.CreateExecutionRepository();
        var conflict = await Assert.ThrowsAsync<IssuePreparationRejectedException>(() =>
            retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, true, 2, CancellationToken.None));

        Assert.Contains("conflict", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("task replacement", await File.ReadAllTextAsync(Path.Combine(originalDirectory, "base.txt")));
        Assert.Equal("incompatible newer base change", await fixture.Git("show", "main:base.txt"));
        Assert.True(Directory.Exists(originalDirectory));
        Assert.DoesNotContain("retry-2", await fixture.Git("branch", "--list"));
    }

    [Fact]
    public async Task RetryRestartUsesCurrentBaseWithoutCopyingPreviousPartialFiles()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(first.ExecutionDirectory, "partial.txt"), "do not use");
        var recovery = await first.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var previous = new ExecutionHistoryEntry(firstId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery!.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);

        using var retry = first.CreateExecutionRepository();
        await retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, false, 2, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(retry.ExecutionDirectory, "partial.txt")));
        await retry.DiscardUncommittedIssueChangesAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("ignored", false)]
    [InlineData("unignored", true)]
    [InlineData("tracked", true)]
    [InlineData("ancestor", true)]
    public async Task ResumeUsesGitPathsAndRejectsCaptureLinks(string scenario, bool rejected)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await RepositoryFixture.CreateAsync();
        await fixture.AddAndPushAsync("generated/tracked.txt", "tracked source");
        await fixture.AddAndPushAsync(".gitignore", "generated/\n");
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await original.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var workspace = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "preserved source");
        var generated = Path.Combine(workspace, "generated");
        await File.WriteAllTextAsync(Path.Combine(generated, "tracked.txt"), "changed tracked source");
        var bin = Path.Combine(generated, "node_modules", ".bin");
        Directory.CreateDirectory(bin);
        File.CreateSymbolicLink(Path.Combine(bin, "esbuild"), "/outside/missing/esbuild");
        Directory.CreateSymbolicLink(Path.Combine(bin, "external-tree"), "/outside/missing/tree");
        await File.WriteAllTextAsync(Path.Combine(generated, "staged-source.txt"), "staged source");
        await fixture.GitAt(workspace, "add", "--force", "generated/staged-source.txt");
        if (scenario == "unignored") File.CreateSymbolicLink(Path.Combine(workspace, "unsafe.txt"), "/etc/passwd");
        if (scenario == "tracked")
        {
            File.Delete(Path.Combine(generated, "tracked.txt"));
            File.CreateSymbolicLink(Path.Combine(generated, "tracked.txt"), "/etc/passwd");
        }
        if (scenario == "ancestor")
        {
            Directory.Move(generated, generated + "-saved");
            Directory.CreateSymbolicLink(generated, generated + "-saved");
        }
        var recovery = Assert.IsType<GitRecoveryInfo>(await original.PreserveFailedIssueChangesAsync(CancellationToken.None));
        var previous = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "recoverable", recovery.BaseCommit, recovery.StatusSummary);
        using var retry = original.CreateExecutionRepository();
        if (rejected)
            await Assert.ThrowsAsync<IssuePreparationRejectedException>(() => retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue,
                previous, true, 2, CancellationToken.None));
        else
        {
            await retry.StartIssueAsync(Guid.NewGuid(), fixture.Issue, previous, true, 2, CancellationToken.None);
            Assert.Equal("preserved source", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "partial.txt")));
            Assert.Equal("changed tracked source", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "generated", "tracked.txt")));
            Assert.Equal("staged source", await File.ReadAllTextAsync(Path.Combine(retry.ExecutionDirectory, "generated", "staged-source.txt")));
            Assert.False(Directory.Exists(Path.Combine(retry.ExecutionDirectory, "generated", "node_modules")));
        }
        Assert.Equal("preserved source", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public async Task ResumeRejectsMissingOrUnsafePersistedRecoveryMetadata()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(first.ExecutionDirectory, "partial.txt"), "useful partial work");
        var recovery = await first.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var previous = new ExecutionHistoryEntry(firstId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery!.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "uncertain", recovery.BaseCommit, recovery.StatusSummary);

        using var retry = first.CreateExecutionRepository();
        var retryId = Guid.NewGuid();
        await Assert.ThrowsAsync<IssuePreparationRejectedException>(() => retry.StartIssueAsync(retryId, fixture.Issue,
            previous, true, 2, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreeRoot, retryId.ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(fixture.WorktreeRoot, firstId.ToString("N"))));
    }

    [Fact]
    public async Task ExistingHistoricalNumberFirstBranchDoesNotBlockOrGetRemovedWithNewExecution()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        const string historicalBranch = "feature/17-example-task";
        await fixture.Git("branch", historicalBranch, "main");
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);

        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        Assert.Equal("feature/example-task-17", await fixture.GitAt(git.ExecutionDirectory, "branch", "--show-current"));
        await git.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        Assert.Contains(historicalBranch, await fixture.Git("branch", "--list", historicalBranch));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
    }

    [Fact]
    public async Task SuccessfulIntegrationCommitsFromWorktreeAndPreservesFeatureBranch()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false, DeleteLocalFeatureBranch = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var executionDirectory = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(executionDirectory, "implemented.txt"), "implementation");
        await git.VerifyCodexStateAsync(CancellationToken.None);

        var result = await git.CommitAndIntegrateAsync(fixture.Issue, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        Assert.True(result.HasChanges);
        Assert.False(Directory.Exists(executionDirectory));
        Assert.False(File.Exists(Path.Combine(fixture.Checkout, "implemented.txt")));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.Contains("feature/example-task-17", await fixture.Git("branch", "--list", "feature/example-task-17"));
        Assert.Equal("implementation", await fixture.Git("show", "feature/example-task-17:implemented.txt"));
    }

    [Fact]
    public async Task AutoMergeFastForwardsWithoutMergeCommitAndPreservesCompletedBranch()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true, PushCompletedBranch = true, CompletedPrefix = "done/feature/" });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");

        var result = await git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        Assert.Equal(1, (await fixture.Git("rev-list", "--parents", "-n", "1", "main")).Split(' ').Length - 1);
        Assert.Equal("implementation", await fixture.Git("show", "main:implemented.txt"));
        Assert.Equal("implementation", await fixture.Git("show", "done/feature/example-task-17:implemented.txt"));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.Contains("done/feature/example-task-17", result.Summary);
    }

    // Investigation baseline: a local remote can observe ref transactions, but
    // cannot reproduce GitHub's asynchronous Issue timeline indexing.
    [Theory]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    public async Task DirectIntegrationPushSequenceIsIndependentOfRetryAndRecovery(int attempt, bool recover, bool retainCompleted)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await RepositoryFixture.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true, PushCompletedBranch = retainCompleted });
        await original.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await original.StartIssueAsync(id, fixture.Issue, null, false, attempt, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(original.ExecutionDirectory, "implemented.txt"), "implementation");
        using var restarted = original.CreateExecutionRepository();
        IGitRepository integrating = original;
        if (recover)
        {
            await fixture.AdvanceBaseAsync();
            await Assert.ThrowsAsync<PostRebaseValidationException>(() => original.CommitAndIntegrateAsync(fixture.Issue,
                _ => Task.FromResult(new ValidationResult(new ValidationFailure(1, "local-check", 1, "", "failed", false))),
                CancellationToken.None));
            var preserved = await original.PreserveIntegrationConflictAsync(CancellationToken.None);
            Assert.NotNull(preserved);
            var source = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
                preserved.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "IntegrationConflict", null,
                "Implemented", "failed: post-rebase validation", 0, [], preserved.BaseCommit, null, null, null,
                RecoveryState: "integration-conflict", RecoveryBaseCommit: preserved.BaseCommit, AttemptNumber: attempt);
            await restarted.StartIntegrationRecoveryAsync(source, CancellationToken.None);
            integrating = restarted;
        }

        var baseCommit = await fixture.Git("rev-parse", "main");
        await fixture.RecordPushTransactionsAsync();

        var result = await integrating.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        Assert.NotNull(result.CommitSha);
        // The blank lines separate pushes. The completed ref is first published
        // only after the exact same implementation SHA has reached the base.
        var received = await fixture.ReadPushTransactionsAsync();
        Assert.Equal($"{baseCommit} {result.CommitSha} refs/heads/main\n\n" +
            (retainCompleted ? $"{new string('0', 40)} {result.CommitSha} refs/heads/{result.CompletedBranch}\n\n" : ""), received);
        Assert.Equal($"Implement #{fixture.Issue.Number}: {fixture.Issue.Title}",
            await fixture.Git("log", "-1", "--format=%s", "main"));
        Assert.Equal($"{result.CommitSha} {baseCommit}", await fixture.Git("rev-list", "--parents", "-n", "1", "main"));
        Assert.Equal(result.CommitSha, (await fixture.Git("ls-remote", "origin", "refs/heads/main")).Split('\t')[0]);
        if (retainCompleted)
        {
            Assert.NotNull(result.CompletedBranch);
            Assert.Equal(result.CommitSha, (await fixture.Git("ls-remote", "origin", $"refs/heads/{result.CompletedBranch}")).Split('\t')[0]);
        }
        else
        {
            Assert.Null(result.CompletedBranch);
            Assert.Empty(await fixture.Git("ls-remote", "origin", "refs/heads/completed/*"));
        }
    }

    [Fact]
    public async Task HistoricalMergeSequencePublishesImplementationWithBaseBeforeCompletedRef()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await RepositoryFixture.CreateAsync();
        // Reproduce the pre-fast-forward Git sequence in a disposable local repository.
        // This comparison does not change the production integration workflow.
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var baseCommit = await fixture.Git("rev-parse", "main");
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        await fixture.RecordPushTransactionsAsync();
        var result = await git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);
        Assert.NotNull(result.CommitSha);
        Assert.NotNull(result.CompletedBranch);
        Assert.Empty(await fixture.ReadPushTransactionsAsync());

        await fixture.Git("merge", "--no-ff", "--no-edit", $"refs/heads/{result.CompletedBranch}");
        var mergeCommit = await fixture.Git("rev-parse", "main");
        var completed = GitRepository.CompletedBranchName(new GitSettings(), fixture.Issue);
        await fixture.Git("push", "origin", "refs/heads/main:refs/heads/main");
        await fixture.Git("push", "origin", $"{result.CompletedBranch}:refs/heads/{completed}");

        Assert.Equal($"{baseCommit} {mergeCommit} refs/heads/main\n\n" +
            $"{new string('0', 40)} {result.CommitSha} refs/heads/{completed}\n\n", await fixture.ReadPushTransactionsAsync());
        Assert.Equal($"{mergeCommit} {baseCommit} {result.CommitSha}",
            await fixture.Git("rev-list", "--parents", "-n", "1", "main"));
        Assert.Equal($"{result.CommitSha} {baseCommit}",
            await fixture.Git("rev-list", "--parents", "-n", "1", result.CommitSha));
        Assert.Equal($"Implement #{fixture.Issue.Number}: {fixture.Issue.Title}",
            await fixture.Git("log", "-1", "--format=%s", result.CommitSha));
        Assert.Equal(mergeCommit, (await fixture.Git("ls-remote", "origin", "refs/heads/main")).Split('\t')[0]);
        Assert.Equal(result.CommitSha, (await fixture.Git("ls-remote", "origin", $"refs/heads/{completed}")).Split('\t')[0]);
    }

    [Fact]
    public async Task ReopenedIssueArchivesEachExecutionWithoutReplacingHistoricalCompletedBranches()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        var settings = new GitSettings { AutoMerge = true, PushCompletedBranch = true, CompletedPrefix = "done/feature/" };
        var historicalCommit = string.Empty;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var git = fixture.CreateRepository(settings);
            await git.InitializeAsync(CancellationToken.None);
            await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, null, false, attempt, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, $"implementation-{attempt}.txt"), $"attempt {attempt}");

            var result = await git.CommitAndIntegrateAsync(fixture.Issue,
                _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

            Assert.True(result.HasChanges);
            var expected = attempt == 1
                ? "done/feature/example-task-17"
                : $"done/feature/example-task-17-retry-{attempt}";
            Assert.Equal(expected, result.CompletedBranch);
            Assert.Equal($"attempt {attempt}", await fixture.Git("show", $"{expected}:implementation-{attempt}.txt"));
            if (attempt == 1) historicalCommit = (await fixture.Git("rev-parse", expected)).Trim();
            else Assert.Equal(historicalCommit, (await fixture.Git("rev-parse", "done/feature/example-task-17")).Trim());
        }
    }

    [Fact]
    public async Task ExistingLocalAndRemoteRetryCompletionRefsArePreservedAndExecutionGetsUniqueName()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        var settings = new GitSettings { AutoMerge = true, PushCompletedBranch = true, CompletedPrefix = "done/feature/" };
        const string occupied = "done/feature/example-task-17-retry-2";
        await fixture.Git("branch", occupied, "main");
        await fixture.Git("push", "origin", $"refs/heads/{occupied}");
        var historical = (await fixture.Git("rev-parse", occupied)).Trim();
        var executionId = Guid.NewGuid();

        using var git = fixture.CreateRepository(settings);
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(executionId, fixture.Issue, null, false, 2, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "retry-implementation.txt"), "new implementation");

        var result = await git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        var unique = $"{occupied}-execution-{executionId:N}";
        Assert.Equal(unique, result.CompletedBranch);
        Assert.Equal(historical, (await fixture.Git("rev-parse", occupied)).Trim());
        Assert.Equal(historical, (await fixture.Git("ls-remote", "origin", $"refs/heads/{occupied}")).Split('\t')[0]);
        Assert.Equal("new implementation", await fixture.Git("show", $"{unique}:retry-implementation.txt"));
    }

    [Fact]
    public async Task AdvancedBaseRebasesFeatureValidatesAndFastForwardsFeatureCommit()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true, PushCompletedBranch = true, CompletedPrefix = "done/feature/" });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var featureFile = Path.Combine(git.ExecutionDirectory, "implemented.txt");
        await File.WriteAllTextAsync(featureFile, "implementation");
        var validations = 0;

        await fixture.AdvanceBaseAsync();
        var result = await git.CommitAndIntegrateAsync(fixture.Issue, _ =>
        {
            validations++;
            return Task.FromResult(ValidationResult.Success);
        }, CancellationToken.None);

        Assert.Equal(1, validations);
        Assert.Equal("independent base change", await fixture.Git("show", "main:base-advanced.txt"));
        Assert.Equal("implementation", await fixture.Git("show", "main:implemented.txt"));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
        Assert.Contains("done/feature/example-task-17", result.Summary);
        var featureCommit = (await fixture.Git("rev-parse", "done/feature/example-task-17")).Trim();
        Assert.Contains(featureCommit, await fixture.Git("rev-list", "main"));
    }

    [Fact]
    public async Task BaseAdvanceDuringValidationReconcilesAgainWithoutMergeCommit()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        await fixture.AdvanceBaseAsync();

        var advanced = false;
        var result = await git.CommitAndIntegrateAsync(fixture.Issue, async _ =>
        {
            // Simulate another base update after the integration fetch/rebase but before the fast-forward.
            if (!advanced)
            {
                advanced = true;
                await fixture.AdvanceBaseAsync("raced-base.txt", "base changed during integration");
            }
            return ValidationResult.Success;
        }, CancellationToken.None);
        Assert.True(result.HasChanges);

        Assert.Equal("base changed during integration", await fixture.Git("show", "main:raced-base.txt"));
        Assert.Equal("implementation", await fixture.Git("show", "main:implemented.txt"));
        var parents = (await fixture.Git("rev-list", "--parents", "-n", "1", "main")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parents.Length);

    }

    [Fact]
    public async Task RebaseConflictStopsBeforeFastForwardAndPreservesExecutionWorktree()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "base.txt"), "feature change");
        var executionDirectory = git.ExecutionDirectory;
        await fixture.AdvanceBaseAsync("base.txt", "base branch change");

        var conflict = await Assert.ThrowsAsync<GitIntegrationConflictException>(() => git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));

        Assert.Contains("rebase was aborted", conflict.Message);
        Assert.Equal("base branch change", await fixture.Git("show", "main:base.txt"));
        Assert.True(Directory.Exists(executionDirectory));
        Assert.Equal(string.Empty, await fixture.GitAt(executionDirectory, "status", "--short"));
        Assert.Equal("feature/example-task-17", await fixture.GitAt(executionDirectory, "branch", "--show-current"));
        Assert.Empty(await fixture.GitAt(executionDirectory, "ls-files", "-u"));
        var rebaseMerge = (await fixture.GitAt(executionDirectory, "rev-parse", "--git-path", "rebase-merge")).Trim();
        var rebaseApply = (await fixture.GitAt(executionDirectory, "rev-parse", "--git-path", "rebase-apply")).Trim();
        Assert.False(Directory.Exists(Path.GetFullPath(rebaseMerge, executionDirectory)));
        Assert.False(Directory.Exists(Path.GetFullPath(rebaseApply, executionDirectory)));
    }

    [Fact]
    public async Task FreshAttemptAfterConflictUsesCurrentMainAndLeavesOldWorktreeUntouched()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var oldId = Guid.NewGuid();
        await original.StartIssueAsync(oldId, fixture.Issue, CancellationToken.None);
        var oldDirectory = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(oldDirectory, "base.txt"), "old implementation");
        await fixture.AdvanceBaseAsync("base.txt", "current authoritative main");
        await Assert.ThrowsAsync<GitIntegrationConflictException>(() => original.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));
        var oldHead = await fixture.GitAt(oldDirectory, "rev-parse", "HEAD");
        var mainHead = await fixture.Git("rev-parse", "origin/main");

        using var fresh = original.CreateExecutionRepository();
        var newId = Guid.NewGuid();
        await fresh.StartIssueAsync(newId, fixture.Issue, null, false, 2, CancellationToken.None);

        Assert.NotEqual(oldDirectory, fresh.ExecutionDirectory);
        Assert.Equal(mainHead, await fixture.GitAt(fresh.ExecutionDirectory, "rev-parse", "HEAD"));
        Assert.Equal("current authoritative main", await File.ReadAllTextAsync(Path.Combine(fresh.ExecutionDirectory, "base.txt")));
        Assert.Equal(oldHead, await fixture.GitAt(oldDirectory, "rev-parse", "HEAD"));
        Assert.Equal("old implementation", await File.ReadAllTextAsync(Path.Combine(oldDirectory, "base.txt")));
        Assert.Equal(string.Empty, await fixture.GitAt(oldDirectory, "status", "--short"));
        Assert.Contains($"worktree {oldDirectory}", await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task IntegrationRecoveryReusesPreservedImplementationAndValidatesBeforeCompleting()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var executionId = Guid.NewGuid();
        await original.StartIssueAsync(executionId, fixture.Issue, CancellationToken.None);
        var directory = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "base.txt"), "implemented result");
        await fixture.AdvanceBaseAsync("base.txt", "new base value");
        await Assert.ThrowsAsync<GitIntegrationConflictException>(() => original.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));
        var preserved = await original.PreserveIntegrationConflictAsync(CancellationToken.None);
        Assert.NotNull(preserved);
        var source = new ExecutionHistoryEntry(executionId, "demo", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            preserved.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "IntegrationConflict", null,
            "implementation complete", "passed", 0, [], preserved.BaseCommit, null, null, null,
            RecoveryState: "integration-conflict", RecoveryBaseCommit: preserved.BaseCommit,
            RecoveryStatus: preserved.StatusSummary);

        using var recovery = original.CreateExecutionRepository();
        await recovery.StartIntegrationRecoveryAsync(source, CancellationToken.None);
        var resolverCalled = false;
        var validationCalled = false;
        var result = await recovery.CommitAndIntegrateAsync(fixture.Issue, _ =>
        {
            validationCalled = true;
            return Task.FromResult(ValidationResult.Success);
        }, async (_, _) =>
        {
            resolverCalled = true;
            await File.WriteAllTextAsync(Path.Combine(directory, "base.txt"), "implemented result");
            return true;
        }, CancellationToken.None);

        Assert.True(result.HasChanges);
        Assert.True(resolverCalled);
        Assert.True(validationCalled);
        Assert.Equal("implemented result", await fixture.Git("show", "main:base.txt"));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task LegacyIntegrationConflictUsesPersistedCommitWhenRecoveryStateIsMissing()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var original = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await original.InitializeAsync(CancellationToken.None);
        var executionId = Guid.NewGuid();
        await original.StartIssueAsync(executionId, fixture.Issue, CancellationToken.None);
        var directory = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(directory, "base.txt"), "implemented result");
        await fixture.AdvanceBaseAsync("base.txt", "new base value");
        await Assert.ThrowsAsync<GitIntegrationConflictException>(() => original.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));
        var preserved = await original.PreserveIntegrationConflictAsync(CancellationToken.None);
        Assert.NotNull(preserved);
        var legacy = new ExecutionHistoryEntry(executionId, "demo", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            preserved.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "IntegrationConflict", null,
            "implementation complete", "passed", 0, [], preserved.BaseCommit, null, null, null,
            RecoveryBaseCommit: null, RecoveryStatus: null);

        var reason = await original.ValidateIntegrationRecoveryAsync(legacy, CancellationToken.None);
        Assert.Null(reason);
        using var recovery = original.CreateExecutionRepository();
        await recovery.StartIntegrationRecoveryAsync(legacy, CancellationToken.None);
        Assert.Equal(directory, recovery.ExecutionDirectory);
        Assert.Equal("feature/example-task-17", await fixture.GitAt(directory, "branch", "--show-current"));
    }

    [Fact]
    public async Task LegacyIntegrationConflictIsRejectedWhenPreservedWorktreeIsMissing()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var source = new ExecutionHistoryEntry(id, "demo", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            "feature/example-task-17", "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "IntegrationConflict", null,
            "implementation complete", "passed", 0, [], "0123456789abcdef", null, null, null);

        var reason = await git.ValidateIntegrationRecoveryAsync(source, CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("missing", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CodexResolvedRebaseConflictContinuesAndValidatesBeforeIntegration()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var executionDirectory = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(executionDirectory, "base.txt"), "feature and base changes combined");
        await fixture.AdvanceBaseAsync("base.txt", "base branch change");
        var resolverCalled = false;
        var validationCalled = false;

        var result = await git.CommitAndIntegrateAsync(fixture.Issue, _ =>
        {
            validationCalled = true;
            return Task.FromResult(ValidationResult.Success);
        }, async (details, _) =>
        {
            resolverCalled = true;
            Assert.NotEmpty(details);
            Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(Path.Combine(executionDirectory, "base.txt")));
            await File.WriteAllTextAsync(Path.Combine(executionDirectory, "base.txt"), "feature and base changes combined");
            return true;
        }, CancellationToken.None);

        Assert.True(resolverCalled);
        Assert.True(validationCalled);
        Assert.Equal("feature and base changes combined", await fixture.Git("show", "main:base.txt"));
        Assert.True(result.HasChanges);
    }

    [Fact]
    public async Task FailedValidationAfterRebaseStopsBeforeIntegration()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        await fixture.AdvanceBaseAsync();

        var failure = await Assert.ThrowsAsync<PostRebaseValidationException>(() => git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(new ValidationResult(new ValidationFailure(1, "configured check", 1, "", "failed", false))),
            CancellationToken.None));

        Assert.Contains("Attempt 1/2", failure.Message);
        Assert.Contains("Attempt 2/2", failure.Message);
        Assert.NotNull(await git.PreserveIntegrationConflictAsync(CancellationToken.None));
        Assert.Equal("base", await fixture.Git("show", "main:base.txt"));
        Assert.DoesNotContain("implemented.txt", await fixture.Git("ls-tree", "-r", "--name-only", "main"));
        Assert.True(Directory.Exists(git.ExecutionDirectory));
    }

    [Fact]
    public async Task DistinctExecutionsUseDistinctDirectoriesAndOccupiedPathsArePreserved()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        await git.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        var firstDirectory = git.ExecutionDirectory;
        await git.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        var secondId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(fixture.WorktreeRoot, secondId.ToString("N")));
        var exception = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            git.StartIssueAsync(secondId, fixture.Issue, CancellationToken.None));

        Assert.Contains("already exists", exception.Message);
        Assert.NotEqual(firstDirectory, Path.Combine(fixture.WorktreeRoot, secondId.ToString("N")));
        Assert.True(Directory.Exists(Path.Combine(fixture.WorktreeRoot, secondId.ToString("N"))));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
    }

    [Fact]
    public async Task SimultaneousCleanupRemovesOnlyEachExecutionAndKeepsGitMetadataValid()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        var secondIssue = fixture.Issue with { Number = 18, Title = "Second task" };
        await second.StartIssueAsync(secondId, secondIssue, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "second.txt"), "second");

        await Task.WhenAll(
            first.DiscardUncommittedIssueChangesAsync(CancellationToken.None),
            second.DiscardUncommittedIssueChangesAsync(CancellationToken.None));

        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
        Assert.DoesNotContain(firstId.ToString("N"), await fixture.Git("worktree", "list", "--porcelain"));
        Assert.DoesNotContain(secondId.ToString("N"), await fixture.Git("worktree", "list", "--porcelain"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/example-task-17"));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", "feature/second-task-18"));
    }

    [Fact]
    public async Task PreservingOneFailedExecutionLeavesConcurrentExecutionUntouched()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var failed = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var active = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await failed.InitializeAsync(CancellationToken.None);
        var failedId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        await failed.StartIssueAsync(failedId, fixture.Issue, CancellationToken.None);
        await active.StartIssueAsync(activeId, fixture.Issue with { Number = 18, Title = "Active task" }, CancellationToken.None);
        var failedDirectory = failed.ExecutionDirectory;
        var activeDirectory = active.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(failedDirectory, "partial.txt"), "partial");
        await File.WriteAllTextAsync(Path.Combine(activeDirectory, "active.txt"), "active");

        var recovery = await failed.PreserveFailedIssueChangesAsync(CancellationToken.None);

        Assert.NotNull(recovery);
        Assert.True(Directory.Exists(failedDirectory));
        Assert.True(Directory.Exists(activeDirectory));
        Assert.Equal("active", await File.ReadAllTextAsync(Path.Combine(activeDirectory, "active.txt")));
        var worktrees = await fixture.Git("worktree", "list", "--porcelain");
        Assert.Contains($"worktree {failedDirectory}", worktrees);
        Assert.Contains($"worktree {activeDirectory}", worktrees);
    }

    [Fact]
    public async Task SimultaneousSuccessfulIntegrationsCleanOnlyTheirOwnWorktrees()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false, DeleteLocalFeatureBranch = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false, DeleteLocalFeatureBranch = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstIssue = fixture.Issue;
        var secondIssue = fixture.Issue with { Number = 18, Title = "Second task" };
        await first.StartIssueAsync(firstId, firstIssue, CancellationToken.None);
        await second.StartIssueAsync(secondId, secondIssue, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(firstDirectory, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "second.txt"), "second");

        var results = await Task.WhenAll(
            first.CommitAndIntegrateAsync(firstIssue, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None),
            second.CommitAndIntegrateAsync(secondIssue, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));

        Assert.All(results, result => Assert.True(result.HasChanges));
        Assert.False(Directory.Exists(firstDirectory));
        Assert.False(Directory.Exists(secondDirectory));
        var worktrees = await fixture.Git("worktree", "list", "--porcelain");
        Assert.DoesNotContain(firstId.ToString("N"), worktrees);
        Assert.DoesNotContain(secondId.ToString("N"), worktrees);
        Assert.Equal("first", await fixture.Git("show", "feature/example-task-17:first.txt"));
        Assert.Equal("second", await fixture.Git("show", "feature/second-task-18:second.txt"));
    }

    [Fact]
    public async Task CleaningExecutionADoesNotRemoveExecutionB()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await second.StartIssueAsync(secondId, fixture.Issue with { Number = 18, Title = "Second task" }, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(secondDirectory, "diagnostic.txt"), "still active");

        await first.DiscardUncommittedIssueChangesAsync(CancellationToken.None);

        Assert.False(Directory.Exists(firstDirectory));
        Assert.True(Directory.Exists(secondDirectory));
        Assert.Equal("still active", await File.ReadAllTextAsync(Path.Combine(secondDirectory, "diagnostic.txt")));
        Assert.Contains(secondDirectory, await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task CancellationBeforeCleanupPreservesThisAndOtherExecutionWorkspaces()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var first = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var second = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await first.InitializeAsync(CancellationToken.None);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await first.StartIssueAsync(firstId, fixture.Issue, CancellationToken.None);
        await second.StartIssueAsync(secondId, fixture.Issue with { Number = 18, Title = "Second task" }, CancellationToken.None);
        var firstDirectory = first.ExecutionDirectory;
        var secondDirectory = second.ExecutionDirectory;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => first.DiscardUncommittedIssueChangesAsync(cancelled.Token));

        Assert.True(Directory.Exists(firstDirectory));
        Assert.True(Directory.Exists(secondDirectory));
        Assert.Contains(firstDirectory, await fixture.Git("worktree", "list", "--porcelain"));
        Assert.Contains(secondDirectory, await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task UnexpectedWorktreeBranchPreservesExecutionState()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var executionDirectory = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(executionDirectory, "diagnostic.txt"), "keep for inspection");
        await fixture.GitAt(executionDirectory, "switch", "-c", "unexpected");

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => git.DiscardUncommittedIssueChangesAsync(CancellationToken.None));

        Assert.True(Directory.Exists(executionDirectory));
        Assert.Equal("keep for inspection", await File.ReadAllTextAsync(Path.Combine(executionDirectory, "diagnostic.txt")));
    }

    [Theory]
    [InlineData("active", "active", "execution-active")]
    [InlineData("dirty", "keep", "recovery-changes-required")]
    [InlineData("ignored-changes", "keep", "recovery-changes-required")]
    [InlineData("integrated", "safe", "integrated")]
    [InlineData("local-only-integration", "keep", "unmerged-commit-required")]
    [InlineData("remote-ahead", "review", "authoritative-base-unavailable")]
    [InlineData("claimed-recovery", "active", "recovery-or-attempt-active")]
    [InlineData("missing-registered", "review", "missing-registered-worktree")]
    [InlineData("branch-mismatch", "review", "worktree-registration-mismatch")]
    [InlineData("head-mismatch", "review", "worktree-head-mismatch")]
    [InlineData("unknown-recovery", "review", "unknown-recovery-state")]
    [InlineData("stale-recovery", "review", "incomplete-recovery-metadata")]
    [InlineData("clean-recoverable", "review", "recovery-changes-missing")]
    [InlineData("superseded", "keep", "superseded-unmerged-changes")]
    [InlineData("newer-active", "active", "recovery-or-attempt-active")]
    [InlineData("unmerged", "keep", "unmerged-commit-required")]
    [InlineData("unknown-commit", "review", "commit-unavailable")]
    [InlineData("already-clean", "safe", "already-clean")]
    [InlineData("resumed-cleaned", "safe", "already-clean")]
    [InlineData("discarded", "safe", "already-clean")]
    [InlineData("cleaned-no-changes", "safe", "already-clean")]
    [InlineData("missing-resources", "review", "missing-recovery-resources")]
    [InlineData("missing-parent", "review", "lineage-inconsistent")]
    [InlineData("fresh-after-conflict", "safe", "integrated")]
    [InlineData("remote-unavailable", "review", "inspection-unavailable")]
    public async Task CleanupInspectionCorrelatesHistoryAndGitWithoutMutation(string scenario, string decision, string reason)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        var head = await fixture.GitAt(workspace, "rev-parse", "HEAD");
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(workspace, "branch", "--show-current"), "main", started, started.AddMinutes(1), "Failed", 1,
            null, null, 0, [], null, null, null, "failed", "recoverable", head);
        var history = new List<ExecutionHistoryEntry>();
        switch (scenario)
        {
            case "active": entry = entry with { State = "Implementing", CompletedAtUtc = null }; break;
            case "claimed-recovery": entry = entry with { IntegrationRecoveryClaim = Guid.NewGuid() }; break;
            case "ignored-changes":
                entry = entry with { RecoveryState = "integration-conflict" };
                await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), "private.txt\n");
                await fixture.GitAt(workspace, "add", ".gitignore");
                await fixture.GitAt(workspace, "commit", "-m", "ignore local resource");
                entry = entry with { RecoveryBaseCommit = await fixture.GitAt(workspace, "rev-parse", "HEAD") };
                await fixture.Git("merge", "--ff-only", entry.FeatureBranch);
                await fixture.Git("push", "origin", "main");
                await File.WriteAllTextAsync(Path.Combine(workspace, "private.txt"), "useful ignored resource");
                break;
            case "dirty":
            case "superseded":
            case "newer-active":
                await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "useful recovery work");
                if (scenario != "dirty")
                    history.Add(entry with { ExecutionId = Guid.NewGuid(), RetryOfExecutionId = id, AttemptNumber = 2,
                        StartedAtUtc = started.AddHours(1), State = scenario == "newer-active" ? "Implementing" : "Failed",
                        CompletedAtUtc = scenario == "newer-active" ? null : started.AddHours(2) });
                break;
            case "integrated":
            case "local-only-integration":
            case "unmerged":
            case "head-mismatch":
                await File.WriteAllTextAsync(Path.Combine(workspace, "implemented.txt"), "implementation");
                await fixture.GitAt(workspace, "add", "implemented.txt");
                await fixture.GitAt(workspace, "commit", "-m", "retained implementation");
                if (scenario != "head-mismatch")
                    entry = entry with { RecoveryBaseCommit = await fixture.GitAt(workspace, "rev-parse", "HEAD"),
                        RecoveryState = "integration-conflict" };
                if (scenario is "integrated" or "local-only-integration")
                {
                    await fixture.Git("merge", "--ff-only", entry.FeatureBranch);
                    if (scenario == "integrated") await fixture.Git("push", "origin", "main");
                }
                break;
            case "remote-ahead":
                entry = entry with { RecoveryState = "integration-conflict" };
                var origin = Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git"));
                var tree = await fixture.GitAt(origin, "rev-parse", "main^{tree}");
                var remoteCommit = await fixture.GitAt(origin, "-c", "user.name=Worker Tests", "-c", "user.email=worker-tests@example.invalid",
                    "commit-tree", tree, "-p", head, "-m", "remote advance");
                await fixture.GitAt(origin, "update-ref", "refs/heads/main", remoteCommit);
                break;
            case "missing-registered": Directory.Delete(workspace, recursive: true); break;
            case "branch-mismatch": await fixture.GitAt(workspace, "switch", "-c", "unexpected"); break;
            case "unknown-recovery": entry = entry with { RecoveryState = "unknown" }; break;
            case "stale-recovery": entry = entry with { RecoveryState = null }; break;
            case "unknown-commit": entry = entry with { CommitSha = new string('f', 40), RecoveryState = "integration-conflict" }; break;
            case "missing-parent": entry = entry with { RetryOfExecutionId = Guid.NewGuid() }; break;
            case "fresh-after-conflict":
                history.Add(entry with { ExecutionId = Guid.NewGuid(), State = "IntegrationConflict", RecoveryState = "integration-conflict" });
                await fixture.GitAt(workspace, "branch", "-m", entry.FeatureBranch + "-retry-2");
                entry = entry with { AttemptNumber = 2, FeatureBranch = entry.FeatureBranch + "-retry-2", RecoveryState = "cleanup-pending" };
                break;
            case "remote-unavailable":
                entry = entry with { RecoveryState = "integration-conflict" };
                await fixture.Git("config", "--remove-section", "url.file://" + Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git")));
                await fixture.Git("config", "url.file:///nonexistent-cw-origin.insteadOf", "https://github.com/owner/repo");
                break;
            case "already-clean":
            case "resumed-cleaned":
            case "discarded":
            case "cleaned-no-changes":
            case "missing-resources":
                await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);
                if (scenario != "missing-resources") entry = entry with { RecoveryState = scenario == "already-clean" ? "expired-cleaned" : scenario };
                break;
        }
        history.Add(entry);
        var refsBefore = await fixture.Git("show-ref");
        var registrationsBefore = await fixture.Git("worktree", "list", "--porcelain");
        var indexPath = Directory.Exists(workspace) ? Path.GetFullPath(await fixture.GitAt(workspace, "rev-parse", "--git-path", "index"), workspace) : null;
        var indexBefore = indexPath is null ? null : await File.ReadAllBytesAsync(indexPath);

        var result = await git.InspectCleanupAsync(entry, history, CancellationToken.None);

        Assert.Equal(decision, result.Decision);
        Assert.Equal(reason, result.ReasonCode);
        Assert.InRange(result.Message.Length, 1, 250);
        if (scenario is "superseded" or "newer-active") Assert.Equal(history[0].ExecutionId, result.NewerExecutionId);
        if (scenario == "integrated") Assert.Equal(await fixture.Git("rev-parse", "main"), result.AuthoritativeBaseCommit);
        Assert.Equal(refsBefore, await fixture.Git("show-ref"));
        Assert.Equal(registrationsBefore, await fixture.Git("worktree", "list", "--porcelain"));
        if (indexPath is not null) Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(result, await git.InspectCleanupAsync(entry, history, CancellationToken.None));
    }

    [Fact]
    public async Task RecoveryCleanupRemovesOnlyThePersistedWorkerOwnedWorkspaceAndBranch()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var active = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var activeId = Guid.NewGuid();
        await active.StartIssueAsync(activeId, fixture.Issue with { Number = 18, Title = "Active execution" }, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(active.ExecutionDirectory, "active.txt"), "keep active");
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "partial.txt"), "recoverable");
        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            recovery!.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "cleanup-pending", recovery.BaseCommit, recovery.StatusSummary);

        await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);
        await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(fixture.WorktreeRoot, id.ToString("N"))));
        Assert.True(Directory.Exists(Path.Combine(fixture.WorktreeRoot, activeId.ToString("N"))));
        Assert.Equal("keep active", await File.ReadAllTextAsync(Path.Combine(active.ExecutionDirectory, "active.txt")));
        Assert.Equal(string.Empty, await fixture.Git("branch", "--list", recovery.Branch));
        Assert.Equal("main", await fixture.Git("branch", "--show-current"));
    }

    [Fact]
    public async Task RecoveryCleanupRefusesBranchNotMatchingExecutionIdentity()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "partial.txt"), "recoverable");
        var recovery = await git.PreserveFailedIssueChangesAsync(CancellationToken.None);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            "unrelated/branch", "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed", 1, null,
            null, 0, [], null, null, null, "failed", "cleanup-pending", recovery!.BaseCommit, recovery.StatusSummary);

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None));

        Assert.True(Directory.Exists(git.ExecutionDirectory));
        Assert.Contains(id.ToString("N"), await fixture.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public async Task PostRebaseValidationDoesNotRetryWhenSourceChanges()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreservedValidationFailureCanRetryIntegrationWithoutAnotherRebase(bool retrySucceeds)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentExecutionsRetryRebasedValidationAndReleaseCapacity(bool retrySucceeds)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Fact]
    public async Task ShutdownDuringCodexPreservesRealWorkspaceAndRestartContinuesIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await RepositoryFixture.CreateAsync();
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
        ExecutionHistoryEntry interrupted;
        using (var git = fixture.CreateRepository(config.Git))
        {
            await git.InitializeAsync(CancellationToken.None);
            var worker = new Worker(config, new ConcurrentGitHub(fixture.Issue), git, codex,
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
        var restarted = new Worker(config, new ConcurrentGitHub(fixture.Issue), restartedGit,
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
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteIntegrationRepairIsRevalidatedAndRemainsRecoverable(bool validationPasses)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Theory]
    [InlineData("history")]
    [InlineData("validation")]
    [InlineData("authority")]
    [InlineData("infrastructure")]
    public async Task IntegrationRepairDoesNotRelaxHistoryValidationAuthorityOrInfrastructureSafety(string violation)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Fact]
    public async Task CancellationDuringIntegrationRepairPreservesWorkAndNeverIntegrates()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
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

    [Fact]
    public async Task LaterRepairCommitConflictAbortsRebaseAndPreservesCompleteImplementation()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "first.txt"), "original implementation");
        await fixture.AdvanceBaseAsync();
        var repaired = false;
        var advanced = false;
        string? preservedHead = null;
        string? currentBase = null;
        var resolutions = 0;
        var error = await Assert.ThrowsAsync<GitIntegrationConflictException>(() => git.CommitAndIntegrateAsync(fixture.Issue,
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
            }, CancellationToken.None));

        Assert.Equal(1, resolutions);
        Assert.Contains("rebase was aborted", error.Message);
        Assert.Contains("conflicts unresolved", error.Message);
        Assert.Equal(currentBase, await fixture.Git("rev-parse", "main"));
        Assert.Equal(preservedHead, await fixture.GitAt(workspace, "rev-parse", "HEAD"));
        Assert.Equal("original implementation", await File.ReadAllTextAsync(Path.Combine(workspace, "first.txt")));
        Assert.Equal("semantic repair", await File.ReadAllTextAsync(Path.Combine(workspace, "second.txt")));
        Assert.NotNull(await git.PreserveIntegrationConflictAsync(CancellationToken.None));
    }

    private sealed class SemanticCodex(RepositoryFixture fixture, bool obsoleteExpectation, bool exhausted,
        bool advanceDuringRepair) : ICodexExecutor
    {
        public bool Exhausted { get; set; } = exhausted;
        public int ImplementationCalls { get; private set; }
        public Dictionary<int, string> StartingHeads { get; } = [];
        public List<IntegrationRepairContext> RepairContexts { get; } = [];
        public string? AdvancedHead { get; private set; }
        public ICodexExecutor WithProfile(CodexExecutionProfile profile)
        {
            Assert.Equal("configured-model", profile.Model);
            Assert.Equal("high", profile.Effort);
            return this;
        }
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            ImplementationCalls++;
            StartingHeads[issue.Number] = await fixture.GitAt(projectDirectory, "rev-parse", "HEAD");
            if (issue.Number == 17)
            {
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "provider.txt"), "Installed", ct);
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "status-implementation.txt"), obsoleteExpectation ? "contract" : "readiness", ct);
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "expected-status.txt"), obsoleteExpectation ? "Missing" : "specification", ct);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "status-contract.txt"), "installation", ct);
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "unrelated.txt"), "B's unrelated work", ct);
            }
            return new CodexOutcome("success", "Implemented provider/status separation", [], false, null);
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("Initial validation passed; implementation repair is inappropriate.");
        public async Task<CodexOutcome> RepairIntegrationAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            IntegrationRepairContext context, string? implementationSummary, IReadOnlyList<string> validationCommands,
            int attempt, int maximumAttempts, CancellationToken ct)
        {
            RepairContexts.Add(context);
            Assert.Equal(17, issue.Number);
            Assert.Equal("Implement this request", issue.Body);
            Assert.Equal("Implemented provider/status separation", implementationSummary);
            Assert.Equal("verify status contract", Assert.Single(validationCommands));
            Assert.Equal(2, maximumAttempts);
            Assert.Equal(context.RebasedCommit, await fixture.GitAt(projectDirectory, "rev-parse", "HEAD"));
            Assert.NotEqual(fixture.Checkout, projectDirectory);
            Assert.Equal("installation", await File.ReadAllTextAsync(Path.Combine(projectDirectory, "status-contract.txt"), ct));
            if (Exhausted)
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "repair-note.txt"), $"preserved repair attempt {attempt}", ct);
            else if (obsoleteExpectation)
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "expected-status.txt"), "Installed", ct);
            else
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "status-implementation.txt"), "installation", ct);
            if (advanceDuringRepair)
            {
                // Advance only origin using another checkout, as a remote Worker would. The
                // canonical main remains unchanged until the integration refresh observes it.
                var peer = Path.Combine(fixture.Checkout, "../peer");
                await fixture.Git("clone", Path.Combine(fixture.Checkout, "../origin.git"), peer);
                await fixture.GitAt(peer, "switch", "main");
                await fixture.GitAt(peer, "config", "user.name", "Peer Worker");
                await fixture.GitAt(peer, "config", "user.email", "peer@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(peer, "during-repair.txt"), "new concurrent work", ct);
                await fixture.GitAt(peer, "add", "during-repair.txt");
                await fixture.GitAt(peer, "commit", "-m", "concurrent update during repair");
                await fixture.GitAt(peer, "push", "origin", "main");
                AdvancedHead = await fixture.GitAt(peer, "rev-parse", "HEAD");
            }
            return new CodexOutcome("success", Exhausted ? "Could not yet reconcile status" : "Reconciled installation status with the current contract", [], false, null);
        }
    }

    private sealed class SemanticValidation(RepositoryFixture fixture, bool obsoleteExpectation) : IValidationRunner
    {
        public TaskCompletionSource InitialPassed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowIntegration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Failures { get; private set; }
        public int PassedAfterRebase { get; private set; }
        private bool _initial = true;
        public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            if (!File.Exists(Path.Combine(directory, "provider.txt"))) return ValidationResult.Success;
            var contractPath = Path.Combine(directory, "status-contract.txt");
            var contract = File.Exists(contractPath) ? await File.ReadAllTextAsync(contractPath, ct) : "readiness";
            var implementation = await File.ReadAllTextAsync(Path.Combine(directory, "status-implementation.txt"), ct);
            var installed = await File.ReadAllTextAsync(Path.Combine(directory, "provider.txt"), ct);
            var actual = (implementation == "contract" ? contract : implementation) == "installation" ? installed : "Missing";
            var expected = obsoleteExpectation ? await File.ReadAllTextAsync(Path.Combine(directory, "expected-status.txt"), ct) :
                contract == "installation" ? installed : "Missing";
            if (actual != expected)
            {
                Failures++;
                var notePath = Path.Combine(directory, "repair-note.txt");
                var remaining = File.Exists(notePath) ? $"Remaining incompatibility after: {await File.ReadAllTextAsync(notePath, ct)}" : "";
                return new ValidationResult(new ValidationFailure(1, Assert.Single(commands), 1,
                    $"Status separates installation from readiness\nExpected: {expected}\nActual: {actual}", remaining, false));
            }
            if (_initial)
            {
                _initial = false;
                Assert.False(File.Exists(contractPath));
                InitialPassed.SetResult();
                await AllowIntegration.Task.WaitAsync(ct);
            }
            else
            {
                Assert.Contains("installation", await File.ReadAllTextAsync(contractPath, ct));
                Assert.Equal(await fixture.GitAt(directory, "rev-parse", "HEAD"), await fixture.GitAt(directory, "rev-parse", "refs/heads/feature/example-task-17"));
                PassedAfterRebase++;
            }
            return ValidationResult.Success;
        }
    }

    private sealed class ImmediateValidation : IValidationRunner
    {
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct) =>
            Task.FromResult(ValidationResult.Success);
    }

    private sealed class ShutdownCodex : ICodexExecutor
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "partial.txt"), "useful partial implementation", ct);
            Started.SetResult(projectDirectory);
            await new ProcessRunner().RunAsync("/bin/sh", ["-c", "touch child-started; sleep 60"], projectDirectory, cancellationToken: ct);
            throw new InvalidOperationException("The controlled child should be interrupted.");
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class ConcurrentGitHub(GitHubIssue template) : IGitHubClient
    {
        public Task<string> GetIssueCommentContextAsync(int issueNumber, CancellationToken cancellationToken,
            IReadOnlyList<string>? secretValues = null) => Task.FromResult("");
        private int _number = 16;
        public List<(int Issue, string Label)> Labels { get; } = [];
        public List<(int Issue, string Body)> Comments { get; } = [];
        public GitHubIssue? RecoveryIssue { get; set; }
        public GitHubIssue? AutomaticConflictIssue { get; set; }
        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken)
        {
            if (label == "codex-integration-recovery" && RecoveryIssue is { } recovery)
            {
                RecoveryIssue = null;
                return Task.FromResult<GitHubIssue?>(recovery);
            }
            if (label == "codex-integration-conflict" && AutomaticConflictIssue is { } conflict)
            {
                AutomaticConflictIssue = null;
                return Task.FromResult<GitHubIssue?>(conflict with { Labels = [label] });
            }
            return Task.FromResult<GitHubIssue?>(label == "ready" ? template with { Number = ++_number } : null);
        }
        public Task<GitHubIssue?> GetCompletionIssueAsync(int issueNumber, CancellationToken ct) =>
            Task.FromResult<GitHubIssue?>(template with { Number = issueNumber });
        public Task<GitHubIssueState> ReadIssueStateAsync(int issueNumber, CancellationToken ct) =>
            Task.FromResult(new GitHubIssueState(true, Labels.Where(item => item.Issue == issueNumber).TakeLast(1).Select(item => item.Label).ToArray()));
        public Task EnsureSuccessCommentAsync(int issueNumber, Guid executionId, string comment, CancellationToken ct, bool allowCreate = true) =>
            Comments.Any(item => item.Issue == issueNumber && item.Body == comment) ? Task.CompletedTask : CommentAsync(issueNumber, comment, ct);
        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        { Labels.Add((issueNumber, add)); return Task.CompletedTask; }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct)
        { Comments.Add((issueNumber, comment)); return Task.CompletedTask; }
        public Task CloseAsync(int issueNumber, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedRecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FileCodex : ICodexExecutor
    {
        public string? LastDirectory { get; private set; }
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            LastDirectory = projectDirectory;
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, $"issue-{issue.Number}.txt"), "implementation", ct);
            return new CodexOutcome("success", "Implemented", [], false, null);
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("Post-rebase validation must not run implementation repair.");
    }

    private sealed class RebaseValidation(RepositoryFixture fixture, bool retrySucceeds) : IValidationRunner
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource IndependentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIndependent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> RebasedHeads { get; } = [];
        public List<string> RebasedParents { get; } = [];
        public string? FirstIntegratedHead { get; private set; }
        private int _secondCalls;
        public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            var branch = await fixture.GitAt(directory, "branch", "--show-current");
            if (branch.EndsWith("-17", StringComparison.Ordinal))
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(ct);
            }
            else if (branch.EndsWith("-19", StringComparison.Ordinal))
            {
                IndependentStarted.TrySetResult();
                await ReleaseIndependent.Task.WaitAsync(ct);
            }
            else if (branch.EndsWith("-18", StringComparison.Ordinal))
            {
                if (++_secondCalls == 1)
                {
                    SecondStarted.TrySetResult();
                    await ReleaseSecond.Task.WaitAsync(ct);
                    FirstIntegratedHead = await fixture.Git("rev-parse", "main");
                    return ValidationResult.Success;
                }
                RebasedHeads.Add(await fixture.GitAt(directory, "rev-parse", "HEAD"));
                RebasedParents.Add(await fixture.GitAt(directory, "rev-parse", "HEAD^"));
                if (RebasedHeads.Count == 1 || !retrySucceeds)
                    return new ValidationResult(new ValidationFailure(1, "local-check", 1, "", $"failed test: attempt {RebasedHeads.Count}", false));
            }
            return ValidationResult.Success;
        }
    }

    [Theory]
    [InlineData("integrated", "eligible")]
    [InlineData("flat-prefix", "eligible")]
    [InlineData("new", "skipped")]
    [InlineData("unmerged", "review")]
    [InlineData("mismatch", "review")]
    [InlineData("worktree", "review")]
    [InlineData("orphan", "review")]
    [InlineData("local-only", "eligible")]
    [InlineData("remote-only", "eligible")]
    [InlineData("stale-base", "eligible")]
    [InlineData("ambiguous", "review")]
    [InlineData("claimed", "review")]
    public async Task CompletedBranchCleanupRequiresHistoryAgeExactTipsAndIntegration(string scenario, string decision)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        var prefix = scenario == "flat-prefix" ? "done-" : "done/";
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = prefix });
        var branch = prefix + "example-task-17";
        var integrated = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("branch", branch);
        if (scenario is "unmerged" or "mismatch" or "stale-base")
        {
            await fixture.Git("checkout", branch);
            await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "unmerged.txt"), "unmerged");
            await fixture.Git("add", "unmerged.txt");
            await fixture.Git("commit", "-m", "unmerged");
            await fixture.Git("checkout", "main");
        }
        var tip = await fixture.Git("rev-parse", branch);
        if (scenario != "local-only")
            await fixture.Git("push", "origin", scenario == "mismatch" ? $"{integrated}:refs/heads/{branch}" : branch);
        if (scenario == "stale-base") await fixture.Git("push", "origin", $"{tip}:refs/heads/main");
        if (scenario == "remote-only") await fixture.Git("branch", "-d", branch);
        if (scenario == "worktree") await fixture.Git("worktree", "add", Path.Combine(fixture.WorktreeRoot, "checked-out"), branch);
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var completed = scenario == "new" ? now.AddDays(-1) : now.AddDays(-40);
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", completed.AddMinutes(-1), completed, "Completed", 1, null, null, 0, [],
            tip, "main", branch, null, IntegrationRecoveryClaim: scenario == "claimed" ? Guid.NewGuid() : null);
        IReadOnlyList<ExecutionHistoryEntry> history = scenario switch
        {
            "orphan" => [],
            "ambiguous" => [entry, entry with { ExecutionId = Guid.NewGuid() }],
            _ => [entry]
        };
        var preview = await git.CleanupCompletedBranchesAsync("sample", history, 30, false, now, CancellationToken.None);
        Assert.Equal(decision, Assert.Single(preview.Branches).Decision);
        Assert.False(preview.Applied);
        // Commit timestamps are current; the old lifecycle timestamp is the authoritative age.
        var applied = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None);
        if (decision == "eligible")
        {
            var deleted = Assert.Single(applied.Branches);
            Assert.Equal("deleted", deleted.Decision);
            Assert.Equal(scenario != "remote-only", deleted.LocalDeleted);
            Assert.Equal(scenario != "local-only", deleted.RemoteDeleted);
            Assert.Equal(string.Empty, await fixture.Git("branch", "--list", branch));
            Assert.Equal(string.Empty, await fixture.Git("ls-remote", "--heads", "origin", "refs/heads/" + branch));
            var repeated = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None);
            Assert.Empty(repeated.Branches);
        }
        else
        {
            Assert.Equal(decision, Assert.Single(applied.Branches).Decision);
            Assert.Equal(0, applied.Deleted);
            Assert.Equal(tip, await fixture.Git("rev-parse", branch));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedBranchMaintenanceApiAcceptsCompletedDrainAndAllowsResume(bool initiallyActive)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "maintenance-history.db"));
        using var gate = new SemaphoreSlim(1, 1);
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout }
        };
        var model = new WorkerRuntimeReadModel(new GlobalWorkerConfiguration(), [("project.yml", config)], history) { State = "running" };
        model.CompletedBranchMaintenance.Register(config, git, gate);
        const string branch = "done/example-task-17";
        await fixture.Git("branch", branch);
        await fixture.Git("push", "origin", branch);
        var tip = await fixture.Git("rev-parse", branch);
        var now = DateTimeOffset.UtcNow;
        await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [],
            tip, "main", branch, null));
        var app = await ManagementApi.StartAsync(model, new ManagementApiSettings { ListenUrl = "http://127.0.0.1:0" }, CancellationToken.None);
        Assert.NotNull(app);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            Assert.NotNull(addresses);
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
            var request = new CompletedBranchCleanupRequest(fixture.Checkout, 1, Apply: true, Limit: 3);
            using var beforeDrain = await client.PostAsJsonAsync("/api/maintenance/completed-branches", request);
            Assert.Equal(HttpStatusCode.Conflict, beforeDrain.StatusCode);
            if (initiallyActive) Assert.True(model.Registry.TryReserve("sample"));
            using var drain = await client.PostAsync("/api/worker/drain", null);
            Assert.Equal(HttpStatusCode.OK, drain.StatusCode);
            if (initiallyActive)
            {
                using var pendingDrain = await client.PostAsJsonAsync("/api/maintenance/completed-branches", request);
                Assert.Equal(HttpStatusCode.Conflict, pendingDrain.StatusCode);
                model.Registry.Release("sample");
            }
            using var status = JsonDocument.Parse(await client.GetStringAsync("/api/worker/drain"));
            Assert.Equal("drained", status.RootElement.GetProperty("state").GetString());
            Assert.True(status.RootElement.GetProperty("drainComplete").GetBoolean());
            Assert.Equal("drained", model.State);
            Assert.Equal("drained", model.HeartbeatStatus(new WorkerHeartbeatStatus(0, [], "running")).State);
            using var applied = await client.PostAsJsonAsync("/api/maintenance/completed-branches", request);
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            using var result = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
            Assert.Equal(1, result.RootElement.GetProperty("deleted").GetInt32());
            using var cancel = await client.PostAsync("/api/worker/drain/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            Assert.Equal("running", model.State);
            Assert.True(model.Registry.TryReserve("sample"));
            model.Registry.Release("sample");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompletedBranchMaintenanceUsesPersistedHistoryAndRepositoryGate()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "maintenance-history.db"));
        using var gate = new SemaphoreSlim(1, 1);
        var config = new WorkerConfiguration
        {
            Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
            Codex = new CodexSettings { InstructionsFile = Path.Combine(fixture.Checkout, "base.txt") }
        };
        var registry = new ProjectRuntimeRegistry([("project.yml", config)]);
        var service = new CompletedBranchMaintenanceService(registry, history);
        service.Register(config, git, gate);
        const string branch = "done/example-task-17";
        await fixture.Git("branch", branch);
        await fixture.Git("push", "origin", branch);
        var tip = await fixture.Git("rev-parse", branch);
        var now = DateTimeOffset.UtcNow;
        await history.CreateAsync(new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [],
            tip, "main", branch, null));
        var request = new CompletedBranchCleanupRequest(fixture.Checkout, 30, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(request, CancellationToken.None));
        Assert.True(registry.TryReserve("sample"));
        registry.DrainWorker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(request, CancellationToken.None));
        registry.Release("sample");
        Assert.False(registry.TryReserve("sample"));
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = service.CleanupAsync(request, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        Assert.False(registry.CancelWorkerDrain());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(tip, await fixture.Git("rev-parse", branch));
        gate.Release();
        var result = await service.CleanupAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(1, result.Deleted);
        Assert.Null(await service.CleanupAsync(request with { RepositoryDirectory = fixture.WorktreeRoot }, CancellationToken.None));
        registry.ReplaceConfiguration([("project.yml", new WorkerConfiguration { Project = config.Project, Codex = config.Codex })]);
        Assert.Null(await service.CleanupAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CompletedBranchCleanupBoundsTheSameEligibleSetForPreviewAndApply()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        var tip = await fixture.Git("rev-parse", "HEAD");
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new List<ExecutionHistoryEntry>();
        foreach (var number in new[] { 17, 18 })
        {
            var branch = $"done/example-task-{number}";
            await fixture.Git("branch", branch);
            await fixture.Git("push", "origin", branch);
            history.Add(new(Guid.NewGuid(), "sample", "owner/repo", number, "Example task",
                $"feature/example-task-{number}", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1,
                null, null, 0, [], tip, "main", branch, null));
        }
        var preview = await git.CleanupCompletedBranchesAsync("sample", history, 30, false, now, CancellationToken.None, limit: 1);
        Assert.Equal(1, preview.Eligible);
        Assert.Equal(1, preview.Skipped);
        var applied = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None, limit: 1);
        Assert.Equal(1, applied.Deleted);
        Assert.Equal(preview.Branches.Select(b => b.Branch), applied.Branches.Select(b => b.Branch));
        Assert.Equal("deleted", applied.Branches[0].Decision);
        Assert.Equal("skipped", applied.Branches[1].Decision);
        Assert.Equal(tip, await fixture.Git("rev-parse", "done/example-task-18"));
        Assert.Contains(tip, await fixture.Git("ls-remote", "--heads", "origin", "refs/heads/done/example-task-18"), StringComparison.Ordinal);
        var remaining = await git.CleanupCompletedBranchesAsync("sample", history, 30, true, now, CancellationToken.None, limit: 1);
        Assert.Equal(1, remaining.Deleted);
    }

    [Fact]
    public async Task CompletedBranchCleanupRemoteLeasePreservesRefChangedDuringPush()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { CompletedPrefix = "done/" });
        const string branch = "done/example-task-17";
        var tip = await fixture.Git("rev-parse", "HEAD");
        await fixture.Git("branch", branch);
        await fixture.Git("push", "origin", branch);
        await fixture.AdvanceBaseAsync();
        var changedTip = await fixture.Git("rev-parse", "HEAD");
        var remotePath = Path.Combine(Path.GetDirectoryName(fixture.Checkout) ?? "", "origin.git");
        var hook = Path.Combine(fixture.Checkout, ".git", "hooks", "pre-push");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\ngit --git-dir='{remotePath}' update-ref refs/heads/{branch} {changedTip} {tip}\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var now = DateTimeOffset.UtcNow;
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), "sample", "owner/repo", 17, "Example task",
            "feature/example-task-17", "main", now.AddDays(-41), now.AddDays(-40), "Completed", 1, null, null, 0, [],
            tip, "main", branch, null);
        var result = await git.CleanupCompletedBranchesAsync("sample", [entry], 30, true, now, CancellationToken.None);
        Assert.Equal("review", Assert.Single(result.Branches).Decision);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(tip, await fixture.Git("rev-parse", branch));
        Assert.StartsWith(changedTip, await fixture.Git("ls-remote", "--heads", "origin", "refs/heads/" + branch));
    }

    [Theory]
    [InlineData("safe", "cleaned", "integrated")]
    [InlineData("dirty", "refused", "recovery-changes-required")]
    [InlineData("recoverable", "refused", "recovery-changes-missing")]
    [InlineData("authoritative", "refused", "authoritative-recovery")]
    [InlineData("authoritative-pending", "refused", "authoritative-recovery")]
    [InlineData("uncertain", "refused", "uncertain-execution")]
    [InlineData("active", "refused", "execution-active")]
    [InlineData("branch", "refused", "worktree-registration-mismatch")]
    [InlineData("head", "refused", "worktree-head-mismatch")]
    [InlineData("path", "refused", "missing-registered-worktree")]
    [InlineData("unmerged", "refused", "unmerged-commit-required")]
    [InlineData("race", "refused", "recovery-changes-required")]
    [InlineData("failure", "failed", "cleanup-failed")]
    [InlineData("partial-failure", "failed", "cleanup-failed")]
    [InlineData("already-clean", "already-clean", "already-clean")]
    public async Task OperatorCleanupRevalidatesAndPreservesUnsafeResources(string scenario, string outcome, string reason)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var workspace = git.ExecutionDirectory;
        var head = await fixture.GitAt(workspace, "rev-parse", "HEAD");
        var started = DateTimeOffset.UtcNow.AddDays(-40);
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(workspace, "branch", "--show-current"), "main", started, started.AddMinutes(1), "Failed", 1,
            "primary outcome", "failed", 0, [], null, null, null, "task failed", "cleanup-pending", head);
        switch (scenario)
        {
            case "dirty": await File.WriteAllTextAsync(Path.Combine(workspace, "useful.txt"), "preserve"); break;
            case "recoverable": entry = entry with { RecoveryState = "recoverable" }; break;
            case "authoritative": entry = entry with { State = "IntegrationConflict", RecoveryState = "integration-conflict" }; break;
            case "authoritative-pending": entry = entry with { State = "IntegrationConflict" }; break;
            case "uncertain": entry = entry with { State = "InfrastructureFailure" }; break;
            case "active": entry = entry with { State = "Implementing", CompletedAtUtc = null }; break;
            case "branch": await fixture.GitAt(workspace, "switch", "-c", "unrelated-branch"); break;
            case "head":
            case "unmerged":
                await File.WriteAllTextAsync(Path.Combine(workspace, "useful.txt"), "preserve");
                await fixture.GitAt(workspace, "add", "useful.txt");
                await fixture.GitAt(workspace, "commit", "-m", "unmerged work");
                if (scenario == "unmerged") entry = entry with { RecoveryBaseCommit = await fixture.GitAt(workspace, "rev-parse", "HEAD") };
                break;
            case "path": Directory.Delete(workspace, recursive: true); break;
            case "failure": await fixture.Git("worktree", "lock", workspace); break;
            case "already-clean":
                await git.CleanupRecoveryWorkspaceAsync(entry, CancellationToken.None);
                entry = entry with { RecoveryState = "expired-cleaned" };
                break;
        }
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "history.db"));
        await history.CreateAsync(entry);
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)]);
        var service = new ExecutionCleanupService(history, registry, c => fixture.CreateRepository(c.Git));
        var dryRun = Assert.Single(await service.RunAsync(new(ExecutionId: id), CancellationToken.None));
        Assert.Equal(entry.RecoveryState, (await history.ReadExecutionAsync(id))?.RecoveryState);
        if (scenario == "race")
        {
            Assert.Equal("dry-run", dryRun.Outcome);
            await File.WriteAllTextAsync(Path.Combine(workspace, "new-useful.txt"), "changed since inspection");
        }
        string? branchLock = null;
        if (scenario == "partial-failure")
        {
            branchLock = Path.Combine(fixture.Checkout, ".git", "refs", "heads", entry.FeatureBranch + ".lock");
            await File.WriteAllTextAsync(branchLock, "simulate another ref writer");
        }
        registry.DrainWorker();
        var result = Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Inspection.ReasonCode);
        var saved = await history.ReadExecutionAsync(id);
        Assert.NotNull(saved);
        Assert.Equal(entry.State, saved.State);
        Assert.Equal(entry.ImplementationSummary, saved.ImplementationSummary);
        Assert.Equal(entry.FailureReason, saved.FailureReason);
        Assert.Equal(outcome is "cleaned" or "already-clean" ? "operator-cleaned" : entry.RecoveryState, saved.RecoveryState);
        if (outcome is "cleaned" or "already-clean")
        {
            Assert.False(Directory.Exists(workspace));
            Assert.Equal(string.Empty, await fixture.Git("branch", "--list", entry.FeatureBranch));
            Assert.Equal("already-clean", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None)).Outcome);
            var preview = Assert.Single(await service.RunAsync(new(ExecutionId: id, Action: "archive"), CancellationToken.None));
            Assert.Equal("dry-run", preview.Outcome);
            Assert.Null(await history.ReadArchiveAuditAsync(id));
            Assert.Equal("archived", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true, Action: "archive"), CancellationToken.None)).Outcome);
            Assert.Equal("already-archived", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true, Action: "archive"), CancellationToken.None)).Outcome);
            Assert.NotNull(await history.ReadExecutionAsync(id));
            Assert.Empty(await history.ReadRecentAsync(20));
        }
        else if (scenario == "partial-failure")
        {
            Assert.False(Directory.Exists(workspace));
            Assert.NotEqual(string.Empty, await fixture.Git("branch", "--list", entry.FeatureBranch));
            Assert.NotNull(branchLock);
            File.Delete(branchLock);
            Assert.Equal("cleaned", Assert.Single(await service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None)).Outcome);
        }
        else if (scenario != "path") Assert.True(Directory.Exists(workspace));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorCleanupSelectsBoundedHistoryAndOnlyRemovesOlderEligibleAttempt(bool stale)
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var olderGit = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var newerGit = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        using var unrelatedGit = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await olderGit.InitializeAsync(CancellationToken.None);
        var oldId = Guid.NewGuid();
        await olderGit.StartIssueAsync(oldId, fixture.Issue, CancellationToken.None);
        var head = await fixture.GitAt(olderGit.ExecutionDirectory, "rev-parse", "HEAD");
        var started = DateTimeOffset.UtcNow.AddDays(-10);
        var older = new ExecutionHistoryEntry(oldId, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(olderGit.ExecutionDirectory, "branch", "--show-current"), "main", started, started.AddMinutes(1), "IntegrationConflict", 1,
            "original outcome", null, 0, [], head, null, null, null, "integration-conflict", head);
        var newId = Guid.NewGuid();
        await newerGit.StartIssueAsync(newId, fixture.Issue, older, false, 2, CancellationToken.None);
        var newer = older with { ExecutionId = newId, FeatureBranch = older.FeatureBranch + "-retry-2", AttemptNumber = 2,
            RetryOfExecutionId = oldId, StartedAtUtc = started.AddHours(1), CompletedAtUtc = started.AddHours(2) };
        await unrelatedGit.StartIssueAsync(Guid.NewGuid(), fixture.Issue with { Number = 99 }, CancellationToken.None);
        var unrelatedBranch = await fixture.GitAt(unrelatedGit.ExecutionDirectory, "branch", "--show-current");
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "history.db"));
        await history.CreateAsync(older);
        await history.CreateAsync(newer);
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)]);
        var service = new ExecutionCleanupService(history, registry, c => fixture.CreateRepository(c.Git));
        registry.DrainWorker();
        var request = stale ? new ExecutionCleanupRequest(Stale: true, Limit: 1, Apply: true) :
            new ExecutionCleanupRequest(IssueNumber: fixture.Issue.Number, Apply: true);
        var results = await service.RunAsync(request, CancellationToken.None);
        Assert.Equal("cleaned", results[0].Outcome);
        Assert.Equal(oldId, results[0].Inspection.ExecutionId);
        if (!stale) Assert.Equal("authoritative-recovery", results[1].Inspection.ReasonCode);
        else
        {
            Assert.Single(results);
            var next = Assert.Single(await service.RunAsync(request, CancellationToken.None));
            Assert.Equal(newId, next.Inspection.ExecutionId);
            Assert.Equal("authoritative-recovery", next.Inspection.ReasonCode);
        }
        Assert.False(Directory.Exists(olderGit.ExecutionDirectory));
        Assert.True(Directory.Exists(newerGit.ExecutionDirectory));
        Assert.True(Directory.Exists(unrelatedGit.ExecutionDirectory));
        Assert.NotEqual(string.Empty, await fixture.Git("branch", "--list", newer.FeatureBranch));
        Assert.NotEqual(string.Empty, await fixture.Git("branch", "--list", unrelatedBranch));
        Assert.Equal("IntegrationConflict", (await history.ReadExecutionAsync(oldId))?.State);
    }

    [Fact]
    public async Task OperatorCleanupReadsHistoryAfterAcquiringRepositoryGateAndHoldsDrain()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = false });
        await git.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await git.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var head = await fixture.GitAt(git.ExecutionDirectory, "rev-parse", "HEAD");
        var now = DateTimeOffset.UtcNow;
        var entry = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            await fixture.GitAt(git.ExecutionDirectory, "branch", "--show-current"), "main", now, now, "Failed", 1,
            null, null, 0, [], null, null, null, null, "cleanup-pending", head);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "history.db"));
        await history.CreateAsync(entry);
        var config = new WorkerConfiguration { Project = new ProjectSettings { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var registry = new ProjectRuntimeRegistry([("sample.yml", config)]);
        using var gate = new SemaphoreSlim(0, 1);
        var gates = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        Assert.True(gates.TryAdd("owner/repo", gate));
        var service = new ExecutionCleanupService(history, registry, c => fixture.CreateRepository(c.Git), gates);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None));
        registry.DrainWorker();
        var pending = service.RunAsync(new(ExecutionId: id, Apply: true), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.False(registry.CancelWorkerDrain());
        await history.UpdateRecoveryAsync(id, "integration-conflict");
        gate.Release();
        var result = Assert.Single(await pending);
        Assert.Equal("authoritative-recovery", result.Inspection.ReasonCode);
        Assert.True(Directory.Exists(git.ExecutionDirectory));
        Assert.True(registry.CancelWorkerDrain());
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _root;
        private RepositoryFixture(string root)
        {
            _root = root;
            Checkout = Path.Combine(root, "checkout");
            WorktreeRoot = Path.Combine(root, "managed-worktrees");
        }
        public string Checkout { get; }
        public string WorktreeRoot { get; }
        public GitHubIssue Issue { get; } = new(17, "Example task", "Implement this request", DateTimeOffset.UtcNow);
        public GitRepository CreateRepository(GitSettings settings) => new(new ProcessRunner(), Checkout, "owner/repo", settings,
            new WorkerSettings(), WorktreeRoot);

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var fixture = new RepositoryFixture(Path.Combine(Path.GetTempPath(), "codex-worker-worktree-tests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(fixture.Checkout);
            var bare = Path.Combine(fixture._root, "origin.git");
            await RunGit(fixture._root, "init", "--bare", bare);
            await RunGit(fixture.Checkout, "init", "-b", "main");
            await RunGit(fixture.Checkout, "config", "user.name", "Codex Worker Tests");
            await RunGit(fixture.Checkout, "config", "user.email", "worker-tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "base.txt"), "base");
            await RunGit(fixture.Checkout, "add", "base.txt");
            await RunGit(fixture.Checkout, "commit", "-m", "base");
            await RunGit(fixture.Checkout, "remote", "add", "origin", "https://github.com/owner/repo");
            await RunGit(fixture.Checkout, "config", $"url.file://{bare}.insteadOf", "https://github.com/owner/repo");
            await RunGit(fixture.Checkout, "push", "-u", "origin", "main");
            return fixture;
        }

        public async Task<string> Git(params string[] args) => await RunGit(Checkout, args);
        public async Task<string> GitAt(string directory, params string[] args) => await RunGit(directory, args);
        public async Task RecordPushTransactionsAsync()
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The receive hook requires a POSIX shell.");
            var remote = Path.Combine(_root, "origin.git");
            await File.WriteAllTextAsync(Path.Combine(remote, "received-refs"), "");
            var hook = Path.Combine(remote, "hooks", "post-receive");
            await File.WriteAllTextAsync(hook, "#!/bin/sh\ncat >> received-refs\nprintf '\\n' >> received-refs\n");
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public async Task<string> ReadPushTransactionsAsync() =>
            (await File.ReadAllTextAsync(Path.Combine(_root, "origin.git", "received-refs")))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        public async Task AdvanceBaseAsync(string path = "base-advanced.txt", string content = "independent base change")
        {
            await File.WriteAllTextAsync(Path.Combine(Checkout, path), content);
            await RunGit(Checkout, "add", path);
            await RunGit(Checkout, "commit", "-m", "advance base independently");
            await RunGit(Checkout, "push", "origin", "main");
        }
        public async Task AddAndPushAsync(string path, string content, bool executable = false)
        {
            var fullPath = Path.Combine(Checkout, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
            if (executable)
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                await RunGit(Checkout, "add", "--chmod=+x", "--", path);
            }
            else
                await RunGit(Checkout, "add", "--", path);
            await RunGit(Checkout, "commit", "-m", $"advance base with {path}");
            await RunGit(Checkout, "push", "origin", "main");
        }
        public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

        private static async Task<string> RunGit(string directory, params string[] args)
        {
            var result = await new ProcessRunner().RunAsync("git", args, directory, TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
            return result.StandardOutput.Trim();
        }
    }
}
