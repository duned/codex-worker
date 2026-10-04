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
    public async Task ShutdownDuringCodexPreservesRealWorkspaceAndRestartUsesFreshCapacity()
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
            Assert.Equal("uncertain", interrupted.RecoveryState);
            Assert.Contains(workspace, interrupted.FailureReason);
            Assert.Equal("useful partial implementation", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
            Assert.DoesNotContain("infrastructure failure", writer.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        using var restartedGit = fixture.CreateRepository(config.Git);
        await restartedGit.InitializeAsync(CancellationToken.None);
        var restarted = new Worker(config, new ConcurrentGitHub(fixture.Issue), restartedGit,
            new FileCodex(), new ImmediateValidation(), telegram, console, history);
        // Ready here represents an explicit operator retry after inspecting uncertain state.
        var retry = await restarted.ProcessOneAsync(CancellationToken.None);
        Assert.Equal(IssueOutcomeKind.Succeeded, retry!.Kind);
        Assert.Equal(2, retry.Report.AttemptNumber);
        var preserved = Path.Combine(fixture.WorktreeRoot, interrupted.ExecutionId.ToString("N"));
        Assert.True(Directory.Exists(preserved));
        Assert.Equal("useful partial implementation", await File.ReadAllTextAsync(Path.Combine(preserved, "partial.txt")));
        Assert.Equal(interrupted.FeatureBranch, await fixture.GitAt(preserved, "branch", "--show-current"));
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
            GitHub = new GitHubSettings { ReadyLabel = "ready" },
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
        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        { Labels.Add((issueNumber, add)); return Task.CompletedTask; }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct)
        { Comments.Add((issueNumber, comment)); return Task.CompletedTask; }
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
