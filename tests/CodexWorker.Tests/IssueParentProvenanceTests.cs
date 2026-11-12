using System.Text.Json;
using CodexProvisioning;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class IssueParentProvenanceTests
{
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("parent-provenance-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

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
        Assert.Equal("issue-parent-history-timeout", error.ReasonCode);
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
                parent = new { number = 32, repository = new { nameWithOwner = repo } },
                timelineItems = new { totalCount = total, pageInfo = new { hasNextPage = more, endCursor = cursor },
                    nodes = events.Select(item => new { __typename = item.Added ? "ParentIssueAddedEvent" : "ParentIssueRemovedEvent",
                        id = item.Id, createdAt = item.AtUtc, parent = new { number = item.ParentNumber, repository = new { nameWithOwner = repo } } }) }
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
        Assert.Equal(scenario == "errors" ? "issue-parent-history-graphql" : "issue-parent-history-incomplete", error.ReasonCode);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("success", null)]
    [InlineData("environment", "issue-parent-history-environment")]
    [InlineData("start", "issue-parent-history-process-start")]
    [InlineData("authentication", "issue-parent-history-authentication")]
    [InlineData("process", "issue-parent-history-process-failed")]
    [InlineData("graphql", "issue-parent-history-graphql")]
    [InlineData("parse", "issue-parent-history-result-invalid")]
    [InlineData("pagination", "issue-parent-history-incomplete")]
    [InlineData("parent-mismatch", "issue-parent-history-incomplete")]
    [InlineData("history-changed", "issue-parent-changed")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ManagedProcessUsesOwnedEnvironmentAndReportsSafeBoundary(string scenario, string? code)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (code is null)
        {
            var json = await QueryThroughManagedProcessAsync("owner/repo", 34, Assigned.AddSeconds(5), scenario);
            var proof = Assert.IsType<IssueParentProvenance>(IssueParentProvenance.Parse(json));
            Assert.True(proof.IsConsistent(Assigned));
            Assert.Equal(Added, Assert.Single(proof.Events));
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConflictIntentException>(() =>
                QueryThroughManagedProcessAsync("owner/repo", 34, Assigned.AddSeconds(5), scenario));
            Assert.Equal(code, error.ReasonCode);
            Assert.DoesNotContain("secret-sentinel", error.Message, StringComparison.Ordinal);
        }
    }

    // The production constructor builds the environment and launches ProcessRunner. Only the
    // executable is a local deterministic stub; no live gh/authentication/network is involved.
    internal static async Task<string?> QueryThroughManagedProcessAsync(string repository, int number,
        DateTimeOffset started, string scenario = "success")
    {
        using var directory = new TestDirectory();
        var variables = new[] { "PATH", "GH_CONFIG_DIR", "XDG_CONFIG_HOME", "GH_TOKEN", "GITHUB_TOKEN",
            "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN" };
        var previous = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory.Path + ":" + previous["PATH"]);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", Path.Combine(directory.Path, "github"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory.Path);
            foreach (var key in variables.Skip(3)) Environment.SetEnvironmentVariable(key, "secret-sentinel");
            var setup = new NodeGitHubSetup(directory.Path, unrelatedAuthentication: () => false);
            Assert.Equal(ProvisioningCommandStatus.Succeeded, (await setup.ExecuteAsync(
                new(Guid.NewGuid().ToString("N"), "github-cli", ProvisioningCommandAction.PrepareAuthentication), CancellationToken.None)).Status);
            var response = Response([Added], 1, repo: repository, number: number);
            if (scenario == "pagination") response = Response([Added], 2, true, repo: repository, number: number);
            if (scenario == "parent-mismatch") response = response.Replace("\"number\":32", "\"number\":33", StringComparison.Ordinal);
            if (scenario == "history-changed") response = Response([Added,
                new("removed", started.AddMinutes(1), false, 32),
                new("restored", started.AddMinutes(2), true, 32)], 3, repo: repository, number: number);
            var output = scenario switch
            {
                "authentication" => "echo 'HTTP 401 secret-sentinel' >&2; exit 1",
                "process" => "echo 'secret-sentinel' >&2; exit 1",
                "graphql" => "echo '{\"errors\":[{\"message\":\"secret-sentinel\"}],\"data\":null}'; exit 1",
                "parse" => "echo 'secret-sentinel'",
                _ => "cat <<'RESPONSE'\n" + response + "\nRESPONSE"
            };
            var script = """
                #!/bin/sh
                test "$1" = api && test "$2" = graphql || exit 90
                test "$3" = -f && test "$5" = -f && test "$7" = -f && test "$9" = -F || exit 94
                test -f "$GH_CONFIG_DIR/managed-by-codex" || exit 91
                test -z "${GH_TOKEN}${GITHUB_TOKEN}${GH_ENTERPRISE_TOKEN}${GITHUB_ENTERPRISE_TOKEN}" || exit 92
                printf '%s' "$4" > "$GH_CONFIG_DIR/query.txt"
                case "$4" in query=*PARENT_ISSUE_ADDED_EVENT*PARENT_ISSUE_REMOVED_EVENT*TRANSFERRED_EVENT*) ;; *) exit 93;; esac
                """ + "\n" + output + "\n";
            if (scenario == "start") script = "#!/missing-secret-sentinel-interpreter\n";
            var executable = Path.Combine(directory.Path, "gh");
            await File.WriteAllTextAsync(executable, script);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (scenario == "environment") await File.WriteAllTextAsync(Path.Combine(directory.Path, "github", "managed-by-codex"), "invalid-secret-sentinel");
            var client = new GitHubClient(new ProcessRunner(), repository, 5, true, directory.Path);
            var result = await client.GetParentProvenanceAsync(number, started, 32, CancellationToken.None);
            // Inspect the actual argument received by the stub executable, after ProcessRunner
            // argument handling. A canned provider response alone cannot detect a broken query.
            var query = await File.ReadAllTextAsync(Path.Combine(directory.Path, "github", "query.txt"));
            Assert.StartsWith("query=query(", query, StringComparison.Ordinal);
            var delimiters = new Stack<char>();
            foreach (var character in query)
            {
                if (character is '{' or '(' or '[') delimiters.Push(character);
                else if (character is '}' or ')' or ']')
                {
                    Assert.NotEmpty(delimiters);
                    Assert.Equal(character switch { '}' => '{', ')' => '(', _ => '[' }, delimiters.Pop());
                }
            }
            Assert.Empty(delimiters);
            Assert.Contains("parent { number repository { nameWithOwner } }", query, StringComparison.Ordinal);
            Assert.Contains("... on ParentIssueAddedEvent", query, StringComparison.Ordinal);
            Assert.Contains("... on ParentIssueRemovedEvent", query, StringComparison.Ordinal);
            Assert.Contains("__typename", query, StringComparison.Ordinal);
            return result;
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task ProcessTimeoutNeverLeaksCapturedOutput()
    {
        var client = new GitHubClient("owner/repo", (_, _) => throw new ProcessTimeoutException(
            "gh", TimeSpan.FromSeconds(1), "secret-sentinel", "secret-sentinel"));
        var error = await Assert.ThrowsAsync<ConflictIntentException>(() =>
            client.GetParentProvenanceAsync(34, Assigned, 32, CancellationToken.None));
        Assert.Equal("issue-parent-history-timeout", error.ReasonCode);
        Assert.DoesNotContain("secret-sentinel", error.Message, StringComparison.Ordinal);
    }

}
