namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed class GitHubDeviceLoginTests
{
    [Theory]
    [InlineData("! First copy your one-time code: ABCD-1234\nOpen this URL to continue in your web browser: https://github.com/login/device\n", true)]
    [InlineData("! First copy your one-time code: \u001b[1mABCD-1234\u001b[0m\nhttps://github.com/login/device\nprivate-token", true)]
    [InlineData("oauth_token: ABCD-1234\nhttps://github.com/login/device", false)]
    [InlineData("one-time code: ABCD-1234\nhttps://github.com/login/device/evil", false)]
    [InlineData("one-time code: ABCD-1234\nhttps://github.com/login/device?token=secret", false)]
    [InlineData("one-time code: ABCD-12345\nhttps://github.com/login/device", false)]
    [InlineData("one-time code: ABCD-1234\nhttps://github.com/login/device", false)]
    public void ParsesOnlyExplicitGitHubChallenge(string output, bool valid)
    {
        var instructions = GitHubDeviceLogin.Parse(output);
        Assert.Equal(valid, instructions is not null);
        if (valid) Assert.Equal(new("https://github.com/login/device", "ABCD-1234"), instructions);
    }

    [Fact]
    public void LoginUsesIsolatedHeadlessProductContext()
    {
        var start = GitHubDeviceLogin.CreateStartInfo("/product/github");
        Assert.Equal("/usr/bin/gh", start.FileName);
        Assert.Equal(new[] { "auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https", "--insecure-storage" }, start.ArgumentList);
        Assert.Equal("/product/github", start.Environment["GH_CONFIG_DIR"]);
        Assert.Equal("/usr/bin/true", start.Environment["GH_BROWSER"]);
        Assert.Equal("1", start.Environment["GH_PROMPT_DISABLED"]);
        Assert.Equal("C", start.Environment["LC_ALL"]);
        foreach (var secret in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GH_DEBUG" })
            Assert.False(start.Environment.ContainsKey(secret));
    }

    [Fact]
    public async Task ProcessCapturesSplitInstructionsOnceAndDrainsUnrelatedOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        var start = Script("printf '! First copy your one-time code: ABCD-1234\\n' >&2\nprintf 'https://github.com/login/device\\nprivate-token\\n'\nexit 0");
        var reports = new List<CodexLoginInstructions>();
        Assert.Equal(0, await GitHubDeviceLogin.RunAsync(start, (instructions, _) =>
        {
            reports.Add(instructions);
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal(new("https://github.com/login/device", "ABCD-1234"), Assert.Single(reports));
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(reports), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrPublicationFailureKillsRunningLogin(bool publicationFails)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var cancellation = new CancellationTokenSource();
        var start = Script("printf '! First copy your one-time code: ABCD-1234\\nhttps://github.com/login/device\\n'\nexec sleep 60");
        // Use the process handle captured by the script's PID to verify termination after cleanup.
        using var temporary = new TemporaryDirectory();
        var pidFile = Path.Combine(temporary.Path, "pid");
        start.ArgumentList[1] = "echo $$ > '" + pidFile + "'\n" + start.ArgumentList[1];
        async Task Publish(CodexLoginInstructions _, CancellationToken token)
        {
            if (publicationFails) throw new InvalidOperationException("publication rejected");
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        }
        var login = GitHubDeviceLogin.RunAsync(start, Publish, cancellation.Token);
        if (publicationFails) await Assert.ThrowsAsync<InvalidOperationException>(() => login.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(5)));
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(Directory.Exists($"/proc/{pid}"));
    }

    [Theory]
    [InlineData(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed)]
    [InlineData(ProvisioningCommandStatus.Failed, ProvisioningDiagnostic.ProcessFailed)]
    [InlineData(ProvisioningCommandStatus.Cancelled, ProvisioningDiagnostic.Cancelled)]
    [InlineData(ProvisioningCommandStatus.TimedOut, ProvisioningDiagnostic.TimedOut)]
    public async Task SeparateCliReadsActiveChallengeAndTerminalReportsEraseIt(ProvisioningCommandStatus status, ProvisioningDiagnostic diagnostic)
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "codex-server.db");
        var store = new ProvisioningCommandStore(database);
        await store.InitializeAsync();
        var command = await store.CreateAsync(new("server", "github-cli", ProvisioningCommandAction.Login));
        await store.ClaimAsync("server");
        await store.ReportAsync(command.Id, "server", new(ProvisioningCommandStatus.Running,
            ProvisioningDiagnostic.Executing, LoginInstructions: new("https://github.com/login/device", "ABCD-1234")));
        var output = new StringWriter();
        var cli = new ServerProvisioningCommandCli(new ServerConfigurationAdministrationService(),
            new LocalProvisioningCommandAdministrationServiceFactory(), output, new StringWriter());
        var configuration = $"--Server:DataDirectory={temporary.Path}";
        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["show", command.Id, configuration]));
        Assert.Contains("Device login: https://github.com/login/device", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("One-time code: ABCD-1234", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();
        Assert.Equal(ServerAdministrationExitCodes.Success, await cli.RunAsync(["show", command.Id, "--json", configuration]));
        using (var json = JsonDocument.Parse(output.ToString()))
            Assert.Equal("ABCD-1234", json.RootElement.GetProperty("loginInstructions").GetProperty("userCode").GetString());
        Assert.NotNull(Assert.Single(await new ProvisioningCommandStore(database).ListAsync()).LoginInstructions);
        await store.ReportAsync(command.Id, "server", new(status, diagnostic));
        Assert.Null((await new ProvisioningCommandStore(database).GetAsync(command.Id))?.LoginInstructions);
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT body FROM provisioning_commands";
        Assert.DoesNotContain("ABCD-1234", Assert.IsType<string>(await query.ExecuteScalarAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredChallengeIsHiddenAndBackupExcludesActiveChallenge()
    {
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "codex-server.db");
        await new SqliteRegistryStore(database).InitializeAsync();
        await new SqliteCredentialStore(database,
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))).InitializeAsync();
        var clock = new LoginClock();
        var store = new ProvisioningCommandStore(database, clock);
        await store.InitializeAsync();
        var command = await store.CreateAsync(new("server", "github-cli", ProvisioningCommandAction.Login));
        await store.ClaimAsync("server");
        await store.ReportAsync(command.Id, "server", new(ProvisioningCommandStatus.Running,
            ProvisioningDiagnostic.Executing, LoginInstructions: new("https://github.com/login/device", "ABCD-1234")));
        var archive = Path.Combine(temporary.Path, "backup.zip");
        await new ServerBackup(database).ExportAsync(archive);
        var restored = Path.Combine(temporary.Path, "restored.db");
        await new ServerBackup(restored).RestoreOfflineAsync(archive);
        Assert.Null((await new ProvisioningCommandStore(restored, clock).GetAsync(command.Id))?.LoginInstructions);
        Assert.DoesNotContain("ABCD-1234", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(restored)), StringComparison.Ordinal);
        Assert.NotNull((await store.GetAsync(command.Id))?.LoginInstructions);
        clock.Now = clock.Now.AddMinutes(3);
        var reader = new ProvisioningCommandStore(database, clock);
        Assert.Null((await reader.GetAsync(command.Id))?.LoginInstructions);
        Assert.Null(Assert.Single(await reader.ListAsync()).LoginInstructions);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(command.Id, "server",
            new(ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing,
                LoginInstructions: new("https://github.com/login/device", "ABCD-1234"))));
        Assert.Null((await reader.ReconcileAsync(command.Id))?.LoginInstructions);
    }

    [Fact]
    public async Task LoginWithoutDeviceFlowReturnsFailureWithoutRawOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        var published = false;
        var exit = await GitHubDeviceLogin.RunAsync(Script("printf 'private-token startup failed\\n' >&2; exit 7"), (_, _) =>
        {
            published = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(7, exit);
        Assert.False(published);
    }

    [Theory]
    [InlineData(7, 0, ProvisioningFailureCode.ProcessExited)]
    [InlineData(0, 1, ProvisioningFailureCode.VerificationFailed)]
    [InlineData(-1, 0, ProvisioningFailureCode.ExecutableNotFound)]
    public async Task ExecutorReturnsSafeLoginAndVerificationFailureDiagnostics(int loginExit, int statusExit, ProvisioningFailureCode expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temporary = new TemporaryDirectory();
        var setup = new NodeGitHubSetup(Path.Combine(temporary.Path, "setup"),
            (_, _, _) => Task.FromResult((statusExit, "private-token")), unrelatedAuthentication: () => false,
            login: (_, _, _) => loginExit == -1 ? Task.FromException<int>(new FileNotFoundException("private-token")) : Task.FromResult(loginExit));
        var request = new ProvisioningCommandRequest("server", "github-cli", ProvisioningCommandAction.Login);
        await setup.ExecuteAsync(request with { Action = ProvisioningCommandAction.PrepareAuthentication }, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"), request, now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(1));
        var executor = new NodeProvisioningCommandExecutor(
            new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0"))), githubSetup: setup);
        var result = await executor.ExecuteAsync(command, true, reportProgress: (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.Failed, result.Status);
        Assert.Equal(expected, result.FailureDetail?.Code);
        if (loginExit > 0) Assert.Equal(loginExit, result.FailureDetail?.ProcessExitCode);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredLoginDeadlineDoesNotLaunchProcess()
    {
        var launched = false;
        var setup = new NodeGitHubSetup(login: (_, _, _) =>
        {
            launched = true;
            return Task.FromResult(0);
        });
        var executor = new NodeProvisioningCommandExecutor(new NodeCapabilityDiscovery(), githubSetup: setup);
        var now = DateTimeOffset.UtcNow;
        var command = new ProvisioningCommand(Guid.NewGuid().ToString("N"),
            new("server", "github-cli", ProvisioningCommandAction.Login), now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddSeconds(-1));
        var result = await executor.ExecuteAsync(command, true, reportProgress: (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.TimedOut, result.Status);
        Assert.Equal(ProvisioningFailureCode.TimedOut, result.FailureDetail?.Code);
        Assert.False(launched);
    }

    [Fact]
    public async Task RunningLoginDeadlineCancelsExecutorAndClearsPersistedChallenge()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temporary = new TemporaryDirectory();
        var database = Path.Combine(temporary.Path, "commands.db");
        var clock = new DeadlineClock();
        var store = new ProvisioningCommandStore(database, clock);
        await store.InitializeAsync();
        var created = await store.CreateAsync(new("server", "github-cli", ProvisioningCommandAction.Login));
        var running = Assert.IsType<ProvisioningCommand>(await store.ClaimAsync("server"));
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = new NodeGitHubSetup(Path.Combine(temporary.Path, "setup"), unrelatedAuthentication: () => false,
            login: async (_, publish, token) =>
            {
                await publish(new("https://github.com/login/device", "ABCD-1234"), token);
                published.SetResult();
                return await blocked.Task.WaitAsync(token);
            });
        await setup.ExecuteAsync(created.Request with { Action = ProvisioningCommandAction.PrepareAuthentication }, CancellationToken.None);
        var executor = new NodeProvisioningCommandExecutor(
            new NodeCapabilityDiscovery((_, _, _) => Task.FromResult((0, "1.0"))), githubSetup: setup, timeProvider: clock);
        var execution = executor.ExecuteAsync(running, true,
            reportProgress: async (report, token) => { await store.ReportAsync(created.Id, "server", report, token); });
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull((await new ProvisioningCommandStore(database, clock).GetAsync(created.Id))?.LoginInstructions);
        clock.Expire();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProvisioningCommandStatus.TimedOut, result.Status);
        await store.ReportAsync(created.Id, "server", result);
        Assert.Null((await new ProvisioningCommandStore(database, clock).GetAsync(created.Id))?.LoginInstructions);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private DeadlineTimer? _timer;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new DeadlineTimer(callback, state);
            return _timer;
        }
        public void Expire() => _timer?.Fire();
        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class LoginClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ProcessStartInfo Script(string script)
    {
        var start = GitHubDeviceLogin.CreateStartInfo("/unused");
        start.FileName = "/bin/sh";
        start.ArgumentList.Clear();
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return start;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"github-device-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, true);
        }
    }
}
