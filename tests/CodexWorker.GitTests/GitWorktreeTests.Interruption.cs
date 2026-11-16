using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed partial class GitWorktreeTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartReconcilesIncompleteImplementationWithoutDiscardingChangesOrResettingBudget(bool parkedContext)
    {
        if (parkedContext && !OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        using var history = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../restart-history.db"));
        var config = new WorkerConfiguration { Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout } };
        var id = Guid.NewGuid();
        string workspace;
        using (var original = fixture.CreateRepository(config.Git))
        {
            await original.InitializeAsync(CancellationToken.None);
            await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
            workspace = original.ExecutionDirectory;
            await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "preserved after abrupt exit");
            var snapshot = Assert.IsType<GitRecoveryInfo>(await original.InspectExecutionWorkspaceAsync(CancellationToken.None));
            await history.CreateAsync(new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
                snapshot.Branch, "main", DateTimeOffset.UnixEpoch, parkedContext ? DateTimeOffset.UnixEpoch.AddMinutes(1) : null,
                parkedContext ? "InfrastructureFailure" : "Implementing", null, null, null, 0, [], null, null, null,
                parkedContext ? "Codex context limit: Model context window exhausted" : null,
                RecoveryState: parkedContext ? "uncertain" : null,
                RecoveryBaseCommit: parkedContext ? null : snapshot.BaseCommit, OriginalIssueBody: fixture.Issue.Body, EffectiveEffort: "medium",
                CodexRecovery: new(id, 1, 2, CodexInterruptionRecovery.Fingerprint(config), Guid.NewGuid().ToString(),
                    ProcessScope: await InterruptedWorkspaceProcesses.ReadScopeAsync(CancellationToken.None))));
        }
        using var restarted = fixture.CreateRepository(config.Git);
        await restarted.InitializeAsync(CancellationToken.None);
        using var gate = new SemaphoreSlim(1, 1);
        using var messages = new StringWriter();
        var clock = new FixedRecoveryClock(DateTimeOffset.UnixEpoch.AddHours(1));
        await new CodexRecoveryCoordinator(config, restarted, history, gate, new WorkerConsole(messages, false), clock)
            .ReconcileAsync(CancellationToken.None);
        using var reopened = new ExecutionHistoryStore(Path.Combine(fixture.Checkout, "../restart-history.db"));
        var interrupted = Assert.Single(await reopened.ReadAllAsync());
        Assert.Equal("InfrastructureFailure", interrupted.State);
        Assert.Equal("codex-interrupted", interrupted.RecoveryState);
        Assert.Equal(2, interrupted.CodexRecovery?.ResumeCount);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), interrupted.CodexRecovery?.RetryAfterUtc);
        Assert.Equal("preserved after abrupt exit", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task HostStaysOnlineAfterCodexInterruptionAndPreservesActiveSiblingOnShutdown()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await seed.CreateAsync();
        await fixture.GitAt(Path.GetFullPath(Path.Combine(fixture.Checkout, "../origin.git")), "symbolic-ref", "HEAD", "refs/heads/main");
        var bin = Path.Combine(fixture.Checkout, "../bin");
        Directory.CreateDirectory(bin);
        var instructions = Path.Combine(fixture.Checkout, "../instructions.md");
        await File.WriteAllTextAsync(instructions, "Project instructions");
        var executable = Path.Combine(bin, "codex");
        await File.WriteAllTextAsync(executable, """
            #!/bin/sh
            if [ "$1" = --version ]; then printf 'codex-cli 1.0.0'; exit 0; fi
            printf 'partial implementation' > partial.txt
            printf '%s\n' '{"type":"thread.started","thread_id":"11111111-1111-1111-1111-111111111111"}'
            for argument do prompt=$argument; done
            case "$prompt" in
              *'Number: 17'*) IFS= read -r ready < "$0.second-started"; printf "error: You've hit your usage limit token=private-value\n" >&2; exit 1;;
              *'Number: 18'*) printf 'ready\n' > "$0.second-started"; IFS= read -r release < "$0.release-second";;
              *) exit 2;;
            esac
            """);
        await File.WriteAllTextAsync(Path.Combine(bin, "gh"), """
            #!/bin/sh
            case "$1 $2" in
              'api repos/owner/repo') printf '%s' '{"push":true}';;
              'api --paginate') printf '%s' '[]';;
              'api graphql')
                for argument do
                  case "$argument" in
                    number=17|number=18) printf '{"data":{"repository":{"issue":{"number":%s,"parent":null}}}}' "${argument#number=}";;
                  esac
                done;;
              'api '*'/issues/'*) printf '0';;
              'issue view') printf '%s' '{"state":"OPEN","labels":[{"name":"codex-ready"}]}';;
              'issue list')
                case " $* " in
                  *' --label codex-ready '*) printf '%s' '[{"number":17,"title":"Example task","body":"Original intent","createdAt":"2026-01-01T00:00:00Z"},{"number":18,"title":"Sibling task","body":"Original sibling intent","createdAt":"2026-01-02T00:00:00Z"}]';;
                  *) printf '[]';;
                esac;;
              'label list') printf '[]';;
            esac
            exit 0
            """);
        foreach (var tool in new[] { executable, Path.Combine(bin, "gh") })
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new ProcessRunner();
        await runner.RunAsync("mkfifo", [executable + ".second-started", executable + ".release-second"], bin);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousExecutable = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        using var stop = new CancellationTokenSource();
        using var output = new StringWriter();
        var releasedCapacity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = false;
        Task? run = null;
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + previousPath);
            Directory.CreateDirectory(Path.Combine(bin, "home"));
            Environment.SetEnvironmentVariable("HOME", Path.Combine(bin, "home"));
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            var database = Path.Combine(bin, "history.db");
            var config = new WorkerConfiguration
            {
                Project = new() { Name = "sample", Repository = "owner/repo", Directory = fixture.Checkout },
                GitHub = new ManagedProjectRuntimeSettings().GitHub,
                Codex = new() { InstructionsFile = instructions, Model = "original-model" },
                Worker = new() { MaxParallelTasks = 2 }
            };
            var host = new WorkerHost(new GlobalWorkerConfiguration
            {
                Api = new() { Enabled = false }, Projects = new() { Ownership = "standalone", Directory = bin },
                Worker = new() { MaxParallelTasks = 2 }
            }, [("project.yml", config)], new WorkerConsole(output, false),
                executionHistoryPath: database, agentAuthentication: new HealthyAgent(),
                registrationClient: new WorkerRegistrationClient(provisioningDiscovery:
                    new CodexProvisioning.NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0.0")))),
                operationalLog: message =>
                {
                    if (message.Contains("Codex usage limit", StringComparison.Ordinal)) interrupted = true;
                    if (interrupted && message.Contains("global 1/2", StringComparison.Ordinal)) releasedCapacity.TrySetResult();
                });
            run = host.RunAsync(stop.Token);
            try { await Task.WhenAny(releasedCapacity.Task, run).WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { Assert.Fail(output.ToString()); }
            if (run.IsCompleted) await run;
            Assert.True(releasedCapacity.Task.IsCompleted, output.ToString());
            Assert.False(run.IsCompleted);
            using var history = new ExecutionHistoryStore(database);
            var entries = await history.ReadAllAsync();
            Assert.Equal(2, entries.Count);
            var failed = entries.Single(entry => entry.IssueNumber == 17);
            var sibling = entries.Single(entry => entry.IssueNumber == 18);
            Assert.Equal("codex-interrupted", failed.RecoveryState);
            Assert.Equal("Implementing", sibling.State);
            Assert.DoesNotContain("private-value", failed.FailureReason, StringComparison.Ordinal);
            var siblingWorkspace = Path.Combine(GitRepository.DefaultWorktreeRoot("owner/repo"), sibling.ExecutionId.ToString("N"));
            Assert.Equal("partial implementation", await File.ReadAllTextAsync(Path.Combine(siblingWorkspace, "partial.txt")));
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            var preserved = (await history.ReadAllAsync()).Single(entry => entry.ExecutionId == sibling.ExecutionId);
            Assert.Equal("Cancelled", preserved.State);
            Assert.Equal("codex-interrupted", preserved.RecoveryState);
            Assert.True(Directory.Exists(siblingWorkspace));
        }
        finally
        {
            stop.Cancel();
            try { if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", previousPath);
                Environment.SetEnvironmentVariable("HOME", previousHome);
                Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previousExecutable);
            }
        }
    }

    private sealed class HealthyAgent : IAgentAuthenticationProvider
    {
        public string Provider => "codex";
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task CodexInterruptionUsesSameWorkspaceAfterRestartAndPreservesAdvancedMain()
    {
        using var fixture = await seed.CreateAsync();
        var settings = new GitSettings { AutoMerge = true, PushCompletedBranch = false };
        var id = Guid.NewGuid();
        string workspace;
        GitRecoveryInfo snapshot;
        using (var original = fixture.CreateRepository(settings))
        {
            await original.InitializeAsync(CancellationToken.None);
            await original.StartIssueAsync(id, fixture.Issue, CancellationToken.None);
            workspace = original.ExecutionDirectory;
            await File.WriteAllTextAsync(Path.Combine(workspace, "partial.txt"), "original useful implementation");
            snapshot = Assert.IsType<GitRecoveryInfo>(await original.InspectExecutionWorkspaceAsync(CancellationToken.None));
        }
        var now = DateTimeOffset.UtcNow;
        var source = new ExecutionHistoryEntry(id, "sample", "owner/repo", fixture.Issue.Number, fixture.Issue.Title,
            snapshot.Branch, "main", now, now, "InfrastructureFailure", 1, null, null, 0, [], null, null, null,
            "Codex usage limit", "codex-interrupted", snapshot.BaseCommit, snapshot.StatusSummary,
            OriginalIssueBody: fixture.Issue.Body, CodexRecovery: new(id, 1, 0, "configuration"));
        await fixture.AdvanceBaseAsync();
        using var resumed = fixture.CreateRepository(settings);
        await resumed.InitializeAsync(CancellationToken.None);
        Assert.Null(await resumed.ValidateCodexRecoveryAsync(source, CancellationToken.None));
        await resumed.StartCodexRecoveryAsync(source, CancellationToken.None);
        Assert.Equal(workspace, resumed.ExecutionDirectory);
        Assert.Equal("original useful implementation", await File.ReadAllTextAsync(Path.Combine(workspace, "partial.txt")));
        await File.WriteAllTextAsync(Path.Combine(workspace, "finished.txt"), "continued implementation");
        var validations = 0;
        var result = await resumed.CommitAndIntegrateAsync(fixture.Issue, _ =>
        {
            validations++;
            Assert.True(File.Exists(Path.Combine(workspace, "base-advanced.txt")));
            Assert.True(File.Exists(Path.Combine(workspace, "partial.txt")));
            return Task.FromResult(ValidationResult.Success);
        }, CancellationToken.None);
        Assert.True(result.HasChanges);
        Assert.True(validations > 0);
        Assert.Equal("original useful implementation", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "partial.txt")));
        Assert.Equal("independent base change", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "base-advanced.txt")));
        Assert.Equal("continued implementation", await File.ReadAllTextAsync(Path.Combine(fixture.Checkout, "finished.txt")));
    }
}
