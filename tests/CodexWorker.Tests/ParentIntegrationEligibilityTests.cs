using System.Text.Json;
using CodexProvisioning;
using CodexServer;
using CodexWorker;

namespace CodexWorker.Tests;

/// <summary>Pull-request mode: a prerequisite delivered into the integration branch an Issue starts from no
/// longer blocks it; every other prerequisite still has to be closed (merged into the base).</summary>
public sealed class ParentIntegrationEligibilityTests
{
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData("pull-request", 31, 30, "codex-done", 30, true)]   // sibling under the same parent
    [InlineData("pull-request", 30, null, "codex-done", 30, true)] // the parent's own sub-issue
    [InlineData("pull-request", 31, 30, "codex-done", 40, false)]  // another parent's sub-issue: wait for the merge
    [InlineData("pull-request", 31, null, "codex-done", null, false)]
    [InlineData("pull-request", 31, 30, "codex-ready", 30, false)] // not delivered yet
    [InlineData(null, 31, 30, "codex-done", 30, false)]            // direct mode: closed is the only rule
    public void DeliveredPrerequisitesOnlyUnblockTheirOwnIntegrationBranch(string? mode, int issue, int? issueParent,
        string label, int? blockerParent, bool expected) =>
        Assert.Equal(expected, ProjectIntegrationMode.DeliveredToSharedBranch(mode, issue, issueParent, [label], blockerParent));

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void ServerEligibilityAppliesTheSharedBranchRuleOnlyInPullRequestMode()
    {
        var now = DateTimeOffset.UnixEpoch;
        var project = new CentralProject("p", "P", "owner/repo", "main", "", [], 1, now, now, IssueReadyLabel: "codex-ready",
            IntegrationMode: ProjectIntegrationMode.PullRequest);
        ManagedGitHubIssue Issue(params GitHubBlockingIssue[] blockers) => new(31, "Child", "", "OPEN", now, now,
            "https://github.com/owner/repo/issues/31", ["codex-ready"], blockers, false, []) { ParentNumber = 30 };
        GitHubBlockingIssue Blocker(int number, string state, int? parent, params string[] labels) =>
            new(number, "Blocker", state, $"https://github.com/owner/repo/issues/{number}") { Labels = labels, ParentNumber = parent };

        Assert.True(ManagedGitHubIssueEligibility.Evaluate(project, Issue(Blocker(32, "open", 30, "codex-done"), Blocker(9, "closed", null))).IsEligible);
        var external = ManagedGitHubIssueEligibility.Evaluate(project, Issue(Blocker(41, "open", 40, "codex-done")));
        Assert.False(external.IsEligible);
        Assert.Contains(external.EligibilityReasons, reason => reason.Contains("#41", StringComparison.Ordinal));
        Assert.False(ManagedGitHubIssueEligibility.Evaluate(project with { IntegrationMode = null },
            Issue(Blocker(32, "open", 30, "codex-done"))).IsEligible);
        // Labels/parent are internal evidence: unread values are never serialized as nulls.
        Assert.DoesNotContain("parentNumber", JsonSerializer.Serialize(Blocker(9, "closed", null) with { Labels = null }, JsonSerializerOptions.Web));
    }

    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    [Theory]
    [InlineData(30, true)]
    [InlineData(40, false)]
    public async Task StandaloneQueueStartsASubIssueOnceItsSiblingIsInTheParentBranch(int blockerParent, bool eligible)
    {
        var commands = new List<string[]>();
        var client = new GitHubClient("owner/repo", (arguments, _) =>
        {
            var args = arguments.ToArray();
            commands.Add(args);
            string output;
            if (args[0] == "issue")
                output = """[{"number":31,"title":"Second part","body":"","createdAt":"2025-01-01T00:00:00Z","labels":[{"name":"codex-ready"}]}]""";
            else if (args.Contains("repos/owner/repo/issues/31/dependencies/blocked_by"))
                output = """[{"number":32,"state":"open","labels":[{"name":"codex-done"}]}]""";
            else
            {
                var number = int.Parse(args.Single(arg => arg.StartsWith("number=", StringComparison.Ordinal))["number=".Length..]);
                var parent = number == 31 ? 30 : blockerParent;
                output = "{\"data\":{\"repository\":{\"issue\":{\"number\":" + number + ",\"parent\":{\"number\":" + parent +
                    ",\"repository\":{\"nameWithOwner\":\"owner/repo\"}}}}}}";
            }
            return Task.FromResult(new ProcessResult(0, output, ""));
        }) { IntegrationBranchDoneLabel = "codex-done" };

        Assert.Equal(eligible ? 31 : null, (await client.FindOldestReadyAsync("codex-ready", CancellationToken.None))?.Number);

        // Without pull-request mode an open prerequisite always blocks, and no parent is read.
        commands.Clear();
        client.IntegrationBranchDoneLabel = null;
        Assert.Null(await client.FindOldestReadyAsync("codex-ready", CancellationToken.None));
        Assert.DoesNotContain(commands, args => args.Contains("graphql"));
    }
}
