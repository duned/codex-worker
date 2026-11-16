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
    public async Task SuccessfulIntegrationCommitsFromWorktreeAndPreservesFeatureBranch()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task AutoMergeFastForwardsWithoutMergeCommitAndPreservesCompletedBranch()
    {
        using var fixture = await seed.CreateAsync();
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
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(1, false, true)]
    [InlineData(2, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    public async Task DirectIntegrationPushSequenceIsIndependentOfRetryAndRecovery(int attempt, bool recover, bool retainCompleted)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task HistoricalMergeSequencePublishesImplementationWithBaseBeforeCompletedRef()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ReopenedIssueArchivesEachExecutionWithoutReplacingHistoricalCompletedBranches()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task ExistingLocalAndRemoteRetryCompletionRefsArePreservedAndExecutionGetsUniqueName()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task AdvancedBaseRebasesFeatureValidatesAndFastForwardsFeatureCommit()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task BaseAdvanceDuringValidationReconcilesAgainWithoutMergeCommit()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RebaseConflictStopsBeforeFastForwardAndPreservesExecutionWorktree()
    {
        using var fixture = await seed.CreateAsync();
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

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task FreshAttemptAfterConflictUsesCurrentMainAndLeavesOldWorktreeUntouched()
    {
        using var fixture = await seed.CreateAsync();
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
}
