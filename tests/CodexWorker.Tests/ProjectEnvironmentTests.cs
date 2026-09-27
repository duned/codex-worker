using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ProjectEnvironmentTests
{
    [Fact]
    public void LoadsDotenvLinesCommentsEmptyLinesAndEqualsInValues()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "test.env");
        File.WriteAllText(path, "# comment\n\nFIRST=value\nCONNECTION=Host=localhost;Password=placeholder\nEMPTY=\n");

        var values = ProjectEnvironmentFile.Load(path);

        Assert.Equal("value", values["FIRST"]);
        Assert.Equal("Host=localhost;Password=placeholder", values["CONNECTION"]);
        Assert.Equal("", values["EMPTY"]);
    }

    [Theory]
    [InlineData("NOT_AN_ASSIGNMENT\n")]
    [InlineData("=missing-key\n")]
    [InlineData("BAD-NAME=value\n")]
    [InlineData("BAD.NAME=value\n")]
    public void RejectsMalformedEntriesWithoutIncludingValues(string content)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "test.env");
        File.WriteAllText(path, content.Replace("value", "secret-marker", StringComparison.Ordinal));

        var error = Assert.Throws<InvalidDataException>(() => ProjectEnvironmentFile.Load(path));

        Assert.Contains("line 1", error.Message);
        Assert.DoesNotContain("secret-marker", error.ToString());
    }

    [Fact]
    public void ProjectWithoutEnvironmentConfigurationHasNoProjectVariables()
    {
        using var fixture = new Fixture();
        var config = WorkerConfiguration.Load(fixture.WriteProject(""));
        Assert.Null(config.Environment.File);
        Assert.Empty(config.Environment.Variables);
    }

    [Fact]
    public void LoadsEnvironmentFileRelativeToProjectYamlAndRejectsMissingFilesWithoutSecrets()
    {
        using var fixture = new Fixture();
        var envPath = Path.Combine(fixture.Root, "private.env");
        File.WriteAllText(envPath, "TOKEN=secret-marker\n");
        var config = WorkerConfiguration.Load(fixture.WriteProject("environment:\n  file: ./private.env\n"));
        Assert.Equal(envPath, config.Environment.File);
        Assert.Equal("secret-marker", config.Environment.Variables["TOKEN"]);

        var missing = Assert.Throws<InvalidDataException>(() => WorkerConfiguration.Load(fixture.WriteProject("environment:\n  file: ./missing.env\n")));
        Assert.DoesNotContain("secret-marker", missing.ToString());
    }

    [Fact]
    public void ConfigurationDiagnosticsForMalformedEnvironmentNeverIncludeValues()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "private.env"), "BAD-NAME=secret-marker\n");

        var error = Assert.Throws<InvalidDataException>(() => WorkerConfiguration.Load(
            fixture.WriteProject("environment:\n  file: ./private.env\n")));

        Assert.Contains("line 1", error.Message);
        Assert.DoesNotContain("secret-marker", error.ToString());
    }

    [Fact]
    public async Task ValidationReceivesProjectEnvironment()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await new ValidationRunner(new ProcessRunner(), 5,
                new Dictionary<string, string> { ["PROJECT_TEST_VALUE"] = "left=right" })
            .RunAsync(["test \"$PROJECT_TEST_VALUE\" = 'left=right'"] , Path.GetTempPath(), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void CodexEnvironmentReceivesOnlyItsProjectsValuesAndKeepsProtectedFiltering()
    {
        using var a = CodexEnvironment.Create(new Dictionary<string, string>
        {
            ["PROJECT_A_ONLY"] = "a-value",
            ["GITHUB_TOKEN"] = "must-not-return",
            ["GIT_CONFIG_GLOBAL"] = "/unsafe/config",
            ["HOME"] = "/unsafe/home"
        });
        using var b = CodexEnvironment.Create(new Dictionary<string, string> { ["PROJECT_B_ONLY"] = "b-value" });

        Assert.Equal("a-value", a.Variables["PROJECT_A_ONLY"]);
        Assert.DoesNotContain("PROJECT_B_ONLY", a.Variables.Keys);
        Assert.Equal("b-value", b.Variables["PROJECT_B_ONLY"]);
        Assert.DoesNotContain("PROJECT_A_ONLY", b.Variables.Keys);
        Assert.True(!a.Variables.TryGetValue("GITHUB_TOKEN", out var githubToken) || githubToken is null);
        Assert.NotEqual("/unsafe/config", a.Variables["GIT_CONFIG_GLOBAL"]);
        Assert.NotEqual("/unsafe/home", a.Variables["HOME"]);
        Assert.Equal("1", a.Variables["GIT_CONFIG_NOSYSTEM"]);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codex-worker-env-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string WriteProject(string extra)
        {
            var path = Path.Combine(Root, "project.yml");
            File.WriteAllText(path, "project:\n  name: Demo\n  repository: owner/demo\n  directory: .\ngit:\n  baseBranch: main\ngithub:\n  readyLabel: ready\n  workingLabel: working\n  blockedLabel: blocked\n  failedLabel: failed\n  doneLabel: done\ncodex:\n  instructionsFile: AGENTS.md\n" + extra);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
