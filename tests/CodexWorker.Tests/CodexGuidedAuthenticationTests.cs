namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using CodexWorker;
using System.Text.Json;

[Collection("ServerTokenEnvironment")]
public sealed class CodexGuidedAuthenticationTests
{
    [Fact]
    public async Task LoginPublishesOnlyValidatedInstructionsAndVerifiesAuthentication()
    {
        var calls = new List<string>();
        var progress = new List<ProvisioningCommandReport>();
        var refreshed = 0;
        var discovery = new NodeCapabilityDiscovery((_, _, _) =>
        {
            refreshed++;
            return Task.FromResult((0, "1.0.0"));
        });
        var executor = new NodeProvisioningCommandExecutor(discovery, (executable, args, _) =>
        {
            calls.Add(string.Join(' ', args));
            return Task.FromResult(1); // Login exited successfully, but authentication is not ready.
        }, login: async (publish, token) =>
        {
            await publish(new(CodexDeviceLogin.VerificationUri, "ABCD-EFGHI"), token);
            return 0;
        });
        var result = await executor.ExecuteAsync(Running(), true, reportProgress: (report, _) =>
        {
            progress.Add(report);
            return Task.CompletedTask;
        });
        Assert.Equal(ProvisioningCommandStatus.Running, Assert.Single(progress).Status);
        Assert.Equal("ABCD-EFGHI", progress[0].LoginInstructions?.UserCode);
        Assert.Equal("login status", Assert.Single(calls));
        Assert.Equal(ProvisioningCommandStatus.Failed, result.Status);
        Assert.Null(result.LoginInstructions);
        Assert.True(refreshed > 0);
    }

    [Fact]
    public async Task DeniedLoginDoesNotLaunchAndCancellationClearsChallenge()
    {
        var launched = false;
        using var cancelled = new CancellationTokenSource();
        var executor = new NodeProvisioningCommandExecutor(Discovery(), login: async (publish, token) =>
        {
            launched = true;
            await publish(new(CodexDeviceLogin.VerificationUri, "ABCD-EFGHI"), token);
            cancelled.Cancel();
            token.ThrowIfCancellationRequested();
            return 0;
        });
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(Running(), false)).Diagnostic);
        Assert.False(launched);
        var result = await executor.ExecuteAsync(Running(), true, cancelled.Token,
            (_, _) => Task.CompletedTask);
        Assert.Equal(ProvisioningCommandStatus.Cancelled, result.Status);
        Assert.Null(result.LoginInstructions);
    }

    [Theory]
    [InlineData("https://evil.example", "ABCD-EFGHI")]
    [InlineData(CodexDeviceLogin.VerificationUri, "token-secret")]
    public void ProtocolRejectsUntrustedGuidance(string url, string code)
    {
        Assert.False(ProvisioningCommandProtocol.ValidReport(new(ProvisioningCommandStatus.Running,
            ProvisioningDiagnostic.Executing, LoginInstructions: new(url, code))));
        Assert.False(ProvisioningCommandProtocol.ValidReport(new(ProvisioningCommandStatus.Succeeded,
            ProvisioningDiagnostic.Completed, LoginInstructions: new(CodexDeviceLogin.VerificationUri, "ABCD-EFGHI"))));
    }

    [Fact]
    public async Task InstructionsAreTransientOwnedAndClearedOnCompletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "registry.db");
            var clock = new LoginClock();
            var store = new ProvisioningCommandStore(path, clock);
            await store.InitializeAsync();
            var operation = await store.CreateAsync(new("server", "codex-cli", ProvisioningCommandAction.Login));
            await store.ClaimAsync("server");
            var progress = new ProvisioningCommandReport(ProvisioningCommandStatus.Running,
                ProvisioningDiagnostic.Executing, LoginInstructions: new(CodexDeviceLogin.VerificationUri, "ABCD-EFGHI"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReportAsync(operation.Id, new string('a', 32), progress));
            await store.ReportAsync(operation.Id, "server", progress);
            Assert.NotNull(Assert.Single(await store.ListAsync()).LoginInstructions);
            var restarted = new ProvisioningCommandStore(path);
            Assert.Null(Assert.Single(await restarted.ListAsync()).LoginInstructions);
            clock.Now = clock.Now.AddMinutes(3);
            Assert.Null(Assert.Single(await store.ListAsync()).LoginInstructions);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReportAsync(operation.Id, "server", progress));
            await store.ReportAsync(operation.Id, "server", new(ProvisioningCommandStatus.Succeeded, ProvisioningDiagnostic.Completed));
            Assert.Null(Assert.Single(await store.ListAsync()).LoginInstructions);
            Assert.DoesNotContain("ABCD-EFGHI", JsonSerializer.Serialize(await restarted.ListAsync()), StringComparison.Ordinal);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task ManagedReadinessProcessesProvisioningUntilAuthenticationIsObserved(bool installed, int expectedCommands)
    {
        var authenticated = false;
        var commands = 0;
        var discovery = new NodeCapabilityDiscovery((executable, args, _) =>
        {
            if (executable == CodexServiceEnvironment.Executable && !installed)
                return Task.FromException<(int, string)>(new FileNotFoundException());
            return Task.FromResult((executable == CodexServiceEnvironment.Executable &&
                args.SequenceEqual(new[] { "login", "status" }) && !authenticated ? 1 : 0, "1.0.0"));
        });
        var readiness = new ManagedCodexReadiness(new ReadyProvider());
        while (!await readiness.EvaluateAsync(discovery, false, CancellationToken.None))
        {
            commands++;
            if (!installed) installed = true;
            else authenticated = true;
            await discovery.GetAsync(refresh: true);
        }
        Assert.Equal(expectedCommands, commands);
    }

    [Fact]
    public async Task RealLoginProcessUsesServiceHomeAndExtractsChallengeWithoutReturningLogs()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "codex-device-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previousExecutable = Environment.GetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE");
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var executable = Path.Combine(directory, "codex");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                test "$1" = login && test "$2" = --device-auth || exit 4
                printf '%s' "$CODEX_HOME" > "$0.home"
                printf 'private-token https://auth.openai.com/codex/device\n'
                printf '\033[1mABCD-EFGHI\033[0m' >&2
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(directory, "service-home"));
            var progress = new List<ProvisioningCommandReport>();
            var executor = new NodeProvisioningCommandExecutor(Discovery(), (_, _, _) => Task.FromResult(0));
            var result = await executor.ExecuteAsync(Running(), true, reportProgress: (report, _) =>
            {
                progress.Add(report);
                return Task.CompletedTask;
            });
            Assert.Equal(ProvisioningCommandStatus.Succeeded, result.Status);
            Assert.Equal("ABCD-EFGHI", Assert.Single(progress).LoginInstructions?.UserCode);
            Assert.DoesNotContain("private-token", JsonSerializer.Serialize(progress), StringComparison.Ordinal);
            Assert.Equal(Path.Combine(directory, "service-home"), await File.ReadAllTextAsync(executable + ".home"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", previousExecutable);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Directory.Delete(directory, true);
        }
    }

    private sealed class ReadyProvider : IAgentAuthenticationProvider
    {
        public string Provider => "codex";
        public Task ValidateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class LoginClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static NodeCapabilityDiscovery Discovery() => new((_, _, _) => Task.FromResult((0, "1.0.0")));
    private static ProvisioningCommand Running()
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid().ToString("N"), new("server", "codex-cli", ProvisioningCommandAction.Login), now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(1));
    }
}
