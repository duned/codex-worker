using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ManagedServerRequiresSecureRemoteTransport()
    {
        var server = new WorkerServerSettings { Enabled = true, Url = "http://server.example:5090" };
        var error = Assert.Throws<InvalidDataException>(server.Validate);
        Assert.Contains("must use HTTPS", error.Message);
        server.Url = "http://127.0.0.1:5090";
        server.Validate();
        server.Url = "https://server.example:5090";
        server.Validate();
    }

    [Fact]
    public void ProjectOwnershipDefaultsToStandaloneAndManagedRequiresServerConfiguration()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-ownership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "worker.yml");
            File.WriteAllText(path, "projects:\n  directory: ./projects\n");
            Assert.Equal("standalone", GlobalWorkerConfiguration.Load(path).Projects.Ownership);
            File.WriteAllText(path, "projects:\n  directory: ./projects\n  ownership: managed\n");
            Assert.Throws<InvalidDataException>(() => GlobalWorkerConfiguration.Load(path));
            File.WriteAllText(path, "projects:\n  directory: ./projects\n  ownership: managed\nserver:\n  enabled: true\n  url: https://server.example:5090\n");
            Assert.Equal("managed", GlobalWorkerConfiguration.Load(path).Projects.Ownership);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void IdentityPathResolvesRelativeToConfigurationAndExpandsHome()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "worker.yml");
            File.WriteAllText(path, "projects:\n  directory: ./projects\nserver:\n  enabled: true\n  url: https://server.example\n  identityFile: ./state/worker-id\n");
            Assert.Equal(Path.Combine(folder, "state", "worker-id"), GlobalWorkerConfiguration.Load(path).Server.IdentityFile);

            File.WriteAllText(path, "projects:\n  directory: ./projects\nserver:\n  enabled: true\n  url: https://server.example\n  identityFile: ~/worker-id\n");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "worker-id"),
                GlobalWorkerConfiguration.Load(path).Server.IdentityFile);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void GlobalYamlLoadsWorkerProvisioningPolicyAndDefaultsToDisabled()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "worker.yml");
            File.WriteAllText(path, "projects:\n  directory: ./projects\n");
            Assert.False(GlobalWorkerConfiguration.Load(path).Worker.Provisioning.Enabled);

            File.WriteAllText(path, """
                projects:
                  directory: ./projects
                worker:
                  provisioning:
                    enabled: true
                    allowNonPrivileged: true
                    allowCredentials: false
                    allowedPrivilegedActions:
                      - tool:git:install
                    deniedActions:
                      - tool:docker:install
                """);
            var policy = GlobalWorkerConfiguration.Load(path).Worker.Provisioning;
            Assert.True(policy.Enabled);
            Assert.True(policy.AllowNonPrivileged);
            Assert.False(policy.AllowCredentials);
            Assert.Equal("tool:git:install", Assert.Single(policy.AllowedPrivilegedActions));
            Assert.Equal("tool:docker:install", Assert.Single(policy.DeniedActions));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void LoadsYamlAndResolvesProjectAndInstructionPaths()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "project.yml");
            File.WriteAllText(path, """
                project:
                  name: Example
                  repository: owner/repo
                  directory: ./checkout
                git:
                  baseBranch: main
                  featurePrefix: feature/
                  completedPrefix: completed/
                  autoMerge: false
                  pushCompletedBranch: false
                  deleteLocalFeatureBranch: true
                github:
                  readyLabel: ready
                  workingLabel: working
                  blockedLabel: blocked
                  failedLabel: failed
                  doneLabel: done
                codex:
                  instructionsFile: AGENTS.md
                  reasoningEffort: high
                  timeoutMinutes: 17
                validation:
                  commands:
                    - ./check.sh
                worker:
                  gitTimeoutSeconds: 80
                  githubTimeoutSeconds: 40
                # nested validation timeout is optional
                """);

            var config = WorkerConfiguration.Load(path);

            Assert.Equal(Path.Combine(folder, "checkout"), config.Project.Directory);
            Assert.Equal(Path.Combine(folder, "checkout", "AGENTS.md"), config.Codex.InstructionsFile);
            Assert.Equal("./check.sh", Assert.Single(config.Validation.Commands));
            Assert.Equal(80, config.Worker.GitTimeoutSeconds);
            Assert.Equal(900, config.Validation.TimeoutSeconds);
            Assert.Equal(2, config.Validation.MaxFixAttempts);
            Assert.Equal(7, config.Worker.RecoveryRetentionDays);
            Assert.False(config.Git.AutoMerge);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void RejectsUnknownAndDuplicateYamlKeys()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "project.yml");
            File.WriteAllText(path, "worker:\n  pollingSeconds: 10\n  pollSeconds: 3\n");
            Assert.Throws<InvalidDataException>(() => WorkerConfiguration.Load(path));
            File.WriteAllText(path, "worker:\n  pollingSeconds: 10\n  pollingSeconds: 3\n");
            Assert.Throws<InvalidDataException>(() => WorkerConfiguration.Load(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void RejectsWorkerGlobalSettingsInsideProjectFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "project.yml");
            File.WriteAllText(path, "worker:\n  pollingSeconds: 10\n");
            Assert.Throws<InvalidDataException>(() => WorkerConfiguration.Load(path));
            File.WriteAllText(path, "telegram:\n  enabled: false\n");
            Assert.Throws<InvalidDataException>(() => WorkerConfiguration.Load(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("owner/repo", true)]
    [InlineData("owner", false)]
    [InlineData("owner/repo/extra", false)]
    public void ValidatesRepositoryShape(string repository, bool valid)
    {
        var config = ValidConfig();
        config.Project.Repository = repository;
        if (valid) config.Validate();
        else Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void RejectsDuplicateGitHubLabelsAndInvalidGitTimeout()
    {
        var config = ValidConfig();
        config.GitHub.FailedLabel = config.GitHub.ReadyLabel;
        config.Worker.GitTimeoutSeconds = 0;
        var error = Assert.Throws<InvalidDataException>(config.Validate);
        Assert.Contains("labels must be distinct", error.Message);
        Assert.Contains("gitTimeoutSeconds", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void RejectsInvalidValidationRepairAttemptCount(int attempts)
    {
        var config = ValidConfig();
        config.Validation.MaxFixAttempts = attempts;
        var error = Assert.Throws<InvalidDataException>(config.Validate);
        Assert.Contains("validation.maxFixAttempts", error.Message);
    }

    [Fact]
    public void DefaultsValidationRepairAttemptsToTwo()
    {
        Assert.Equal(2, new ValidationSettings().MaxFixAttempts);
    }

    private static WorkerConfiguration ValidConfig() => new()
    {
        Project = new ProjectSettings { Name = "Example", Repository = "owner/repo", Directory = "/tmp/project" },
        Git = new GitSettings(),
        GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" },
        Codex = new CodexSettings { InstructionsFile = "/tmp/project/AGENTS.md" },
        Validation = new ValidationSettings(),
        Worker = new WorkerSettings()
    };
}
