namespace CodexWorker.Tests;

using System.Text.Json;
using CodexProvisioning;
using CodexWorker;

public sealed class ExpandedToolProvisioningTests
{
    [Fact]
    public async Task CatalogAndLocalInventoryDescribeProvidedToolsAndSeparateDependencies()
    {
        var inventory = await CapabilityInventoryReporter.CreateAsync(MissingTools());
        var sdk = Assert.Single(inventory.Capabilities, item => item.Id == "dotnet-sdk");
        Assert.Equal([ProvidedToolKind.DotNetSdk, ProvidedToolKind.DotNetRuntime, ProvidedToolKind.AspNetCoreRuntime], sdk.Provides);
        Assert.Equal(10, sdk.RequiredMajorVersion);
        Assert.Empty(sdk.AuthenticationDependencies);
        Assert.Null(sdk.ConfigurationDependency);
        var runtime = Assert.Single(inventory.Capabilities, item => item.Id == "dotnet-runtime");
        Assert.Equal([ProvidedToolKind.DotNetRuntime, ProvidedToolKind.AspNetCoreRuntime], runtime.Provides);
        Assert.Empty(runtime.AuthenticationDependencies);
        var docker = Assert.Single(inventory.Capabilities, item => item.Id == "docker");
        Assert.Equal([ProvidedToolKind.DockerEngine], docker.Provides);
        Assert.Empty(docker.AuthenticationDependencies);
        Assert.Null(docker.Authentication);
        Assert.Equal(LocalConfigurationDependencyKind.DockerDaemonAccess, docker.ConfigurationDependency);
        Assert.Equal([AuthenticationDependencyKind.GitHubCliLogin],
            Assert.Single(inventory.Capabilities, item => item.Id == "github-cli").AuthenticationDependencies);
        Assert.Equal([AuthenticationDependencyKind.CodexCliLogin],
            Assert.Single(inventory.Capabilities, item => item.Id == "codex-cli").AuthenticationDependencies);
        foreach (var id in new[] { "dotnet-sdk", "dotnet-runtime", "docker" })
        {
            Assert.False(Assert.Single(inventory.Capabilities, item => item.Id == id).RequiredForExecution);
            Assert.False(ProvisioningCommandProtocol.Supported(new("server", id, ProvisioningCommandAction.Login)));
            Assert.False(ProvisioningCommandProtocol.Supported(new("server", id, ProvisioningCommandAction.CheckAuthentication)));
        }
    }

    [Theory]
    [InlineData("dotnet-sdk", "8.0.100 [/sdk]\n11.0.100 [/sdk]", null)]
    [InlineData("dotnet-sdk", "10.0.100-preview.1 [/sdk]", null)]
    [InlineData("dotnet-sdk", "10.0.999999999999999999 [/sdk]", null)]
    [InlineData("dotnet-sdk", "10.0.100 [/sdk]\n10.0.200 [/sdk]\n11.0.100 [/sdk]", "10.0.200")]
    [InlineData("dotnet-runtime", "Microsoft.NETCore.App 9.0.1 [/runtime]", null)]
    [InlineData("dotnet-runtime", "Microsoft.NETCore.App 10.0.1 [/runtime]", null)]
    [InlineData("dotnet-runtime", "Microsoft.AspNetCore.App 10.0.1 [/runtime]", null)]
    [InlineData("dotnet-runtime", "Microsoft.NETCore.App 10.0.10 [/runtime]\nMicrosoft.AspNetCore.App 10.0.2 [/runtime]\nMicrosoft.AspNetCore.App 10.0.10 [/runtime]", "10.0.10")]
    public async Task DotNetDetectionRequiresStableNetTenComponent(string id, string output, string? expected)
    {
        var calls = new List<string>();
        var discovery = new NodeCapabilityDiscovery((tool, args, _) =>
        {
            calls.Add(tool + " " + string.Join(' ', args));
            if (tool != "dotnet") return Task.FromException<(int, string)>(new FileNotFoundException());
            return Task.FromResult((0, output));
        });
        var states = await discovery.GetAsync();
        var state = Assert.Single(states, item => item.Id == id);
        Assert.Equal(expected is null ? InstallationState.Missing : InstallationState.Installed, state.Installation);
        Assert.Equal(expected, state.DetectedVersion);
        Assert.Null(state.Authentication);
        Assert.Null(state.Configuration);
        Assert.Contains("dotnet --list-sdks", calls);
        Assert.Contains("dotnet --list-runtimes", calls);
        Assert.DoesNotContain("dotnet --version", calls);
        Assert.True(CapabilityCatalog.ValidInventory(states));
    }

