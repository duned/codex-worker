using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitBranchTests
{
    [Theory]
    [InlineData("Add Fast Search!", "add-fast-search")]
    [InlineData("  Café / 東京  ", "caf")]
    [InlineData("---", "issue")]
    [InlineData("A___B", "a-b")]
    public void SanitizesIssueTitles(string title, string expected) =>
        Assert.Equal(expected, GitRepository.SanitizeTitle(title));

    [Fact]
    public void BranchSlugHasBoundedLength()
    {
        var slug = GitRepository.SanitizeTitle(new string('A', 200));
        Assert.Equal(72, slug.Length);
    }

    [Fact]
    public void FeatureAndCompletedBranchesPlaceIssueNumberAfterSlugAndPreservePrefixes()
    {
        var issue = new GitHubIssue(18, "7.2 · Add local project configuration management", "", DateTimeOffset.UtcNow);
        var settings = new GitSettings { FeaturePrefix = "feature/", CompletedPrefix = "done/feature/" };

        Assert.Equal("feature/7-2-add-local-project-configuration-management-18", GitRepository.FeatureBranchName(settings, issue));
        Assert.Equal("done/feature/7-2-add-local-project-configuration-management-18", GitRepository.CompletedBranchName(settings, issue));
    }

    [Theory]
    [InlineData("Add [local] config!", "feature/task/add-local-config-31", "archive/task/add-local-config-31")]
    [InlineData("plain title", "feature/task/plain-title-31", "archive/task/plain-title-31")]
    public void BranchNamesNormalizeTitlesWithoutRequiringReleasePrefix(string title, string feature, string completed)
    {
        var issue = new GitHubIssue(31, title, "", DateTimeOffset.UtcNow);
        var settings = new GitSettings { FeaturePrefix = "feature/task/", CompletedPrefix = "archive/task/" };

        Assert.Equal(feature, GitRepository.FeatureBranchName(settings, issue));
        Assert.Equal(completed, GitRepository.CompletedBranchName(settings, issue));
    }

    [Fact]
    public void IssueDisplayUsesTitleThenNumber()
    {
        var issue = new GitHubIssue(18, "7.2 · Add local project configuration management", "", DateTimeOffset.UtcNow);

        Assert.Equal("7.2 · Add local project configuration management #18", IssueFormatting.Display(issue));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", true)]
    [InlineData("git@github.com:owner/repo.git", true)]
    [InlineData("https://attacker.example/owner/repo.git", false)]
    [InlineData("https://secret@github.com/owner/repo.git", false)]
    [InlineData("git@github.com:other/repo.git", false)]
    public void ValidatesOriginHostAndRepository(string origin, bool expected) =>
        Assert.Equal(expected, GitRepository.OriginMatchesRepository(origin, "owner/repo"));
}
