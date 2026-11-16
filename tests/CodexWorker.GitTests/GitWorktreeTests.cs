using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed partial class GitWorktreeTests(GitWorktreeTests.RepositorySeed seed) : IClassFixture<GitWorktreeTests.RepositorySeed>
{
    private sealed class SemanticCodex(RepositoryFixture fixture, bool obsoleteExpectation, bool exhausted,
        bool advanceDuringRepair) : ICodexExecutor
    {
        public bool Exhausted { get; set; } = exhausted;
        public int ImplementationCalls { get; private set; }
        public Dictionary<int, string> StartingHeads { get; } = [];
        public List<IntegrationRepairContext> RepairContexts { get; } = [];
        public string? AdvancedHead { get; private set; }
        public ICodexExecutor WithProfile(CodexExecutionProfile profile)
        {
            Assert.Equal("configured-model", profile.Model);
            Assert.Equal("high", profile.Effort);
            return this;
        }
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            ImplementationCalls++;
            StartingHeads[issue.Number] = await fixture.GitAt(projectDirectory, "rev-parse", "HEAD");
            if (issue.Number == 17)
            {
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "provider.txt"), "Installed", ct);
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "status-implementation.txt"), obsoleteExpectation ? "contract" : "readiness", ct);
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "expected-status.txt"), obsoleteExpectation ? "Missing" : "specification", ct);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "status-contract.txt"), "installation", ct);
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "unrelated.txt"), "B's unrelated work", ct);
            }
            return new CodexOutcome("success", "Implemented provider/status separation", [], false, null);
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("Initial validation passed; implementation repair is inappropriate.");
        public async Task<CodexOutcome> RepairIntegrationAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            IntegrationRepairContext context, string? implementationSummary, IReadOnlyList<string> validationCommands,
            int attempt, int maximumAttempts, CancellationToken ct)
        {
            RepairContexts.Add(context);
            Assert.Equal(17, issue.Number);
            Assert.Equal("Implement this request", issue.Body);
            Assert.Equal("Implemented provider/status separation", implementationSummary);
            Assert.Equal("verify status contract", Assert.Single(validationCommands));
            Assert.Equal(2, maximumAttempts);
            Assert.Equal(context.RebasedCommit, await fixture.GitAt(projectDirectory, "rev-parse", "HEAD"));
            Assert.NotEqual(fixture.Checkout, projectDirectory);
            Assert.Equal("installation", await File.ReadAllTextAsync(Path.Combine(projectDirectory, "status-contract.txt"), ct));
            if (Exhausted)
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "repair-note.txt"), $"preserved repair attempt {attempt}", ct);
            else if (obsoleteExpectation)
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "expected-status.txt"), "Installed", ct);
            else
                await File.WriteAllTextAsync(Path.Combine(projectDirectory, "status-implementation.txt"), "installation", ct);
            if (advanceDuringRepair)
            {
                // Advance only origin using another checkout, as a remote Worker would. The
                // canonical main remains unchanged until the integration refresh observes it.
                var peer = Path.Combine(fixture.Checkout, "../peer");
                await fixture.Git("clone", Path.Combine(fixture.Checkout, "../origin.git"), peer);
                await fixture.GitAt(peer, "switch", "main");
                await fixture.GitAt(peer, "config", "user.name", "Peer Worker");
                await fixture.GitAt(peer, "config", "user.email", "peer@example.invalid");
                await File.WriteAllTextAsync(Path.Combine(peer, "during-repair.txt"), "new concurrent work", ct);
                await fixture.GitAt(peer, "add", "during-repair.txt");
                await fixture.GitAt(peer, "commit", "-m", "concurrent update during repair");
                await fixture.GitAt(peer, "push", "origin", "main");
                AdvancedHead = await fixture.GitAt(peer, "rev-parse", "HEAD");
            }
            return new CodexOutcome("success", Exhausted ? "Could not yet reconcile status" : "Reconciled installation status with the current contract", [], false, null);
        }
    }

    private sealed class SemanticValidation(RepositoryFixture fixture, bool obsoleteExpectation) : IValidationRunner
    {
        public TaskCompletionSource InitialPassed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowIntegration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Failures { get; private set; }
        public int PassedAfterRebase { get; private set; }
        private bool _initial = true;
        public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            if (!File.Exists(Path.Combine(directory, "provider.txt"))) return ValidationResult.Success;
            var contractPath = Path.Combine(directory, "status-contract.txt");
            var contract = File.Exists(contractPath) ? await File.ReadAllTextAsync(contractPath, ct) : "readiness";
            var implementation = await File.ReadAllTextAsync(Path.Combine(directory, "status-implementation.txt"), ct);
            var installed = await File.ReadAllTextAsync(Path.Combine(directory, "provider.txt"), ct);
            var actual = (implementation == "contract" ? contract : implementation) == "installation" ? installed : "Missing";
            var expected = obsoleteExpectation ? await File.ReadAllTextAsync(Path.Combine(directory, "expected-status.txt"), ct) :
                contract == "installation" ? installed : "Missing";
            if (actual != expected)
            {
                Failures++;
                var notePath = Path.Combine(directory, "repair-note.txt");
                var remaining = File.Exists(notePath) ? $"Remaining incompatibility after: {await File.ReadAllTextAsync(notePath, ct)}" : "";
                return new ValidationResult(new ValidationFailure(1, Assert.Single(commands), 1,
                    $"Status separates installation from readiness\nExpected: {expected}\nActual: {actual}", remaining, false));
            }
            if (_initial)
            {
                _initial = false;
                Assert.False(File.Exists(contractPath));
                InitialPassed.SetResult();
                await AllowIntegration.Task.WaitAsync(ct);
            }
            else
            {
                Assert.Contains("installation", await File.ReadAllTextAsync(contractPath, ct));
                Assert.Equal(await fixture.GitAt(directory, "rev-parse", "HEAD"), await fixture.GitAt(directory, "rev-parse", "refs/heads/feature/example-task-17"));
                PassedAfterRebase++;
            }
            return ValidationResult.Success;
        }
    }

    private sealed class ImmediateValidation : IValidationRunner
    {
        public Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct) =>
            Task.FromResult(ValidationResult.Success);
    }

    private sealed class ShutdownCodex : ICodexExecutor
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "partial.txt"), "useful partial implementation", ct);
            Started.SetResult(projectDirectory);
            await new ProcessRunner().RunAsync("/bin/sh", ["-c", "touch child-started; sleep 60"], projectDirectory, cancellationToken: ct);
            throw new InvalidOperationException("The controlled child should be interrupted.");
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class ConcurrentGitHub(GitHubIssue template) : IGitHubClient
    {
        public IReadOnlyList<int> Blockers { get; set; } = [];
        public Task<IReadOnlyList<int>?> GetBlockerIssueNumbersAsync(int issueNumber, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>?>(Blockers);
        public int? ParentNumber { get; set; } = template.ParentIssueNumber;
        public string? ParentProvenance { get; set; }
        public Task<int?> GetParentIssueNumberAsync(int issueNumber, CancellationToken ct) => Task.FromResult(ParentNumber);
        public Task<string?> GetParentProvenanceAsync(int issueNumber, DateTimeOffset startedAt, int? currentParent, CancellationToken ct) => Task.FromResult(ParentProvenance);
        public string? ChangedBody { get; set; }
        public Task<GitHubIssue?> GetIssueAsync(int issueNumber, CancellationToken ct) =>
            Task.FromResult<GitHubIssue?>(template with { Number = issueNumber, Body = ChangedBody ?? template.Body });
        public Task<string> GetIssueCommentContextAsync(int issueNumber, CancellationToken cancellationToken,
            IReadOnlyList<string>? secretValues = null) => Task.FromResult("");
        private int _number = 16;
        public List<(int Issue, string Label)> Labels { get; } = [];
        public List<(int Issue, string Body)> Comments { get; } = [];
        public GitHubIssue? RecoveryIssue { get; set; }
        public GitHubIssue? AutomaticConflictIssue { get; set; }
        public Task<GitHubIssue?> FindOldestReadyAsync(string label, CancellationToken cancellationToken)
        {
            if (label == "codex-integration-recovery" && RecoveryIssue is { } recovery)
            {
                RecoveryIssue = null;
                return Task.FromResult<GitHubIssue?>(recovery);
            }
            if (label == "codex-integration-conflict" && AutomaticConflictIssue is { } conflict)
            {
                AutomaticConflictIssue = null;
                return Task.FromResult<GitHubIssue?>(conflict with { Labels = [label] });
            }
            return Task.FromResult<GitHubIssue?>(label == "ready" ? template with { Number = ++_number } : null);
        }
        public Task<GitHubIssue?> GetCompletionIssueAsync(int issueNumber, CancellationToken ct) =>
            Task.FromResult<GitHubIssue?>(template with { Number = issueNumber });
        public Task<GitHubIssueState> ReadIssueStateAsync(int issueNumber, CancellationToken ct) =>
            Task.FromResult(new GitHubIssueState(true, Labels.Where(item => item.Issue == issueNumber).TakeLast(1).Select(item => item.Label).ToArray()));
        public Task EnsureSuccessCommentAsync(int issueNumber, Guid executionId, string comment, CancellationToken ct, bool allowCreate = true) =>
            Comments.Any(item => item.Issue == issueNumber && item.Body == comment) ? Task.CompletedTask : CommentAsync(issueNumber, comment, ct);
        public Task ReplaceLabelAsync(int issueNumber, string remove, string add, CancellationToken ct)
        { Labels.Add((issueNumber, add)); return Task.CompletedTask; }
        public Task CommentAsync(int issueNumber, string comment, CancellationToken ct)
        { Comments.Add((issueNumber, comment)); return Task.CompletedTask; }
        public Task CloseAsync(int issueNumber, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedRecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FileCodex : ICodexExecutor
    {
        public string? LastDirectory { get; private set; }
        public Task PreflightAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<CodexOutcome> RunAsync(string projectDirectory, string instructionsFile, GitHubIssue issue, CancellationToken ct)
        {
            LastDirectory = projectDirectory;
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, $"issue-{issue.Number}.txt"), "implementation", ct);
            return new CodexOutcome("success", "Implemented", [], false, null);
        }
        public Task<CodexOutcome> RepairAsync(string projectDirectory, string instructionsFile, GitHubIssue issue,
            ValidationFailure failure, int attempt, int maximumAttempts, CancellationToken ct) =>
            throw new InvalidOperationException("Post-rebase validation must not run implementation repair.");
    }

    private sealed class RebaseValidation(RepositoryFixture fixture, bool retrySucceeds) : IValidationRunner
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource IndependentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIndependent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> RebasedHeads { get; } = [];
        public List<string> RebasedParents { get; } = [];
        public string? FirstIntegratedHead { get; private set; }
        private int _secondCalls;
        public async Task<ValidationResult> RunAsync(IEnumerable<string> commands, string directory, CancellationToken ct)
        {
            var branch = await fixture.GitAt(directory, "branch", "--show-current");
            if (branch.EndsWith("-17", StringComparison.Ordinal))
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(ct);
            }
            else if (branch.EndsWith("-19", StringComparison.Ordinal))
            {
                IndependentStarted.TrySetResult();
                await ReleaseIndependent.Task.WaitAsync(ct);
            }
            else if (branch.EndsWith("-18", StringComparison.Ordinal))
            {
                if (++_secondCalls == 1)
                {
                    SecondStarted.TrySetResult();
                    await ReleaseSecond.Task.WaitAsync(ct);
                    FirstIntegratedHead = await fixture.Git("rev-parse", "main");
                    return ValidationResult.Success;
                }
                RebasedHeads.Add(await fixture.GitAt(directory, "rev-parse", "HEAD"));
                RebasedParents.Add(await fixture.GitAt(directory, "rev-parse", "HEAD^"));
                if (RebasedHeads.Count == 1 || !retrySucceeds)
                    return new ValidationResult(new ValidationFailure(1, "local-check", 1, "", $"failed test: attempt {RebasedHeads.Count}", false));
            }
            return ValidationResult.Success;
        }
    }

    // Initialize Git once, then copy the immutable seed. Each test still owns a
    // separate checkout, object database, remote and worktree registry.
    public sealed class RepositorySeed : IAsyncLifetime
    {
        private RepositoryFixture? _template;

        public async Task InitializeAsync() => _template = await RepositoryFixture.CreateAsync();

        public Task DisposeAsync()
        {
            _template?.Dispose();
            return Task.CompletedTask;
        }

        internal async Task<RepositoryFixture> CreateAsync()
        {
            var template = _template ?? throw new InvalidOperationException("Repository seed was not initialized.");
            var fixture = new RepositoryFixture(Path.Combine(Path.GetTempPath(), "codex-worker-worktree-tests", Guid.NewGuid().ToString("N")));
            try
            {
                CopyDirectory(template._root, fixture._root);
                await fixture.Git("config", "--rename-section",
                    "url.file://" + Path.Combine(template._root, "origin.git"),
                    "url.file://" + Path.Combine(fixture._root, "origin.git"));
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var directory in Directory.EnumerateDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    internal sealed class RepositoryFixture : IDisposable
    {
        internal readonly string _root;
        internal RepositoryFixture(string root)
        {
            _root = root;
            Checkout = Path.Combine(root, "checkout");
            WorktreeRoot = Path.Combine(root, "managed-worktrees");
        }
        public string Checkout { get; }
        public string WorktreeRoot { get; }
        public GitHubIssue Issue { get; } = new(17, "Example task", "Implement this request", DateTimeOffset.UtcNow);
        public GitRepository CreateRepository(GitSettings settings) => new(new ProcessRunner(), Checkout, "owner/repo", settings,
            new WorkerSettings(), WorktreeRoot);

        public static async Task<RepositoryFixture> CreateAsync()
        {
            var fixture = new RepositoryFixture(Path.Combine(Path.GetTempPath(), "codex-worker-worktree-tests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(fixture.Checkout);
            var bare = Path.Combine(fixture._root, "origin.git");
            await RunGit(fixture._root, "init", "--bare", bare);
            await RunGit(fixture.Checkout, "init", "-b", "main");
            await RunGit(fixture.Checkout, "config", "user.name", "Codex Worker Tests");
            await RunGit(fixture.Checkout, "config", "user.email", "worker-tests@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "base.txt"), "base");
            await RunGit(fixture.Checkout, "add", "base.txt");
            await RunGit(fixture.Checkout, "commit", "-m", "base");
            await RunGit(fixture.Checkout, "remote", "add", "origin", "https://github.com/owner/repo");
            await RunGit(fixture.Checkout, "config", $"url.file://{bare}.insteadOf", "https://github.com/owner/repo");
            await RunGit(fixture.Checkout, "push", "-u", "origin", "main");
            return fixture;
        }

        public async Task<string> Git(params string[] args) => await RunGit(Checkout, args);
        public async Task<string> GitAt(string directory, params string[] args) => await RunGit(directory, args);
        public async Task RecordPushTransactionsAsync()
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The receive hook requires a POSIX shell.");
            var remote = Path.Combine(_root, "origin.git");
            await File.WriteAllTextAsync(Path.Combine(remote, "received-refs"), "");
            var hook = Path.Combine(remote, "hooks", "post-receive");
            await File.WriteAllTextAsync(hook, "#!/bin/sh\ncat >> received-refs\nprintf '\\n' >> received-refs\n");
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public async Task<string> ReadPushTransactionsAsync() =>
            (await File.ReadAllTextAsync(Path.Combine(_root, "origin.git", "received-refs")))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        public async Task AdvanceBaseAsync(string path = "base-advanced.txt", string content = "independent base change")
        {
            await File.WriteAllTextAsync(Path.Combine(Checkout, path), content);
            await RunGit(Checkout, "add", path);
            await RunGit(Checkout, "commit", "-m", "advance base independently");
            await RunGit(Checkout, "push", "origin", "main");
        }
        public async Task AddAndPushAsync(string path, string content, bool executable = false)
        {
            var fullPath = Path.Combine(Checkout, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
            if (executable)
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                await RunGit(Checkout, "add", "--chmod=+x", "--", path);
            }
            else
                await RunGit(Checkout, "add", "--", path);
            await RunGit(Checkout, "commit", "-m", $"advance base with {path}");
            await RunGit(Checkout, "push", "origin", "main");
        }
        public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

        private static async Task<string> RunGit(string directory, params string[] args)
        {
            var result = await new ProcessRunner().RunAsync("git", args, directory, TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
            return result.StandardOutput.Trim();
        }
    }
}
