namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;

[Trait("TestType", "Unit")]
[Trait("Duration", "Fast")]
public sealed class ServerArchivedBranchTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly CentralProject Project = new("p1", "demo", "team/demo", "main", "", [], 3, Now, Now);
    private static readonly string Old = new('a', 40), Fresh = new('b', 40), Unmerged = new('c', 40);

    /// <summary>A GitHub stand-in: refs by name, and per tip whether it is contained in main and when it was committed.</summary>
    private sealed class FakeGitHub
    {
        public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal)
        {
            ["done/feature/1-old"] = Old, ["done/feature/2-fresh"] = Fresh, ["completed/3-unmerged"] = Unmerged, ["done/featurette"] = Old,
        };
        public Dictionary<string, (string Status, DateTimeOffset Date)> Commits { get; } = new(StringComparer.Ordinal)
        {
            [Old] = ("behind", Now.AddDays(-60)), [Fresh] = ("identical", Now.AddDays(-2)), [Unmerged] = ("diverged", Now.AddDays(-90)),
        };
        public List<string> Deleted { get; } = [];
        public string? DeleteError { get; set; }
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GitHubReadCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
        {
            lock (Calls) Calls.Add(arguments);
            var path = arguments.First(a => a.StartsWith("repos/", StringComparison.Ordinal));
            if (path.Contains("/git/matching-refs/heads/", StringComparison.Ordinal))
            {
                var prefix = path.Split("/git/matching-refs/heads/")[1].Split('?')[0];
                return Ok(string.Join('\n', Refs.Where(r => r.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(r => $"refs/heads/{r.Key}\tcommit\t{r.Value}")));
            }
            if (path.Contains("/compare/main...", StringComparison.Ordinal))
            {
                var tip = path.Split("...")[1].Split('?')[0];
                return Commits.TryGetValue(tip, out var commit)
                    ? Ok($"{commit.Status}\t{(commit.Status == "diverged" ? new string('f', 40) : tip)}\t{commit.Date:O}")
                    : Task.FromResult(new GitHubReadCommandResult(1, "", "gh: Not Found (HTTP 404)"));
            }
            var branch = Uri.UnescapeDataString(path.Split("/heads/", 2)[1]);
            if (arguments.Contains("DELETE"))
            {
                if (DeleteError is not null) return Task.FromResult(new GitHubReadCommandResult(1, "", DeleteError));
                Deleted.Add(branch);
                Refs.Remove(branch);
                return Ok("");
            }
            return Refs.TryGetValue(branch, out var sha) ? Ok($"refs/heads/{branch}\tcommit\t{sha}")
                : Task.FromResult(new GitHubReadCommandResult(1, "", "gh: Not Found (HTTP 404)"));
        }

        private static Task<GitHubReadCommandResult> Ok(string output) => Task.FromResult(new GitHubReadCommandResult(0, output, ""));
    }

    private static ServerArchivedBranchService Service(FakeGitHub github) => new(github.RunAsync, new Clock(Now));

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task InventoryOffersOnlyOldBranchesWhoseTipIsAlreadyInTheBase()
    {
        var github = new FakeGitHub();
        var page = await Service(github).ListAsync(Project, 30, 0, CancellationToken.None);

        Assert.Equal(3, page.Total);
        Assert.Equal(["completed/3-unmerged", "done/feature/1-old", "done/feature/2-fresh"], page.Branches.Select(b => b.Branch));
        var byName = page.Branches.ToDictionary(b => b.Branch);
        Assert.Equal(("eligible", "integrated"), (byName["done/feature/1-old"].Decision, byName["done/feature/1-old"].Reason));
        Assert.Equal(Now.AddDays(-60), byName["done/feature/1-old"].CommittedAtUtc);
        Assert.Equal("recent", byName["done/feature/2-fresh"].Decision);
        Assert.Equal(("not-integrated", "not-in-base"), (byName["completed/3-unmerged"].Decision, byName["completed/3-unmerged"].Reason));
        Assert.DoesNotContain(github.Calls, call => call.Contains("DELETE"));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task InventoryPagesAreStableAndBounded()
    {
        var github = new FakeGitHub();
        for (var i = 0; i < 120; i++) github.Refs[$"done/feature/x-{i:D3}"] = Old;
        var service = Service(github);
        var first = await service.ListAsync(Project, 30, 0, CancellationToken.None);
        var second = await service.ListAsync(Project, 30, first.Branches.Count, CancellationToken.None);

        Assert.Equal(123, first.Total);
        Assert.Equal(ServerArchivedBranchService.PageSize, first.Branches.Count);
        Assert.Equal(23, second.Branches.Count);
        Assert.Empty(first.Branches.Select(b => b.Branch).Intersect(second.Branches.Select(b => b.Branch)));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task UnknownCommitsAreKeptRatherThanGuessed()
    {
        var github = new FakeGitHub();
        github.Commits.Remove(Old);
        var page = await Service(github).ListAsync(Project, 30, 0, CancellationToken.None);
        Assert.Equal(("unknown", "commit-not-found"), page.Branches.Where(b => b.Branch == "done/feature/1-old").Select(b => (b.Decision, b.Reason)).Single());
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task DeletionRechecksEveryBranchAndDeletesOnlyProvenOnes()
    {
        var github = new FakeGitHub();
        github.Refs["done/feature/4-moved"] = Old;
        var result = await Service(github).DeleteAsync(Project, new(30,
        [
            new("done/feature/1-old", Old),
            new("done/feature/2-fresh", Fresh),
            new("completed/3-unmerged", Unmerged),
            new("done/feature/4-moved", Fresh),
            new("done/feature/5-gone", Old),
        ]), CancellationToken.None);

        Assert.Equal(["done/feature/1-old"], github.Deleted);
        Assert.Equal([("deleted", "deleted"), ("retained", "newer-than-threshold"), ("retained", "not-in-base"), ("changed", "tip-changed"), ("missing", "branch-missing")],
            result.Branches.Select(b => (b.Outcome, b.Reason)));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task ARefusedDeletionStopsTheRest()
    {
        var github = new FakeGitHub { DeleteError = "gh: Resource not accessible by integration (HTTP 403)" };
        github.Refs["done/feature/6-old"] = Old;
        var result = await Service(github).DeleteAsync(Project, new(30, [new("done/feature/1-old", Old), new("done/feature/6-old", Old)]), CancellationToken.None);

        Assert.Empty(github.Deleted);
        Assert.Equal([("failed", "permission"), ("not-attempted", "stopped-after-failure")], result.Branches.Select(b => (b.Outcome, b.Reason)));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("main")]
    [InlineData("feature/1-active")]
    [InlineData("done/feature/")]
    [InlineData("done/feature/../main")]
    [InlineData("done/feature/a%2Fb")]
    public async Task OnlyArchivedBranchNamesCanBeSelected(string branch)
    {
        var github = new FakeGitHub();
        Assert.NotNull(ServerArchivedBranchService.RequestError(new(30, [new(branch, Old)])));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(github).DeleteAsync(Project, new(30, [new(branch, Old)]), CancellationToken.None));
        Assert.Empty(github.Calls);
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void RequestsAreBounded()
    {
        Assert.NotNull(ServerArchivedBranchService.RequestError(0, 0));
        Assert.NotNull(ServerArchivedBranchService.RequestError(30, -1));
        Assert.NotNull(ServerArchivedBranchService.RequestError(new(30, [])));
        Assert.NotNull(ServerArchivedBranchService.RequestError(new(30, [new("done/feature/1-old", "zz")])));
        Assert.NotNull(ServerArchivedBranchService.RequestError(new(30, [new("done/feature/1-old", Old), new("done/feature/1-old", Old)])));
        Assert.NotNull(ServerArchivedBranchService.RequestError(new(30, Enumerable.Range(0, 101).Select(i => new ArchivedBranchSelection($"done/feature/{i}", Old)).ToArray())));
        Assert.Null(ServerArchivedBranchService.RequestError(new(30, [new("done/feature/1-old", Old)])));
    }
}

[Collection("ServerTokenEnvironment")]
[Trait("TestType", "Integration")]
[Trait("Duration", "Fast")]
public sealed class ServerArchivedBranchApiTests
{
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task EndpointsRequireManagementAndRejectInvalidRequestsBeforeReachingGitHub()
    {
        var temporary = Directory.CreateTempSubdirectory("archived-branches-");
        var prior = Environment.GetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN");
        Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", "archived-branch-test");
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var url = $"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}";
            listener.Stop();
            await using var app = await ServerApplication.BuildAsync([$"--Server:ListenUrl={url}",
                $"--Server:DatabasePath={Path.Combine(temporary.FullName, "server.db")}", "--Logging:LogLevel:Default=Warning"]);
            await app.StartAsync();
            var registry = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IRegistryStore>(app.Services);
            var project = await registry.CreateProjectAsync(new("demo", "team/demo", "main", "", []));
            var path = $"/api/v1/projects/{project.Id}/archived-branches";
            using var anonymous = new HttpClient { BaseAddress = new(url) };
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);

            using var client = new HttpClient { BaseAddress = new(url) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", "archived-branch-test");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/projects/missing/archived-branches")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync(path + "?olderThanDays=0")).StatusCode);
            var invalid = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, path + "/delete",
                new { olderThanDays = 30, branches = new[] { new { branch = "main", expectedTip = new string('a', 40) } } });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_MANAGEMENT_TOKEN", prior);
            temporary.Delete(recursive: true);
        }
    }
}
