using System.Text.Json;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class BlockerCodexSessionTests
{
    [Theory]
    [InlineData("linear", true, "all blockers")]
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
                    ParentIssueNumber: scenario == "no-parent" ? null : 2));
            var result = await BlockerCodexSession.SelectAsync(request, "sample", "owner/repo", scenario == "claude" ? "ccc" : "codex",
                history, github, "local", new LegacyCodexSessionStore(directory).ContainsAsync, CancellationToken.None);
            Assert.Equal(inherits ? thread : null, result.Thread);
            Assert.Contains(reason, result.Reason, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("unsupported")]
    [InlineData("missing")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task InheritedThreadUsesChildPromptProfileAndExplicitWorktree(string mode)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "inherited-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        try
        {
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
                printf '%s\0' "$@" > "$0.args"
                pwd > "$0.cwd"
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --output-last-message ]; then shift; output=$1; fi
                  shift
                done
                printf '%s' '{"status":"success","summary":"Done","testsOrValidationPerformed":[],"needsHumanInput":false,"question":null,"blockerType":null}' > "$output"
                """.Replace("MODE", mode, StringComparison.Ordinal));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            var workspace = Path.Combine(directory, "child-worktree");
            Directory.CreateDirectory(workspace);
            var instructions = Path.Combine(directory, "AGENTS.md");
            await File.WriteAllTextAsync(instructions, "Project instructions");
            var thread = Guid.NewGuid().ToString().ToUpperInvariant();
            var executor = new CodexExecutor(new ProcessRunner(), new CodexSettings { Model = "child-model", ReasoningEffort = "high" });
            var engine = new CodexImplementationEngine(executor);
            var diagnostics = new List<string>();
            await engine.RunAsync(new ImplementationRequest(workspace, instructions,
                new GitHubIssue(18, "Child title", "Child's authoritative request", DateTimeOffset.UnixEpoch),
                InheritedCodexThreadId: thread, CodexSessionDiagnostic: diagnostics.Add), CancellationToken.None);
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
            Assert.Contains("Child's authoritative request", args[^1], StringComparison.Ordinal);
            Assert.Contains("Child title", args[^1], StringComparison.Ordinal);
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
