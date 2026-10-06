namespace CodexWorker.Tests;

using System.Text.Json;
using CodexWorker;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerRegistrationCliTests
{
    [Theory]
    [InlineData("enroll")]
    [InlineData("associate")]
    public async Task PairingPrintsPublicRequestAndUsesProtectedInputWithoutReplacingExistingState(string operation)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pairing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "identity");
            var identity = await WorkerIdentity.LoadOrCreateAsync(path);
            var preserved = Path.Combine(directory, "recovery-resource");
            await File.WriteAllTextAsync(preserved, "retained history and uncertain work");
            using var writer = new StringWriter();
            var invoked = false;
            var cli = new WorkerRegistrationCli((settings, _, token, _) =>
            {
                invoked = true;
                Assert.Equal(operation, settings.RegistrationOperation);
                Assert.Equal("protected-authorization", token);
                return Task.CompletedTask;
            }, new WorkerConsole(writer, interactive: false, errorWriter: writer), TextReader.Null, writer,
                _ => Task.FromResult("protected-authorization"));
            Assert.Equal(ProcessExitCodes.Success, await cli.ExecuteAsync(new("register", null,
                ["--server", "https://server.example", "--identity-file", path, "--operation", operation, "--pair"])));
            Assert.True(invoked);
            Assert.Contains(identity, writer.ToString());
            Assert.Contains("https://server.example", writer.ToString());
            Assert.DoesNotContain("protected-authorization", writer.ToString());
            Assert.Equal(identity, await WorkerIdentity.LoadAsync(path));
            Assert.Equal("retained history and uncertain work", await File.ReadAllTextAsync(preserved));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("http://127.0.0.1:5090", "enroll")]
    [InlineData("https://server.example", "associate")]
    public async Task PairingRejectsHttpAndMissingExistingIdentityBeforeSecretOrBootstrap(string server, string operation)
    {
        var path = Path.Combine(Path.GetTempPath(), $"absent-pairing-{Guid.NewGuid():N}");
        using var writer = new StringWriter();
        var invoked = false;
        var cli = new WorkerRegistrationCli((_, _, _, _) => { invoked = true; return Task.CompletedTask; },
            new WorkerConsole(writer, interactive: false, errorWriter: writer), TextReader.Null, writer,
            _ => throw new InvalidOperationException("Secret input must not be requested."));
        Assert.Equal(ProcessExitCodes.StartupFailure, await cli.ExecuteAsync(new("register", null,
            ["--server", server, "--identity-file", path, "--operation", operation, "--pair"])));
        Assert.False(invoked);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PairingCancellationDuringProtectedInputPreservesIdentityAndDoesNotBootstrap()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cancel-pairing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "identity");
            var identity = await WorkerIdentity.LoadOrCreateAsync(path);
            using var cancellation = new CancellationTokenSource();
            using var writer = new StringWriter();
            var invoked = false;
            var cli = new WorkerRegistrationCli((_, _, _, _) => { invoked = true; return Task.CompletedTask; },
                new WorkerConsole(writer, interactive: false, errorWriter: writer), TextReader.Null, writer,
                token => { cancellation.Cancel(); return Task.FromCanceled<string>(token); });
            Assert.Equal(WorkerCliOutput.Cancelled, await cli.ExecuteAsync(new("register", null,
                ["--server", "https://server.example", "--identity-file", path, "--pair"]), cancellation.Token));
            Assert.False(invoked);
            Assert.Equal(identity, await WorkerIdentity.LoadAsync(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("enroll")]
    [InlineData("rotate")]
    [InlineData("recover")]
    [InlineData("associate")]
    public async Task ExplicitOperationReachesRegistrationSeam(string operation)
    {
        using var writer = new StringWriter();
        var invoked = false;
        var cli = new WorkerRegistrationCli((settings, capacity, token, cancellationToken) =>
        {
            Assert.Equal(operation, settings.RegistrationOperation);
            invoked = true;
            return Task.CompletedTask;
        }, new WorkerConsole(writer, interactive: false, errorWriter: writer), new StringReader("protected-token"), writer);
        Assert.Equal(ProcessExitCodes.Success, await cli.ExecuteAsync(new("register", null,
            ["--server", "https://server.example", "--operation", operation, "--token-stdin"])));
        Assert.True(invoked);
        Assert.DoesNotContain("protected-token", writer.ToString());
    }

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
    [InlineData("sensitive-bootstrap", true)]
    [InlineData("abc", true)]
    [InlineData("  sensitive-bootstrap  ", true)]
    [InlineData("sensitive-bootstrap", false)]
    [InlineData("abc", false)]
    [InlineData("  sensitive-bootstrap  ", false)]
    public async Task EnrollmentFailureIsVersionedRedactedAndDoesNotMixHumanOutputIntoJson(string token, bool json)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new WorkerRegistrationCli((_, _, _, _) =>
            Task.FromException(new WorkerStartupException($"Rejected {token.Trim()}")),
            new WorkerConsole(stdout, interactive: false, errorWriter: stderr), new StringReader(token + "\n"), stdout);

        string[] arguments = json ? ["--server", "https://server.example", "--token-stdin", "--json"] :
            ["--server", "https://server.example", "--token-stdin"];
        var exitCode = await cli.ExecuteAsync(new("register", null, arguments));

        Assert.Equal(ProcessExitCodes.StartupFailure, exitCode);
        if (json)
        {
            using var result = JsonDocument.Parse(stdout.ToString());
            Assert.Equal("registration-failed", result.RootElement.GetProperty("diagnostic").GetProperty("code").GetString());
            Assert.Equal("", stderr.ToString());
        }
        else Assert.Contains("Worker registration failed", stderr.ToString());
        Assert.DoesNotContain(token.Trim(), stdout.ToString());
        Assert.DoesNotContain(token.Trim(), stderr.ToString());
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
            ["--server", "https://server.example", "--token-stdin", "--json"]));

        Assert.Equal(ProcessExitCodes.StartupFailure, exitCode);
        Assert.Equal(0, calls);
        using var result = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CommandLineTokenIsRejectedBeforeConfigurationInputOrEnrollment(bool json, bool equalsSyntax)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rejected-registration-{Guid.NewGuid():N}");
        var identity = Path.Combine(directory, "worker-id");
        using var writer = new StringWriter();
        var calls = 0;
        using var input = new PendingReader();
        var cli = new WorkerRegistrationCli((_, _, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }, new WorkerConsole(writer, interactive: false, errorWriter: writer), input, writer);
        const string sentinel = "SENTINEL-command-line-secret";
        string[] tokenArguments = equalsSyntax ? ["--token=" + sentinel] : ["--token", sentinel];
        var arguments = new List<string> { "--identity-file", identity, "--token-stdin" };
        arguments.AddRange(tokenArguments);
        if (json) arguments.Add("--json");
        var result = await cli.ExecuteAsync(new("register", Path.Combine(directory, "missing.yml"), arguments));
        Assert.Equal(ProcessExitCodes.StartupFailure, result);
        Assert.Equal(0, calls);
        Assert.False(input.Started.Task.IsCompleted);
        Assert.False(Directory.Exists(directory));
        Assert.Contains("--token is no longer supported", writer.ToString());
        Assert.DoesNotContain(sentinel, writer.ToString());
        if (json)
        {
            using var document = JsonDocument.Parse(writer.ToString());
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        }
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
