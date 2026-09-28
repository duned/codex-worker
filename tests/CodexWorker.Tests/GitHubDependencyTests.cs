using System.Text.Json;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class GitHubDependencyTests
{
    [Fact]
    public async Task ReadyIssueWithoutDependenciesIsEligibleAndQueueReadIsReadOnly()
    {
        var fixture = new Fixture([Issue(1, "2025-01-01T00:00:00Z")]);

        var issue = await fixture.Client.FindOldestReadyAsync("codex-ready", CancellationToken.None);

        Assert.Equal(1, issue?.Number);
        Assert.Contains(fixture.Commands[0], arg => arg.Contains("blockedBy", StringComparison.Ordinal));
        Assert.Contains("--label", fixture.Commands[0]);
        Assert.DoesNotContain(fixture.Commands.SelectMany(args => args), arg => arg is "--add-label" or "--remove-label");
        Assert.Single(fixture.Commands);
    }

    [Fact]
    public async Task ReadyIssueWithAllDependenciesClosedIsEligible()
    {
        var fixture = new Fixture([Issue(2, "2025-01-01T00:00:00Z", "CLOSED", "CLOSED")]);

        var issue = await fixture.Client.FindOldestReadyAsync("codex-ready", CancellationToken.None);

        Assert.Equal(2, issue?.Number);
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("CLOSED", "OPEN")]
    public async Task CandidateWithAnyOpenDependencyIsSkippedForLaterIndependentIssue(params string[] states)
    {
        var fixture = new Fixture([
            Issue(10, "2025-01-01T00:00:00Z", states),
            Issue(11, "2025-01-02T00:00:00Z"),
            Issue(12, "2025-01-03T00:00:00Z", "OPEN")
        ]);

        var issue = await fixture.Client.FindOldestReadyAsync("codex-ready", CancellationToken.None);

        Assert.Equal(11, issue?.Number);
    }

    [Fact]
    public async Task ParentAndSubIssueRelationshipsDoNotCreateDependencies()
    {
        var parentOnly = Issue(20, "2025-01-01T00:00:00Z");
        parentOnly["parent"] = JsonDocument.Parse("{\"number\":5}").RootElement.Clone();
        parentOnly["subIssues"] = JsonDocument.Parse("[{\"number\":21}]").RootElement.Clone();
        var fixture = new Fixture([parentOnly, Issue(21, "2025-01-02T00:00:00Z")]);

        var issue = await fixture.Client.FindOldestReadyAsync("codex-ready", CancellationToken.None);

        Assert.Equal(20, issue?.Number);
    }

    [Fact]
    public async Task MalformedDependencyStateFailsClosedAsInfrastructureFailure()
    {
        var candidate = Issue(30, "2025-01-01T00:00:00Z");
        candidate["blockedBy"] = JsonDocument.Parse("{\"nodes\":[{\"number\":7}],\"totalCount\":1}").RootElement.Clone();
        var fixture = new Fixture([candidate]);

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            fixture.Client.FindOldestReadyAsync("codex-ready", CancellationToken.None));

        Assert.Contains("Could not reliably read ready Issue dependencies", failure.Message);
        Assert.Single(fixture.Commands);
    }

    [Fact]
    public async Task TruncatedDependencyListFailsClosedInsteadOfAssumingReady()
    {
        var candidate = Issue(31, "2025-01-01T00:00:00Z", "CLOSED");
        candidate["blockedBy"] = JsonDocument.Parse("{\"nodes\":[{\"number\":7,\"state\":\"CLOSED\"}],\"totalCount\":51}").RootElement.Clone();
        var fixture = new Fixture([candidate]);

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            fixture.Client.FindOldestReadyAsync("codex-ready", CancellationToken.None));

        Assert.Contains("dependency state is incomplete", failure.Message);
    }

    [Fact]
    public async Task DependencyQueueLookupFailureDoesNotReturnAnEligibleIssue()
    {
        var client = new GitHubClient("owner/repo", (_, _) => Task.FromResult(new ProcessResult(1, "", "permission denied")));

        var failure = await Assert.ThrowsAsync<WorkerInfrastructureException>(() =>
            client.FindOldestReadyAsync("codex-ready", CancellationToken.None));

        Assert.Contains("permission denied", failure.Message);
    }

    [Fact]
    public async Task CancellationDuringDependencyQueueLookupPropagatesSafely()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new GitHubClient("owner/repo", (_, ct) => Task.FromException<ProcessResult>(new OperationCanceledException(ct)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.FindOldestReadyAsync("codex-ready", cancellation.Token));
    }

    private static Dictionary<string, JsonElement> Issue(int number, string createdAt, params string[] dependencyStates)
    {
        var json = JsonSerializer.Serialize(new
        {
            number,
            title = $"Issue {number}",
            body = "",
            createdAt,
            blockedBy = new
            {
                nodes = dependencyStates.Select((state, index) => new { number = index + 1, state }).ToArray(),
                totalCount = dependencyStates.Length
            }
        });
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private sealed class Fixture
    {
        public List<string[]> Commands { get; } = [];
        public GitHubClient Client { get; }

        public Fixture(IEnumerable<Dictionary<string, JsonElement>> issues)
        {
            var output = JsonSerializer.Serialize(issues);
            Client = new GitHubClient("owner/repo", (args, _) =>
            {
                Commands.Add(args.ToArray());
                return Task.FromResult(new ProcessResult(0, output, ""));
            });
        }
    }
}
