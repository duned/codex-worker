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
}
