namespace CodexWorker.Tests;

public sealed class GitHubParentSessionScopeTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("null", null)]
    [InlineData("{\"number\":2,\"repository\":{\"nameWithOwner\":\"OWNER/REPO\"}}", 2)]
    public async Task ParentScopeComesFromVerifiedGitHubRelationship(string parent, int? expected)
    {
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            Assert.Contains("graphql", args);
            return Task.FromResult(new ProcessResult(0,
                "{\"data\":{\"repository\":{\"issue\":{\"number\":8,\"parent\":" + parent + "}}}}", ""));
        });
        Assert.Equal(expected, await client.GetParentIssueNumberAsync(8, CancellationToken.None));
    }

    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("{\"errors\":[{\"message\":\"unavailable\"}],\"data\":null}")]
    [InlineData("{\"data\":{\"repository\":{\"issue\":null}}}")]
    [InlineData("{\"data\":{\"repository\":{\"issue\":{\"number\":9,\"parent\":null}}}}")]
    [InlineData("{\"data\":{\"repository\":{\"issue\":{\"number\":8,\"parent\":{\"number\":2,\"repository\":{\"nameWithOwner\":\"other/repo\"}}}}}}")]
    public async Task UnavailableOrUnrelatedParentDoesNotBecomeAnAbsentRelationship(string response)
    {
        var client = new GitHubClient("owner/repo", (_, _) => Task.FromResult(new ProcessResult(0, response, "")));
        await Assert.ThrowsAsync<GitHubOperationException>(() => client.GetParentIssueNumberAsync(8, CancellationToken.None));
    }
}
