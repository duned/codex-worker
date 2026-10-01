namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;

public sealed class WorkerAdministrationCliTests
{
    [Fact]
    public async Task LocalCommandAdaptersUseAdministrationServiceContracts()
    {
        var status = new StubStatusService();
        var configuration = new StubConfigurationService();
        var capabilities = new StubCapabilityService();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var resolvedConfigurationPath = Path.GetFullPath("worker.yml");
        var cli = new WorkerAdministrationCli(status, configuration, capabilities,
            new WorkerConsole(stdout, interactive: false, errorWriter: stderr), stdout);

        Assert.Equal(ProcessExitCodes.Success, await cli.ShowStatusAsync(new WorkerCommandLine("status", "worker.yml", ["--json"])));
        Assert.Equal("worker.yml", status.Path);

        Assert.Equal(ProcessExitCodes.Success, cli.AdministerConfiguration(new WorkerCommandLine("config", "worker.yml", ["show", "--json"])));
        Assert.Equal(resolvedConfigurationPath, configuration.ShownPath);
        Assert.Contains(resolvedConfigurationPath, stdout.ToString(), StringComparison.Ordinal);

        Assert.Equal(ProcessExitCodes.Success, cli.AdministerConfiguration(new WorkerCommandLine("config", "worker.yml", ["validate", "--json"])));
        Assert.Equal(resolvedConfigurationPath, configuration.ValidatedPath);
        Assert.Equal(ProcessExitCodes.Success, cli.AdministerConfiguration(new WorkerCommandLine("config", "worker.yml", ["set", "worker.maxParallelTasks", "3"])));
        Assert.Equal(resolvedConfigurationPath, configuration.Updated?.Path);
        Assert.Equal("worker.maxParallelTasks", configuration.Updated?.Setting);
        Assert.Equal("3", configuration.Updated?.Item3);

        Assert.Equal(ProcessExitCodes.Success, await cli.ShowCapabilitiesAsync(new WorkerCommandLine("capabilities", "worker.yml", ["refresh", "--json"])));
        Assert.Equal(("worker.yml", true), capabilities.Request);
    }

    [Fact]
    public async Task ProvisioningCommandAdapterDelegatesTypedOperationToAdministrationService()
    {
        var service = new StubProvisioningService();
        var command = ProvisioningCli.Parse(["install", "git", "--allow-elevation"]);

        var result = await ProvisioningCli.ExecuteAsync(command, service, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(new WorkerProvisioningOperation("git", ProvisioningCommandAction.Install, AllowElevation: true), service.Operation);
        Assert.Equal(0, service.StatusCalls);
    }

    [Fact]
    public void ConfigurationAdministrationServiceReturnsVersionedRedactedDiagnosticContract()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-worker-config-{Guid.NewGuid():N}.yml");

        var result = new WorkerConfigurationAdministrationService().Validate(path);

        Assert.Equal(1, result.ContractVersion);
        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("configuration-not-found", diagnostic.Code);
        Assert.DoesNotContain(path, diagnostic.Message, StringComparison.Ordinal);

        var update = new WorkerConfigurationAdministrationService().Set(path, "worker.maxParallelTasks", "3");
        Assert.False(update.Succeeded);
        Assert.Equal("configuration-not-found", update.Diagnostic?.Code);
    }

    private sealed class StubStatusService : IWorkerStatusService
    {
        public string? Path { get; private set; }
        public Task<WorkerStatusDocument> GetStatusAsync(string configurationPath, CancellationToken cancellationToken = default)
        {
            Path = configurationPath;
            return Task.FromResult(new WorkerStatusDocument(1, "test", "test", new("valid", configurationPath, 0, "standalone", "disabled", null),
                new("standalone", "disabled", "not-applicable", null), new("not-running", "ready", "local"),
                new(null, null, "local"), new(false, false, false, 0, 0), [], []));
        }
    }

    private sealed class StubConfigurationService : IWorkerConfigurationAdministrationService
    {
        public string? ShownPath { get; private set; }
        public string? ValidatedPath { get; private set; }
        public (string Path, string Setting, string Value)? Updated { get; private set; }

        public WorkerConfigurationDocument Show(string configurationPath)
        {
            ShownPath = configurationPath;
            return new(1, configurationPath, new(60, 30, 1, new(false, false, false, [], [])),
                new("./projects", "standalone"), new(false), new(false, "http://127.0.0.1:5080", 100),
                new(false, "", 60, null));
        }

        public WorkerConfigurationValidationResult Validate(string configurationPath)
        {
            ValidatedPath = configurationPath;
            return new(configurationPath, []);
        }

        public WorkerConfigurationUpdateResult Set(string configurationPath, string setting, string value)
        {
            Updated = (configurationPath, setting, value);
            return new(configurationPath, setting, Succeeded: true, RestartRequired: true, Diagnostic: null);
        }
    }

    private sealed class StubCapabilityService : IWorkerCapabilityAdministrationService
    {
        public (string? Path, bool Refresh) Request { get; private set; }
        public Task<CapabilityInventoryContract> GetCapabilitiesAsync(string? configurationPath, bool refresh,
            CancellationToken cancellationToken = default)
        {
            Request = (configurationPath, refresh);
            return Task.FromResult(new CapabilityInventoryContract(1, []));
        }
    }

    private sealed class StubProvisioningService : IWorkerProvisioningAdministrationService
    {
        public WorkerProvisioningOperation? Operation { get; private set; }
        public int StatusCalls { get; private set; }

        public Task<WorkerProvisioningResult> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Task.FromResult(new WorkerProvisioningResult("status", "succeeded", ProvisioningDiagnostic.Completed));
        }

        public Task<WorkerProvisioningResult> ExecuteAsync(WorkerProvisioningOperation operation,
            CancellationToken cancellationToken = default, Func<ProvisioningCommandReport, CancellationToken, Task>? progress = null)
        {
            Operation = operation;
            return Task.FromResult(new WorkerProvisioningResult("install", "succeeded", ProvisioningDiagnostic.Completed));
        }
    }
}
