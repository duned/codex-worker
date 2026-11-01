namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using CodexWorker;
using System.Diagnostics;
using System.Text.Json;

public sealed class ClaudeNativeLoginTests
{
    private const string Challenge = "https://claude.com/cai/oauth/authorize?code=private-code&state=private-state&code_challenge=private-pkce&redirect_uri=https%3A%2F%2Fclaude.ai";

    [Theory]
    [InlineData("https://attacker.example/cai/oauth/authorize")]
    [InlineData("https://claude.com.attacker.example/cai/oauth/authorize")]
    [InlineData("https://user@claude.com/cai/oauth/authorize")]
    [InlineData("https://claude.com:444/cai/oauth/authorize")]
    [InlineData("http://claude.com/cai/oauth/authorize")]
    [InlineData("https://claude.com/cai/oauth/authorize")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void RejectsUntrustedOrIncompleteChallenges(string url) => Assert.False(ClaudeNativeLogin.HasTrustedChallenge(url));

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task FakeNativeCliRequiresLocalCompletionAndSuppressesAllChallengeSecrets()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TemporaryDirectory();
        var script = Path.Combine(directory.Path, "claude");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n[ \"$1 $2\" = 'auth login' ] || exit 9\nprintf '%s\\n' '" + Challenge + "' 'Paste code here'\nread code\nexit 7\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo(script) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reports = new List<ClaudeLoginProgress>();
        var result = await ClaudeNativeLogin.RunProcessAsync(start, (progress, _) =>
        {
            reports.Add(progress);
            return Task.CompletedTask;
        }, deadline.Token);
        Assert.Equal(ClaudeNativeLogin.ManualCompletionExitCode, result);
        Assert.Equal(ClaudeNativeLogin.ManualProgress, Assert.Single(reports));
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(reports), StringComparison.Ordinal);
        Assert.True(ClaudeNativeLogin.HasTrustedChallenge(Challenge));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task NativeCompletionStillRequiresAnObservedVendorChallenge()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TemporaryDirectory();
        var script = Path.Combine(directory.Path, "claude");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf '%s\\n' '" + Challenge + "'\nexit 0\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await ClaudeNativeLogin.RunProcessAsync(new ProcessStartInfo(script)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }, (_, _) => throw new InvalidOperationException("Unexpected paste prompt"), deadline.Token));
    }

    [Theory]
    [InlineData(0, true, ProvisioningCommandStatus.Succeeded, null)]
    [InlineData(0, false, ProvisioningCommandStatus.Failed, ProvisioningFailureCode.VerificationFailed)]
    [InlineData(-2, false, ProvisioningCommandStatus.Failed, ProvisioningFailureCode.ManualVendorCliCompletionRequired)]
    [InlineData(1, false, ProvisioningCommandStatus.Failed, ProvisioningFailureCode.ProcessExited)]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ExecutorVerifiesStatusAndRefreshesInventory(int exit, bool authenticated,
        ProvisioningCommandStatus expected, ProvisioningFailureCode? failure)
    {
        var probes = 0;
        var refreshed = 0;
        var discovery = new NodeCapabilityDiscovery((_, _, _) =>
        {
            refreshed++;
            return Task.FromException<(int, string)>(new FileNotFoundException());
        });
        var executor = new NodeProvisioningCommandExecutor(discovery, processRunner: (tool, args, _) =>
        {
            probes++;
            Assert.Equal(ClaudeServiceEnvironment.Executable, tool);
            Assert.Equal(new[] { "auth", "status" }, args);
            return Task.FromResult(new ProvisioningProcessResult(authenticated ? 0 : 1,
                StandardOutput: authenticated ? "{\"loggedIn\":true}" : "{\"loggedIn\":false}"));
        }, claudeLogin: async (publish, token) =>
        {
            if (exit == -2) await publish(ClaudeNativeLogin.ManualProgress, token);
            return exit;
        });
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand("test", new("server", "claude-cli", ProvisioningCommandAction.Login),
            now, ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(1));
        var reports = new List<ProvisioningCommandReport>();
        var result = await executor.ExecuteAsync(command, true, reportProgress: (report, _) =>
        {
            reports.Add(report);
            return Task.CompletedTask;
        });
        Assert.Equal(expected, result.Status);
        Assert.Equal(failure, result.FailureDetail?.Code);
        Assert.Equal(1, probes);
        Assert.True(refreshed > 0);
        Assert.All(reports, report => Assert.True(ProvisioningCommandProtocol.ValidReport(report)));
    }

    [Fact]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public void LoginRequiresEveryLocalPolicyPermission()
    {
        var request = new ProvisioningCommandRequest("server", "claude-cli", ProvisioningCommandAction.Login);
        var policy = new ProvisioningPolicy { Enabled = true, AllowCredentials = true, AllowNonPrivileged = true };
        Assert.True(WorkerProvisioning.Permitted(request, policy));
        policy.AllowCredentials = false;
        Assert.False(WorkerProvisioning.Permitted(request, policy));
        policy.AllowCredentials = true;
        policy.AllowNonPrivileged = false;
        Assert.False(WorkerProvisioning.Permitted(request, policy));
        policy.AllowNonPrivileged = true;
        policy.Enabled = false;
        Assert.False(WorkerProvisioning.Permitted(request, policy));
        policy.Enabled = true;
        policy.DeniedActions.Add("authentication:claude-cli:login");
        Assert.False(WorkerProvisioning.Permitted(request, policy));
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ServerProgressIsTransientScopedAndNeverReplayedAfterReconnect()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "server.db");
        var store = new ProvisioningCommandStore(path);
        await store.InitializeAsync();
        var node = Guid.NewGuid().ToString("N");
        var request = new ProvisioningCommandRequest(node, "claude-cli", ProvisioningCommandAction.Login);
        var created = await store.CreateAsync(request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(request));
        var running = Assert.IsType<ProvisioningCommand>(await store.ClaimAsync(node));
        Assert.Equal(created.Id, running.Id);
        var progress = new ProvisioningCommandReport(ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing,
            ClaudeLogin: ClaudeNativeLogin.ManualProgress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReportAsync(created.Id, Guid.NewGuid().ToString("N"), progress));
        await store.ReportAsync(created.Id, node, progress);
        Assert.Equal(ClaudeNativeLogin.ManualProgress, (await store.GetAsync(created.Id))?.ClaudeLogin);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT body FROM provisioning_commands WHERE id=$id";
            query.Parameters.AddWithValue("$id", created.Id);
            var durable = Assert.IsType<string>(await query.ExecuteScalarAsync());
            Assert.DoesNotContain("ClaudeLogin", durable, StringComparison.Ordinal);
            Assert.DoesNotContain("private-", durable, StringComparison.Ordinal);
        }
        var restarted = new ProvisioningCommandStore(path);
        Assert.Null((await restarted.GetAsync(created.Id))?.ClaudeLogin);
        Assert.Null(await restarted.ClaimAsync(node));
        await store.ReportAsync(created.Id, node, new(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.ProcessFailed,
            FailureDetail: new(ProvisioningFailureCode.ManualVendorCliCompletionRequired)));
        Assert.Null((await store.GetAsync(created.Id))?.ClaudeLogin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReportAsync(created.Id, node, progress));
    }
    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task CancellationAndParallelLoginDoNotStartAnotherNativeCli()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new NodeCapabilityDiscovery((_, _, _) =>
            Task.FromException<(int, string)>(new FileNotFoundException()));
        var calls = 0;
        var executor = new NodeProvisioningCommandExecutor(discovery, claudeLogin: async (_, token) =>
        {
            calls++;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand("test", new("server", "claude-cli", ProvisioningCommandAction.Login),
            now, ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var first = executor.ExecuteAsync(command, true, cancellation.Token, (_, _) => Task.CompletedTask);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await executor.ExecuteAsync(command with { Id = "other" }, true,
            reportProgress: (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningDiagnostic.Denied, second.Diagnostic);
        cancellation.Cancel();
        Assert.Equal(ProvisioningCommandStatus.Cancelled, (await first).Status);
        Assert.Equal(1, calls);
        var expired = await executor.ExecuteAsync(command with { DeadlineUtc = now.AddMinutes(-1) }, true,
            reportProgress: (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.TimedOut, expired.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task ExpiredProgressIsHiddenAndRejectedAndWrongProviderCannotUseIt()
    {
        using var directory = new TemporaryDirectory();
        var clock = new LoginClock();
        var store = new ProvisioningCommandStore(Path.Combine(directory.Path, "server.db"), clock);
        await store.InitializeAsync();
        var operation = await store.CreateAsync(new("server", "claude-cli", ProvisioningCommandAction.Login));
        await store.ClaimAsync("server");
        var progress = new ProvisioningCommandReport(ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing,
            ClaudeLogin: ClaudeNativeLogin.ManualProgress);
        await store.ReportAsync(operation.Id, "server", progress);
        clock.Now = clock.Now.AddMinutes(11);
        Assert.Null((await store.GetAsync(operation.Id))?.ClaudeLogin);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(operation.Id, "server", progress));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(operation.Id, "server",
            new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed)));
        var codex = await store.CreateAsync(new("server", "codex-cli", ProvisioningCommandAction.Login));
        await store.ClaimAsync("server");
        await store.ClaimAsync("server");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(codex.Id, "server", progress));
        Assert.False(ProvisioningCommandProtocol.ValidReport(progress with
        {
            ClaudeLogin = ClaudeNativeLogin.ManualProgress with { InstructionsUri = "https://attacker.example" }
        }));
    }

    [Theory]
    [InlineData("printf 'no challenge'; exit 0")]
    [InlineData("printf 'https://attacker.example Paste code here'; exit 0")]
    [InlineData("exit 7")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task FakeCliCannotSucceedWithoutTrustedChallenge(string body)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TemporaryDirectory();
        var script = Path.Combine(directory.Path, "claude");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ClaudeNativeLogin.RunProcessAsync(new ProcessStartInfo(script)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }, (_, _) => throw new InvalidOperationException("Unexpected challenge"), deadline.Token);
        Assert.NotEqual(0, result);
        Assert.NotEqual(ClaudeNativeLogin.ManualCompletionExitCode, result);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task AcknowledgedNativeSuccessRemainsIdempotentAfterDeadline()
    {
        using var directory = new TemporaryDirectory();
        var clock = new LoginClock();
        var store = new ProvisioningCommandStore(Path.Combine(directory.Path, "server.db"), clock);
        await store.InitializeAsync();
        var command = await store.CreateAsync(new("server", "claude-cli", ProvisioningCommandAction.Login));
        await store.ClaimAsync("server");
        var success = new ProvisioningCommandReport(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed);
        await store.ReportAsync(command.Id, "server", success);
        clock.Now = clock.Now.AddMinutes(11);
        Assert.Equal(ProvisioningCommandStatus.Succeeded, (await store.ReportAsync(command.Id, "server", success))?.Status);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task CancellationWhilePublishingChallengeJoinsOriginalNativeSession()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TemporaryDirectory();
        var script = Path.Combine(directory.Path, "claude");
        var pidFile = Path.Combine(directory.Path, "pid");
        await File.WriteAllTextAsync(script,
            "#!/bin/sh\nprintf '%s' \"$$\" > pid\nprintf '%s\\n' '" + Challenge + "' 'Paste code here'\nread code\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var published = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaudeNativeLogin.RunProcessAsync(
            new ProcessStartInfo(script)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = directory.Path
            }, (_, token) =>
            {
                published = true;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, cancellation.Token));
        Assert.True(published);
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    private sealed class LoginClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"claude-login-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }

}
