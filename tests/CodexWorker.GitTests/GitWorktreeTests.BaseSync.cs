using CodexWorker;

namespace CodexWorker.Tests;

public sealed partial class GitWorktreeTests
{
    private static async Task<string> PushFromSeparateCloneAsync(RepositoryFixture fixture, string path, string content)
    {
        var clone = Path.Combine(fixture._root, "other-" + Guid.NewGuid().ToString("N"));
        await fixture.GitAt(fixture._root, "clone", "--branch", "main", Path.Combine(fixture._root, "origin.git"), clone);
        await fixture.GitAt(clone, "config", "user.name", "Other Integrator");
        await fixture.GitAt(clone, "config", "user.email", "other@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(clone, path), content);
        await fixture.GitAt(clone, "add", path);
        await fixture.GitAt(clone, "commit", "-m", $"other integrator adds {path}");
        await fixture.GitAt(clone, "push", "origin", "main");
        return await fixture.GitAt(clone, "rev-parse", "HEAD");
    }

    private static async Task InstallRejectingHookAsync(RepositoryFixture fixture, string message)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The receive hook requires a POSIX shell.");
        var hook = Path.Combine(fixture._root, "origin.git", "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\necho \"{message}\" >&2\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task RejectedPushIsContainedRealignsLocalBaseAndReportsWorkflowScope()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "ci.yml"), "workflow");
        var remoteBefore = await fixture.GitAt(Path.Combine(fixture._root, "origin.git"), "rev-parse", "main");
        await InstallRejectingHookAsync(fixture,
            "refusing to allow an OAuth App to create or update workflow `.github/workflows/ci.yml` without `workflow` scope");

        var withdrawn = 0;
        git.WithIntegrationWithdrawnObserver(_ => { withdrawn++; return Task.CompletedTask; });
        var conflict = await Assert.ThrowsAsync<GitIntegrationConflictException>(() => git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None));
        var rejection = Assert.IsType<GitPushRejectedException>(conflict.InnerException);
        Assert.Equal(1, withdrawn);
        Assert.False(conflict.RebaseAborted);

        Assert.Equal(GitPushRejection.WorkflowScope, rejection.Reason);
        Assert.Contains("'workflow' scope", rejection.Message);
        Assert.DoesNotContain(".github/workflows/ci.yml", rejection.Message);
        // The local base must not keep unpublished work, while the feature branch preserves it.
        Assert.Equal(remoteBefore, await fixture.Git("rev-parse", "main"));
        Assert.Equal("workflow", await fixture.Git("show", "feature/example-task-17:ci.yml"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task LocalBaseLeftAheadByRejectedPushDoesNotLeakIntoOtherExecutions()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        var remoteBase = await fixture.Git("rev-parse", "main");
        // Reproduce a Worker state from a push rejected after the local fast-forward: local main holds an
        // unpublished commit that a preserved feature branch still owns.
        await fixture.Git("switch", "-c", "feature/other-task-60");
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "unpublished.txt"), "unpublished");
        await fixture.Git("add", "unpublished.txt");
        await fixture.Git("commit", "-m", "unpublished");
        await fixture.Git("switch", "main");
        await fixture.Git("merge", "--ff-only", "feature/other-task-60");
        await PushFromSeparateCloneAsync(fixture, "remote-progress.txt", "remote");

        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(git.ExecutionDirectory, "unpublished.txt")));
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        var result = await git.CommitAndIntegrateAsync(fixture.Issue,
            _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        Assert.True(result.HasChanges);
        // The change is measured against the base it landed on, so newer remote work is not counted.
        var changes = Assert.IsType<CodexProvisioning.ExecutionChanges>(result.Changes);
        Assert.Equal((1, 1, 0), (changes.FilesChanged, changes.Insertions, changes.Deletions));
        Assert.Equal(["implemented.txt"], changes.Files);
        Assert.Equal(result.CommitSha, changes.Commit);
        Assert.Equal(await fixture.Git("rev-parse", "main~1"), changes.BaseCommit);
        var remote = Path.Combine(fixture._root, "origin.git");
        Assert.Equal(await fixture.GitAt(remote, "rev-parse", "main"), await fixture.Git("rev-parse", "main"));
        Assert.Equal("implementation", await fixture.GitAt(remote, "show", "main:implemented.txt"));
        Assert.Equal("remote", await fixture.GitAt(remote, "show", "main:remote-progress.txt"));
        Assert.DoesNotContain("unpublished.txt", await fixture.GitAt(remote, "ls-tree", "--name-only", "main"));
        Assert.Equal("unpublished", await fixture.Git("show", "feature/other-task-60:unpublished.txt"));
        Assert.NotEqual(remoteBase, await fixture.Git("rev-parse", "main"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task PullRequestModePushesTheRebasedValidatedBranchAndLeavesTheBaseUntouched()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true, Integration = "pull-request" });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        var remoteProgress = await PushFromSeparateCloneAsync(fixture, "remote-progress.txt", "remote");
        var validations = 0;

        var result = await git.CommitAndIntegrateAsync(fixture.Issue,
            _ => { validations++; return Task.FromResult(ValidationResult.Success); }, CancellationToken.None);

        Assert.True(result.HasChanges);
        Assert.Null(result.IntegrationBranch);
        Assert.Equal("main", result.PullRequestBase);
        var head = Assert.IsType<string>(result.CompletedBranch);
        Assert.Contains("Pushed as", result.Summary);
        Assert.DoesNotContain("Merged into", result.Summary);
        // The branch was rebased onto the advanced base and validated again before it was offered.
        Assert.Equal(1, validations);
        var remote = Path.Combine(fixture._root, "origin.git");
        Assert.Equal(remoteProgress, await fixture.GitAt(remote, "rev-parse", "main"));
        Assert.Equal(remoteProgress, await fixture.Git("rev-parse", "main"));
        Assert.Equal(result.CommitSha, await fixture.GitAt(remote, "rev-parse", $"refs/heads/{head}"));
        Assert.Equal("implementation", await fixture.GitAt(remote, "show", $"{head}:implemented.txt"));
        Assert.Equal(remoteProgress, await fixture.GitAt(remote, "rev-parse", $"{head}~1"));
        var changes = Assert.IsType<CodexProvisioning.ExecutionChanges>(result.Changes);
        Assert.Equal(["implemented.txt"], changes.Files);
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task UnpreservedLocalBaseCommitsStopBeforeIntegration()
    {
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "orphan.txt"), "orphan");
        await fixture.Git("add", "orphan.txt");
        await fixture.Git("commit", "-m", "local only");
        var local = await fixture.Git("rev-parse", "main");

        var error = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None));

        Assert.Contains("unpublished commits", error.Message);
        Assert.Equal(local, await fixture.Git("rev-parse", "main"));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task NonFastForwardPushRaceRebasesRevalidatesAndIntegrates()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        using var git = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await git.InitializeAsync(CancellationToken.None);
        await git.StartIssueAsync(Guid.NewGuid(), fixture.Issue, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(git.ExecutionDirectory, "implemented.txt"), "implementation");
        var validations = 0;
        var authorityChecks = 0;
        var raced = false;

        var result = await git.CommitAndIntegrateAsync(fixture.Issue, _ =>
            {
                validations++;
                return Task.FromResult(ValidationResult.Success);
            }, (_, _) => Task.FromResult(false), (_, _) => Task.FromResult(new IntegrationRepairResult(false, false)),
            CancellationToken.None, async _ =>
            {
                // The fourth check runs immediately before the push: another Worker wins the race there.
                if (++authorityChecks == 4 && !raced)
                {
                    raced = true;
                    await PushFromSeparateCloneAsync(fixture, "raced.txt", "other worker");
                }
            });

        Assert.True(raced);
        Assert.True(result.HasChanges);
        Assert.True(validations >= 1);
        var remote = Path.Combine(fixture._root, "origin.git");
        Assert.Equal("other worker", await fixture.GitAt(remote, "show", "main:raced.txt"));
        Assert.Equal("implementation", await fixture.GitAt(remote, "show", "main:implemented.txt"));
        Assert.Equal(await fixture.GitAt(remote, "rev-parse", "main"), await fixture.Git("rev-parse", "main"));
    }

    [Theory]
    [InlineData(" ! [rejected]        abc -> main (fetch first)", GitPushRejection.NonFastForward)]
    [InlineData(" ! [rejected]        abc -> main (non-fast-forward)", GitPushRejection.NonFastForward)]
    [InlineData(" ! [remote rejected] abc -> main (refusing to allow an OAuth App to create or update workflow `.github/workflows/ci.yml` without `workflow` scope)", GitPushRejection.WorkflowScope)]
    [InlineData("remote: error: GH006: Protected branch update failed for refs/heads/main.", GitPushRejection.ProtectedBranch)]
    [InlineData("remote: error: GH013: Repository rule violations found for refs/heads/main.", GitPushRejection.ProtectedBranch)]
    [InlineData("remote: Permission to owner/repo.git denied to someone.", GitPushRejection.PermissionDenied)]
    public void PushRejectionsAreClassifiedWithoutRawOutput(string output, GitPushRejection expected)
    {
        var rejection = GitRepository.ClassifyPushRejection(output, "main");
        Assert.NotNull(rejection);
        Assert.Equal(expected, rejection.Reason);
        Assert.DoesNotContain("someone", rejection.Message);
        Assert.Null(GitRepository.ClassifyPushRejection("fatal: unable to access remote: Could not resolve host", "main"));
    }
}
