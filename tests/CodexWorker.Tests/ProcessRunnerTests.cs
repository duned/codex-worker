using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProcessRunnerTests
{
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
