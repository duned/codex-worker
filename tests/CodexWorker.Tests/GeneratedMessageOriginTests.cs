using CodexProvisioning;
using CodexServer;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class GeneratedMessageOriginTests
{
    [Theory]
    [InlineData(false, "codex1-vm")]
    [InlineData(true, "codex2-vm")]
    public async Task StandaloneAndManagedWorkerCommentsUseOperationalDisplayName(bool managed, string displayName)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = new TestDirectory();
        var variables = new[] { "HOME", "PATH", "CODEX_WORKER_DISPLAY_NAME", "GH_CONFIG_DIR", "XDG_CONFIG_HOME" };
        var previous = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("HOME", directory.Path);
            Environment.SetEnvironmentVariable("PATH", directory.Path + ":" + previous["PATH"]);
            Environment.SetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME", displayName);
            Environment.SetEnvironmentVariable("GH_CONFIG_DIR", Path.Combine(directory.Path, "operator"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(directory.Path, "xdg"));
            if (managed)
            {
                var prepared = await new NodeGitHubSetup().ExecuteAsync(
                    new("0123456789abcdef0123456789abcdef", "github-cli", ProvisioningCommandAction.PrepareAuthentication), CancellationToken.None);
                Assert.Equal(ProvisioningCommandStatus.Succeeded, prepared.Status);
            }
            var executable = Path.Combine(directory.Path, "gh");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = '--body' ]; then
                    shift
                    printf '%s' "$1" > "$0.body"
                    exit 0
                  fi
                  shift
                done
                exit 1
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var client = new GitHubClient(new ProcessRunner(), "owner/repo", 10, requireManagedAuthentication: managed);
            const string report = "### Execution report\n\nCompleted successfully.";

            await client.CommentAsync(7, report, CancellationToken.None);

            Assert.Equal($"🤖 Codex Worker · {displayName}\n\n{report}", await File.ReadAllTextAsync(executable + ".body"));
            Assert.Equal(displayName, WorkerIdentity.DisplayName);
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Theory]
    [InlineData("### Execution report\n\nCompleted")]
    [InlineData("### Failure\n\nFailed\n\n### Recovery\n\n- Inspect preserved work.")]
    [InlineData("### Integration recovery requires inspection\n\nOriginal execution: unavailable.")]
    public async Task EveryCommentRetainsContentAndIncludesOriginOnce(string report)
    {
        var calls = new List<string[]>();
        var client = new GitHubClient("owner/repo", (args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new ProcessResult(0, "", ""));
        }, new(CodexComponent.Worker, "codex2-vm"));

        await client.CommentAsync(7, report, CancellationToken.None);
        await client.ReplaceLabelAsync(7, "working", "done", CancellationToken.None);
        await client.CloseAsync(7, CancellationToken.None);

        Assert.Equal(new[] { "issue", "comment", "7", "--repo", "owner/repo", "--body",
            $"🤖 Codex Worker · codex2-vm\n\n{report}" }, calls[0]);
        Assert.Equal(new[] { "issue", "edit", "7", "--repo", "owner/repo", "--remove-label", "working", "--add-label", "done" }, calls[1]);
        Assert.Equal(new[] { "issue", "close", "7", "--repo", "owner/repo" }, calls[2]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("codex-server-main")]
    public void ServerOriginUsesConfiguredDisplayNameOrHostFallback(string? displayName)
    {
        var configuration = new ServerConfiguration { DisplayName = displayName };
        configuration.Validate();
        var expected = string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName;

        Assert.Equal(CodexComponent.Server, configuration.MessageOrigin.Component);
        Assert.Equal(expected, configuration.MessageOrigin.DisplayName);
        Assert.Equal($"🧭 Codex Server · {expected}\n\n### Report", configuration.MessageOrigin.Format("### Report"));
    }

    [Fact]
    public void DistinctWorkerNamesRemainDistinguishableWithTheSameReport()
    {
        var first = new GeneratedMessageOrigin(CodexComponent.Worker, "codex1-vm").Format("Completed.");
        var second = new GeneratedMessageOrigin(CodexComponent.Worker, "codex2-vm").Format("Completed.");

        Assert.NotEqual(first, second);
        Assert.Contains("codex1-vm", first);
        Assert.Contains("codex2-vm", second);
    }

    [Theory]
    [InlineData("invalid\nname")]
    [InlineData("invalid\tname")]
    public void ServerDisplayNamesRejectControlCharacters(string displayName)
    {
        var configuration = new ServerConfiguration { DisplayName = displayName };

        Assert.Throws<InvalidDataException>(configuration.Validate);
    }

    [Fact]
    public void DisplayNamesCannotInjectMarkupMentionsOrAdditionalLines()
    {
        var origin = new GeneratedMessageOrigin(CodexComponent.Worker, "node\n<script>@team_*[link]");

        Assert.Equal("🤖 Codex Worker · node &lt;script&gt;&#64;team&#95;&#42;&#91;link&#93;\n\nReport", origin.Format("Report"));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "message-origin-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
