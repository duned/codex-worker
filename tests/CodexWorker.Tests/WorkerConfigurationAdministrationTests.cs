using System.Text.Json;
using System.Diagnostics;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerConfigurationAdministrationTests
{
    [Fact]
    public void ResolvePathUsesInstalledDefaultOrExplicitOverride()
    {
        Assert.Equal(Path.GetFullPath(WorkerCommandLine.DefaultConfigurationPath),
            WorkerConfigurationAdministration.ResolvePath(null));
        Assert.Equal(Path.GetFullPath("custom-worker.yml"),
            WorkerConfigurationAdministration.ResolvePath("custom-worker.yml"));
    }

    [Fact]
    public async Task ShowJsonRedactsIdentityPathAndUrlCredentials()
    {
        using var fixture = new Fixture();
        fixture.Write("""
            projects:
              directory: ./projects
            server:
              url: https://operator:private-token@server.example/path?token=query-secret#section
              identityFile: ./private/worker-id
            api:
              listenUrl: http://operator:api-secret@127.0.0.1:5080/?token=api-query-secret
            """);

        var (exitCode, output) = await RunAsync("config", "show", "--json", "--config", fixture.ConfigPath);

        Assert.Equal(ProcessExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("[redacted]", json.RootElement.GetProperty("server").GetProperty("identityFile").GetString());
        Assert.Equal("https://server.example/path", json.RootElement.GetProperty("server").GetProperty("url").GetString());
        Assert.DoesNotContain("private-token", output, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("api-query-secret", output, StringComparison.Ordinal);
        Assert.Contains(fixture.ConfigPath, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateJsonReturnsFieldDiagnosticAndFailureExitCode()
    {
        using var fixture = new Fixture();
        fixture.Write("worker:\n  pollingSeconds: 0\nprojects:\n  directory: ./projects\n");

        var (exitCode, output) = await RunAsync("config", "validate", "--json", "--config", fixture.ConfigPath);

        Assert.Equal(ProcessExitCodes.StartupFailure, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.False(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetString()),
            message => message is not null && message.Contains("worker.pollingSeconds", StringComparison.Ordinal));
        var diagnostic = Assert.Single(json.RootElement.GetProperty("diagnosticDetails").EnumerateArray());
        Assert.Equal("configuration-invalid", diagnostic.GetProperty("code").GetString());
        Assert.Contains("worker.pollingSeconds", diagnostic.GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateJsonReportsValidManagedConfiguration()
    {
        using var fixture = new Fixture();
        fixture.Write("""
            projects:
              directory: ./projects
              ownership: managed
            server:
              enabled: true
              url: http://127.0.0.1:5090
            """);

        var (exitCode, output) = await RunAsync("config", "validate", "--json", "--config", fixture.ConfigPath);

        Assert.Equal(ProcessExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("diagnostics").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("diagnosticDetails").EnumerateArray());
    }

    [Fact]
    public async Task SetCommandUsesOverridePathAndExplainsRestart()
    {
        using var fixture = new Fixture();
        fixture.Write("worker:\n  maxParallelTasks: 1\nprojects:\n  directory: ./projects\n");

        var (exitCode, output) = await RunAsync("config", "set", "worker.maxParallelTasks", "3", "--config", fixture.ConfigPath);

        Assert.Equal(ProcessExitCodes.Success, exitCode);
        Assert.Equal(3, GlobalWorkerConfiguration.Load(fixture.ConfigPath).Worker.MaxParallelTasks);
        Assert.Contains("Restart the Worker service", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetJsonReturnsVersionedUpdateContractAndAppliesTheSetting()
    {
        using var fixture = new Fixture();
        fixture.Write("worker:\n  maxParallelTasks: 1\nprojects:\n  directory: ./projects\n");

        var (exitCode, output) = await RunAsync("config", "set", "worker.maxParallelTasks", "3", "--json", "--config", fixture.ConfigPath);

        Assert.Equal(ProcessExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.True(json.RootElement.GetProperty("restartRequired").GetBoolean());
        Assert.Equal("worker.maxParallelTasks", json.RootElement.GetProperty("setting").GetString());
        Assert.Equal(3, GlobalWorkerConfiguration.Load(fixture.ConfigPath).Worker.MaxParallelTasks);
    }

    [Fact]
    public void SetUpdatesKnownProvisioningPolicyAtomicallyAndPreservesOtherSettings()
    {
        using var fixture = new Fixture();
        fixture.Write("""
            worker:
              pollingSeconds: 60
              provisioning:
                enabled: false
                allowedPrivilegedActions: []
            projects:
              directory: ./projects
            """);

        WorkerConfigurationAdministration.Set(fixture.ConfigPath, "worker.provisioning.enabled", "true");
        WorkerConfigurationAdministration.Set(fixture.ConfigPath, "worker.provisioning.allowedPrivilegedActions", "tool:git:install,tool:docker:update");

        var configuration = GlobalWorkerConfiguration.Load(fixture.ConfigPath);
        Assert.Equal(60, configuration.Worker.PollingSeconds);
        Assert.True(configuration.Worker.Provisioning.Enabled);
        Assert.Equal(new[] { "tool:git:install", "tool:docker:update" }, configuration.Worker.Provisioning.AllowedPrivilegedActions);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task SetPreservesInstalledUnixAccessModeAndLeavesUpdatedConfigurationReadable()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new Fixture();
        fixture.Write("worker:\n  maxParallelTasks: 1\nprojects:\n  directory: ./projects\n");
        File.SetUnixFileMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        File.SetUnixFileMode(fixture.ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var originalOwner = OperatingSystem.IsLinux() ? ReadOwnerGroup(fixture.ConfigPath) : null;

        WorkerConfigurationAdministration.Set(fixture.ConfigPath, "worker.maxParallelTasks", "3");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(fixture.ConfigPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(fixture.Root));
        Assert.Equal(3, GlobalWorkerConfiguration.Load(fixture.ConfigPath).Worker.MaxParallelTasks);
        if (originalOwner is not null) Assert.Equal(originalOwner, ReadOwnerGroup(fixture.ConfigPath));

        var (exitCode, output) = await RunAsync("config", "show", "--config", fixture.ConfigPath);
        Assert.Equal(ProcessExitCodes.Success, exitCode);
        Assert.Contains("worker.maxParallelTasks: 3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SetOnReadOnlyInstalledConfigurationReturnsElevationRemediation()
    {
        if (OperatingSystem.IsWindows() || IsRoot()) return;

        using var fixture = new Fixture();
        fixture.Write("worker:\n  maxParallelTasks: 1\nprojects:\n  directory: ./projects\n");
        File.SetUnixFileMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        File.SetUnixFileMode(fixture.ConfigPath, UnixFileMode.UserRead | UnixFileMode.GroupRead);

        try
        {
            var result = new WorkerConfigurationAdministrationService().Set(fixture.ConfigPath, "worker.maxParallelTasks", "3");

            Assert.False(result.Succeeded);
            Assert.Equal("configuration-elevation-required", result.Diagnostic?.Code);
            Assert.Contains("sudo codex-worker config set", result.Diagnostic?.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(1, GlobalWorkerConfiguration.Load(fixture.ConfigPath).Worker.MaxParallelTasks);
        }
        finally
        {
            File.SetUnixFileMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
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

    private static bool IsRoot()
    {
        using var process = Process.Start(new ProcessStartInfo("id")
        {
            ArgumentList = { "-u" },
            RedirectStandardOutput = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start id to inspect the current test user.");
        var userId = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return userId == "0";
    }

    [Theory]
    [InlineData("worker.maxParallelTasks", "9")]
    [InlineData("worker.provisioning.enabled", "sometimes")]
    [InlineData("worker.provisioning.allowedPrivilegedActions", "tool:git:install,,tool:node:install")]
    public void SetRejectsInvalidValuesWithoutChangingConfiguration(string setting, string value)
    {
        using var fixture = new Fixture();
        fixture.Write("worker:\n  maxParallelTasks: 2\nprojects:\n  directory: ./projects\n");
        var original = File.ReadAllText(fixture.ConfigPath);

        Assert.Throws<ArgumentException>(() => WorkerConfigurationAdministration.Set(fixture.ConfigPath, setting, value));

        Assert.Equal(original, File.ReadAllText(fixture.ConfigPath));
    }

    [Fact]
    public void SetRejectsUnknownSettingsWithoutChangingConfiguration()
    {
        using var fixture = new Fixture();
        fixture.Write("worker:\n  pollingSeconds: 60\nprojects:\n  directory: ./projects\n");
        var original = File.ReadAllText(fixture.ConfigPath);

        var error = Assert.Throws<ArgumentException>(() => WorkerConfigurationAdministration.Set(fixture.ConfigPath, "worker.customYaml", "anything"));

        Assert.Contains("Unknown setting", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(fixture.ConfigPath));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var original = Console.Out;
        var originalError = Console.Error;
        using var capture = new StringWriter();
        using var errorCapture = new StringWriter();
        Console.SetOut(capture);
        Console.SetError(errorCapture);
        try
        {
            var exitCode = await Program.Main(args);
            return (exitCode, capture.ToString() + errorCapture.ToString());
        }
        finally
        {
            Console.SetOut(original);
            Console.SetError(originalError);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "worker-config-admin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "projects"));
            ConfigPath = Path.Combine(Root, "worker.yml");
        }

        public string Root { get; }
        public string ConfigPath { get; }

        public void Write(string contents) => File.WriteAllText(ConfigPath, contents);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
