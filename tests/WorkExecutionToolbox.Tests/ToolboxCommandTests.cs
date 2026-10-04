using System.Text.Json;
using System.Reflection;
using WorkExecutionToolbox.Cli;

namespace WorkExecutionToolbox.Tests;

public sealed partial class ToolboxCommandTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("v")]
    public async Task VersionUsesAssemblyMetadataWithoutRepositoryOrProviderAccess(string argument)
    {
        var provider = new FakeProvider { OnCall = () => throw new InvalidOperationException("Provider accessed") };
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exit = await ToolboxCommand.RunAsync([argument], provider, provider, output, error,
            cancellation.Token, _ => throw new InvalidOperationException("Repository accessed"));
        var metadata = typeof(ToolboxCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Assert.NotNull(metadata);
        Assert.Equal($"wet {metadata.InformationalVersion.Split('+')[0]}", output.ToString().Trim());
        Assert.Equal(0, exit);
        Assert.Empty(error.ToString());
        Assert.Null(provider.Operation);
        using var standaloneOutput = new StringWriter();
        Assert.Equal(0, await ToolboxCommand.RunInformationAsync([argument], standaloneOutput));
        Assert.Equal(output.ToString(), standaloneOutput.ToString());
    }

    [Theory]
    [InlineData("parent set 9 3", "parent", true)]
    [InlineData("parent clear 9", "parent", false)]
    [InlineData("dependency add 9 3 4", "dependency", true)]
    [InlineData("dependency remove 9 3 4", "dependency", false)]
    [InlineData("children 9", "relationships", false)]
    [InlineData("relationships 9", "relationships", false)]
    [InlineData("graph 9", "graph", false)]
    public async Task CommandsRouteScopedHumanNumbers(string words, string expected, bool applied)
    {
        var provider = new FakeProvider();
        var run = await RunAsync(["--repo", "owner/repo", .. words.Split(' '), "--json"], provider);
        Assert.Equal(0, run.Exit);
        Assert.Equal(expected, provider.Operation);
        Assert.Equal(9, provider.Issue?.Number);
        Assert.Equal("owner/repo", provider.Issue?.Repository.Repository);
        if (expected == "parent") Assert.Equal(applied ? 3 : (int?)null, provider.Parent?.ParentIssueNumber);
        if (expected == "dependency")
        {
            Assert.Equal(applied, provider.Dependencies?.Applied);
            Assert.Equal([3, 4], provider.Dependencies?.BlockerIssueNumbers);
        }
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("owner/repo", json.RootElement.GetProperty("repository").GetString());
        Assert.Equal(9, json.RootElement.GetProperty("issue").GetInt32());
        Assert.True(json.RootElement.TryGetProperty("data", out _));
        Assert.Empty(run.Error);
    }

    [Theory]
    [InlineData("parent set 9 3")]
    [InlineData("relationships 9")]
    [InlineData("--repo owner/repo parent set 9 9")]
    [InlineData("--repo owner/repo dependency add 9 3 3")]
    [InlineData("--repo owner/repo dependency add 9 9")]
    [InlineData("--repo owner/repo dependency remove 9")]
    [InlineData("--repo owner/repo children 0")]
    [InlineData("--repo owner/repo graph -1")]
    [InlineData("--repo owner/repo graph 2147483648")]
    [InlineData("--repo owner/repo parent clear 9 3")]
    [InlineData("--repo owner/repo unknown 9")]
    [InlineData("--repo https://github.com/owner/repo graph 9")]
    [InlineData("--repo owner/repo --repo other/repo graph 9")]
    [InlineData("--repo owner/repo --unexpected graph 9")]
    [InlineData("v 9")]
    [InlineData("--version --repo owner/repo")]
    [InlineData("--repo owner/repo --json --json graph 9")]
    [InlineData("--repo owner/repo --refresh --refresh graph 9")]
    [InlineData("--repo owner/repo parent set 9 3 --refresh")]
    public async Task InvalidInputNeverCallsProvider(string arguments)
    {
        var provider = new FakeProvider();
        var run = await RunAsync(arguments.Split(' '), provider);
        Assert.Equal(2, run.Exit);
        Assert.Null(provider.Operation);
        Assert.Empty(run.Output);
        Assert.Contains("wet --help", run.Error);
    }

    [Fact]
    public async Task OversizedDependencyBatchRejectedBeforeProvider()
    {
        var provider = new FakeProvider();
        var numbers = Enumerable.Range(10, 51).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var run = await RunAsync(["dependency", "add", "9", .. numbers, "--repo", "owner/repo"], provider);
        Assert.Equal(2, run.Exit);
        Assert.Null(provider.Operation);
    }

    [Fact]
    public async Task HelpNeedsNeitherRepositoryNorAuthentication()
    {
        var provider = new FakeProvider();
        var run = await RunAsync(["--help"], provider);
        Assert.Equal(0, run.Exit);
        Assert.Contains("dependency add ISSUE BLOCKER", run.Output);
        Assert.Contains("gh auth login", run.Output);
        Assert.Null(provider.Operation);
    }

    [Fact]
    public async Task HumanRelationshipsDistinguishOrganizationAndBlockingAndEscapeControls()
    {
        var run = await RunAsync(["relationships", "9", "--repo", "owner/repo"], new FakeProvider());
        Assert.Contains("Parent: 3", run.Output);
        Assert.Contains("Children: none", run.Output);
        Assert.Contains("Issue 9 is blocked by:", run.Output);
        Assert.Contains("Issue 9 blocks:", run.Output);
        Assert.DoesNotContain('\u001b', run.Output);
    }

    [Fact]
    public async Task HumanGraphShowsDirectionAndInspectionLimits()
    {
        var run = await RunAsync(["graph", "9", "--repo", "owner/repo"], new FakeProvider());
        Assert.Contains("#3 [open] Title", run.Output);
        Assert.Contains("└── #9 [open] Title  (root)", run.Output);
        Assert.Contains("    └── blocked by #3", run.Output);
        Assert.Contains("Depth truncated:", run.Output);
        Assert.DoesNotContain("cycle", run.Output);
    }

    [Fact]
    public async Task JsonRelationshipsRetainNumbersAndStringStates()
    {
        var run = await RunAsync(["--json", "relationships", "9", "--repo", "owner/repo"], new FakeProvider());
        using var json = JsonDocument.Parse(run.Output);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetProperty("blockedBy")[0].GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("open", data.GetProperty("issue").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(RelationshipChangeStatus.Changed, 0)]
    [InlineData(RelationshipChangeStatus.Unchanged, 0)]
    [InlineData(RelationshipChangeStatus.Conflict, 4)]
    [InlineData(RelationshipChangeStatus.Failed, 1)]
    [InlineData(RelationshipChangeStatus.Partial, 5)]
    public async Task MutationStatusesArePreserved(RelationshipChangeStatus status, int expectedExit)
    {
        var provider = new FakeProvider { Status = status };
        var run = await RunAsync(["--json", "parent", "set", "9", "3", "--repo", "owner/repo"], provider);
        Assert.Equal(expectedExit, run.Exit);
        using var json = JsonDocument.Parse(run.Output);
        Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(status.ToString()),
            json.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task PartialDependencyOutputPreservesIndividualOutcomes()
    {
        var provider = new FakeProvider { Status = RelationshipChangeStatus.Partial };
        var run = await RunAsync(["dependency", "remove", "9", "3", "4", "--repo", "owner/repo"], provider);
        Assert.Equal(5, run.Exit);
        Assert.Contains("dependency remove: Issue 9 blocked by Issue 3: Changed", run.Output);
        Assert.Contains("dependency remove: Issue 9 blocked by Issue 4: Failed", run.Output);
        Assert.Contains("Refresh relationships", run.Output);
    }

    [Theory]
    [InlineData("children")]
    [InlineData("relationships")]
    [InlineData("graph")]
    public async Task MissingIssueHasStructuredError(string command)
    {
        var run = await RunAsync([command, "9", "--repo", "owner/repo", "--json"], new FakeProvider { Missing = true });
        Assert.Equal(3, run.Exit);
        Assert.Empty(run.Output);
        using var json = JsonDocument.Parse(run.Error);
        Assert.Equal("missingIssue", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(GitHubIssueFailure.Authorization, "authorization")]
    [InlineData(GitHubIssueFailure.RateLimited, "rateLimited")]
    [InlineData(GitHubIssueFailure.Transport, "transport")]
    public async Task ProviderErrorsAreStructured(GitHubIssueFailure failure, string code)
    {
        var run = await RunAsync(["graph", "9", "--repo", "owner/repo", "--json"],
            new FakeProvider { Failure = new GitHubIssueException(failure, "Safe provider diagnostic.") });
        Assert.Equal(1, run.Exit);
        Assert.Empty(run.Output);
        using var json = JsonDocument.Parse(run.Error);
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        if (failure == GitHubIssueFailure.Authorization) Assert.Contains("gh auth status", run.Error);
    }

    [Fact]
    public async Task UnexpectedExceptionsDoNotExposeRawDiagnostics()
    {
        var run = await RunAsync(["graph", "9", "--repo", "owner/repo"],
            new FakeProvider { Failure = new IOException("sensitive process output") });
        Assert.Equal(1, run.Exit);
        Assert.DoesNotContain("sensitive", run.Error);
    }

    [Fact]
    public async Task CancellationReachesProviderAndHasDistinctExit()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider { OnCall = cancellation.Cancel };
        var run = await RunAsync(["graph", "9", "--repo", "owner/repo"], provider, cancellation.Token);
        Assert.Equal(130, run.Exit);
        Assert.Equal(cancellation.Token, provider.Token);
        Assert.Contains("Refresh relationships", run.Error);
    }

    [Fact]
    public async Task TimeoutDoesNotMasqueradeAsUserCancellation()
    {
        var run = await RunAsync(["graph", "9", "--repo", "owner/repo", "--json"],
            new FakeProvider { Failure = new OperationCanceledException() });
        Assert.Equal(1, run.Exit);
        Assert.Contains("timeout", run.Error);
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(string[] args, FakeProvider provider,
        CancellationToken token = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await ToolboxCommand.RunAsync(args, provider, provider, output, error, token, _ => Task.FromResult<IReadOnlyList<string>>([]));
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class FakeProvider : IIssueRelationshipProvider, IIssueGraphProvider
    {
        public string? Operation { get; private set; }
        public IssueReference? Issue { get; private set; }
        public SetParentRequest? Parent { get; private set; }
        public SetDependenciesRequest? Dependencies { get; private set; }
        public CancellationToken Token { get; private set; }
        public RelationshipChangeStatus Status { get; init; } = RelationshipChangeStatus.Changed;
        public bool Missing { get; init; }
        public Exception? Failure { get; init; }
        public Action? OnCall { get; init; }
        public IssueGraph? Graph { get; init; }

        private void Called(string operation, IssueReference issue, CancellationToken token)
        {
            Operation = operation;
            Issue = issue;
            Token = token;
            OnCall?.Invoke();
            token.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
        }

        private static IssueSummary Summary(IssueReference issue) => new(issue, "Title\u001b", IssueState.Open);
        public Task<IssueRelationships?> GetRelationshipsAsync(IssueReference issue, CancellationToken cancellationToken = default)
        {
            Called("relationships", issue, cancellationToken);
            var blocker = Summary(new(issue.Repository, 3));
            return Task.FromResult(Missing ? null : new IssueRelationships(Summary(issue), blocker, [], [blocker], []));
        }
        public Task<ParentBatchResult> SetParentsAsync(SetParentsRequest request, CancellationToken cancellationToken = default)
        {
            Called("parents", request.Parent, cancellationToken);
            return Task.FromResult(new ParentBatchResult(request.Parent.Number, Status, request.ChildIssueNumbers.Select(n =>
                new ParentChangeResult(n, new RelationshipChangeResult(Status))).ToArray()));
        }
        public Task<RelationshipChangeResult> SetParentAsync(SetParentRequest request, CancellationToken cancellationToken = default)
        {
            Called("parent", request.Child, cancellationToken);
            Parent = request;
            return Task.FromResult(new RelationshipChangeResult(Status, "Safe diagnostic."));
        }
        public Task<DependencyBatchResult> SetDependenciesAsync(SetDependenciesRequest request, CancellationToken cancellationToken = default)
        {
            Called("dependency", request.Issue, cancellationToken);
            Dependencies = request;
            return Task.FromResult(new DependencyBatchResult(Status, request.BlockerIssueNumbers.Select((n, i) =>
                new DependencyChangeResult(n, new(i == 0 ? RelationshipChangeStatus.Changed : RelationshipChangeStatus.Failed))).ToArray()));
        }
        public Task<IssueGraph?> GetGraphAsync(IssueReference root, IssueGraphOptions? options = null, CancellationToken cancellationToken = default)
        {
            Called("graph", root, cancellationToken);
            return Task.FromResult(Missing ? null : Graph ?? new IssueGraph(root, [Summary(root), Summary(new(root.Repository, 3))],
                [new(9, 3, IssueGraphEdgeKind.BlockedBy), new(3, 9, IssueGraphEdgeKind.ParentChild)], true, false));
        }
        public Task<IssueSummary?> GetIssueAsync(IssueReference issue, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IssueSummary>?> GetBlockedByAsync(IssueReference issue, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RelationshipChangeResult> SetDependencyAsync(SetDependencyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
