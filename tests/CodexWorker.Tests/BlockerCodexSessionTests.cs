using System.Text.Json;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class BlockerCodexSessionTests
{
    [Theory]
    [InlineData("linear", true, "all blockers")]
    [InlineData("retry", false, "own continuation policy")]
    [InlineData("interrupted-recovery", false, "own continuation policy")]
    [InlineData("shared", true, "all blockers")]
    [InlineData("different", false, "IDs differ")]
    [InlineData("missing", false, "lacks a successful")]
    [InlineData("one-missing", false, "lacks a successful")]
    [InlineData("blocker-claude", false, "lacks a successful")]
    [InlineData("failed", false, "lacks a successful")]
    [InlineData("newer-failed", false, "lacks a successful")]
    [InlineData("other-store", false, "lacks a successful")]
    [InlineData("unavailable", false, "lacks a successful")]
    [InlineData("history-parent", false, "lacks a successful")]
    [InlineData("parent", false, "another parent")]
    [InlineData("repository", false, "outside repository")]
    [InlineData("history-repository", false, "lacks a successful")]
    [InlineData("independent", false, "no native blockers")]
    [InlineData("no-parent", false, "no same-repository parent")]
    [InlineData("claude", false, "selected engine")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task OnlyCompatibleAuthoritativeBlockersSupplyAThread(string scenario, bool inherits, string reason)
    {
        var directory = Path.Combine(Path.GetTempPath(), "blocker-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var history = new ExecutionHistoryStore(Path.Combine(directory, "history.db"));
            var thread = Guid.NewGuid().ToString().ToUpperInvariant();
            var entry = LegacyCodexSessionTests.Source() with
            {
                State = scenario == "failed" ? "Failed" : "Completed", Engine = scenario == "blocker-claude" ? "ccc" : "codex",
                CodexThreadId = thread, CodexSessionOwner = scenario == "other-store" ? "remote" : "local",
                ParentIssueNumber = scenario == "history-parent" ? 3 : 2,
                Repository = scenario == "history-repository" ? "other/repo" : "owner/repo"
            };
            if (scenario != "missing") await history.CreateAsync(entry);
            if (scenario == "newer-failed") await history.CreateAsync(entry with
            { ExecutionId = Guid.NewGuid(), AttemptNumber = 2, State = "Failed" });
            var multiple = scenario is "shared" or "different" or "one-missing";
            var secondThread = scenario == "different" ? Guid.NewGuid().ToString() : thread;
            if (multiple && scenario != "one-missing") await history.CreateAsync(entry with
            { ExecutionId = Guid.NewGuid(), IssueNumber = 19, CodexThreadId = secondThread });
            var sessions = Path.Combine(directory, "sessions", "2026", "01", "02");
            Directory.CreateDirectory(sessions);
            if (scenario != "unavailable") await File.WriteAllTextAsync(Path.Combine(sessions, $"rollout-test-{thread}.jsonl"),
                LegacyCodexSessionTests.Header(Guid.Parse(thread), "old-worktree", entry.FeatureBranch, new string('a', 40), entry.StartedAtUtc));
            if (scenario == "different") await File.WriteAllTextAsync(Path.Combine(sessions, $"rollout-test-{secondThread}.jsonl"),
                LegacyCodexSessionTests.Header(Guid.Parse(secondThread), "old-worktree", entry.FeatureBranch, new string('a', 40), entry.StartedAtUtc));
            var github = new GitHubClient("owner/repo", (args, _) =>
            {
                var arguments = args.ToArray();
                if (arguments.Contains("graphql", StringComparer.Ordinal))
                    return Task.FromResult(new ProcessResult(0,
                        "{\"data\":{\"repository\":{\"issue\":{\"number\":" +
                        (arguments.Contains("number=19", StringComparer.Ordinal) ? "19" : "17") +
                        ",\"parent\":{\"number\":" + (scenario == "parent" ? "3" : "2") +
                        ",\"repository\":{\"nameWithOwner\":\"owner/repo\"}}}}}}", ""));
                var blockers = scenario == "independent" ? Array.Empty<int>() : multiple ? [17, 19] : new[] { 17 };
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(blockers.Select(number => new
                { number, repository_url = "https://api.github.com/repos/" + (scenario == "repository" ? "other/repo" : "owner/repo") })), ""));
            });
            var request = new ImplementationRequest("child-worktree", "AGENTS.md",
                new GitHubIssue(18, "Child", "Child's own prompt", DateTimeOffset.UnixEpoch,
                    ParentIssueNumber: scenario == "no-parent" ? null : 2),
                RetryOf: scenario is "retry" or "interrupted-recovery" ? entry with
                { RecoveryState = scenario == "retry" ? "recoverable" : "codex-interrupted" } : null);
            var result = await BlockerCodexSession.SelectAsync(request, "sample", "owner/repo", scenario == "claude" ? "ccc" : "codex",
                history, github, "local", new LegacyCodexSessionStore(directory).ContainsAsync, CancellationToken.None);
            Assert.Equal(inherits ? thread : null, result.Thread);
            Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task DependencyGraphPreservesExactThreadAcrossChainSiblingsAndRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "child-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "history.db");
            var sessions = new LegacyCodexSessionStore(directory);
            var sessionDirectory = Path.Combine(directory, "sessions", "2026", "01", "02");
            Directory.CreateDirectory(sessionDirectory);
            var dependencies = new Dictionary<int, int[]>
            {
                [17] = [], [18] = [17], [19] = [18], // linear chain
                [20] = [17], [21] = [17], // siblings advance the same mutable history
                [22] = [20, 21], // unambiguous join
                [23] = [], [24] = [19, 23], // independent root and ambiguous join
                [2] = [] // parent final has no implicit dependency on its children
            };
            var github = new GitHubClient("owner/repo", (args, _) =>
            {
                var arguments = args.ToArray();
                if (arguments.Contains("graphql", StringComparer.Ordinal))
                {
                    var number = int.Parse(arguments.Single(a => a.StartsWith("number=", StringComparison.Ordinal))[7..],
                        System.Globalization.CultureInfo.InvariantCulture);
                    return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                    { data = new { repository = new { issue = new { number, parent = new
                    { number = 2, repository = new { nameWithOwner = "owner/repo" } } } } } }), ""));
                }
                var issue = int.Parse(arguments.Single(a => a.Contains("/dependencies/blocked_by", StringComparison.Ordinal)).Split('/')[4],
                    System.Globalization.CultureInfo.InvariantCulture);
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(dependencies[issue].Select(number => new
                { number, repository_url = "https://api.github.com/repos/owner/repo" })), ""));
            });
            var threads = new Dictionary<int, string>();
            foreach (var number in dependencies.Keys)
            {
                // Reopen both durable history and host coordination for every child: no in-memory lineage.
                using var history = new ExecutionHistoryStore(database);
                var request = new ImplementationRequest($"worktree-{number}", "AGENTS.md",
                    new GitHubIssue(number, $"Issue {number}", $"Request {number}", DateTimeOffset.UnixEpoch,
                        ParentIssueNumber: number == 2 ? null : 2));
                var selected = await BlockerCodexSession.SelectAsync(request, "sample", "owner/repo", "codex",
                    history, github, "local", sessions.ContainsAsync, CancellationToken.None);
                var expected = number is 18 or 19 or 20 or 21 or 22 ? threads[17] : null;
                Assert.Equal(expected, selected.Thread);
                await new CodexThreadCoordinator().RunAsync(selected.Thread, _ => Task.FromResult(true), async (thread, ct) =>
                {
                    Assert.Equal(expected, thread);
                    var actual = thread ?? Guid.NewGuid().ToString();
                    threads.Add(number, actual);
                    await File.WriteAllTextAsync(Path.Combine(sessionDirectory, $"rollout-test-{actual}.jsonl"),
                        LegacyCodexSessionTests.Header(Guid.Parse(actual), request.ProjectDirectory, $"feature/{number}",
                            new string('a', 40), DateTimeOffset.UnixEpoch) + "\nprivate-transcript-sentinel", ct);
                    await history.CreateAsync(LegacyCodexSessionTests.Source() with
                    {
                        ExecutionId = Guid.NewGuid(), IssueNumber = number, State = "Completed",
                        ParentIssueNumber = request.Issue.ParentIssueNumber, CodexThreadId = actual, CodexSessionOwner = "local",
                        CodexRecovery = null, RecoveryState = null, ImplementationSummary = "Done"
                    }, ct);
                    return new ImplementationResult("success", "Done", [], false, null);
                }, CancellationToken.None);
                Assert.DoesNotContain("private-transcript-sentinel", JsonSerializer.Serialize(await history.ReadAllAsync()), StringComparison.Ordinal);
            }
            Assert.NotEqual(threads[17], threads[23]);
            Assert.NotEqual(threads[17], threads[24]);
            Assert.NotEqual(threads[23], threads[24]);
            Assert.NotEqual(threads[17], threads[2]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("resume", 18)]
    [InlineData("resume", 19)]
    [InlineData("unsupported", 18)]
    [InlineData("missing", 18)]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task InheritedThreadUsesChildPromptProfileAndExplicitWorktree(string mode, int issueNumber)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "inherited-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        try
        {
            var thread = Guid.NewGuid().ToString().ToUpperInvariant();
            var freshThread = Guid.NewGuid().ToString();
            var executable = Path.Combine(directory, "codex");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                case "$*" in *--help*)
                  if [ MODE = unsupported ]; then exit 1; fi
                  printf '%s\n' '--output-schema --json'; exit 0 ;;
                esac
                case "$*" in *'exec resume'*)
                  if [ MODE = missing ]; then printf '%s\n' 'No session found' >&2; exit 1; fi ;;
                esac
                if [ -n "$GH_TOKEN$GITHUB_TOKEN" ]; then exit 91; fi
                actual=FRESH_THREAD
                case "$*" in *'exec resume'*) actual=INHERITED_THREAD ;; esac
                printf '{"type":"thread.started","thread_id":"%s"}\n' "$actual"
                printf '%s\0' "$@" > "$0.args"
                pwd > "$0.cwd"
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output-last-message ]; then shift; output=$1; fi
                  shift
                done
                printf '%s' '{"status":"success","summary":"Done","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}' > "$output"
                """.Replace("MODE", mode, StringComparison.Ordinal)
                    .Replace("FRESH_THREAD", freshThread, StringComparison.Ordinal)
                    .Replace("INHERITED_THREAD", thread, StringComparison.Ordinal));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            var workspace = Path.Combine(directory, "child-worktree");
            Directory.CreateDirectory(workspace);
            var instructions = Path.Combine(directory, "AGENTS.md");
            await File.WriteAllTextAsync(instructions, "Project instructions");
            var observed = new List<string>();
            var executor = new CodexExecutor(new ProcessRunner(), new CodexSettings { Model = "child-model", ReasoningEffort = "high" },
                new Dictionary<string, string> { ["GH_TOKEN"] = "credential-sentinel", ["GITHUB_TOKEN"] = "credential-sentinel" });
            var engine = new CodexImplementationEngine(executor.WithSessionObserver(id =>
            {
                observed.Add(id);
                return Task.CompletedTask;
            }));
            var diagnostics = new List<string>();
            var outcome = await engine.RunAsync(new ImplementationRequest(workspace, instructions,
                new GitHubIssue(issueNumber, $"Child {issueNumber}", $"Authoritative request {issueNumber}", DateTimeOffset.UnixEpoch),
                InheritedCodexThreadId: thread, CodexSessionDiagnostic: diagnostics.Add), CancellationToken.None);
            Assert.Equal("success", outcome.Status);
            Assert.Equal(mode == "resume" ? thread : freshThread, Assert.Single(observed));
            var args = (await File.ReadAllTextAsync(executable + ".args")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (mode == "resume")
            {
                Assert.Equal("--cd", args[0]);
                Assert.Equal(workspace, args[1]);
                Assert.Equal("--approve-for-me", args[2]);
                Assert.Equal("exec", args[3]);
                Assert.Equal("resume", args[4]);
                Assert.Equal(thread, args[^2]);
                Assert.Empty(diagnostics);
            }
            else
            {
                Assert.DoesNotContain("resume", args);
                Assert.DoesNotContain(thread, args);
                Assert.Contains("starting a fresh session", Assert.Single(diagnostics), StringComparison.Ordinal);
            }
            Assert.DoesNotContain("credential-sentinel", string.Join(" ", args), StringComparison.Ordinal);
            Assert.Contains($"Authoritative request {issueNumber}", args[^1], StringComparison.Ordinal);
            Assert.Contains($"Child {issueNumber}", args[^1], StringComparison.Ordinal);
            Assert.Contains("child-model", args);
            Assert.Contains("model_reasoning_effort=\"high\"", args);
            Assert.Contains("--approve-for-me", args);
            Assert.Equal(workspace, (await File.ReadAllTextAsync(executable + ".cwd")).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previous);
            Directory.Delete(directory, true);
        }
    }
}
