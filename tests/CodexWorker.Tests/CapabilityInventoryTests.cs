using System.Text.Json;
using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class CapabilityInventoryTests
{
    [Fact]
    public async Task InventoryIncludesInstalledMissingAndUnknownObservationsWithoutProcessOutput()
    {
        var discovery = new NodeCapabilityDiscovery((executable, arguments, _) =>
        {
            if (executable == "git") return Task.FromException<(int, string)>(new FileNotFoundException());
            if (arguments[0] == "--version")
                return executable == "gh"
                    ? Task.FromResult((0, "gh version 2.3.4 private-identity"))
                    : Task.FromException<(int, string)>(new TimeoutException("private-key"));
            if (arguments[0] == "auth") return Task.FromResult((1, "private-identity"));
            if (arguments[0] == "login") return Task.FromResult((0, "private-identity"));
            return Task.FromResult((1, "private-key"));
        });

        var inventory = await CapabilityInventoryReporter.CreateAsync(discovery);
        var git = Assert.Single(inventory.Capabilities, capability => capability.Id == "git");
        Assert.Equal(InstallationState.Missing, git.Installation);
        Assert.Equal("tool-missing", git.DiagnosticCode);
        var github = Assert.Single(inventory.Capabilities, capability => capability.Id == "github-cli");
        Assert.Equal(InstallationState.Installed, github.Installation);
        Assert.Equal("2.3.4", github.DetectedVersion);
        Assert.Equal(RequirementState.Required, github.Authentication);
        var codex = Assert.Single(inventory.Capabilities, capability => capability.Id == "codex-cli");
        Assert.Equal(InstallationState.Unknown, codex.Installation);
        Assert.Null(codex.DetectedVersion);
        Assert.Equal(UpdateState.Unknown, codex.Update);
        Assert.Equal(CapabilityHealth.Error, codex.Health);
        Assert.Equal(1, inventory.ContractVersion);
        Assert.All(inventory.Capabilities, capability => Assert.Contains("refresh", capability.AvailableActions));

        var json = JsonSerializer.Serialize(inventory, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("private-identity", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key", json, StringComparison.Ordinal);
        Assert.Contains("diagnosticCode", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCommandRequestsFreshObservationsWhileListUsesCache()
    {
        var detection = 0;
        var discovery = new NodeCapabilityDiscovery((_, arguments, _) =>
        {
            if (arguments[0] == "--version")
            {
                detection++;
                return Task.FromResult((0, $"tool {detection}.0.0"));
            }
            return Task.FromResult((0, ""));
        });

        var listed = await CapabilityInventoryReporter.CreateAsync(discovery);
        var listedAgain = await CapabilityInventoryReporter.CreateAsync(discovery);
        Assert.Equal(listed.Capabilities, listedAgain.Capabilities);
        Assert.Equal(3, detection);

        var refreshed = await CapabilityInventoryReporter.CreateAsync(discovery, refresh: true);
        Assert.Equal(6, detection);
        Assert.NotEqual(listed.Capabilities, refreshed.Capabilities);
    }
}
