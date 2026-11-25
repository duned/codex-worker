using CodexWorker;

namespace CodexWorker.Tests;

public sealed partial class GitWorktreeTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task SubIssuesCollectInTheParentBranchAndTheParentOffersItForReview()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        var settings = new GitSettings { AutoMerge = true, Integration = "pull-request" };
        using var git = fixture.CreateRepository(settings);
        await git.InitializeAsync(CancellationToken.None);
        var parent = new GitHubIssue(50, "Parent feature", "Deliver both parts", DateTimeOffset.UtcNow, ParentIssueCaptured: true);
        var first = new GitHubIssue(51, "First part", "a", DateTimeOffset.UtcNow, ParentIssueNumber: 50, ParentIssueCaptured: true);
        var second = new GitHubIssue(52, "Second part", "b", DateTimeOffset.UtcNow, ParentIssueNumber: 50, ParentIssueCaptured: true);
        var remote = Path.Combine(fixture._root, "origin.git");
        var baseCommit = await fixture.GitAt(remote, "rev-parse", "main");

        // Both sub-issues start before either is integrated, so both start from the base.
        using var firstRun = (GitRepository)git.CreateExecutionRepository();
        using var secondRun = (GitRepository)git.CreateExecutionRepository();
        await firstRun.StartIssueAsync(Guid.NewGuid(), first, CancellationToken.None);
        await secondRun.StartIssueAsync(Guid.NewGuid(), second, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(firstRun.ExecutionDirectory, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(secondRun.ExecutionDirectory, "second.txt"), "second");

        var firstResult = await firstRun.CommitAndIntegrateAsync(first, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);
        Assert.Equal("integration/50", firstResult.ParentBranch);
        Assert.Equal(50, firstResult.ParentIssueNumber);
        Assert.Null(firstResult.IntegrationBranch);
        Assert.Null(firstResult.PullRequestBase);
        Assert.Contains("Integrated into `integration/50`", firstResult.Summary);
        Assert.DoesNotContain("Merged into", firstResult.Summary);
        Assert.Equal(firstResult.CommitSha, await fixture.GitAt(remote, "rev-parse", "refs/heads/integration/50"));

        // The second one is rebased onto its sibling's work and validated again before it is delivered.
        var validations = 0;
        var secondResult = await secondRun.CommitAndIntegrateAsync(second,
            _ => { validations++; return Task.FromResult(ValidationResult.Success); }, CancellationToken.None);
        Assert.Equal(1, validations);
        Assert.Equal("integration/50", secondResult.ParentBranch);
        Assert.Equal(secondResult.CommitSha, await fixture.GitAt(remote, "rev-parse", "refs/heads/integration/50"));
        Assert.Equal(firstResult.CommitSha, await fixture.GitAt(remote, "rev-parse", "integration/50~1"));
        Assert.Equal(["second.txt"], Assert.IsType<CodexProvisioning.ExecutionChanges>(secondResult.Changes).Files);
        // The base never moves while sub-issues are delivered.
        Assert.Equal(baseCommit, await fixture.GitAt(remote, "rev-parse", "main"));

        // The base advances independently; the parent needs no change of its own and still offers all the work.
        var remoteProgress = await PushFromSeparateCloneAsync(fixture, "remote-progress.txt", "remote");
        using var parentRun = (GitRepository)git.CreateExecutionRepository();
        await parentRun.StartIssueAsync(Guid.NewGuid(), parent, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(parentRun.ExecutionDirectory, "first.txt")));
        Assert.True(File.Exists(Path.Combine(parentRun.ExecutionDirectory, "second.txt")));
        var parentResult = await parentRun.CommitAndIntegrateAsync(parent, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);

        Assert.True(parentResult.HasChanges);
        Assert.Null(parentResult.ParentBranch);
        Assert.Equal("main", parentResult.PullRequestBase);
        var head = Assert.IsType<string>(parentResult.CompletedBranch);
        Assert.Equal(parentResult.CommitSha, await fixture.GitAt(remote, "rev-parse", $"refs/heads/{head}"));
        Assert.Equal("first", await fixture.GitAt(remote, "show", $"{head}:first.txt"));
        Assert.Equal("second", await fixture.GitAt(remote, "show", $"{head}:second.txt"));
        Assert.Equal("remote", await fixture.GitAt(remote, "show", $"{head}:remote-progress.txt"));
        Assert.Equal(remoteProgress, await fixture.GitAt(remote, "rev-parse", "main"));
        Assert.Equal(["first.txt", "second.txt"], Assert.IsType<CodexProvisioning.ExecutionChanges>(parentResult.Changes).Files.Order());
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task DirectModeIgnoresTheParentAndASubIssueWithoutChangesClosesNormally()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await seed.CreateAsync();
        var child = new GitHubIssue(61, "Child", "c", DateTimeOffset.UtcNow, ParentIssueNumber: 60, ParentIssueCaptured: true);
        using var direct = fixture.CreateRepository(new GitSettings { AutoMerge = true });
        await direct.InitializeAsync(CancellationToken.None);
        await direct.StartIssueAsync(Guid.NewGuid(), child, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(direct.ExecutionDirectory, "child.txt"), "child");
        var result = await direct.CommitAndIntegrateAsync(child, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);
        Assert.Equal("main", result.IntegrationBranch);
        Assert.Null(result.ParentBranch);
        var remote = Path.Combine(fixture._root, "origin.git");
        Assert.Equal("", await fixture.GitAt(remote, "branch", "--list", "integration/*"));

        direct.Dispose();
        using var review = fixture.CreateRepository(new GitSettings { AutoMerge = true, Integration = "pull-request" });
        await review.InitializeAsync(CancellationToken.None);
        await review.StartIssueAsync(Guid.NewGuid(), child with { Number = 62 }, CancellationToken.None);
        var empty = await review.CommitAndIntegrateAsync(child with { Number = 62 }, _ => Task.FromResult(ValidationResult.Success), CancellationToken.None);
        Assert.False(empty.HasChanges);
        Assert.Equal("", await fixture.GitAt(remote, "branch", "--list", "integration/*"));
    }
}
