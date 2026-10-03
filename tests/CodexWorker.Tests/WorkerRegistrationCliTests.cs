namespace CodexWorker.Tests;

using System.Text.Json;
using CodexWorker;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerRegistrationCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationDefaultsAndExplicitOverridesReachEnrollmentWithoutLeakingToken(bool overrides)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"registration-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "worker.yml");
            await File.WriteAllTextAsync(path, """
                projects:
                  ownership: managed
                server:
                  enabled: true
                  url: https://configured.example
                  identityFile: ./identity
                worker:
                  maxParallelTasks: 3
                """);
            WorkerServerSettings? settings = null;
            var capacity = 0;
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var cli = new WorkerRegistrationCli((received, slots, token, cancellationToken) =>
            {
                settings = received;
                capacity = slots;
                Assert.Equal("sensitive-bootstrap", token);
                Assert.True(cancellationToken.CanBeCanceled);
                return Task.CompletedTask;
            }, new WorkerConsole(stdout, interactive: false, errorWriter: stderr),
                new StringReader("sensitive-bootstrap\n"), stdout);
            string[] arguments = overrides
                ? ["--server", "https://override.example", "--identity-file", Path.Combine(directory, "override"), "--capacity", "2", "--token-stdin", "--json"]
                : ["--token-stdin", "--json"];

            var exitCode = await cli.ExecuteAsync(new("register", path, arguments));

            Assert.Equal(ProcessExitCodes.Success, exitCode);
            Assert.NotNull(settings);
            Assert.Equal(overrides ? "https://override.example" : "https://configured.example", settings.Url);
            Assert.Equal(Path.Combine(directory, overrides ? "override" : "identity"), settings.IdentityFile);
            Assert.Equal(overrides ? 2 : 3, capacity);
            using var result = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(1, result.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal("registered", result.RootElement.GetProperty("status").GetString());
            Assert.Equal("not-checked", result.RootElement.GetProperty("executionReadiness").GetString());
            Assert.DoesNotContain("sensitive-bootstrap", stdout.ToString());
            Assert.Equal("", stderr.ToString());
            Assert.False(File.Exists(settings.IdentityFile));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("sensitive-bootstrap")]
    [InlineData("abc")]
    [InlineData("  sensitive-bootstrap  ")]
    public async Task EnrollmentFailureIsVersionedRedactedAndDoesNotMixHumanOutputIntoJson(string token)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new WorkerRegistrationCli((_, _, _, _) =>
            Task.FromException(new WorkerStartupException($"Rejected {token.Trim()}")),
            new WorkerConsole(stdout, interactive: false, errorWriter: stderr), new StringReader(""), stdout);

        var exitCode = await cli.ExecuteAsync(new("register", null,
            ["--server", "https://server.example", "--token", token, "--json"]));

        Assert.Equal(ProcessExitCodes.StartupFailure, exitCode);
        using var result = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("registration-failed", result.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
        Assert.DoesNotContain(token.Trim(), stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public async Task CancelledStandardInputReturnsCancellationWithoutEnrollment()
    {
        var stdout = new StringWriter();
        var calls = 0;
        var cli = new WorkerRegistrationCli((_, _, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, new WorkerConsole(stdout, interactive: false), new CancellationReader(), stdout);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await cli.ExecuteAsync(new("register", null,
            ["--server", "https://server.example", "--token-stdin", "--json"]), cancellation.Token);

        Assert.Equal(130, exitCode);
        Assert.Equal(0, calls);
        using var result = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("cancelled", result.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CancellationBoundsInputThatDoesNotHonorTheTokenAndLateInputCannotEnroll()
    {
        var stdout = new StringWriter();
        var calls = 0;
        using var input = new PendingReader();
        var cli = new WorkerRegistrationCli((_, _, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, new WorkerConsole(stdout, interactive: false), input, stdout);
        using var cancellation = new CancellationTokenSource();
        var execution = cli.ExecuteAsync(new("register", null,
            ["--server", "https://server.example", "--token-stdin", "--json"]), cancellation.Token);
        await input.Started.Task;
        cancellation.Cancel();
        try
        {
            Assert.Equal(130, await execution.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, calls);
        }
        finally { input.Result.TrySetResult("late-sensitive-token"); }
        Assert.DoesNotContain("late-sensitive-token", stdout.ToString());
    }

    [Fact]
    public async Task ExplicitMissingConfigurationFailsBeforeEnrollment()
    {
        var stdout = new StringWriter();
        var calls = 0;
        var cli = new WorkerRegistrationCli((_, _, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, new WorkerConsole(stdout, interactive: false), new StringReader(""), stdout);

        var exitCode = await cli.ExecuteAsync(new("register", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.yml"),
            ["--server", "https://server.example", "--token", "sensitive-bootstrap", "--json"]));

        Assert.Equal(ProcessExitCodes.StartupFailure, exitCode);
        Assert.Equal(0, calls);
        using var result = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
    }

    private sealed class CancellationReader : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromCanceled<string?>(cancellationToken);
    }

    private sealed class PendingReader : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return new(Result.Task);
        }
    }
}