    [Theory]
    [InlineData(0, RequirementState.Satisfied)]
    [InlineData(1, RequirementState.Required)]
    public async Task DockerReadinessChecksDaemonAsServiceUserWithoutAuthenticationOrElevation(int exitCode, RequirementState expected)
    {
        var discovery = new NodeCapabilityDiscovery((tool, args, _) =>
        {
            if (tool != "docker") return Task.FromException<(int, string)>(new FileNotFoundException());
            return Task.FromResult(args[0] == "--version" ? (0, "Docker version 28.0.0") : (exitCode, "private-output"));
        });
        var state = Assert.Single(await discovery.GetAsync(), item => item.Id == "docker");
        Assert.Equal(InstallationState.Installed, state.Installation);
        Assert.Equal(expected, state.Configuration);
        Assert.Null(state.Authentication);
        Assert.Equal(exitCode == 0, CapabilityCatalog.Ready([state]));
        Assert.Equal(exitCode == 0 ? null : "configuration-required", state.DiagnosticCode);
        Assert.DoesNotContain("private-output", JsonSerializer.Serialize(state), StringComparison.Ordinal);
        var executor = new NodeProvisioningCommandExecutor(discovery, (tool, args, _) =>
        {
            Assert.Equal("docker", tool);
            Assert.Equal(["info", "--format", "{{.ServerVersion}}"], args);
            return Task.FromResult(exitCode);
        });
        Assert.Equal(exitCode == 0 ? ProvisioningCommandStatus.Succeeded : ProvisioningCommandStatus.Failed,
            (await executor.ExecuteAsync(Command("docker", ProvisioningCommandAction.CheckConfiguration), true)).Status);
    }

    [Theory]
    [InlineData("dotnet-sdk", "dotnet-sdk-10.0")]
    [InlineData("dotnet-runtime", "aspnetcore-runtime-10.0")]
    [InlineData("docker", "docker.io")]
    public async Task NewProvidersConvergeUsingOnlyFixedPackageCommandsAndPreserveData(string id, string package)
    {
        var installed = false;
        var calls = new List<(string Tool, IReadOnlyList<string> Args)>();
        var discovery = new NodeCapabilityDiscovery((tool, args, _) =>
        {
            if (tool == "/usr/bin/apt-cache") return Task.FromResult((0, "Installed: 10.0.100\nCandidate: 10.0.100"));
            if (tool is not ("dotnet" or "/usr/bin/dotnet" or "docker" or "/usr/bin/docker"))
                return Task.FromException<(int, string)>(new FileNotFoundException());
            if (!installed) return Task.FromException<(int, string)>(new FileNotFoundException());
            return Task.FromResult((0, args[0] switch
            {
                "--list-sdks" => "10.0.100 [/sdk]",
                "--list-runtimes" => "Microsoft.NETCore.App 10.0.100 [/runtime]\nMicrosoft.AspNetCore.App 10.0.100 [/runtime]",
                _ => "10.0.100"
            }));
        });
        var executor = new NodeProvisioningCommandExecutor(discovery, (tool, args, _) =>
        {
            calls.Add((tool, args));
            Assert.Equal("/usr/bin/sudo", tool);
            Assert.Equal(["-n", "/usr/bin/apt-get"], args.Take(2));
            if (args.Contains(package)) installed = args[2] != "remove";
            return Task.FromResult(0);
        }, supportsApt: () => true, isRoot: () => false);
        foreach (var action in new[] { ProvisioningCommandAction.Install, ProvisioningCommandAction.Install,
            ProvisioningCommandAction.Update, ProvisioningCommandAction.Uninstall, ProvisioningCommandAction.Uninstall })
        {
            Assert.Equal(ProvisioningCommandStatus.Succeeded, (await executor.ExecuteAsync(Command(id, action), true)).Status);
            var state = Assert.Single(await discovery.GetAsync(), item => item.Id == id);
            Assert.Equal(action == ProvisioningCommandAction.Uninstall ? InstallationState.Missing : InstallationState.Installed, state.Installation);
        }
        Assert.Contains(calls, call => call.Args.SequenceEqual(["-n", "/usr/bin/apt-get", "install", "-y", "--no-install-recommends", package]));
        Assert.Contains(calls, call => call.Args.SequenceEqual(["-n", "/usr/bin/apt-get", "remove", "-y", package]));
        Assert.DoesNotContain(calls, call => call.Args.Any(arg => arg is "purge" or "autoremove" or "rm" or "logout" or "usermod"));
    }

