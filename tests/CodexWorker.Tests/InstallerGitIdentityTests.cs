using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class InstallerGitIdentityTests
{
    [Fact]
    public async Task InstalledServiceAccountIdentitySatisfiesGitConfigurationCheckAndInventory()
    {
        if (!OperatingSystem.IsLinux()) return;
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tests", "worker-installer-lifecycle-tests.sh")))
            root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(Path.GetTempPath(), "worker-git-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var identityPath = Path.Combine(directory, ".gitconfig");
            var runner = new ProcessRunner();
            var installed = await runner.RunAsync("bash",
                [Path.Combine(root.FullName, "tests", "worker-installer-lifecycle-tests.sh")], directory,
                TimeSpan.FromSeconds(60), environment: new Dictionary<string, string?>
                {
                    ["LIFECYCLE_IDENTITY_OUTPUT"] = identityPath
                });
            Assert.True(installed.ExitCode == 0, installed.StandardError + installed.StandardOutput);
            var environment = new Dictionary<string, string?>
            {
                ["HOME"] = directory,
                ["XDG_CONFIG_HOME"] = Path.Combine(directory, ".config"),
                ["GIT_CONFIG_GLOBAL"] = identityPath,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_COUNT"] = null
            };
            async Task<(int ExitCode, string Output)> Probe(string executable, IReadOnlyList<string> arguments,
                CancellationToken cancellationToken)
            {
                if (executable != "git") throw new FileNotFoundException();
                var process = await runner.RunAsync(executable, arguments, directory,
                    TimeSpan.FromSeconds(10), cancellationToken, environment);
                return (process.ExitCode, process.StandardOutput);
            }
            var discovery = new NodeCapabilityDiscovery(Probe);
            var executor = new NodeProvisioningCommandExecutor(discovery, processRunner: async (executable, arguments, token) =>
            {
                var result = await Probe(executable, arguments, token);
                return new ProvisioningProcessResult(result.ExitCode);
            });
            var service = new WorkerProvisioningAdministrationService(new ProvisioningPolicy(), discovery,
                new string('a', 32), executor);

            var result = await service.ExecuteAsync(new("git", ProvisioningCommandAction.CheckConfiguration));

            Assert.Equal("succeeded", result.Status);
            Assert.Equal(RequirementState.Satisfied, result.Capability?.Configuration);
            using var output = new StringWriter();
            ProvisioningCli.Write(result, json: false, output);
            Assert.Contains("node-configuration=satisfied", output.ToString(), StringComparison.Ordinal);
            var inventory = await CapabilityInventoryReporter.CreateAsync(discovery);
            var git = Assert.Single(inventory.Capabilities, item => item.Id == "git");
            Assert.Equal(RequirementState.Satisfied, git.Configuration);
            Assert.True(git.Readiness.Available);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
