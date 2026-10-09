using CodexWorker;
using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class ManagedConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(8)]
    public void ProjectConcurrencyIsDeliveredAndCachedWithoutLocalThrottle(int? limit)
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var project = fixture.Project(1) with { MaxParallelTasks = limit };
        var snapshot = new ServerManagedConfigurationContract(2, ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]);
        Assert.Equal(8, Assert.Single(synchronizer.Apply(snapshot)).Configuration.Worker.MaxParallelTasks);
        var restarted = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        restarted.LoadLastValid();
        Assert.Equal(limit, Assert.Single(restarted.AppliedProjects).MaxParallelTasks);
        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(snapshot with { ContractVersion = 1 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void InvalidServerProjectConcurrencyRetainsLastValidSnapshot(int limit)
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        synchronizer.Apply(fixture.Snapshot(1));
        var project = fixture.Project(2) with { MaxParallelTasks = limit };
        var invalid = new ServerManagedConfigurationContract(2, ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]);
        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(invalid));
        Assert.Null(Assert.Single(synchronizer.AppliedProjects).MaxParallelTasks);
    }

    [Fact]
    public async Task SuccessfulRetrievalWithLocalMismatchReportsSynchronizationFailureAndProjectRevision()
    {
        using var fixture = new Fixture();
        fixture.Runtime.GitHub.WorkingLabel = fixture.Runtime.GitHub.ReadyLabel;
        fixture.Runtime.Environment.Variables = new Dictionary<string, string> { ["SECRET"] = "private-value" };
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => synchronizer.RetrieveAndApplyAsync(
            _ => Task.FromResult(fixture.Snapshot(2)), CancellationToken.None));

        var diagnostics = Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics);
        Assert.Equal("retrieved", diagnostics.Retrieval);
        Assert.Equal("synchronization", diagnostics.FailureStage);
        Assert.Equal("managed-local-configuration-invalid", diagnostics.DiagnosticCode);
        Assert.Equal("repository", diagnostics.FailedProjectId);
        Assert.Equal(2, diagnostics.FailedProjectRevision);
        Assert.DoesNotContain("unavailable", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(synchronizer.Status), StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.CachePath));
    }

    [Theory]
    [InlineData(401, "unauthorized", "server-unauthorized")]
    [InlineData(503, "unavailable", "server-unavailable")]
    public async Task RetrievalFailurePreservesCacheAndReportsConnectivity(int statusCode, string retrieval, string code)
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        synchronizer.Apply(fixture.Snapshot(1));

        await Assert.ThrowsAsync<HttpRequestException>(() => synchronizer.RetrieveAndApplyAsync(
            _ => Task.FromException<ServerManagedConfigurationContract>(new HttpRequestException(
                "raw private-value", null, (System.Net.HttpStatusCode)statusCode)), CancellationToken.None));

        Assert.Equal(retrieval, synchronizer.Status.Diagnostics?.Retrieval);
        Assert.Equal("retrieval", synchronizer.Status.Diagnostics?.FailureStage);
        Assert.Equal(code, synchronizer.Status.Diagnostics?.DiagnosticCode);
        Assert.Equal("cached", synchronizer.Status.Diagnostics?.Source);
        Assert.Equal(fixture.Snapshot(1).Version, synchronizer.Status.AppliedVersion);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(synchronizer.Status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidJsonIsAContractFailureAfterRetrieval()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);

        await Assert.ThrowsAsync<JsonException>(() => synchronizer.RetrieveAndApplyAsync(
            _ => Task.FromException<ServerManagedConfigurationContract>(new JsonException("private-value")), CancellationToken.None));

        Assert.Equal("retrieved", synchronizer.Status.Diagnostics?.Retrieval);
        Assert.Equal("contract-validation", synchronizer.Status.Diagnostics?.FailureStage);
        Assert.DoesNotContain("private-value", JsonSerializer.Serialize(synchronizer.Status), StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationAuthorizationFailureIsNotReportedAsServerUnavailability()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        synchronizer.RecordUnavailable(new WorkerStartupException("registration rejected",
            new HttpRequestException("rejected", null, System.Net.HttpStatusCode.Unauthorized)));

        Assert.Equal("unauthorized", synchronizer.Status.Diagnostics?.Retrieval);
        Assert.Equal("server-unauthorized", synchronizer.Status.Diagnostics?.DiagnosticCode);
    }

    [Fact]
    public async Task CancelledRetrievalDoesNotBecomeConnectivityFailure()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        synchronizer.Apply(fixture.Snapshot(1));
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => synchronizer.RetrieveAndApplyAsync(
            token => Task.FromCanceled<ServerManagedConfigurationContract>(token), stop.Token));

        Assert.Equal("synchronized", synchronizer.Status.SynchronizationStatus);
    }

    [Fact]
    public void ProjectObservationsKeepRevisionContextAndAreNotRestoredAsReadinessAuthority()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        synchronizer.Apply(fixture.Snapshot(1));
        Assert.Equal("not-materialized", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics).Projects).State);
        synchronizer.RecordProjectState(fixture.Project(1), "blocked", "project-capabilities-missing");
        Assert.Equal("project-capabilities-missing", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics).Projects).DiagnosticCode);
        synchronizer.RecordProjectState(fixture.Project(1), "materializing");
        synchronizer.RecordProjectState(fixture.Project(1), "failed", "project-preparation-failed");
        Assert.Equal("failed", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics).Projects).State);
        synchronizer.RecordProjectState(fixture.Project(1), "ready");
        synchronizer.Apply(fixture.Snapshot(1));
        Assert.Equal("ready", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics).Projects).State);
        var restarted = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        restarted.LoadLastValid();
        Assert.Equal("cached", restarted.Status.Diagnostics?.Source);
        Assert.Equal("unverified", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(restarted.Status.Diagnostics).Projects).State);
        synchronizer.Apply(fixture.Snapshot(2));
        Assert.Equal("not-materialized", Assert.Single(Assert.IsType<CodexProvisioning.ManagedWorkerDiagnostics>(synchronizer.Status.Diagnostics).Projects).State);
    }

    [Fact]
    public void AppliesAnEmptyServerSnapshotForAColdStartWorker()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(Path.Combine(Path.GetDirectoryName(fixture.CachePath)!, "empty-configuration.json"), fixture.Runtime);
        var snapshot = new ServerManagedConfigurationContract(2, ManagedConfigurationSynchronizer.CalculateVersion([]), []);

        var configured = synchronizer.Apply(snapshot);

        Assert.Empty(configured);
        Assert.Equal("synchronized", synchronizer.Status.SynchronizationStatus);
    }

    [Fact]
    public void InitialSnapshotIsValidatedAppliedAndReported()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);

        var applied = synchronizer.Apply(fixture.Snapshot(1));

        Assert.Equal("owner/repository", applied.Single().Configuration.Project.Repository);
        Assert.Equal("main", applied.Single().Configuration.Git.BaseBranch);
        Assert.Equal("synchronized", synchronizer.Status.SynchronizationStatus);
        Assert.Equal(fixture.Snapshot(1).Version, synchronizer.Status.DesiredVersion);
        Assert.Equal(synchronizer.Status.DesiredVersion, synchronizer.Status.AppliedVersion);
        Assert.NotNull(synchronizer.Status.LastSuccessfulUpdateUtc);
    }

    [Fact]
    public void UpdatedSnapshotAppliesOnlyServerOwnedProjectFieldsAndAdvancesVersion()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var initial = synchronizer.Apply(fixture.Snapshot(1)).Single().Configuration;

        var updatedProjects = new[]
        {
            fixture.Project(2) with { Repository = "owner/renamed", DefaultBranch = "trunk" }
        };
        var updatedSnapshot = fixture.Snapshot(2) with
        {
            Version = ManagedConfigurationSynchronizer.CalculateVersion(updatedProjects),
            Projects = updatedProjects
        };
        var updated = synchronizer.Apply(updatedSnapshot).Single().Configuration;

        Assert.Equal("owner/renamed", updated.Project.Repository);
        Assert.Equal("trunk", updated.Git.BaseBranch);
        Assert.Equal(initial.Project.Directory, updated.Project.Directory);
        Assert.Equal(initial.GitHub.ReadyLabel, updated.GitHub.ReadyLabel);
        Assert.NotEqual(synchronizer.Status.AppliedVersion, fixture.Snapshot(1).Version);
    }

    [Fact]
    public void UnchangedSnapshotDoesNotRewriteCacheOrChangeSuccessfulUpdateTime()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var desired = fixture.Snapshot(1);
        synchronizer.Apply(desired);
        var timestamp = synchronizer.Status.LastSuccessfulUpdateUtc;
        var writtenAt = File.GetLastWriteTimeUtc(fixture.CachePath);

        synchronizer.Apply(desired);

        Assert.Equal(timestamp, synchronizer.Status.LastSuccessfulUpdateUtc);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(fixture.CachePath));
    }

    [Fact]
    public void ExecutionRequirementsSurviveInvalidUpdatesAndCachedRestart()
    {
        using var fixture = new Fixture();
        var project = fixture.Project(1) with { Requirements = [new("runtime", "dotnet", ">=10")] };
        var desired = new ServerManagedConfigurationContract(2, ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]);
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        synchronizer.Apply(desired);

        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(desired with { Version = "invalid" }));
        Assert.Equal(project.Requirements, Assert.Single(synchronizer.AppliedProjects).Requirements);
        var restarted = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        restarted.LoadLastValid();
        Assert.Equal(project.Requirements, Assert.Single(restarted.AppliedProjects).Requirements);
    }

    [Fact]
    public void InvalidUpdateKeepsLastValidSnapshotAvailableAndReportsError()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var initial = fixture.Snapshot(1);
        synchronizer.Apply(initial);
        var invalid = fixture.Snapshot(2) with { Version = "incorrect-version" };

        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(invalid));
        var fallback = synchronizer.LoadLastValid().Single().Configuration;

        Assert.Equal("owner/repository", fallback.Project.Repository);
        Assert.Equal(initial.Version, synchronizer.Status.AppliedVersion);
        Assert.Equal(invalid.Version, synchronizer.Status.DesiredVersion);
        Assert.Equal("error", synchronizer.Status.SynchronizationStatus);
        Assert.NotNull(synchronizer.Status.Error);
    }

    [Fact]
    public void TemporaryServerLossPreservesAppliedConfigurationAcrossRestart()
    {
        using var fixture = new Fixture();
        var firstProcess = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var snapshot = fixture.Snapshot(1);
        firstProcess.Apply(snapshot);
        firstProcess.RecordUnavailable(new HttpRequestException("offline"));
        firstProcess.RecordUnavailable(new HttpRequestException("still offline"));
        Assert.Equal("unavailable", firstProcess.Status.SynchronizationStatus);

        var restartedProcess = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var restored = restartedProcess.LoadLastValid().Single().Configuration;

        Assert.Equal(snapshot.Version, restartedProcess.Status.AppliedVersion);
        Assert.Equal("cached", restartedProcess.Status.SynchronizationStatus);
        Assert.Equal("owner/repository", restored.Project.Repository);
        Assert.True(restartedProcess.Status.LastSuccessfulUpdateUtc.HasValue);
    }

    [Fact]
    public void ProjectRevisionTransitionChangesSnapshotVersion()
    {
        using var fixture = new Fixture();

        Assert.NotEqual(fixture.Snapshot(1).Version, fixture.Snapshot(2).Version);
        var changedProject = fixture.Project(1) with { DefaultBranch = "trunk" };
        Assert.NotEqual(fixture.Snapshot(1).Version, ManagedConfigurationSynchronizer.CalculateVersion([changedProject]));
    }

    [Fact]
    public void DisabledCentralLifecycleFieldSurvivesManagedSnapshotCachingAndRestart()
    {
        using var fixture = new Fixture();
        var disabledProject = fixture.Project(2) with { Enabled = false };
        var snapshot = new ServerManagedConfigurationContract(2,
            ManagedConfigurationSynchronizer.CalculateVersion([disabledProject]), [disabledProject]);
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        synchronizer.Apply(snapshot);
        var stored = JsonSerializer.Deserialize<ServerManagedConfigurationContract>(File.ReadAllText(fixture.CachePath), jsonOptions);
        var restarted = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        restarted.LoadLastValid();
        var restored = JsonSerializer.Deserialize<ServerManagedConfigurationContract>(File.ReadAllText(fixture.CachePath), jsonOptions);

        Assert.False(Assert.Single(stored!.Projects).Enabled);
        Assert.False(Assert.Single(restored!.Projects).Enabled);
    }

    [Fact]
    public void ColdStartAcceptsServerProjectWithoutYamlCheckoutOrInstructions()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Worker.LegacyMaxParallelTasks = 3;
        fixture.Runtime.Validation.Commands = ["dotnet test"];
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);

        var applied = Assert.Single(synchronizer.Apply(fixture.Snapshot(1)));

        Assert.Equal("Repository", applied.Configuration.Project.Name);
        Assert.Equal("owner/repository", applied.Configuration.Project.Repository);
        Assert.Equal(8, applied.Configuration.Worker.MaxParallelTasks);
        Assert.Equal(["dotnet test"], applied.Configuration.Validation.Commands);
        Assert.Equal(Path.Combine(applied.Configuration.Project.Directory, "AGENTS.md"), applied.Configuration.Codex.InstructionsFile);
        Assert.False(Directory.Exists(applied.Configuration.Project.Directory));
        Assert.False(File.Exists(applied.Path));
        var restarted = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        Assert.Equal(applied.Configuration.Project.Directory, Assert.Single(restarted.LoadLastValid()).Configuration.Project.Directory);
    }

    [Fact]
    public void RenameAndRevisionRetainCheckoutIdentityAndServerLifecycle()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        var initial = Assert.Single(synchronizer.Apply(fixture.Snapshot(1)));
        var project = fixture.Project(2) with { Name = "Renamed", Enabled = false, Requirements = [new("runtime", "dotnet", ">=10")] };
        var snapshot = new ServerManagedConfigurationContract(2, ManagedConfigurationSynchronizer.CalculateVersion([project]), [project]);

        var updated = Assert.Single(synchronizer.Apply(snapshot));

        Assert.Equal("Renamed", updated.Configuration.Project.Name);
        Assert.Equal(initial.Configuration.Project.Directory, updated.Configuration.Project.Directory);
        var registry = new ProjectRuntimeRegistry([initial]);
        registry.ReplaceConfiguration([updated], validateExecutionResources: false);
        Assert.Equal("Renamed", Assert.Single(registry.Snapshot()).Configuration.Project.Name);
        Assert.Equal(project, Assert.Single(synchronizer.AppliedProjects));
        Assert.False(Assert.Single(synchronizer.AppliedProjects).Enabled);
    }

    [Fact]
    public void RejectsDuplicateServerIdsWithoutReplacingCache()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath, fixture.Runtime);
        synchronizer.Apply(fixture.Snapshot(1));
        var projects = new[] { fixture.Project(2), fixture.Project(2) with { Name = "Another", Repository = "owner/another" } };
        var invalid = new ServerManagedConfigurationContract(2, ManagedConfigurationSynchronizer.CalculateVersion(projects), projects);

        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(invalid));
        Assert.Single(synchronizer.LoadLastValid());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        public Fixture()
        {
            CachePath = Path.Combine(_temporary.Path, "state", "configuration.json");
            Runtime = new ManagedProjectRuntimeSettings { CheckoutDirectory = Path.Combine(_temporary.Path, "checkouts") };
        }

        public string CachePath { get; }
        public ManagedProjectRuntimeSettings Runtime { get; }
        public ServerManagedConfigurationContract Snapshot(long revision)
        {
            var projects = new[] { Project(revision) };
            return new(2, ManagedConfigurationSynchronizer.CalculateVersion(projects), projects);
        }

        public ServerProjectContract Project(long revision) => new("repository", "Repository", "owner/repository", "main",
            "generic project", [], revision, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(revision));

        public void Dispose() => _temporary.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"managed-configuration-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
