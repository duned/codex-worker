using System.Diagnostics;
using System.Text.Json;
using WorkExecutionToolbox.Cli;

namespace WorkExecutionToolbox.Tests;

public sealed partial class ToolboxCommandTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    public async Task DiscoveryResolvesScopedRepositoryBeforeProvider(string remote)
    {
        var provider = new FakeProvider();
        var discovered = false;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ToolboxCommand.RunAsync(["graph", "9"], provider, provider, output, error,
            readRemoteUrls: _ =>
            {
                Assert.Null(provider.Operation);
                discovered = true;
                return Task.FromResult<IReadOnlyList<string>>([remote, remote]);
            });
        Assert.True(discovered);
        Assert.Equal(0, exit);
        Assert.Equal("owner/repo", provider.Issue?.Repository.Repository);
    }

    [Fact]
    public async Task ExplicitRepositoryNeverInvokesDiscovery()
    {
        var provider = new FakeProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ToolboxCommand.RunAsync(["graph", "9", "--repo", "explicit/repo"],
            provider, provider, output, error,
            readRemoteUrls: _ => throw new InvalidOperationException("Discovery must not run."));
        Assert.Equal(0, exit);
        Assert.Equal("explicit/repo", provider.Issue?.Repository.Repository);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.invalid/owner/repo.git")]
    [InlineData("malformed")]
    [InlineData("https://github.com/owner/repo.git", "git@github.com:other/repo.git")]
    [InlineData("https://github.com/owner/repo.git", "malformed")]
    [InlineData("https://secret-credential@github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo.git?secret-credential")]
    public async Task FailedDiscoveryNeverAuthenticatesOrExposesRemote(params string[] remotes)
    {
        var authenticated = false;
        using var http = new HttpClient();
        var provider = new GitHubIssueProvider(http, _ =>
        {
            authenticated = true;
            throw new InvalidOperationException("Authentication must not run.");
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ToolboxCommand.RunAsync(["relationships", "9", "--json"], provider, provider, output, error,
            readRemoteUrls: _ => Task.FromResult<IReadOnlyList<string>>(remotes));
        Assert.Equal(2, exit);
        Assert.False(authenticated);
        Assert.Empty(output.ToString());
        Assert.Contains("--repo owner/name", error.ToString());
        Assert.DoesNotContain("secret-credential", error.ToString());
    }

    [Fact]
    public async Task SuccessfulDiscoveryPrecedesAuthentication()
    {
        var discovered = false;
        var authenticated = false;
        using var http = new HttpClient();
        var provider = new GitHubIssueProvider(http, _ =>
        {
            Assert.True(discovered);
            authenticated = true;
            throw new GitHubIssueException(GitHubIssueFailure.Authorization, "Authentication unavailable.");
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ToolboxCommand.RunAsync(["graph", "9"], provider, provider, output, error,
            readRemoteUrls: _ =>
            {
                Assert.False(authenticated);
                discovered = true;
                return Task.FromResult<IReadOnlyList<string>>(["https://github.com/owner/repo.git"]);
            });
        Assert.Equal(1, exit);
        Assert.True(authenticated);
    }

    [Theory]
    [InlineData("parent set 9 9 3")]
    [InlineData("parent set 9 3 3")]
    [InlineData("parent set 9 0 3")]
    public async Task InvalidParentBatchRejectedBeforeDiscoveryAndProvider(string command)
    {
        var provider = new FakeProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ToolboxCommand.RunAsync([.. command.Split(' '), "--repo", "owner/repo"], provider, provider, output, error,
            readRemoteUrls: _ => throw new InvalidOperationException("Discovery must not run."));
        Assert.Equal(2, exit);
        Assert.Null(provider.Operation);
    }

    [Fact]
    public async Task OversizedParentBatchRejectedBeforeProvider()
    {
        var provider = new FakeProvider();
        var numbers = Enumerable.Range(10, 51).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var run = await RunAsync(["parent", "set", .. numbers, "3", "--repo", "owner/repo"], provider);
        Assert.Equal(2, run.Exit);
        Assert.Null(provider.Operation);
    }

    [Fact]
    public async Task ParentBatchJsonIsOneEnvelopeWithChildAndParentIdentity()
    {
        var run = await RunAsync(["parent", "set", "9", "10", "3", "--repo", "owner/repo", "--json"], new FakeProvider());
        Assert.Equal(0, run.Exit);
        using var json = JsonDocument.Parse(run.Output);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetProperty("parentIssueNumber").GetInt32());
        Assert.Equal([9, 10], data.GetProperty("relations").EnumerateArray()
            .Select(item => item.GetProperty("childIssueNumber").GetInt32()));
        Assert.All(data.GetProperty("relations").EnumerateArray(), item =>
            Assert.Equal("changed", item.GetProperty("result").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task ParentBatchHumanOutputIdentifiesEveryChild()
    {
        var run = await RunAsync(["parent", "set", "9", "10", "3", "--repo", "owner/repo"], new FakeProvider());
        Assert.Equal(0, run.Exit);
        Assert.Contains("Issue 9: parent 3: Changed", run.Output);
        Assert.Contains("Issue 10: parent 3: Changed", run.Output);
    }
}

public sealed class LocalRepositoryDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoCheckoutOrNoRemotesReturnsNoContext(bool checkout)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (checkout) await GitAsync(directory, "init", "--quiet");
            Assert.Empty(await LocalRepositoryDiscovery.ReadRemoteUrlsAsync(directory, CancellationToken.None));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    public async Task ReadsLocalFetchAndPushUrlsWithoutNetwork(string remote)
    {
        var directory = Path.Combine(Path.GetTempPath(), "wet-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await GitAsync(directory, "init", "--quiet");
            await GitAsync(directory, "config", "remote.origin.url", remote);
            var urls = await LocalRepositoryDiscovery.ReadRemoteUrlsAsync(directory, CancellationToken.None);
            Assert.Equal([remote, remote], urls);
            Assert.Equal("owner/repo", GitHubRepositoryContext.Discover(urls)?.Repository);
            await GitAsync(directory, "config", "remote.origin.pushurl", "git@github.com:other/repo.git");
            Assert.Null(GitHubRepositoryContext.Discover(
                await LocalRepositoryDiscovery.ReadRemoteUrlsAsync(directory, CancellationToken.None)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task GitAsync(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Assert.True(process.Start());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
        Assert.Equal(0, process.ExitCode);
    }
}
