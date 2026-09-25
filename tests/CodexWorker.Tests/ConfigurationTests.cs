using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ConfigurationTests
{
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
                telegram:
                  enabled: false
                worker:
                  pollingSeconds: 9
                """);

            var config = WorkerConfiguration.Load(path);

            Assert.Equal(Path.Combine(folder, "checkout"), config.Project.Directory);
            Assert.Equal(Path.Combine(folder, "checkout", "AGENTS.md"), config.Codex.InstructionsFile);
            Assert.Equal("./check.sh", Assert.Single(config.Validation.Commands));
            Assert.Equal(9, config.Worker.PollingSeconds);
            Assert.False(config.Git.AutoMerge);
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
    public void RejectsDuplicateGitHubLabelsAndInvalidPollingInterval()
    {
        var config = ValidConfig();
        config.GitHub.FailedLabel = config.GitHub.ReadyLabel;
        config.Worker.PollingSeconds = 0;
        var error = Assert.Throws<InvalidDataException>(config.Validate);
        Assert.Contains("labels must be distinct", error.Message);
        Assert.Contains("pollingSeconds", error.Message);
    }

    private static WorkerConfiguration ValidConfig() => new()
    {
        Project = new ProjectSettings { Name = "Example", Repository = "owner/repo", Directory = "/tmp/project" },
        Git = new GitSettings(),
        GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" },
        Codex = new CodexSettings { InstructionsFile = "/tmp/project/AGENTS.md" },
        Validation = new ValidationSettings(),
        Telegram = new TelegramSettings(),
        Worker = new WorkerSettings()
    };
}
