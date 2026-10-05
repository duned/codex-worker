using CodexWorker;

namespace CodexWorker.Tests;

public sealed class WorkerServiceAdministrationContextTests
{
    [Fact]
    public void PackagedRootAdministrationTransitionsAndOtherOperatorsCannotAuthenticateThemselvesAsTheNode()
    {
        var command = new WorkerCommandLine("provision", null, ["login", "codex-cli"]);
        Assert.True(WorkerServiceAdministrationContext.RequiresTransition(command, packaged: true, "root"));
        Assert.False(WorkerServiceAdministrationContext.RequiresTransition(command, packaged: true, "codex-worker", serviceContext: true));
        Assert.Throws<InvalidOperationException>(() => WorkerServiceAdministrationContext.RequiresTransition(command, packaged: true, "codex-worker"));
        Assert.Throws<InvalidOperationException>(() => WorkerServiceAdministrationContext.RequiresTransition(command, packaged: true, "operator"));
        Assert.False(WorkerServiceAdministrationContext.RequiresTransition(command, packaged: false, "operator"));
        Assert.False(WorkerServiceAdministrationContext.RequiresTransition(new("run", null, []), packaged: true, "codex-worker"));
    }

    [Theory]
    [InlineData("provision", "login", "codex-cli")]
    [InlineData("provision", "check-authentication", "codex-cli")]
    [InlineData("provision", "logout", "codex-cli")]
    [InlineData("provision", "login", "github-cli")]
    [InlineData("provision", "prepare-authentication", "github-cli")]
    [InlineData("provision", "check-authentication", "github-cli")]
    [InlineData("provision", "logout", "github-cli")]
    [InlineData("provision", "status", null)]
    [InlineData("capabilities", "refresh", null)]
    [InlineData("credential", "check", "codex-cli")]
    public void AuthenticationMutationsAndObservationsUseOneProtectedServiceContext(string command, string action, string? capability)
    {
        var arguments = capability is null ? new[] { action } : [action, capability];
        var start = WorkerServiceAdministrationContext.CreateStartInfo(new(command, null, arguments));
        Assert.Equal("/usr/bin/systemd-run", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(["PATH"], start.Environment.Keys);
        Assert.DoesNotContain("CODEX_HOME", start.Environment.Keys);
        Assert.DoesNotContain("GH_TOKEN", start.Environment.Keys);
        Assert.Contains("--property=User=codex-worker", start.ArgumentList);
        Assert.Contains("--property=Group=codex-worker", start.ArgumentList);
        Assert.Contains("--property=EnvironmentFile=/etc/codex-worker/worker.env", start.ArgumentList);
        Assert.Contains("--property=UMask=0077", start.ArgumentList);
        Assert.Contains("--pipe", start.ArgumentList);
        var boundary = start.ArgumentList.IndexOf("--");
        Assert.Equal(["/opt/codex-worker/CodexWorker", command, "--config", WorkerCommandLine.LinuxDefaultConfigurationPath, .. arguments],
            start.ArgumentList.Skip(boundary + 1));
    }

    [Fact]
    public void ContextMatchesPackagedUnitAndLeavesCodexOverridesToProtectedEnvironmentFile()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CodexWorker.sln")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root unavailable.");
        var unit = File.ReadAllLines(Path.Combine(root, "packaging/linux/codex-worker.service"));
        foreach (var property in WorkerServiceAdministrationContext.ServiceProperties.Where(property =>
            property.StartsWith("Environment", StringComparison.Ordinal) || property.StartsWith("User=", StringComparison.Ordinal) ||
            property.StartsWith("Group=", StringComparison.Ordinal) || property.StartsWith("WorkingDirectory=", StringComparison.Ordinal) ||
            property.StartsWith("UMask=", StringComparison.Ordinal)))
            Assert.Contains(property, unit);
        Assert.DoesNotContain(WorkerServiceAdministrationContext.ServiceProperties,
            property => property.Contains("CODEX_HOME", StringComparison.Ordinal) || property.Contains("CODEX_WORKER_CODEX_EXECUTABLE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("register")]
    [InlineData("config")]
    public void BoundaryCannotLaunchOtherWorkerOperations(string command)
    {
        Assert.Throws<ArgumentException>(() => WorkerServiceAdministrationContext.CreateStartInfo(new(command, null, [])));
    }

    [Theory]
    [InlineData("login", "--property=User=root")]
    [InlineData("shell", "codex-cli")]
    public void BoundaryRejectsUntypedOperations(string action, string capability)
    {
        Assert.Throws<ArgumentException>(() => WorkerServiceAdministrationContext.CreateStartInfo(new("provision", null, [action, capability])));
    }
}
