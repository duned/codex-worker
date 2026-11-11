using System.Text.Json;
using CodexProvisioning;

namespace CodexWorker.Tests;

public sealed class IssueParentProvenanceTests
{
    private static readonly DateTimeOffset Assigned = DateTimeOffset.Parse("2026-11-10T16:47:10Z");
    private static IssueParentProvenance Proof(params IssueParentEvent[] events) => new("owner/repo", 34, "issue-node",
        Assigned.AddDays(-1), Assigned.AddSeconds(5), 32, events);
    private static IssueParentEvent Added => new("added", DateTimeOffset.Parse("2026-11-10T16:46:19Z"), true, 32);

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void NativeEventBeforeAssignmentProvesOriginalParentWithoutChangingHistory()
    {
        var proof = Proof(Added);
        Assert.True(proof.IsConsistent(Assigned));
        Assert.True(proof.IsConsistent(proof.ExecutionStartedAtUtc));
        Assert.False(proof.IsConsistent(Added.AtUtc));
        Assert.False(proof.IsConsistent(Added.AtUtc.AddMilliseconds(500)));
        Assert.Equal(JsonSerializer.Serialize(proof), JsonSerializer.Serialize(IssueParentProvenance.Parse(JsonSerializer.Serialize(proof))));
    }

    [Theory]
    [InlineData("removed", "issue-parent-changed")]
    [InlineData("reparented", "issue-parent-changed")]
    [InlineData("restored", "issue-parent-changed")]
    [InlineData("late-add", "issue-original-parent-absent")]
    [InlineData("gap", "issue-parent-history-incomplete")]
    [InlineData("duplicate", "issue-parent-history-incomplete")]
    [InlineData("equal-time", "issue-parent-history-incomplete")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void AmbiguousOrChangedHistoryCannotAuthorizeReconciliation(string scenario, string expected)
    {
        var removed = new IssueParentEvent("removed", Assigned.AddMinutes(1), false, 32);
        var events = scenario switch
        {
            "removed" => new[] { Added, removed },
            "reparented" => [Added, removed, new("new-parent", Assigned.AddMinutes(2), true, 33)],
            "restored" => [Added, removed, new("restored", Assigned.AddMinutes(2), true, 32)],
            "late-add" => [Added with { AtUtc = Assigned.AddMinutes(1) }],
            "gap" => [removed],
            "duplicate" => [Added, removed with { Id = Added.Id }],
            _ => [Added, removed with { AtUtc = Added.AtUtc }]
        };
        var proof = Proof(events) with { ParentNumber = scenario == "removed" ? null : scenario == "reparented" ? 33 : 32 };
        Assert.Equal(expected, proof.Refusal(Assigned));
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void CompleteEmptyHistoryProvesAbsenceRatherThanInventingAParent()
    {
        var proof = Proof() with { ParentNumber = null };
        Assert.True(proof.IsConsistent(Assigned));
        Assert.Equal("issue-parent-history-incomplete", Proof().Refusal(Assigned));
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CallerCancellationPropagatesWhileProviderDeadlineIsUnavailableHistory()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new GitHubClient("owner/repo", (_, _) => throw new OperationCanceledException());
        var error = await Assert.ThrowsAsync<ConflictIntentException>(() =>
            client.GetParentProvenanceAsync(34, Assigned, 32, cancellation.Token));
        Assert.Equal("issue-parent-history-unavailable", error.ReasonCode);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetParentProvenanceAsync(34, Assigned, 32, cancellation.Token));
    }

    private static string Response(IssueParentEvent[] events, int total, bool more = false, string? cursor = null,
        string repo = "owner/repo", int number = 34, string node = "issue-node") => JsonSerializer.Serialize(new
        {
            data = new { repository = new { nameWithOwner = repo, issue = new
            {
                id = node, number, createdAt = Assigned.AddDays(-1),
                parent = new { number = 32, repository = new { nameWithOwner = "owner/repo" } },
                timelineItems = new { totalCount = total, pageInfo = new { hasNextPage = more, endCursor = cursor },
                    nodes = events.Select(item => new { __typename = item.Added ? "ParentIssueAddedEvent" : "ParentIssueRemovedEvent",
                        id = item.Id, createdAt = item.AtUtc, parent = new { number = item.ParentNumber, repository = new { nameWithOwner = "owner/repo" } } }) }
            } } }
        });

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task CompletePaginationIncludesRemovalAndReparentEventsBeforeAssignment()
    {
        var events = new[] { Added with { AtUtc = Assigned.AddMinutes(-3) },
            new("removed", Assigned.AddMinutes(-2), false, 32), Added with { Id = "added-again" } };
        var calls = 0;
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            var page = calls++;
            if (page == 1) Assert.Contains("after=cursor", args);
            return Task.FromResult(new ProcessResult(0, page == 0 ? Response(events[..2], 3, true, "cursor") : Response(events[2..], 3), ""));
        });
        var json = await client.GetParentProvenanceAsync(34, Assigned.AddSeconds(5), 32, CancellationToken.None);
        var proof = Assert.IsType<IssueParentProvenance>(IssueParentProvenance.Parse(json));
        Assert.True(proof.IsConsistent(Assigned));
        Assert.Equal(3, proof.Events.Length);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("repository")]
    [InlineData("issue")]
    [InlineData("cursor")]
    [InlineData("null-parent")]
    [InlineData("transfer")]
    [InlineData("errors")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task IncompleteOrUnavailableProviderHistoryIsNeverProof(string scenario)
    {
        var response = Response([Added], scenario == "count" ? 2 : 1, scenario == "cursor", null,
            scenario == "repository" ? "other/repo" : "owner/repo", scenario == "issue" ? 35 : 34);
        if (scenario == "null-parent") response = response.Replace("\"parent\":{\"number\":32,\"repository\":{\"nameWithOwner\":\"owner/repo\"}}", "\"parent\":null", StringComparison.Ordinal);
        if (scenario == "transfer") response = response.Replace("ParentIssueAddedEvent", "TransferredEvent", StringComparison.Ordinal);
        if (scenario == "errors") response = "{\"errors\":[{\"message\":\"private provider output\"}],\"data\":null}";
        var client = new GitHubClient("owner/repo", (_, _) => Task.FromResult(new ProcessResult(0, response, "")));
        var error = await Assert.ThrowsAsync<ConflictIntentException>(() => client.GetParentProvenanceAsync(34, Assigned.AddSeconds(5), 32, CancellationToken.None));
        Assert.Equal(scenario == "errors" ? "issue-parent-history-unavailable" : "issue-parent-history-incomplete", error.ReasonCode);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }
}
