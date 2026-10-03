using System.Text.Json;

namespace CodexWorker.Tests;

[Collection("ServerTokenEnvironment")]
public sealed class WorkerStatusTests
{
    [Fact]
    public async Task StatusReportsValidManagedConfigurationAndLocalDependencySummary()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n  ownership: managed\nserver:\n  enabled: true\n  url: https://server.example\n  identityFile: ./worker-id\nworker:\n  maxParallelTasks: 3\n  provisioning:\n    enabled: true\n    allowNonPrivileged: true\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "worker-id"), "local-id");
        var discovery = DiscoveryWith(new WorkerCapabilityContract("tool", "git", "2.45.0"),
            new WorkerCapabilityContract("tool", "github-cli", "2.50.0"),
            new WorkerCapabilityContract("tool", "codex-cli", "1.2.3"));

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, discovery);

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
        Assert.Contains(status.Capabilities, capability => capability.Name == "docker" && capability.State == "missing");
        Assert.Contains("server-state-unverified", status.Diagnostics);
    }

    [Fact]
    public async Task InvalidOrMissingConfigurationIsAStatusResultAndJsonHasVersionedContract()
    {
        using var fixture = new StatusFixture();
        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith());
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

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith());

        Assert.Equal("valid", status.Configuration.Validity);
        Assert.Equal("not-ready", status.Operation.Readiness);
        Assert.Contains("git-unavailable", status.Diagnostics);
        Assert.Contains("github-cli-unavailable", status.Diagnostics);
        Assert.Contains("codex-cli-unavailable", status.Diagnostics);
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
            new WorkerCapabilityContract("tool", "codex-cli", "1.2.3")));

        Assert.Equal("valid", status.Configuration.Validity);
        Assert.Equal("local-prerequisites-present", status.Operation.Readiness);
        Assert.Empty(status.Diagnostics);
    }

    [Fact]
    public async Task InvalidConfiguredPolicyIsReportedWithoutLeakingConfigurationContents()
    {
        using var fixture = new StatusFixture();
        fixture.WriteGlobal("projects:\n  directory: ./projects\n  ownership: managed\nserver:\n  enabled: true\n  url: https://server.example\n");
        Directory.CreateDirectory(fixture.ProjectsPath);
        File.WriteAllText(Path.Combine(fixture.ProjectsPath, "invalid.yml"), "private-value: should-not-be-reported\n");

        var status = await WorkerStatusReporter.CreateAsync(fixture.ConfigurationPath, DiscoveryWith());

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

    private sealed class StatusFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"worker-status-{Guid.NewGuid():N}");
        public StatusFixture()
        {
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
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
