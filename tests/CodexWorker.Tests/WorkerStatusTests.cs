using System.Text.Json;
using CodexProvisioning;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerStatusTests
{
    [Fact]
    public async Task EmptyStandaloneConfigurationIsValidWhilePreparingExecutionDependencies()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n");
        Directory.CreateDirectory(fixture.ProjectsPath);

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(), inventoryDiscovery: InventoryDiscovery(missingTools: true));

        Assert.Equal("valid", status.Configuration.Validity);
        Assert.Equal(0, status.Configuration.ProjectCount);
        Assert.Equal("not-ready", status.Operation.Readiness);
        Assert.DoesNotContain("configuration-invalid", status.Diagnostics);
        Assert.Contains("codex-cli:tool-missing", status.Diagnostics);
    }

    [Fact]
    public async Task StatusReportsValidManagedConfigurationAndLocalDependencySummary()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n  ownership: managed\nserver:\n  enabled: true\n  url: https://server.example\n  identityFile: ./worker-id\nworker:\n  maxParallelTasks: 3\n  provisioning:\n    enabled: true\n    allowNonPrivileged: true\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        await WorkerIdentity.LoadOrCreateAsync(Path.Combine(fixture.DirectoryPath, "worker-id"));
        await WorkerAuthentication.LoadOrCreateTokenAsync(Path.Combine(fixture.DirectoryPath, "worker-id"));
        var discovery = DiscoveryWith(new WorkerCapabilityContract("tool", "git", "2.45.0"),
            new WorkerCapabilityContract("tool", "github-cli", "2.50.0"),
            new WorkerCapabilityContract("tool", "codex-cli", "1.2.3"));

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, discovery, inventoryDiscovery: InventoryDiscovery());

        Assert.Equal(1, status.ContractVersion);
        Assert.Equal("valid", status.Configuration.Validity);
        Assert.Equal("managed", status.Configuration.Ownership);
        Assert.Equal(3, status.Capacity.Maximum);
        Assert.Null(status.Capacity.Active);
        Assert.Equal("not-checked", status.Registration.ServerConnectivity);
        Assert.Equal("unknown", status.Operation.Lifecycle);
        Assert.Equal("server-dependent-unverified", status.Operation.Readiness);
        Assert.True(status.Provisioning.Enabled);
        Assert.Contains(status.Capabilities, capability => capability.Name == "git" && capability.State == "available");
        Assert.Contains(status.Capabilities, capability => capability.Name == "docker" && capability.State == "available");
        Assert.Contains("server-state-unverified", status.Diagnostics);
    }

    [Fact]
    public async Task InvalidOrMissingConfigurationIsAStatusResultAndJsonHasVersionedContract()
    {
        using var fixture = new StatusFixture();
        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(), inventoryDiscovery: InventoryDiscovery(missingTools: true));
        Assert.Equal("invalid", status.Configuration.Validity);
        Assert.Equal("configuration-not-found", status.Configuration.DiagnosticCode);

        var serialized = JsonSerializer.Serialize(status, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var document = JsonDocument.Parse(serialized);
        Assert.Equal(1, document.RootElement.GetProperty("contractVersion").GetInt32());
        Assert.Equal("invalid", document.RootElement.GetProperty("configuration").GetProperty("validity").GetString());
        Assert.Equal("unknown", document.RootElement.GetProperty("operation").GetProperty("lifecycle").GetString());
    }

    [Fact]
    public async Task ConfiguredWorkerWithMissingExecutionToolsIsNotReadyWithFixedDiagnostics()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        fixture.WriteValidProject();

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(), inventoryDiscovery: InventoryDiscovery(missingTools: true));

        Assert.Equal("valid", status.Configuration.Validity);
        Assert.Equal("not-ready", status.Operation.Readiness);
        Assert.Contains("git:tool-missing", status.Diagnostics);
        Assert.Contains("github-cli:tool-missing", status.Diagnostics);
        Assert.Contains("codex-cli:tool-missing", status.Diagnostics);
    }

    [Fact]
    public async Task HealthyStandaloneConfigurationReportsLocallyObservedReadiness()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        fixture.WriteValidProject();

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(
            new WorkerCapabilityContract("tool", "git", "2.45.0"),
            new WorkerCapabilityContract("tool", "github-cli", "2.50.0"),
            new WorkerCapabilityContract("tool", "codex-cli", "1.2.3")), inventoryDiscovery: InventoryDiscovery());

        Assert.Equal("valid", status.Configuration.Validity);
        Assert.Equal("local-prerequisites-present", status.Operation.Readiness);
        Assert.Empty(status.Diagnostics);
        foreach (var name in new[] { "git", "docker", "github-cli", "codex-cli" })
        {
            var capability = Assert.Single(status.Capabilities, capability => capability.Name == name);
            Assert.Equal("available", capability.State);
            Assert.Equal(InstallationState.Installed, capability.Installation);
            Assert.Empty(capability.BlockingReasons ?? []);
        }
    }

    [Fact]
    public async Task StatusReportsSharedExecutionReadinessBlockerForInstalledGitWithoutIdentity()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        fixture.WriteValidProject();

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath,
            DiscoveryWith(new("tool", "git"), new("tool", "github-cli"), new("tool", "codex-cli")),
            inventoryDiscovery: InventoryDiscovery(gitIdentityConfigured: false));

        Assert.Equal("not-ready", status.Operation.Readiness);
        Assert.Contains("git:configuration-required", status.Diagnostics);
        Assert.DoesNotContain("local-prerequisites-present", status.Diagnostics);
    }

    [Fact]
    public async Task CleanHostStatusUsesTypedBlockersAndOnlyObservesLocalState()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        var originalConfiguration = await File.ReadAllTextAsync(fixture.ConfigurationPath);
        var commands = new List<string>();
        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath,
            DiscoveryWith(new("tool", "git", "2.43.0"), new("tool", "docker", "29.1.3")),
            inventoryDiscovery: InventoryDiscovery(gitIdentityConfigured: false, dockerAccessible: false,
                codexMissing: true, commands: commands));

        Assert.Equal("not-ready", status.Operation.Readiness);
        foreach (var (name, dependency) in new[] { ("git", LocalConfigurationDependencyKind.GitIdentity),
                     ("docker", LocalConfigurationDependencyKind.DockerDaemonAccess) })
        {
            var capability = Assert.Single(status.Capabilities, capability => capability.Name == name);
            Assert.Equal("blocked", capability.State);
            Assert.Equal(InstallationState.Installed, capability.Installation);
            Assert.Equal(RequirementState.Required, capability.Configuration);
            Assert.Equal(dependency, capability.ConfigurationDependency);
            Assert.Contains("configuration-required", capability.BlockingReasons ?? []);
        }
        var codex = Assert.Single(status.Capabilities, capability => capability.Name == "codex-cli");
        Assert.Equal("missing", codex.State);
        Assert.Equal(RequirementState.Unknown, codex.Authentication);
        Assert.Contains(AuthenticationDependencyKind.CodexCliLogin, codex.AuthenticationDependencies ?? []);
        Assert.Contains("authentication-required", codex.BlockingReasons ?? []);
        Assert.Equal(new[] { "git:configuration-required", "codex-cli:tool-missing", "docker:configuration-required" }.Order(),
            status.Diagnostics.Order());
        using var output = new StringWriter();
        WorkerStatusReporter.Write(status, false, output);
        var text = output.ToString();
        Assert.Contains("configuration required (GitIdentity)", text, StringComparison.Ordinal);
        Assert.Contains("configuration required (DockerDaemonAccess)", text, StringComparison.Ordinal);
        Assert.Contains("authentication required after installation", text, StringComparison.Ordinal);
        Assert.Contains("Git identity is not configured", text, StringComparison.Ordinal);
        Assert.Contains("Docker daemon is not accessible", text, StringComparison.Ordinal);
        Assert.Contains("Codex CLI is not installed.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Worker is not ready based on local observations.", text, StringComparison.Ordinal);
        Assert.Equal(3, text.Split("Diagnostic:").Length - 1);
        using var jsonOutput = new StringWriter();
        WorkerStatusReporter.Write(status, true, jsonOutput);
        using var json = JsonDocument.Parse(jsonOutput.ToString());
        var git = json.RootElement.GetProperty("capabilities").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "git");
        Assert.Equal("Installed", git.GetProperty("installation").GetString());
        Assert.Equal("GitIdentity", git.GetProperty("configurationDependency").GetString());
        Assert.Equal(originalConfiguration, await File.ReadAllTextAsync(fixture.ConfigurationPath));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "worker-id.token")));
        Assert.DoesNotContain(commands, command => command.Contains("config") && !command.Contains("--get", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("login") && !command.Contains("status", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("usermod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DockerBlockerIsVisibleWithoutBecomingAGenericExecutionPrerequisite()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(),
            inventoryDiscovery: InventoryDiscovery(dockerAccessible: false));
        Assert.Equal("local-prerequisites-present", status.Operation.Readiness);
        Assert.Equal("docker:configuration-required", Assert.Single(status.Diagnostics));
        Assert.Contains(status.Capabilities, capability => capability.Name == "docker" && capability.State == "blocked");
    }

    [Fact]
    public async Task InstalledCodexWithoutAuthenticationIsBlockedSeparatelyFromInstallation()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(),
            inventoryDiscovery: InventoryDiscovery(authenticated: false));
        var codex = Assert.Single(status.Capabilities, capability => capability.Name == "codex-cli");
        Assert.Equal(InstallationState.Installed, codex.Installation);
        Assert.Equal("blocked", codex.State);
        Assert.Equal(RequirementState.Required, codex.Authentication);
        Assert.Contains("codex-cli:authentication-required", status.Diagnostics);
        Assert.DoesNotContain("codex-cli:tool-missing", status.Diagnostics);
    }

    [Fact]
    public async Task InvalidConfiguredPolicyIsReportedWithoutLeakingConfigurationContents()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n  ownership: standalone\nserver:\n  enabled: true\n  url: https://server.example\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        File.WriteAllText(Path.Combine(fixture.ProjectsPath, "invalid.yml"), "private-value: should-not-be-reported\n");

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(), inventoryDiscovery: InventoryDiscovery(missingTools: true));

        Assert.Equal("invalid", status.Configuration.Validity);
        Assert.Equal("configuration-invalid", status.Configuration.DiagnosticCode);
        Assert.DoesNotContain("private-value", string.Join(" ", status.Diagnostics), StringComparison.Ordinal);
        Assert.Equal("unknown", status.Registration.State);
    }

    [Fact]
    public async Task StatusCommandAcceptsJsonAndReturnsSuccessForMissingConfiguration()
    {
        using var fixture = new StatusFixture();
        var original = Console.Out;
        using var capture = new StringWriter();
        Console.SetOut(capture);
        try
        {
            var exitCode = await Program.Main(["status", "--config", fixture.ConfigurationPath, "--json"]);
            Assert.Equal(ProcessExitCodes.Success, exitCode);
        }
        finally { Console.SetOut(original); }

        using var json = JsonDocument.Parse(capture.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
    }

    [Theory]
    [InlineData("missing", "local-identity-missing", "missing")]
    [InlineData("invalid-identity", "local-identity-invalid", "persisted")]
    [InlineData("invalid-token", "local-identity-present", "invalid")]
    [InlineData("valid", "local-identity-present", "persisted")]
    public async Task StatusObservesRegistrationWithoutMutatingOrExposingLocalState(string scenario,
        string expectedState, string expectedCredential)
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n  ownership: managed\nserver:\n  enabled: true\n  url: https://server.example\n  identityFile: ./worker-id\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        var path = Path.Combine(fixture.DirectoryPath, "worker-id");
        string? identity = null;
        string? secret = null;
        if (scenario != "missing")
        {
            identity = await WorkerIdentity.LoadOrCreateAsync(path);
            secret = await WorkerAuthentication.LoadOrCreateTokenAsync(path);
            await File.WriteAllTextAsync(path + ".server", "https://associated.example");
            if (scenario == "invalid-identity") await File.WriteAllTextAsync(path, "private-invalid-identity");
            if (scenario == "invalid-token") await File.WriteAllTextAsync(path + ".token", "private-invalid-token");
        }
        var discovery = DiscoveryWith(new("tool", "git"), new("tool", "github-cli"), new("tool", "codex-cli"));
        var first = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, discovery, inventoryDiscovery: InventoryDiscovery());
        var restarted = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, discovery, inventoryDiscovery: InventoryDiscovery());
        Assert.Equal(expectedState, first.Registration.State);
        Assert.Equal(expectedCredential, first.Registration.CredentialState);
        Assert.Equal(first.Registration, restarted.Registration);
        Assert.Equal("unverified", first.Registration.ServerAcceptance);
        Assert.Equal("not-checked", first.Registration.ServerConnectivity);
        Assert.Equal(scenario == "valid" ? "server-dependent-unverified" : "not-ready", first.Operation.Readiness);
        Assert.Equal(scenario == "missing" ? "https://server.example" : "https://associated.example", first.Registration.ServerUrl);
        Assert.NotNull(first.Runtime);
        Assert.Equal(scenario is "missing" or "invalid-identity" ? null : identity, first.Registration.WorkerId);
        foreach (var json in new[] { false, true })
        {
            using var output = new StringWriter();
            WorkerStatusReporter.Write(first, json, output);
            Assert.DoesNotContain("private-invalid", output.ToString(), StringComparison.Ordinal);
            if (secret is not null) Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
        }
        if (scenario == "missing")
        {
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".token"));
        }
        if (scenario == "invalid-identity") Assert.Equal("private-invalid-identity", await File.ReadAllTextAsync(path));
        if (scenario == "invalid-token") Assert.Equal("private-invalid-token", await File.ReadAllTextAsync(path + ".token"));
    }

    [Fact]
    public async Task StatusDoesNotExposeInvalidPersistedServerAssociation()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n  ownership: managed\nserver:\n  enabled: true\n  url: https://server.example\n  identityFile: ./worker-id\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "worker-id.server"), "https://user:private-secret@server.example/?token=private-secret");
        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith(), inventoryDiscovery: InventoryDiscovery(missingTools: true));
        Assert.Null(status.Registration.ServerUrl);
        Assert.Equal("invalid", status.Registration.AssociationSource);
        Assert.Contains("worker-server-association-invalid", status.Diagnostics);
        using var output = new StringWriter();
        WorkerStatusReporter.Write(status, true, output);
        Assert.DoesNotContain("private-secret", output.ToString(), StringComparison.Ordinal);
    }

    private static WorkerCapabilityDiscovery DiscoveryWith(params WorkerCapabilityContract[] capabilities)
    {
        var available = capabilities.ToDictionary(capability => capability.Name, StringComparer.OrdinalIgnoreCase);
        return new WorkerCapabilityDiscovery((executable, _, _, _, _) =>
        {
            var name = executable switch
            {
                "gh" => "github-cli",
                "codex" => "codex-cli",
                _ when Path.IsPathFullyQualified(executable) => "codex-cli",
                _ => executable
            };
            return Task.FromResult(available.TryGetValue(name, out var capability)
                ? new ProcessResult(0, capability.Version ?? "", "")
                : new ProcessResult(1, "", ""));
        });
    }

    private static NodeCapabilityDiscovery InventoryDiscovery(bool gitIdentityConfigured = true,
        bool dockerAccessible = true, bool missingTools = false, bool codexMissing = false, bool authenticated = true,
        List<string>? commands = null) =>
        new((executable, arguments, _) =>
        {
            commands?.Add($"{executable} {string.Join(" ", arguments)}");
            if ((missingTools && executable is "git" or "gh") ||
                (missingTools || codexMissing) && (executable == "codex" || executable == CodexServiceEnvironment.Executable))
                throw new FileNotFoundException();
            if (arguments.Contains("info") && !dockerAccessible) return Task.FromResult((1, "permission denied"));
            if ((arguments.Contains("auth") || arguments.Contains("login")) && !authenticated) return Task.FromResult((1, ""));
            var isGitIdentityProbe = executable == "git" && arguments.SequenceEqual(new[] { "config", "--get", "user.name" }) ||
                executable == "git" && arguments.SequenceEqual(new[] { "config", "--get", "user.email" });
            return Task.FromResult(isGitIdentityProbe && !gitIdentityConfigured ? (1, "") : (0, "10.0.0"));
        });

    private sealed class StatusFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"worker-status-{Guid.NewGuid():N}");
        private readonly string? _previousServerToken = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        public StatusFixture()
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", null);
            Directory.CreateDirectory(_directory);
            ConfigurationPath = Path.Combine(_directory, "worker.yml");
            ProjectsPath = Path.Combine(_directory, "projects");
        }
        public string ConfigurationPath { get; }
        public string ProjectsPath { get; }
        public string DirectoryPath => _directory;
        public void WriteGlobal(string value) => File.WriteAllText(ConfigurationPath, value);
        public void WriteValidProject() => File.WriteAllText(Path.Combine(ProjectsPath, "project.yml"), """
            project:
              name: Example
              repository: owner/repository
              directory: ./checkout
            git:
              baseBranch: main
              featurePrefix: feature/
              completedPrefix: completed/
            github:
              readyLabel: ready
              workingLabel: working
              blockedLabel: blocked
              failedLabel: failed
              integrationConflictLabel: conflict
              integrationRecoveryLabel: recovery
              doneLabel: done
            codex:
              instructionsFile: AGENTS.md
            """);
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN", _previousServerToken);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
