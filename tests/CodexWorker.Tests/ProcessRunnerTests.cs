using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProcessRunnerTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Slow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationTerminatesSessionIncludingChildren(bool ignoreTermination)
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"shutdown-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var shutdown = new CancellationTokenSource();
        try
        {
            var script = ignoreTermination
                ? "trap '' TERM; sleep 60 & echo $! > child; echo $$ > parent; wait"
                : "trap 'echo graceful > stopped; exit 0' TERM; sleep 60 & echo $! > child; echo $$ > parent; wait";
            var run = new ProcessRunner().RunAsync("/bin/sh", ["-c", script], directory, cancellationToken: shutdown.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(directory, "parent"))) await Task.Delay(20, deadline.Token);
            var child = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child")), System.Globalization.CultureInfo.InvariantCulture);
            var parent = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "parent")), System.Globalization.CultureInfo.InvariantCulture);
            shutdown.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(IsRunning(parent));
            Assert.False(IsRunning(child));
            Assert.Equal(!ignoreTermination, File.Exists(Path.Combine(directory, "stopped")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public async Task TailCaptureRetainsTerminalErrorAfterLargeAgentOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await new ProcessRunner().RunAsync("/bin/sh",
            ["-c", "head -c 180000 /dev/zero | tr '\\000' x; printf '\\n{\"type\":\"turn.failed\",\"error\":{\"code\":\"context_length_exceeded\"}}\\n'; exit 1"],
            Path.GetTempPath(), TimeSpan.FromSeconds(10), captureTail: true);

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.StandardOutput.Length < 161000);
        Assert.Contains("showing tail", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("Codex context limit", CodexFailure.Classify(CodexFailure.ProcessEvidence(result.StandardOutput, result.StandardError)).Category);
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // A killed child can remain a zombie briefly until the system reaps it.
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0] != "Z";
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    [Trait("TestType", "Integration")]
    [Trait("Duration", "Medium")]
    [Fact]
    public async Task TimeoutKillsProcessAndKeepsOutputDiagnostics()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new ProcessRunner();
        var exception = await Assert.ThrowsAsync<ProcessTimeoutException>(() => runner.RunAsync("/bin/sh",
            ["-c", "printf before-timeout; printf diagnostic >&2; sleep 20"], Path.GetTempPath(), TimeSpan.FromMilliseconds(200)));
        Assert.Contains("before-timeout", exception.StandardOutput);
        Assert.Contains("diagnostic", exception.StandardError);
        Assert.Contains("before-timeout", exception.Message);
    }
}
