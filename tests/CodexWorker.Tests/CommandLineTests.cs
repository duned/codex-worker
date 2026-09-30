using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class CommandLineTests
{
    [Theory]
    [MemberData(nameof(GlobalHelpArguments))]
    public async Task GlobalHelpSucceeds(string[] args)
    {
        var (exitCode, output) = await RunAsync(args);

        Assert.Equal(ProcessExitCodes.Success, exitCode);
        Assert.Contains("Usage: codex-worker", output);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task RegisterHelpSucceedsAndDocumentsArgumentsAndExampleWithoutRegistration(string help)
    {
        var (simpleExitCode, simpleOutput) = await RunAsync(["register", help]);
        Assert.Equal(ProcessExitCodes.Success, simpleExitCode);
        Assert.Contains("Usage: codex-worker register", simpleOutput);
        var identityPath = Path.Combine(Path.GetTempPath(), $"help-{Guid.NewGuid():N}", "worker-id");
        var (exitCode, output) = await RunAsync(["register", "--server", "http://127.0.0.1:1", "--token-stdin",
            "--identity-file", identityPath, help]);

        Assert.Equal(ProcessExitCodes.Success, exitCode);
        Assert.Contains("--server <url>", output);
        Assert.Contains("--token <registration-token>", output);
        Assert.Contains("--identity-file <path>", output);
        Assert.Contains("--token-stdin", output);
        Assert.Contains("--capacity <1..8>", output);
        Assert.Contains("Example:", output);
        Assert.DoesNotContain("registration failed", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.GetDirectoryName(identityPath)));
    }

    [Theory]
    [MemberData(nameof(InvalidRegisterArguments))]
    public async Task InvalidRegisterArgumentsShowUsageAndFailBeforeNetworkActivity(string[] args)
    {
        var (exitCode, output) = await RunAsync(args);

        Assert.Equal(ProcessExitCodes.StartupFailure, exitCode);
        Assert.Contains("Usage: codex-worker register", output);
        Assert.Contains("Worker registration failed", output);
        Assert.DoesNotContain("sensitive-value", output, StringComparison.Ordinal);
    }

    public static TheoryData<string[]> GlobalHelpArguments => new()
    {
        new[] { "--help" },
        new[] { "-h" },
        new[] { "worker.yml", "--help" }
    };

    public static TheoryData<string[]> InvalidRegisterArguments => new()
    {
        new[] { "register", "--server", "https://server.example" },
        new[] { "register", "--unknown", "value" },
        new[] { "register", "--server", "not-a-url", "--token", "token" },
        new[] { "register" },
        new[] { "register", "--server" },
        new[] { "register", "--server", "--token", "sensitive-value" },
        new[] { "register", "--sensitive-value" },
        new[] { "register", "--token", "sensitive-value", "--token-stdin" },
        new[] { "register", "--server", "https://server.example", "--capacity", "9", "--token", "sensitive-value" },
        new[] { "register", "--server", "https://user:sensitive-value@server.example", "--token", "token" },
        new[] { "register", "--token", "sensitive-value", "--token", "other" },
        new[] { "register", "--identity-file", "", "--token", "sensitive-value" }
    };

    private static async Task<(int ExitCode, string Output)> RunAsync(string[] args)
    {
        var original = Console.Out;
        var originalError = Console.Error;
        using var capture = new StringWriter();
        using var errorCapture = new StringWriter();
        Console.SetOut(capture);
        Console.SetError(errorCapture);
        try
        {
            var exitCode = await Program.Main(args);
            return (exitCode, capture.ToString() + errorCapture.ToString());
        }
        finally
        {
            Console.SetOut(original);
            Console.SetError(originalError);
        }
    }
}
