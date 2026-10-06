namespace CodexWorker.Tests;

using CodexServer;
using System.Diagnostics;
using System.Text.Json;

[Collection("ServerTokenEnvironment")]
public sealed class ServerConfigurationMutationTests
{
    [Fact]
    public void InstanceDisplayNameCanBeConfiguredThroughInstalledEnvironmentFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "server.env");
        File.WriteAllText(path, "Server__EnableLocalProvisioning=false\n");
        var configuration = new ServerConfiguration();

        ServerConfigurationMutation.Set(path, "DisplayName", "codex-server-main", configuration);

        Assert.Equal("codex-server-main", configuration.MessageOrigin.DisplayName);
        Assert.Contains("Server__DisplayName=\"codex-server-main\"\n", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigSetUpdatesInstalledEnvironmentFileAtomicallyAndReportsRestartRequirement()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = Path.Combine(temporary.Path, "server.env");
        File.WriteAllText(configurationPath,
            "# retained operator comment\r\nServer__EnableLocalProvisioning=false\r\n" +
            "CODEX_SERVER_MANAGEMENT_TOKEN=private-token\r\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(configurationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var originalOwner = OperatingSystem.IsLinux() ? ReadOwnerGroup(configurationPath) : null;

        var originalEnvironment = new Dictionary<string, string?>
        {
            ["CODEX_SERVER_CONFIGURATION_FILE"] = Environment.GetEnvironmentVariable("CODEX_SERVER_CONFIGURATION_FILE"),
            ["Server__ListenUrl"] = Environment.GetEnvironmentVariable("Server__ListenUrl"),
            ["Server__DataDirectory"] = Environment.GetEnvironmentVariable("Server__DataDirectory"),
            ["Server__DatabasePath"] = Environment.GetEnvironmentVariable("Server__DatabasePath"),
            ["Server__EnableLocalProvisioning"] = Environment.GetEnvironmentVariable("Server__EnableLocalProvisioning"),
            ["Server__AllowLocalProvisioningElevation"] = Environment.GetEnvironmentVariable("Server__AllowLocalProvisioningElevation"),
            ["Server__WorkerStaleAfterSeconds"] = Environment.GetEnvironmentVariable("Server__WorkerStaleAfterSeconds"),
            ["Server__ExecutionLeaseDurationSeconds"] = Environment.GetEnvironmentVariable("Server__ExecutionLeaseDurationSeconds"),
            ["Server__ExecutionLeaseRenewalIntervalSeconds"] = Environment.GetEnvironmentVariable("Server__ExecutionLeaseRenewalIntervalSeconds")
        };
        Environment.SetEnvironmentVariable("CODEX_SERVER_CONFIGURATION_FILE", configurationPath);
        Environment.SetEnvironmentVariable("Server__ListenUrl", "http://127.0.0.1:5090");
        Environment.SetEnvironmentVariable("Server__DataDirectory", temporary.Path);
        Environment.SetEnvironmentVariable("Server__DatabasePath", null);
        Environment.SetEnvironmentVariable("Server__EnableLocalProvisioning", "false");
        Environment.SetEnvironmentVariable("Server__AllowLocalProvisioningElevation", "false");
        Environment.SetEnvironmentVariable("Server__WorkerStaleAfterSeconds", "90");
        Environment.SetEnvironmentVariable("Server__ExecutionLeaseDurationSeconds", "900");
        Environment.SetEnvironmentVariable("Server__ExecutionLeaseRenewalIntervalSeconds", "60");

        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            Assert.Equal(ServerAdministrationExitCodes.Success,
                await Program.Main(["config", "set", "Server:EnableLocalProvisioning", "true", "--json"]));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            foreach (var (key, value) in originalEnvironment) Environment.SetEnvironmentVariable(key, value);
        }

        using var result = JsonDocument.Parse(output.ToString());
        Assert.True(result.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.True(result.RootElement.GetProperty("restartRequired").GetBoolean());
        Assert.Equal("Server:EnableLocalProvisioning", result.RootElement.GetProperty("setting").GetString());
        var updated = File.ReadAllText(configurationPath);
        Assert.Contains("# retained operator comment\r\n", updated, StringComparison.Ordinal);
        Assert.Contains("Server__EnableLocalProvisioning=\"true\"\r\n", updated, StringComparison.Ordinal);
        Assert.Contains("CODEX_SERVER_MANAGEMENT_TOKEN=private-token\r\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
                File.GetUnixFileMode(configurationPath));
        if (originalOwner is not null) Assert.Equal(originalOwner, ReadOwnerGroup(configurationPath));
    }

    [Theory]
    [InlineData("UnknownSetting", "true")]
    [InlineData("AdministrationOrigin", "http://external.example")]
    [InlineData("AdministrationOrigin", "https://admin.example/")]
    [InlineData("EnableLocalProvisioning", "sometimes")]
    [InlineData("ExecutionLeaseDurationSeconds", "zero")]
    public void MutationRejectsUnsupportedOrInvalidValuesWithoutReplacingTheFile(string setting, string value)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "server.env");
        const string original = "Server__EnableLocalProvisioning=false\n";
        File.WriteAllText(path, original);

        if (setting == "AdministrationOrigin")
            Assert.Throws<InvalidDataException>(() => ServerConfigurationMutation.Set(path, setting, value, new ServerConfiguration()));
        else
            Assert.Throws<ArgumentException>(() => ServerConfigurationMutation.Set(path, setting, value, new ServerConfiguration()));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void MutationValidatesTheCompleteServerConfigurationBeforeReplacingTheFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "server.env");
        const string original = "Server__EnableLocalProvisioning=false\n";
        File.WriteAllText(path, original);

        Assert.Throws<InvalidDataException>(() => ServerConfigurationMutation.Set(path,
            "ExecutionLeaseDurationSeconds", "0", new ServerConfiguration()));

        Assert.Equal(original, File.ReadAllText(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"server-config-mutation-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static string ReadOwnerGroup(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("stat")
        {
            ArgumentList = { "-c", "%u:%g", path },
            RedirectStandardOutput = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start stat to inspect the configuration file owner.");
        var ownerGroup = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return ownerGroup;
    }
}
