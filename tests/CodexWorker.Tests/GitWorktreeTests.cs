using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitWorktreeTests
{
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
    public async Task FastForwardFailureDoesNotFallBackToMergeCommit()
    {
        using var fixture = await RepositoryFixture.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        await fixture.AdvanceBaseAsync();

        await Assert.ThrowsAsync<WorkerInfrastructureException>(() => git.CommitAndIntegrateAsync(fixture.Issue, async _ =>
        {
            // Simulate another base update after the integration fetch/rebase but before the fast-forward.
            await fixture.AdvanceBaseAsync("raced-base.txt", "base changed during integration");
            return ValidationResult.Success;
        }, CancellationToken.None));

        Assert.Equal("base changed during integration", await fixture.Git("show", "main:raced-base.txt"));
        Assert.DoesNotContain("implemented.txt", await fixture.Git("ls-tree", "-r", "--name-only", "main"));
        var parents = (await fixture.Git("rev-list", "--parents", "-n", "1", "main")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parents.Length);
        Assert.True(Directory.Exists(git.ExecutionDirectory));
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
            Validation = new ValidationSettings { Commands = ["local-check"] },
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
        Assert.Contains("attempt 1/2", output.ToString());
        Assert.Contains("attempt 2/2", output.ToString());
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

    private sealed class ConcurrentGitHub(GitHubIssue template) : IGitHubClient
    {
        private int _number = 16;
        public List<(int Issue, string Label)> Labels { get; } = [];
        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken) =>
            Task.FromResult<GitHubIssue?>(label == "ready" ? template with { Number = ++_number } : null);
        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        { Labels.Add((issueNumber, add)); return Task.CompletedTask; }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct) => Task.CompletedTask;
        public Task CloseAsync(int issueNumber, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FileCodex : ICodexExecutor
    {
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
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
        public async Task AdvanceBaseAsync(string path = "base-advanced.txt", string content = "independent base change")
        {
            await File.WriteAllTextAsync(Path.Combine(Checkout, path), content);
            await RunGit(Checkout, "add", path);
            await RunGit(Checkout, "commit", "-m", "advance base independently");
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
