namespace CodexWorker.Tests;

using CodexProvisioning;
using CodexWorker;

public sealed class WorkerAdministrationIntegrationTests
{
    [Theory]
    [InlineData("git")]
    [InlineData("github-cli")]
    [InlineData("codex-cli")]
    [InlineData("dotnet-sdk")]
    [InlineData("dotnet-runtime")]
    [InlineData("docker")]
    public async Task UpdateLookupPermissionFailurePreservesReadinessAcrossAdministrationViews(string id)
    {
        var discovery = new NodeCapabilityDiscovery((tool, arguments, _) =>
        {
            if (tool is "/usr/bin/apt-cache" or "/usr/bin/npm")
                return Task.FromException<(int, string)>(new UnauthorizedAccessException("private-output"));
            return Task.FromResult((0, arguments[0] switch
            {
                "--list-sdks" => "10.0.100 [/sdk]",
                "--list-runtimes" => "Microsoft.NETCore.App 10.0.100 [/runtime]\nMicrosoft.AspNetCore.App 10.0.100 [/runtime]",
                _ => "10.0.100"
            }));
        });
        var credentials = new NodeCredentialAdministration(discovery, (_, _, _) =>
            throw new InvalidOperationException("Observation must not execute provisioning."));
        var provisioning = new WorkerProvisioningAdministrationService(new ProvisioningPolicy(), discovery, "server");

        var capability = Assert.Single((await CapabilityInventoryReporter.CreateAsync(discovery)).Capabilities,
            item => item.Id == id);
        var credential = Assert.Single((await credentials.StatusAsync()).Credentials, item => item.CapabilityId == id);
        var status = await provisioning.GetStatusAsync();
        Assert.NotNull(status.Capabilities);
        var provisioned = Assert.Single(status.Capabilities, item => item.Id == id);

        Assert.Equal(InstallationState.Installed, capability.Installation);
        Assert.Equal(UpdateState.Unknown, capability.Update);
        Assert.Equal(CapabilityHealth.Healthy, capability.Health);
        Assert.Null(capability.DiagnosticCode);
        Assert.True(capability.Readiness.Available);
        Assert.True(credential.Ready);
        Assert.Equal(capability.Authentication, credential.Authentication);
        Assert.True(CapabilityCatalog.Ready([provisioned.State]));
        Assert.True(CapabilityCatalog.ExecutionReadiness(await discovery.GetAsync()).Available);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("github-cli")]
    [InlineData("codex-cli")]
    [InlineData("dotnet-sdk")]
    [InlineData("dotnet-runtime")]
    [InlineData("docker")]
    public async Task AdministrationViewsAgreeAcrossProviderStateTransitions(string id)
    {
        var installed = false;
        var dependenciesSatisfied = false;
        var probes = new List<string>();
        var discovery = new NodeCapabilityDiscovery((tool, arguments, _) =>
        {
            probes.Add(tool + " " + string.Join(' ', arguments));
            var effectiveArguments = tool == "/usr/sbin/runuser" ? arguments.Skip(4).ToArray() : arguments;
            if (tool == "/usr/sbin/runuser" && (arguments.Count < 4 || arguments[2] != "--" || arguments[3] != "docker"))
                return Task.FromException<(int, string)>(new FileNotFoundException("private-output"));
            if (effectiveArguments[0] == "--host")
            {
                Assert.Equal("unix:///var/run/docker.sock", effectiveArguments[1]);
                effectiveArguments = effectiveArguments.Skip(2).ToArray();
            }
            if (!installed) return Task.FromException<(int, string)>(new FileNotFoundException("private-output"));
            return Task.FromResult(effectiveArguments[0] switch
            {
                "--version" => (0, "10.0.100"),
                "--list-sdks" => (0, "10.0.100 [/sdk]"),
                "--list-runtimes" => (0, "Microsoft.NETCore.App 10.0.100 [/runtime]\nMicrosoft.AspNetCore.App 10.0.100 [/runtime]"),
                "config" or "auth" or "login" or "info" => (dependenciesSatisfied ? 0 : 1, "private-output"),
                _ => (1, "private-output") // Candidate lookup failure is independent of readiness.
            });
        });
        var credentials = new NodeCredentialAdministration(discovery, (_, _, _) =>
            throw new InvalidOperationException("Observation must not execute provisioning."));
        var provisioning = new WorkerProvisioningAdministrationService(new ProvisioningPolicy(), discovery, "server");
        var definition = Assert.Single(CapabilityCatalog.Definitions, item => item.Id == id);

        foreach (var transition in new[]
        {
            (Installed: false, Satisfied: false),
            (Installed: true, Satisfied: false),
            (Installed: true, Satisfied: true),
            (Installed: true, Satisfied: false),
            (Installed: false, Satisfied: false),
            (Installed: true, Satisfied: true)
        })
        {
            installed = transition.Installed;
            dependenciesSatisfied = transition.Satisfied;
            var inventory = await CapabilityInventoryReporter.CreateAsync(discovery, refresh: true);
            var capability = Assert.Single(inventory.Capabilities, item => item.Id == id);
            var credential = Assert.Single((await credentials.StatusAsync()).Credentials, item => item.CapabilityId == id);
            var status = await provisioning.GetStatusAsync();
            Assert.NotNull(status.Capabilities);
            var provisioned = Assert.Single(status.Capabilities, item => item.Id == id);
            var expectedReady = installed && (dependenciesSatisfied ||
                !definition.RequiresAuthentication && !definition.RequiresConfiguration);

            Assert.Equal(installed ? InstallationState.Installed : InstallationState.Missing, capability.Installation);
            Assert.Equal(expectedReady, capability.Readiness.Available);
            Assert.Equal(expectedReady, credential.Ready);
            Assert.Equal(expectedReady, CapabilityCatalog.Evaluate(definition, provisioned.State).Available);
            Assert.Equal(capability.Authentication, credential.Authentication);
            Assert.Equal(capability.Authentication, provisioned.State.Authentication);
            Assert.Equal(definition.AuthenticationDependencies, credential.AuthenticationDependencies);
            Assert.Equal(definition.AuthenticationDependencies, provisioned.AuthenticationDependencies);
            Assert.Equal(UpdateState.Unknown, capability.Update);
            if (!definition.RequiresAuthentication)
                Assert.Null(capability.Authentication);
            else
                Assert.Equal(!installed ? RequirementState.Unknown : dependenciesSatisfied ?
                    RequirementState.Satisfied : RequirementState.Required, capability.Authentication);
            Assert.True(CapabilityCatalog.ValidInventory(await discovery.GetAsync()));
        }
        Assert.DoesNotContain(probes, probe => probe.StartsWith("dotnet login", StringComparison.Ordinal) ||
            probe.StartsWith("docker login", StringComparison.Ordinal) || probe.StartsWith("git login", StringComparison.Ordinal));
    }
}
