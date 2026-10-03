using System.Net;
using System.Net.Http.Json;
using CodexProvisioning;
using CodexWorker;

namespace CodexWorker.Tests;

public sealed class ExecutionEligibilityTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("9.0.1", false)]
    [InlineData("10.0.1", true)]
    public void DotNetRequirementsMatchInstalledVersionsWithoutCredentials(string? version, bool eligible)
    {
        var capabilities = version is null ? Array.Empty<WorkerCapabilityContract>() :
            [new WorkerCapabilityContract("runtime", "dotnet", version)];
        var result = CapabilityEligibility.Evaluate([new ServerProjectRequirementContract("runtime", ".NET", ">=10")],
            capabilities, ReadyInventory());

        Assert.Equal(eligible, result.IsEligible);
        if (!eligible) Assert.Contains("requires .NET >=10", Assert.Single(result.MissingRequirements), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionMatchingAcceptsAnyCompatibleInstalledVersion()
    {
        Assert.True(CapabilityEligibility.Evaluate([new ServerProjectRequirementContract("runtime", "dotnet", ">=10")],
            [new WorkerCapabilityContract("runtime", "dotnet", "9"), new WorkerCapabilityContract("runtime", "dotnet", "10")]).IsEligible);
    }

    [Theory]
    [InlineData("github-cli", "authentication-required")]
    [InlineData("codex-cli", "authentication-required")]
    [InlineData("git", "configuration-required")]
    public void InstalledToolsCannotBypassLocalDependencies(string id, string diagnostic)
    {
        var inventory = ReadyInventory().Select(state => state.Id == id ? state with
        { Authentication = RequirementState.Required, Configuration = RequirementState.Required } : state).ToArray();
        var result = CapabilityEligibility.Evaluate([], [], inventory);

        Assert.False(result.IsEligible);
        Assert.Contains($"{id}:{diagnostic}", result.MissingRequirements);
        Assert.True(CapabilityEligibility.Evaluate([], [], ReadyInventory()).IsEligible);
    }

    [Theory]
    [InlineData("github-api")]
    [InlineData("git-repository")]
    [InlineData("codex")]
    public void ProjectExecutionRequiresRepositoryAuthenticationAndAgentPreflight(string missing)
    {
        var capabilities = WorkerAuthenticationCapabilities.ForRepository("owner/repo")
            .Append(WorkerAgentCapabilities.AuthenticatedProvider("codex")).ToArray();
        var requirements = CapabilityEligibility.ForProject([], "owner/repo");

        var result = CapabilityEligibility.Evaluate(requirements, capabilities.Where(capability => capability.Name != missing), ReadyInventory());

        Assert.False(result.IsEligible);
        Assert.Contains(missing, Assert.Single(result.MissingRequirements), StringComparison.Ordinal);
        Assert.True(CapabilityEligibility.Evaluate(requirements, capabilities, ReadyInventory()).IsEligible);
        Assert.False(CapabilityEligibility.Evaluate(requirements,
            capabilities.Select(capability => capability.Type == "authentication" ? capability with { Scope = "owner/other" } : capability),
            ReadyInventory()).IsEligible);
    }

    [Fact]
    public void MissingCapabilitiesPreventReservationsAndRestorationAllowsCleanRetry()
    {
        var configuration = new WorkerConfiguration { Project = new() { Name = "Project" } };
        var registry = new ProjectRuntimeRegistry([("project.yml", configuration)]);
        ServerProjectRequirementContract[] requirements = [new("runtime", "dotnet", ">=10")];
        var missing = CapabilityEligibility.Evaluate(requirements, []);

        registry.ApplyExecutionEligibility("Project", configuration, missing);

        Assert.False(registry.TryReserve("Project", configuration));
        Assert.Equal(0, registry.WorkerActiveExecutionCount);
        Assert.Contains("requires .NET", registry.Get("Project")?.UnavailableReason, StringComparison.Ordinal);
        var version = registry.Version;
        registry.ApplyExecutionEligibility("Project", configuration, missing);
        Assert.Equal(version, registry.Version);
        registry.ApplyExecutionEligibility("Project", configuration,
            CapabilityEligibility.Evaluate(requirements, [new WorkerCapabilityContract("runtime", "dotnet", "10.0")]));
        Assert.True(registry.TryReserve("Project", configuration));
        registry.Release("Project");
        Assert.Equal(0, registry.WorkerActiveExecutionCount);
        Assert.Null(registry.Get("Project")?.UnavailableReason);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("draining")]
    [InlineData("failure")]
    public void CapabilityRefreshPreservesOperatorAndInfrastructurePauses(string pause)
    {
        var configuration = new WorkerConfiguration { Project = new() { Name = "Project" } };
        var registry = new ProjectRuntimeRegistry([("project.yml", configuration)]);
        registry.ApplyExecutionEligibility("Project", configuration, new(false, ["tool-missing"]));
        switch (pause)
        {
            case "disabled": registry.Disable("Project"); break;
            case "draining": registry.Drain("Project"); break;
            case "failure": registry.MarkUnavailable("Project", "GitHub reconciliation required."); break;
        }
        var paused = registry.Get("Project");

        registry.ApplyExecutionEligibility("Project", configuration, new(false, ["authentication-required"]));
        registry.ApplyExecutionEligibility("Project", configuration, new(true, []));

        Assert.Equal(paused, registry.Get("Project"));
        Assert.False(registry.TryReserve("Project", configuration));
        Assert.Equal(0, registry.WorkerActiveExecutionCount);
    }

    [Fact]
    public void EligibilityMustMatchTheRegisteredConfiguration()
    {
        var configuration = new WorkerConfiguration { Project = new() { Name = "Project" } };
        var registry = new ProjectRuntimeRegistry([("project.yml", configuration)]);
        var replacement = new WorkerConfiguration { Project = new() { Name = "Project" } };

        registry.ApplyExecutionEligibility("Project", replacement, new(false, ["tool-missing"]));

        Assert.True(registry.TryReserve("Project", configuration));
        registry.Release("Project");
    }

    [Fact]
    public async Task AssignmentRefusalRecordsTerminalPreparationFailureAndReportsOwnedGeneration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eligibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var history = new ExecutionHistoryStore(Path.Combine(directory, "history.db"));
            var settings = new WorkerServerSettings { Enabled = true, Url = "https://server.example", IdentityFile = Path.Combine(directory, "identity") };
            var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile);
            await WorkerAuthentication.LoadOrCreateTokenAsync(settings.IdentityFile);
            WorkerExecutionReportContract? report = null;
            using var handler = new Handler(async (request, token) =>
            {
                Assert.EndsWith("/executions/execution/report", request.RequestUri?.AbsolutePath, StringComparison.Ordinal);
                report = await (request.Content ?? throw new InvalidDataException("Missing report")).ReadFromJsonAsync<WorkerExecutionReportContract>(token);
                return new(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var configuration = new WorkerConfiguration { Project = new() { Name = "Project", Repository = "owner/repo", Directory = directory } };
            var host = new WorkerHost(new() { Server = settings }, [], registrationClient: new WorkerRegistrationClient(client));
            var now = DateTimeOffset.UtcNow;
            var assignment = new WorkerAssignmentContract("assignment", "execution",
                new("project", "Project", "owner/repo", "main", "", [], 1, now, now),
                new("github-issue", "17"), workerId, new Dictionary<string, string>(),
                new("execution", workerId, 3, now, now.AddMinutes(5), "Active"));

            await host.RejectIncompatibleAssignmentAsync(assignment, configuration, history,
                "requires .NET >=10; capability unavailable", CancellationToken.None);

            var entry = Assert.Single(await history.ReadAllAsync());
            Assert.Equal("Blocked", entry.State);
            Assert.Equal("preparation-failed", entry.RecoveryState);
            Assert.NotNull(entry.CompletedAtUtc);
            Assert.Null(entry.RecoveryBaseCommit);
            Assert.NotNull(report);
            Assert.Equal("Failed", report.State);
            Assert.Equal(3, report.Generation);
            Assert.Equal(entry.ExecutionId.ToString(), report.WorkerExecutionId);
            Assert.False(report.Recoverable);
            Assert.Contains("requires .NET", report.Summary, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static CapabilityState[] ReadyInventory() => CapabilityCatalog.Definitions.Select(definition =>
        CapabilityCatalog.Unknown(definition) with
        {
            Installation = InstallationState.Installed, Health = CapabilityHealth.Healthy,
            Authentication = definition.RequiresAuthentication ? RequirementState.Satisfied : null,
            Configuration = definition.RequiresConfiguration ? RequirementState.Satisfied : null,
            DiagnosticCode = null
        }).ToArray();

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
