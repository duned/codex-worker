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
    public async Task IntegrationRecoveryReusesPreservedImplementationAndValidatesBeforeCompleting()
    {
        using var fixture = await seed.CreateAsync();
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

    [Theory]
    [InlineData("success", true, true)]
    [InlineData("success", false, false)]
    [InlineData("failed", false, false)]
    [InlineData("cancelled", false, false)]
    [InlineData("changed-intent", false, false)]
    [InlineData("success", true, false)]
    [InlineData("failed", true, false)]
    [InlineData("cancelled", true, false)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    public async Task ExplicitResolutionEditsThePreservedWorktreeAndPreservesFailures(string outcome, bool managed, bool legacyParent)
    {
        using var fixture = await seed.CreateAsync();
        var config = new WorkerConfiguration
        {
            Project = new() { Name = "demo", Repository = "owner/repo", Directory = fixture.Checkout },
            Git = new() { AutoMerge = true },
            GitHub = new() { ReadyLabel = "ready", WorkingLabel = "working", DoneLabel = "done", BlockedLabel = "blocked", FailedLabel = "failed" },
            Validation = new() { Commands = ["verify combined source"], MaxFixAttempts = 1 }
        };
        using var original = fixture.CreateRepository(config.Git);
        await original.InitializeAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
        var workspace = original.ExecutionDirectory;
        await File.WriteAllTextAsync(Path.Combine(workspace, "base.txt"), "implementation");
        await fixture.AdvanceBaseAsync("base.txt", "new base value");
        await Assert.ThrowsAsync<GitIntegrationConflictException>(() => original.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));
        var preserved = Assert.IsType<GitRecoveryInfo>(await original.PreserveIntegrationConflictAsync(CancellationToken.None));
        var source = new ExecutionHistoryEntry(id, "demo", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            preserved.Branch, "main", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "IntegrationConflict", null,
            "original implementation", "passed", 0, [], preserved.BaseCommit, "main", null, "conflict",
            RecoveryState: "integration-conflict", RecoveryBaseCommit: preserved.BaseCommit, OriginalIssueBody: fixture.Issue.Body,
            ServerExecutionId: managed ? "server-source" : null, ParentIssueCaptured: !legacyParent);
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../explicit-conflict.db"));
        await history.CreateAsync(source);
        var proof = legacyParent ? JsonSerializer.Serialize(new CodexProvisioning.IssueParentProvenance(
            source.Repository, source.IssueNumber, "native-issue", source.StartedAtUtc.AddDays(-1), source.StartedAtUtc, 32,
            [new("native-parent-added", source.StartedAtUtc.AddMinutes(-1), true, 32)])) : null;
        var verifiedIssue = fixture.Issue with { ParentIssueCaptured = true, ParentIssueNumber = legacyParent ? 32 : null, ParentProvenance = proof };
        var execution = WorkerExecution.Create(config.Project, config.Git, verifiedIssue, retryOfExecutionId: id,
            attemptNumber: 2, resumed: true, featureBranchOverride: source.FeatureBranch,
            serverExecutionId: managed ? "server-recovery" : null, assignmentId: managed ? "assignment" : null,
            ownershipGeneration: managed ? 1 : null);
        execution.TransitionTo(ExecutionState.Claimed);
        var attempt = source with { ExecutionId = execution.ExecutionId, CompletedAtUtc = null, State = "Created",
            RetryOfExecutionId = id, AttemptNumber = 2, RecoveryState = null, ParentIssueCaptured = true,
            ParentIssueNumber = verifiedIssue.ParentIssueNumber, ParentProvenance = proof };
        Assert.True(await history.TryClaimIntegrationRecoveryAsync(source, attempt, "explicit", true));
        using var cancellation = new CancellationTokenSource();
        var github = new ConcurrentGitHub(fixture.Issue) { ParentNumber = verifiedIssue.ParentIssueNumber, ParentProvenance = proof };
        github.Labels.Add((fixture.Issue.Number, config.GitHub.WorkingLabel));
        var engine = new ConflictFileEngine(workspace, outcome, cancellation, () => github.ChangedBody = "changed intent");
        using var repository = original.CreateExecutionRepository();
        using var output = new StringWriter();
        var validation = new ConflictSourceValidation(workspace);
        var codex = new ConflictFileCodex(engine);
        var runner = new ExecutionRunner(config, repository, codex, validation, new WorkerConsole(output, false), history,
            isAuthoritative: managed ? (_, _) => Task.FromResult(true) : null, github: github);
        var context = new CodexWorker.ExecutionContext(execution, fixture.Issue, source,
            IntegrationRecovery: true, ResolveIntegrationConflict: true);
        if (outcome == "cancelled")
            await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(context, cancellation.Token));
        else Assert.Equal(outcome == "success" ? IssueOutcomeKind.Succeeded : IssueOutcomeKind.IntegrationConflict,
            (await runner.RunAsync(context, cancellation.Token)).Kind);
        Assert.Equal(1, engine.Calls);
        if (outcome == "success")
        {
            Assert.Equal(1, validation.Calls);
            Assert.Equal("combined source", await fixture.Git("show", "main:base.txt"));
            Assert.False(Directory.Exists(workspace));
        }
        else
        {
            Assert.Equal(0, validation.Calls);
            Assert.True(Directory.Exists(workspace));
            Assert.Equal("combined source", await File.ReadAllTextAsync(Path.Combine(workspace, "base.txt")));
            Assert.Equal(preserved.BaseCommit, await fixture.Git("rev-parse", source.FeatureBranch));
            Assert.NotEmpty(await fixture.GitAt(workspace, "ls-files", "-u"));
            Assert.Equal("integration-conflict", (await history.ReadExecutionAsync(id))?.RecoveryState);
        }
    }

    private sealed class ConflictSourceValidation(string workspace) : IValidationRunner
    {
        public int Calls { get; private set; }
        public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(workspace, directory);
            Assert.Equal("combined source", await File.ReadAllTextAsync(Path.Combine(directory, "base.txt"), ct));
            return ValidationResult.Success;
        }
    }

    private sealed class ConflictFileEngine(string workspace, string outcome, CancellationTokenSource cancellation, Action changeIntent)
    {
        public int Calls { get; private set; }
        public async Task<CodexOutcome> ResolveAsync(string directory, string details, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(workspace, directory);
            Assert.Contains("base.txt", details, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "base.txt"), "combined source", ct);
            if (outcome == "cancelled") { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            if (outcome == "changed-intent") changeIntent();
            return new CodexOutcome(outcome == "failed" ? "failed" : "success", "conflict result", [], false, null);
        }
    }

    private sealed class ConflictFileCodex(ConflictFileEngine engine) : ICodexExecutor
    {
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct) =>
            throw new InvalidOperationException("Conflict resolution must not rerun implementation.");
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected validation repair.");
        public Task<CodexOutcome> ResolveIntegrationConflictAsync(string projectDirectory, string instructionsFile,
            GitHubIssue issue, string conflictDetails, CancellationToken ct) => engine.ResolveAsync(projectDirectory, conflictDetails, ct);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task LegacyIntegrationConflictUsesPersistedCommitWhenRecoveryStateIsMissing()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task LegacyIntegrationConflictIsRejectedWhenPreservedWorktreeIsMissing()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CodexResolvedRebaseConflictContinuesAndValidatesBeforeIntegration()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task FailedValidationAfterRebaseStopsBeforeIntegration()
    {
        using var fixture = await seed.CreateAsync();
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
}
