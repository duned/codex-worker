namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexServer;
using System.Diagnostics;

[Collection("ServerTokenEnvironment")]
public sealed class ServerGitHubEnvironmentTests
{
    [Theory]
    [InlineData("auth status --hostname github.com")]
    [InlineData("api repos/team/project --jq .full_name")]
    [InlineData("issue list --repo team/project --json number,title")]
    [InlineData("api --method POST repos/team/project/issues -f title=Task")]
    [InlineData("api --method PATCH repos/team/project/issues/7 -f title=Task")]
    [InlineData("api --method DELETE repos/team/project/issues/7/labels/ready")]
    public async Task AdministrationAndProvisioningUseTheSameIsolatedManagedContext(string command)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TestDirectory();
        var variables = new[] { "GH_CONFIG_DIR", "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN" };
        var previous = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var operatorDirectory = Path.Combine(directory.Path, "operator");
        Directory.CreateDirectory(operatorDirectory);
        var operatorConfig = Path.Combine(operatorDirectory, "config.yml");
        await File.WriteAllTextAsync(operatorConfig, "unrelated operator settings");
        try
        {
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", operatorDirectory);
            foreach (var key in variables.Skip(1)) Environment.SetEnvironmentVariable(key, "inherited-secret-sentinel");
            var setup = new NodeGitHubSetup(directory.Path, unrelatedAuthentication: () => false);
            var prepared = await setup.ExecuteAsync(new("server", "github-cli",
                ProvisioningCommandAction.PrepareAuthentication), CancellationToken.None);
            Assert.Equal(ProvisioningCommandStatus.Succeeded, prepared.Status);

            // Provisioning CheckAuthentication applies this same shared helper to /usr/bin/gh.
            var provisioning = new ProcessStartInfo("/usr/bin/gh");
            await NodeGitHubSetup.ApplyGitHubEnvironmentAsync(provisioning, CancellationToken.None, directory.Path);
            var arguments = command.Split(' ');
            var administration = await ServerGitHubReadService.CreateGhStartInfoAsync(arguments,
                CancellationToken.None, directory.Path);

            Assert.Equal(Path.Combine(directory.Path, "github"), administration.Environment["GH_CONFIG_DIR"]);
            Assert.Equal(provisioning.Environment["GH_CONFIG_DIR"], administration.Environment["GH_CONFIG_DIR"]);
            Assert.Equal(arguments, administration.ArgumentList);
            foreach (var key in variables.Skip(1))
            {
                Assert.False(administration.Environment.ContainsKey(key));
                Assert.False(provisioning.Environment.ContainsKey(key));
            }
            Assert.Equal("unrelated operator settings", await File.ReadAllTextAsync(operatorConfig));
            Assert.Equal(operatorDirectory, Environment.GetEnvironmentVariable("GH_CONFIG_DIR"));
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUnownedManagedSetupCannotFallBackAndReportsUnavailable(bool unowned)
    {
        using var directory = new TestDirectory();
        if (unowned) Directory.CreateDirectory(Path.Combine(directory.Path, "github"));
        await Assert.ThrowsAsync<IOException>(() => ServerGitHubReadService.CreateGhStartInfoAsync(
            ["auth", "status"], CancellationToken.None, directory.Path));
        async Task<GitHubReadCommandResult> Run(IReadOnlyList<string> arguments, CancellationToken token)
        {
            await ServerGitHubReadService.CreateGhStartInfoAsync(arguments, token, directory.Path);
            throw new InvalidOperationException("Unverified authentication must never reach process startup.");
        }
        var project = new CentralProject("project", "Project", "team/project", "main", "", [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var reader = new ServerGitHubReadService(Run);
        var access = await reader.CheckAccessAsync(project);
        Assert.False(access.CliAuthenticated);
        Assert.False(access.RepositoryReadable);
        Assert.Contains("authentication could not be checked", access.Diagnostic, StringComparison.Ordinal);
        await Assert.ThrowsAsync<GitHubReadUnavailableException>(() => reader.ListIssuesAsync(project, new()));
        var writer = new ServerGitHubIssueWriteService(Run);
        await Assert.ThrowsAsync<GitHubIssueWriteUnavailableException>(() => writer.CreateIssueAsync(project, "Task", "Details"));
    }

    [Fact]
    public async Task ManagedAuthenticationIsUsedForAccessReadsAndWrites()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TestDirectory();
        var setup = new NodeGitHubSetup(directory.Path, unrelatedAuthentication: () => false);
        await setup.ExecuteAsync(new("server", "github-cli", ProvisioningCommandAction.PrepareAuthentication), CancellationToken.None);
        var calls = new List<string>();
        async Task<GitHubReadCommandResult> Run(IReadOnlyList<string> arguments, CancellationToken token)
        {
            var start = await ServerGitHubReadService.CreateGhStartInfoAsync(arguments, token, directory.Path);
            Assert.Equal(Path.Combine(directory.Path, "github"), start.Environment["GH_CONFIG_DIR"]);
            calls.Add(arguments[0]);
            var output = arguments[0] switch
            {
                "auth" => "",
                "issue" => "[]",
                _ when arguments.Contains("POST") => """
                    {"number":7,"title":"Task","body":"Details","html_url":"https://github.com/team/project/issues/7"}
                    """,
                _ => "team/project"
            };
            return new(0, output, "");
        }
        var project = new CentralProject("project", "Project", "team/project", "main", "", [], 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var reader = new ServerGitHubReadService(Run);
        var access = await reader.CheckAccessAsync(project);
        Assert.True(access.CliAuthenticated);
        Assert.True(access.RepositoryReadable);
        Assert.Empty(await reader.ListIssuesAsync(project, new()));
        var writer = new ServerGitHubIssueWriteService(Run);
        Assert.Equal(7, (await writer.CreateIssueAsync(project, "Task", "Details")).Number);
        Assert.Equal(new[] { "auth", "api", "issue", "api" }, calls);
    }

    [Fact]
    public async Task OptionalWorkerContextRetainsStandaloneBehaviorWhenManagedSetupIsAbsent()
    {
        using var directory = new TestDirectory();
        Assert.Empty(await NodeGitHubSetup.GitHubEnvironmentAsync(CancellationToken.None, directory.Path));
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string? _previousConfig = Environment.GetEnvironmentVariable("GH_CONFIG_DIR");
        private readonly string? _previousXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "server-github-env-" + Guid.NewGuid().ToString("N"));
        public TestDirectory()
        {
            Directory.CreateDirectory(Path);
            // Ownership checks must not depend on authentication on the machine running the tests.
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", System.IO.Path.Combine(Path, "operator"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", System.IO.Path.Combine(Path, "xdg"));
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", _previousConfig);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousXdg);
            Directory.Delete(Path, recursive: true);
        }
    }
}
