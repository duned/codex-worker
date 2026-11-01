namespace CodexWorker.Tests;

public sealed class InterruptedWorkspaceProcessesTests
{
    [Theory]
    [InlineData(false, null, "process-scope-missing")]
    [InlineData(false, "old-scope", "process-scope-changed")]
    [InlineData(false, "scope", "workspace-process-active")]
    [InlineData(true, "old-scope", "workspace-process-active")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task ScopeAndJoinedEvidenceNeverOverrideActiveWorkspace(bool joined, string? scope, string expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "self"));
            var process = Directory.CreateDirectory(Path.Combine(root, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
            await File.WriteAllTextAsync(Path.Combine(root, "self", "cgroup"), "scope");
            await File.WriteAllTextAsync(Path.Combine(root, "self", "status"), "Uid:\t1000\t1000\t1000\t1000\n");
            await File.WriteAllTextAsync(Path.Combine(process, "stat"), "1 (worker) S 0");
            await File.WriteAllTextAsync(Path.Combine(process, "status"), "Uid:\t1000\t1000\t1000\t1000\n");
            await File.WriteAllTextAsync(Path.Combine(process, "cgroup"), "scope");
            Directory.CreateSymbolicLink(Path.Combine(process, "cwd"), root);
            Assert.Equal(expected, await InterruptedWorkspaceProcesses.CheckAsync(root, joined, scope, CancellationToken.None, root));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                InterruptedWorkspaceProcesses.CheckAsync(root, true, "scope", cancellation.Token, root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("other", false, null)]
    [InlineData("scope", false, "relevant-process-unreadable")]
    [InlineData("other", true, null)]
    [InlineData("scope", true, "relevant-process-unreadable")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task ProtectedProcessRequiresIndependentScopeProof(string childScope, bool joined, string? expected)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "self"));
            var own = Directory.CreateDirectory(Path.Combine(root, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
            await File.WriteAllTextAsync(Path.Combine(root, "self", "cgroup"), "scope");
            await File.WriteAllTextAsync(Path.Combine(root, "self", "status"), "Uid:\t1000\t1000\t1000\t1000\n");
            await File.WriteAllTextAsync(Path.Combine(own, "stat"), "1 (worker) S 0");
            await File.WriteAllTextAsync(Path.Combine(own, "status"), "State:\tZ\n");
            var child = Directory.CreateDirectory(Path.Combine(root, "2000000000")).FullName;
            await File.WriteAllTextAsync(Path.Combine(child, "status"), "Uid:\t0\t0\t0\t0\n");
            await File.WriteAllTextAsync(Path.Combine(child, "cgroup"), childScope);
            // A non-symlink cwd simulates a live process whose workspace cannot be read.
            Directory.CreateDirectory(Path.Combine(child, "cwd"));
            Assert.Equal(expected, await InterruptedWorkspaceProcesses.CheckAsync(root, joined, "scope", CancellationToken.None, root));
            Directory.Delete(child, true);
            Assert.Null(await InterruptedWorkspaceProcesses.CheckAsync(root, joined, "scope", CancellationToken.None, root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
