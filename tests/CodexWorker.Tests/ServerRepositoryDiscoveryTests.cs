namespace CodexWorker.Tests;

using CodexServer;

public sealed class ServerRepositoryDiscoveryTests
{
    private const string RepositoryJson = """
        {"full_name":"team/project","name":"project","default_branch":"release/current","description":"Example"}
        """;

    [Fact]
    public async Task DiscoveryUsesAuthenticatedContextAndBoundsEachPage()
    {
        var commands = new List<IReadOnlyList<string>>();
        var service = new ServerGitHubReadService((arguments, _) =>
        {
            commands.Add(arguments);
            return Task.FromResult(new GitHubReadCommandResult(0,
                "[" + string.Join(',', Enumerable.Repeat(RepositoryJson, 50)) + "]", ""));
        });
        var result = await service.ListRepositoriesAsync(2);
        Assert.Equal(3, result.NextPage);
        Assert.Equal(50, result.Repositories.Count);
        Assert.Equal("release/current", result.Repositories[0].DefaultBranch);
        Assert.Equal("project", result.Repositories[0].Name);
        Assert.Equal(new[] { "api", "--method", "GET", "user/repos?per_page=50&page=2&sort=full_name" }, Assert.Single(commands));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListRepositoriesAsync(0));
        Assert.Single(commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableRepositoryOrMissingBranchCannotBeVerified(bool repositoryReadable)
    {
        var commands = new List<IReadOnlyList<string>>();
        var service = new ServerGitHubReadService((arguments, _) =>
        {
            commands.Add(arguments);
            return Task.FromResult(repositoryReadable && arguments[1] == "repos/team/project"
                ? new GitHubReadCommandResult(0, RepositoryJson, "")
                : new GitHubReadCommandResult(1, "", "HTTP 404"));
        });
        var exception = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.VerifyRepositoryAsync(Definition()));
        Assert.Equal("team/project", exception.Repository);
        Assert.Equal(repositoryReadable ? 2 : 1, commands.Count);
    }

    [Fact]
    public async Task VerificationChecksActualBranchAndDoesNotCheckWriteAuthorization()
    {
        var commands = new List<IReadOnlyList<string>>();
        var service = new ServerGitHubReadService((arguments, _) =>
        {
            commands.Add(arguments);
            return Task.FromResult(new GitHubReadCommandResult(0,
                arguments[1] == "repos/team/project" ? RepositoryJson : "{\"name\":\"release/current\"}", ""));
        });
        var result = await service.VerifyRepositoryAsync(Definition());
        Assert.True(result.RepositoryReadable);
        Assert.True(result.BranchExists);
        Assert.Equal("repos/team/project/branches/release%2Fcurrent", commands[1][1]);
        Assert.Equal(2, commands.Count);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[{\"full_name\":\"bad/path/extra\"}]")]
    public async Task InvalidDiscoveryResponsesAreUnavailable(string response)
    {
        var service = new ServerGitHubReadService((_, _) => Task.FromResult(new GitHubReadCommandResult(0, response, "")));
        var exception = await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => service.ListRepositoriesAsync(1));
        Assert.Equal("invalid-response", exception.Code);
    }

    [Fact]
    public async Task CancellationPropagatesAndValidationRejectsBeforeProviderAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new ServerGitHubReadService((_, token) => Task.FromCanceled<GitHubReadCommandResult>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListRepositoriesAsync(1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyRepositoryAsync(Definition(), cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyRepositoryAsync(Definition() with
        {
            Requirements = [new("authentication", "github-api", Scope: "invalid")]
        }));
    }

    private static CentralProjectDefinition Definition() => new("Project", "team/project", "release/current", "");
}
