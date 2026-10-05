using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerAuthenticationContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginProbeInventoryCheckAndLogoutShareDefaultOrExplicitCodexHome(bool explicitHome)
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "worker-auth-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var variables = new[] { "HOME", "CODEX_HOME", "CODEX_WORKER_CODEX_EXECUTABLE" };
        var previous = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            var serviceHome = Path.Combine(directory, "service-home");
            var codexHome = explicitHome ? Path.Combine(directory, "protected-override") : Path.Combine(serviceHome, ".codex");
            var operatorHome = Path.Combine(directory, "operator-home");
            Directory.CreateDirectory(serviceHome);
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(operatorHome);
            var operatorAuthentication = Path.Combine(operatorHome, "auth-marker");
            await File.WriteAllTextAsync(operatorAuthentication, "operator-owned");
            var executable = Path.Combine(directory, "selected-codex");
            await File.WriteAllTextAsync(executable, """
                #!/bin/sh
                umask 0077
                printf '%s\n' "$HOME|$CODEX_HOME" >> "$0.contexts"
                case "$*" in
                  '--version') printf 'codex 1.0.0';;
                  'login --device-auth')
                    printf 'https://auth.openai.com/codex/device ABCD-1234\n'
                    /usr/bin/id -u > "$CODEX_HOME/auth-marker"
                    /usr/bin/stat -c %u "$CODEX_HOME/auth-marker" > "$CODEX_HOME/file-owner";;
                  'login status') test -f "$CODEX_HOME/auth-marker";;
                  'logout') rm -f "$CODEX_HOME/auth-marker";;
                  *) exit 1;;
                esac
                """);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("HOME", serviceHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", explicitHome ? codexHome : null);
            Environment.SetEnvironmentVariable("CODEX_WORKER_CODEX_EXECUTABLE", executable);
            var runner = new ProcessRunner();
            var discovery = new NodeCapabilityDiscovery(async (tool, arguments, token) =>
            {
                if (tool != executable) return (1, "");
                var result = await runner.RunAsync(tool, arguments, directory, TimeSpan.FromSeconds(10), token,
                    new Dictionary<string, string?> { ["CODEX_HOME"] = CodexServiceEnvironment.Home });
                return (result.ExitCode, result.StandardOutput);
            });
            var service = new WorkerProvisioningAdministrationService(new ProvisioningPolicy
            {
                Enabled = true, AllowCredentials = true, AllowNonPrivileged = true
            }, discovery, "server");
            var challenges = new List<CodexLoginInstructions>();
            var login = await service.ExecuteAsync(new("codex-cli", ProvisioningCommandAction.Login), progress: (report, _) =>
            {
                if (report.LoginInstructions is { } instructions) challenges.Add(instructions);
                return Task.CompletedTask;
            });
            Assert.Equal("succeeded", login.Status);
            Assert.Equal("ABCD-1234", Assert.Single(challenges).UserCode);
            var marker = Path.Combine(codexHome, "auth-marker");
            Assert.Equal(await File.ReadAllTextAsync(marker), await File.ReadAllTextAsync(Path.Combine(codexHome, "file-owner")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(marker));
            Assert.Equal(RequirementState.Satisfied, login.Capability?.Authentication);
            Assert.Equal("succeeded", (await service.ExecuteAsync(new("codex-cli", ProvisioningCommandAction.CheckAuthentication))).Status);
            var status = await service.GetStatusAsync();
            Assert.Equal(RequirementState.Satisfied, Assert.Single(status.Capabilities ?? [], item => item.Id == "codex-cli").State.Authentication);
            var inventory = await CapabilityInventoryReporter.CreateAsync(discovery, refresh: true);
            Assert.Equal(RequirementState.Satisfied, Assert.Single(inventory.Capabilities, item => item.Id == "codex-cli").Authentication);
            Assert.Equal("succeeded", (await service.ExecuteAsync(new("codex-cli", ProvisioningCommandAction.Logout))).Status);
            Assert.False(File.Exists(marker));
            Assert.Equal("failed", (await service.ExecuteAsync(new("codex-cli", ProvisioningCommandAction.CheckAuthentication))).Status);
            Assert.Equal("operator-owned", await File.ReadAllTextAsync(operatorAuthentication));
            Assert.All(await File.ReadAllLinesAsync(executable + ".contexts"), context => Assert.Equal(serviceHome + "|" + codexHome, context));
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            Directory.Delete(directory, recursive: true);
        }
    }
}