    [Theory]
    [InlineData("dotnet-sdk")]
    [InlineData("dotnet-runtime")]
    [InlineData("docker")]
    public async Task NewProviderFailuresStopThePlanAndReturnActionableSafeDetails(string id)
    {
        var calls = 0;
        var executor = new NodeProvisioningCommandExecutor(MissingTools(), supportsApt: () => true, isRoot: () => true,
            processRunner: (_, _, _) =>
            {
                calls++;
                return Task.FromResult(new ProvisioningProcessResult(100, "Unable to locate package; private-output"));
            });
        var report = await executor.ExecuteAsync(Command(id, ProvisioningCommandAction.Install), true);
        Assert.Equal(1, calls);
        Assert.Equal(ProvisioningCommandStatus.Failed, report.Status);
        Assert.Equal(ProvisioningFailureCode.PackageUnavailable, report.FailureDetail?.Code);
        Assert.DoesNotContain("private-output", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.True(ProvisioningCommandProtocol.ValidReport(report));
    }

    [Theory]
    [InlineData("dotnet-sdk")]
    [InlineData("dotnet-runtime")]
    [InlineData("docker")]
    public async Task NewProvidersRejectUnsupportedPlatformsAndDeniedElevationBeforeMutation(string id)
    {
        var executor = new NodeProvisioningCommandExecutor(MissingTools(), (_, _, _) => throw new InvalidOperationException("Must not execute"),
            supportsApt: () => false);
        Assert.Equal(ProvisioningDiagnostic.Unsupported, (await executor.ExecuteAsync(Command(id, ProvisioningCommandAction.Install), true)).Diagnostic);
        executor = new NodeProvisioningCommandExecutor(MissingTools(), (_, _, _) => throw new InvalidOperationException("Must not execute"),
            supportsApt: () => true);
        var command = Command(id, ProvisioningCommandAction.Install);
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(command with { Request = command.Request with { AllowElevation = false } }, true)).Diagnostic);
        Assert.Equal(ProvisioningDiagnostic.Denied, (await executor.ExecuteAsync(command, false)).Diagnostic);
    }

    [Theory]
    [InlineData("dotnet-sdk")]
    [InlineData("dotnet-runtime")]
    [InlineData("docker")]
    public async Task InterruptedNewProviderRefreshesAndDoesNotRunLaterSteps(string id)
    {
        using var cancellation = new CancellationTokenSource();
        var probes = 0;
        var calls = 0;
        var discovery = new NodeCapabilityDiscovery((_, _, _) =>
        {
            probes++;
            return Task.FromException<(int, string)>(new FileNotFoundException());
        });
        var executor = new NodeProvisioningCommandExecutor(discovery, (_, _, token) =>
        {
            calls++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }, supportsApt: () => true, isRoot: () => true);
        var result = await executor.ExecuteAsync(Command(id, ProvisioningCommandAction.Install), true, cancellation.Token);
        Assert.Equal(ProvisioningCommandStatus.Cancelled, result.Status);
        Assert.Equal(1, calls);
        Assert.Equal(2 * CapabilityCatalog.Definitions.Count, probes);
    }

    [Theory]
    [InlineData("dotnet-sdk", "10.0.100", "10.0.200", UpdateState.Available)]
    [InlineData("dotnet-runtime", "10.0.1", "10.0.2", UpdateState.Available)]
    [InlineData("docker", "28.0.0", "28.0.0", UpdateState.Current)]
    public async Task NewProvidersUseTheirOwnAptCandidate(string id, string installed, string candidate, UpdateState expected)
    {
        var package = id switch
        {
            "dotnet-sdk" => "dotnet-sdk-10.0",
            "dotnet-runtime" => "aspnetcore-runtime-10.0",
            _ => "docker.io"
        };
        var probes = new List<string>();
        var discovery = new NodeCapabilityDiscovery((tool, args, _) =>
        {
            if (tool == "/usr/bin/apt-cache")
            {
                probes.Add(args[1]);
                return Task.FromResult((0, $"Installed: {installed}\nCandidate: {candidate}"));
            }
            if (tool == "/usr/bin/dpkg") return Task.FromResult((0, ""));
            if (tool is not ("dotnet" or "docker")) return Task.FromException<(int, string)>(new FileNotFoundException());
            return Task.FromResult((0, args[0] switch
            {
                "--list-sdks" => $"{installed} [/sdk]",
                "--list-runtimes" => $"Microsoft.NETCore.App {installed} [/runtime]\nMicrosoft.AspNetCore.App {installed} [/runtime]",
                _ => installed
            }));
        });
        var state = Assert.Single(await discovery.GetAsync(), item => item.Id == id);
        Assert.Equal(expected, state.Update);
        Assert.Contains(package, probes);
    }

    private static NodeCapabilityDiscovery MissingTools() => new((_, _, _) => Task.FromException<(int, string)>(new FileNotFoundException()));

    private static ProvisioningCommand Command(string id, ProvisioningCommandAction action)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid().ToString("N"), new("server", id, action, AllowElevation: true), now,
            ProvisioningCommandStatus.Running, ProvisioningDiagnostic.Executing, now, now.AddMinutes(2));
    }
}
