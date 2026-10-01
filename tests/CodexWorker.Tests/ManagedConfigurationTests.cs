using CodexWorker;
using System.Text.Json;

namespace CodexWorker.Tests;

public sealed class ManagedConfigurationTests
{
    [Fact]
    public void AppliesAnEmptyServerSnapshotForAColdStartWorker()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(Path.Combine(Path.GetDirectoryName(fixture.CachePath)!, "empty-configuration.json"));
        var snapshot = new ServerManagedConfigurationContract(1, ManagedConfigurationSynchronizer.CalculateVersion([]), []);

        var configured = synchronizer.Apply(snapshot, []);

        Assert.Empty(configured);
        Assert.Equal("synchronized", synchronizer.Status.SynchronizationStatus);
    }

    [Fact]
    public void InitialSnapshotIsValidatedAppliedAndReported()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath);

        var applied = synchronizer.Apply(fixture.Snapshot(1), fixture.LocalProjects);

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
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath);
        var initial = synchronizer.Apply(fixture.Snapshot(1), fixture.LocalProjects).Single().Configuration;

        var updatedProjects = new[]
        {
            fixture.Project(2) with { Repository = "owner/renamed", DefaultBranch = "trunk" }
        };
        var updatedSnapshot = fixture.Snapshot(2) with
        {
            Version = ManagedConfigurationSynchronizer.CalculateVersion(updatedProjects),
            Projects = updatedProjects
        };
        var updated = synchronizer.Apply(updatedSnapshot, fixture.LocalProjects).Single().Configuration;

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
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath);
        var desired = fixture.Snapshot(1);
        synchronizer.Apply(desired, fixture.LocalProjects);
        var timestamp = synchronizer.Status.LastSuccessfulUpdateUtc;
        var writtenAt = File.GetLastWriteTimeUtc(fixture.CachePath);

        synchronizer.Apply(desired, fixture.LocalProjects);

        Assert.Equal(timestamp, synchronizer.Status.LastSuccessfulUpdateUtc);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(fixture.CachePath));
    }

    [Fact]
    public void InvalidUpdateKeepsLastValidSnapshotAvailableAndReportsError()
    {
        using var fixture = new Fixture();
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath);
        var initial = fixture.Snapshot(1);
        synchronizer.Apply(initial, fixture.LocalProjects);
        var invalid = fixture.Snapshot(2) with { Version = "incorrect-version" };

        Assert.Throws<InvalidDataException>(() => synchronizer.Apply(invalid, fixture.LocalProjects));
        var fallback = synchronizer.LoadLastValid(fixture.LocalProjects).Single().Configuration;

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
        var firstProcess = new ManagedConfigurationSynchronizer(fixture.CachePath);
        var snapshot = fixture.Snapshot(1);
        firstProcess.Apply(snapshot, fixture.LocalProjects);
        firstProcess.RecordUnavailable(new HttpRequestException("offline"));

        var restartedProcess = new ManagedConfigurationSynchronizer(fixture.CachePath);
        var restored = restartedProcess.LoadLastValid(fixture.LocalProjects).Single().Configuration;

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
        var snapshot = new ServerManagedConfigurationContract(1,
            ManagedConfigurationSynchronizer.CalculateVersion([disabledProject]), [disabledProject]);
        var synchronizer = new ManagedConfigurationSynchronizer(fixture.CachePath);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        synchronizer.Apply(snapshot, fixture.LocalProjects);
        var stored = JsonSerializer.Deserialize<ServerManagedConfigurationContract>(File.ReadAllText(fixture.CachePath), jsonOptions);
        var restarted = new ManagedConfigurationSynchronizer(fixture.CachePath);
        restarted.LoadLastValid(fixture.LocalProjects);
        var restored = JsonSerializer.Deserialize<ServerManagedConfigurationContract>(File.ReadAllText(fixture.CachePath), jsonOptions);

        Assert.False(Assert.Single(stored!.Projects).Enabled);
        Assert.False(Assert.Single(restored!.Projects).Enabled);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly string _checkout;
        private readonly string _instructions;

        public Fixture()
        {
            _checkout = Path.Combine(_temporary.Path, "checkout");
            Directory.CreateDirectory(_checkout);
            _instructions = Path.Combine(_checkout, "AGENTS.md");
            File.WriteAllText(_instructions, "worker instructions");
            CachePath = Path.Combine(_temporary.Path, "state", "configuration.json");
            LocalProjects =
            [
                (Path.Combine(_temporary.Path, "projects", "repo.yml"), new WorkerConfiguration
                {
                    Project = new ProjectSettings { Name = "Repository", Repository = "local/stale", Directory = _checkout },
                    Codex = new CodexSettings { InstructionsFile = _instructions },
                    GitHub = new GitHubSettings { ReadyLabel = "ready", WorkingLabel = "working", BlockedLabel = "blocked", FailedLabel = "failed", DoneLabel = "done" }
                })
            ];
        }

        public string CachePath { get; }
        public IReadOnlyList<(string Path, WorkerConfiguration Configuration)> LocalProjects { get; }
        public ServerManagedConfigurationContract Snapshot(long revision)
        {
            var projects = new[] { Project(revision) };
            return new(1, ManagedConfigurationSynchronizer.CalculateVersion(projects), projects);
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
