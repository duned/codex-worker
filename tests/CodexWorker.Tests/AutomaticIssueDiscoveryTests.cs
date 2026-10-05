namespace CodexWorker.Tests;

using CodexServer;
using CodexProvisioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AutomaticIssueDiscoveryTests
{
    [Fact]
    public async Task ConfigurationDefaultsDisabledPersistsAndValidatesBounds()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(0, fixture.Reads.Pages);
        Assert.Null(project.AutomaticDiscovery);
        var settings = new AutomaticIssueDiscovery(true, 30, 2, 10);
        project = await fixture.Store.UpdateProjectAsync(project.Id, Definition(settings), project.Revision);
        Assert.NotNull(project);
        var restarted = new SqliteRegistryStore(fixture.Database);
        await restarted.InitializeAsync();
        Assert.Equal(settings, (await restarted.GetProjectAsync(project.Id))?.AutomaticDiscovery);
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(2, (await fixture.Store.GetExecutionsAsync()).Count);
        foreach (var invalid in new[] { settings with { IntervalSeconds = 29 }, settings with { IntervalSeconds = 86401 },
                     settings with { PageSize = 0 }, settings with { PageSize = 101 },
                     settings with { DeadlineSeconds = 9 }, settings with { DeadlineSeconds = 121 } })
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.UpdateProjectAsync(project.Id, Definition(invalid), project.Revision));
    }

    [Fact]
    public async Task PagingUnblocksLaterIssuesSuppressesTerminalWorkAcrossRestartAndAllowsExplicitRetry()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true, 30, 2));
        fixture.Reads.Blocked.Add(2);
        await fixture.Cycle.RunDueCyclesAsync();
        var first = Assert.Single(await fixture.Store.GetExecutionsAsync());
        Assert.Equal("1", first.WorkReference.Id);
        await fixture.Store.CancelQueuedExecutionAsync(first.Id);
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(new string?[] { null, "later" }, fixture.Reads.Cursors);
        Assert.Equal(new[] { "1", "3" }, (await fixture.Store.GetExecutionsAsync()).Select(work => work.WorkReference.Id));
        fixture.Reads.Blocked.Clear();
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(new[] { "1", "3", "2" }, (await fixture.Store.GetExecutionsAsync()).Select(work => work.WorkReference.Id));
        var restarted = new SqliteRegistryStore(fixture.Database);
        await restarted.InitializeAsync();
        var cycle = new AutomaticIssueDiscoveryService(restarted, new(restarted, fixture.Reads),
            NullLogger<AutomaticIssueDiscoveryService>.Instance, fixture.Clock);
        using (cycle) await cycle.RunDueCyclesAsync();
        Assert.Equal(3, (await restarted.GetExecutionsAsync()).Count);
        var explicitRetry = await fixture.GitHub.EnqueueIssueAsync(project.Id, new("issue", "01"));
        Assert.NotEqual(first.Id, explicitRetry.Id);
        Assert.Equal("Queued", explicitRetry.State);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("repository")]
    [InlineData("delete")]
    [InlineData("opt-out")]
    [InlineData("recreate")]
    public async Task ProjectChangesDuringEligibilityReadCannotEnqueueStaleWork(string change)
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true));
        fixture.Reads.BeforeGet = async (_, token) =>
        {
            fixture.Reads.BeforeGet = null;
            switch (change)
            {
                case "disable": await fixture.Store.UpdateProjectLifecycleAsync(project.Id, false, project.Revision, token); break;
                case "delete": await fixture.Store.RemoveProjectAsync(project.Id, project.Revision, token); break;
                case "repository": await fixture.Store.UpdateProjectAsync(project.Id, Definition(new(true)) with { Repository = "team/other" }, project.Revision, token); break;
                case "recreate":
                    await fixture.Store.RemoveProjectAsync(project.Id, project.Revision, token);
                    fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
                    await fixture.CreateProjectAsync(new(true));
                    break;
                case "opt-out": await fixture.Store.UpdateProjectAsync(project.Id, Definition(null), project.Revision, token); break;
            }
        };
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
    }

    [Fact]
    public async Task OverlapIsSkippedAndExplicitEnqueueRaceIsAtomicallyDeduplicated()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reads.BeforePage = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var running = fixture.Cycle.RunDueCyclesAsync();
        await entered.Task;
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(1, fixture.Reads.Pages);
        await fixture.GitHub.EnqueueIssueAsync(project.Id, new("issue", "001"));
        release.SetResult();
        await running;
        Assert.Equal(new[] { "1", "2" }, (await fixture.Store.GetExecutionsAsync()).Select(work => work.WorkReference.Id));
        var races = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            try
            {
                if (index % 2 == 0) await fixture.GitHub.EnqueueIssueAsync(project, new("issue", "099"));
                else await fixture.GitHub.EnqueueIssueAsync(project.Id, new("github-issue", "99"));
                return true;
            }
            catch (ExecutionRequestConflictException) { return false; }
        }));
        Assert.Single(races, result => result);
    }

    [Fact]
    public async Task DeletedProjectAliasCannotReplayCanonicalTerminalIssue()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var original = await fixture.CreateProjectAsync(new(true));
        var first = await fixture.GitHub.EnqueueIssueAsync(original.Id, new("issue", "01"));
        await fixture.Store.CancelQueuedExecutionAsync(first.Id);
        Assert.True(await fixture.Store.RemoveProjectAsync(original.Id, original.Revision));
        var alias = await fixture.Store.CreateProjectAsync(Definition(new(true)) with { Name = "Alias", Repository = "TEAM/PROJECT" });
        await fixture.Cycle.RunDueCyclesAsync();
        var executions = await fixture.Store.GetExecutionsAsync();
        Assert.Equal(2, executions.Count);
        Assert.Single(executions, work => work.WorkReference.Id == "1");
        Assert.Equal(alias.Id, Assert.Single(executions, work => work.WorkReference.Id == "2").ProjectId);
    }

    [Fact]
    public async Task SupportedHistoryWithoutUrlsRetainsCanonicalIdentityBeforeProjectDeletion()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true));
        var first = await fixture.GitHub.EnqueueIssueAsync(project.Id, new("issue", "1"));
        await fixture.Store.CancelQueuedExecutionAsync(first.Id);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE execution_requests SET work_reference_json=json_remove(work_reference_json,'$.url');";
            await command.ExecuteNonQueryAsync();
        }
        var restarted = new SqliteRegistryStore(fixture.Database);
        await restarted.InitializeAsync();
        Assert.Equal("https://github.com/team/project/issues/1", (await restarted.GetExecutionAsync(first.Id))?.WorkReference.Url);
        Assert.True(await restarted.RemoveProjectAsync(project.Id, project.Revision));
        var alias = await restarted.CreateProjectAsync(Definition(new(true)) with { Name = "Alias", Repository = "TEAM/PROJECT" });
        var github = new ServerGitHubAdministrationService(restarted, fixture.Reads);
        await Assert.ThrowsAsync<ExecutionRequestConflictException>(() => github.EnqueueIssueAsync(alias, new("github-issue", "01")));
    }

    [Theory]
    [InlineData("authentication-failed")]
    [InlineData("rate-limited")]
    [InlineData("read-unavailable")]
    public async Task FailedPagesAuthorizeNothingAndHealthyProjectsContinueOnNextInterval(string code)
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        await fixture.CreateProjectAsync(new(true, 30));
        var healthy = await fixture.Store.CreateProjectAsync(Definition(new(true, 30)) with { Name = "Healthy", Repository = "team/healthy" });
        fixture.Reads.FailureRepository = "team/project";
        fixture.Reads.Failure = new("team/project", "Unavailable", code);
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.All(await fixture.Store.GetExecutionsAsync(), work => Assert.Equal(healthy.Id, work.ProjectId));
        var pages = fixture.Reads.Pages;
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(pages, fixture.Reads.Pages);
        fixture.Reads.Failure = null;
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(5, (await fixture.Store.GetExecutionsAsync()).Count);
    }

    [Fact]
    public async Task FailedEligibilityReadAdvancesCompletePageWithoutAuthorizingFailedCandidate()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        await fixture.CreateProjectAsync(new(true, 30));
        fixture.Reads.BeforeGet = (_, _) => throw new GitHubReadUnavailableException("team/project", "Unavailable", "rate-limited");
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
        fixture.Reads.BeforeGet = null;
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal("3", Assert.Single(await fixture.Store.GetExecutionsAsync()).WorkReference.Id);
    }

    [Fact]
    public async Task ShutdownCancelsAndJoinsOwnedReadAndAuthorizesNothing()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        await fixture.CreateProjectAsync(new(true));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reads.BeforePage = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { exited.SetResult(); }
        };
        await fixture.Cycle.StartAsync(CancellationToken.None);
        await entered.Task;
        await fixture.Cycle.StopAsync(CancellationToken.None);
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public async Task AutomaticQueueUsesNormalManagedAssignmentAndNeverReplaysTerminalWork(string state)
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true, 30));
        await fixture.Cycle.RunDueCyclesAsync();
        var worker = Guid.NewGuid().ToString("N");
        CodexServer.WorkerCapability[] capabilities = [new("tool", "git"), new("agent-provider", "codex"),
            .. WorkerAuthenticationRequirements.ForRepository(project.Repository).Select(requirement =>
                new CodexServer.WorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope))];
        await fixture.Store.RegisterWorkerAsync(new(1, worker, "Worker", "1.0", "test", 1, capabilities));
        await fixture.Store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 1, capabilities, []));
        fixture.Reads.Blocked.Add(1);
        var request = new WorkerAssignmentRequest(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        var assignment = Assert.IsType<WorkAssignment>((await fixture.GitHub.RequestAssignmentAsync(request)).Assignment);
        Assert.Equal("2", assignment.Work.Id);
        var blocked = Assert.Single(await fixture.Store.GetExecutionsAsync(), execution => execution.WorkReference.Id == "1");
        Assert.Equal("blocked", blocked.ManagedEligibilityState);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        await fixture.Store.ReportExecutionAsync(assignment.ServerExecutionId,
            new(worker, assignment.AssignmentId, "local-run", "Running", "Codex", Generation: lease.Generation));
        // A cycle must also deduplicate assigned/running work before it becomes terminal.
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        await fixture.Store.ReportExecutionAsync(assignment.ServerExecutionId,
            new(worker, assignment.AssignmentId, "local-run", state, "Codex", Generation: lease.Generation));
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Single(await fixture.Store.GetExecutionsAsync(), execution => execution.WorkReference.Id == "2");
        Assert.Equal(state, (await fixture.Store.GetExecutionAsync(assignment.ServerExecutionId))?.State);
    }

    [Fact]
    public async Task BlockerCompletionAndReadyLabelChangesResumeQueuedWorkThroughNormalAssignment()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true, 30));
        fixture.Reads.OpenDependencies.Add(1);
        fixture.Reads.Unready.Add(2);
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Empty(await fixture.Store.GetExecutionsAsync());

        fixture.Reads.OpenDependencies.Clear();
        fixture.Reads.Unready.Clear();
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync(); // Finish the sweep.
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        var queued = Assert.Single(await fixture.Store.GetExecutionsAsync(), work => work.WorkReference.Id == "1");
        var worker = Guid.NewGuid().ToString("N");
        await fixture.Store.RegisterWorkerAsync(new(1, worker, "Worker", "1.0", "test", 1, []));
        await fixture.Store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 1, [], []));
        var request = new WorkerAssignmentRequest(worker, true, 1, new Dictionary<string, int> { [project.Id] = 1 });
        Assert.False((await fixture.GitHub.RequestAssignmentAsync(request)).HasWork);
        CodexServer.WorkerCapability[] capabilities = [new("tool", "git"), new("agent-provider", "codex"),
            .. WorkerAuthenticationRequirements.ForRepository(project.Repository).Select(requirement =>
                new CodexServer.WorkerCapability(requirement.Type, requirement.Name, Scope: requirement.Scope))];
        await fixture.Store.HeartbeatWorkerAsync(new(1, worker, "1.0", "running", 0, 1, capabilities, []));

        fixture.Reads.OpenDependencies.Add(1);
        fixture.Reads.Unready.UnionWith([2, 3]);
        Assert.False((await fixture.GitHub.RequestAssignmentAsync(request)).HasWork);
        Assert.Equal("blocked", (await fixture.Store.GetExecutionAsync(queued.Id))?.ManagedEligibilityState);
        fixture.Reads.OpenDependencies.Clear();
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal("eligible", (await fixture.Store.GetExecutionAsync(queued.Id))?.ManagedEligibilityState);
        var assignment = Assert.IsType<WorkAssignment>((await fixture.GitHub.RequestAssignmentAsync(request)).Assignment);
        Assert.Equal(queued.Id, assignment.ServerExecutionId);
        var lease = Assert.IsType<ExecutionLease>(assignment.Lease);
        Assert.True(lease.Generation > 1);
        await fixture.Store.ReportExecutionAsync(queued.Id,
            new(worker, assignment.AssignmentId, "local-run", "Running", "Codex", Generation: lease.Generation));
        await fixture.Store.ReportExecutionAsync(queued.Id,
            new(worker, assignment.AssignmentId, "local-run", "Completed", "Codex", Generation: lease.Generation));
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Single(await fixture.Store.GetExecutionsAsync(), work => work.WorkReference.Id == "1");
        Assert.Equal("Completed", (await fixture.Store.GetExecutionAsync(queued.Id))?.State);
        Assert.False((await fixture.GitHub.RequestAssignmentAsync(request)).HasWork);
    }

    [Fact]
    public async Task UnavailableQueuedRefreshPreservesEligibilityAndRedactsProviderFailure()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true, 30));
        var queued = await fixture.GitHub.EnqueueIssueAsync(project.Id, new("issue", "1"));
        await fixture.Store.UpdateManagedEligibilityAsync(queued.Id,
            new("unavailable", ["Previous read unavailable."], fixture.Clock.GetUtcNow()));
        var reads = 0;
        fixture.Reads.BeforeGet = (_, _) =>
        {
            if (++reads == 2) throw new GitHubReadUnavailableException(project.Repository, "private-token", "rate-limited");
            return Task.CompletedTask;
        };
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal("unavailable", (await fixture.Store.GetExecutionAsync(queued.Id))?.ManagedEligibilityState);
        Assert.Equal(queued.Id, Assert.Single(await fixture.Store.GetExecutionsAsync()).Id);
        Assert.Contains(fixture.Logger.Events, entry => entry.Message.Contains("rate-limited", StringComparison.Ordinal));
        Assert.All(fixture.Logger.Events, entry => Assert.DoesNotContain("private-token", entry.Message, StringComparison.Ordinal));
        fixture.Reads.BeforeGet = null;
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal("eligible", (await fixture.Store.GetExecutionAsync(queued.Id))?.ManagedEligibilityState);
        Assert.Single(await fixture.Store.GetExecutionsAsync(), work => work.WorkReference.Id == "1");
    }

    [Fact]
    public async Task DisabledProjectDoesNotDiscoverUntilReenabled()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true, 30));
        var disabled = await fixture.Store.UpdateProjectLifecycleAsync(project.Id, false, project.Revision);
        Assert.NotNull(disabled);
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(0, fixture.Reads.Pages);
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
        await fixture.Store.UpdateProjectLifecycleAsync(project.Id, true, disabled.Revision);
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(2, (await fixture.Store.GetExecutionsAsync()).Count);
    }

    [Fact]
    public async Task EligibilityChangesAndMissingIssuesAreSkippedThenReconsidered()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        await fixture.CreateProjectAsync(new(true, 30));
        fixture.Reads.BeforeGet = (number, _) =>
        {
            fixture.Reads.Blocked.Add(number);
            return Task.CompletedTask;
        };
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
        fixture.Reads.BeforeGet = null;
        fixture.Reads.Missing.Add(3);
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
        fixture.Reads.Missing.Clear();
        fixture.Reads.Blocked.Clear();
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(2, (await fixture.Store.GetExecutionsAsync()).Count);
        fixture.Clock.Advance();
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(3, (await fixture.Store.GetExecutionsAsync()).Count);
    }

    [Fact]
    public async Task DeadlineUsesClockAndStopsReadWithoutImmediateRetry()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        await fixture.CreateProjectAsync(new(true, 30, 25, 10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reads.BeforePage = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var running = fixture.Cycle.RunDueCyclesAsync();
        await entered.Task;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await running;
        Assert.Empty(await fixture.Store.GetExecutionsAsync());
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.Equal(1, fixture.Reads.Pages);
        Assert.Contains(fixture.Logger.Events, entry => entry.Message.Contains("deadline-exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoWorkCyclesStayDebugAndSummariesCarryCountsAndCorrelation()
    {
        using var fixture = new Fixture();
        await fixture.Store.InitializeAsync();
        var project = await fixture.CreateProjectAsync(new(true, 30));
        fixture.Reads.Blocked.UnionWith([1, 2]);
        await fixture.Cycle.RunDueCyclesAsync();
        Assert.All(fixture.Logger.Events, entry => Assert.Equal(LogLevel.Debug, entry.Level));
        var summary = Assert.Single(fixture.Logger.Events, entry => entry.Id == 2203);
        Assert.Contains(project.Id, summary.Message, StringComparison.Ordinal);
        Assert.Contains("discovered 2; enqueued 0; skipped 2; duplicates 0", summary.Message, StringComparison.Ordinal);
        Assert.Contains(fixture.Logger.Events, entry => entry.Message.Contains("blocked", StringComparison.Ordinal));
    }

    private static CentralProjectDefinition Definition(AutomaticIssueDiscovery? settings) =>
        new("Project", "team/project", "main", "", IssueReadyLabel: "ready", IssueBlockedLabel: "blocked", AutomaticDiscovery: settings);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ClockTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now.UtcTicks;
        public void Advance(TimeSpan? duration = null)
        {
            _now += duration ?? TimeSpan.FromSeconds(30);
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        private sealed class ClockTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                _period = period;
                return true;
            }
            public void FireIfDue()
            {
                if (_due is not { } due || due > clock.GetUtcNow()) return;
                _due = _period == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + _period;
                callback(state);
            }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "automatic-discovery-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(_directory, "registry.db");
        public SqliteRegistryStore Store { get; }
        public Reads Reads { get; } = new();
        public Clock Clock { get; } = new();
        public Logger Logger { get; } = new();
        public ServerGitHubAdministrationService GitHub { get; }
        public AutomaticIssueDiscoveryService Cycle { get; }
        public Fixture()
        {
            Store = new(Database, timeProvider: Clock);
            GitHub = new(Store, Reads, timeProvider: Clock, issueWriter: new ServerGitHubIssueWriteService(
                (_, _) => throw new InvalidOperationException("Discovery must not mutate GitHub.")));
            Cycle = new(Store, GitHub, Logger, Clock);
        }
        public Task<CentralProject> CreateProjectAsync(AutomaticIssueDiscovery? settings = null) => Store.CreateProjectAsync(Definition(settings));
        public void Dispose()
        {
            Cycle.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Logger : ILogger<AutomaticIssueDiscoveryService>
    {
        public List<(LogLevel Level, int Id, string Message)> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Events.Add((logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    private sealed class Reads : IServerGitHubReadService
    {
        public int Pages { get; private set; }
        public List<string?> Cursors { get; } = [];
        public HashSet<int> Blocked { get; } = [];
        public HashSet<int> Unready { get; } = [];
        public HashSet<int> OpenDependencies { get; } = [];
        public HashSet<int> Missing { get; } = [];
        public Func<CancellationToken, Task>? BeforePage { get; set; }
        public Func<int, CancellationToken, Task>? BeforeGet { get; set; }
        public string? FailureRepository { get; set; }
        public GitHubReadUnavailableException? Failure { get; set; }
        public async Task<ManagedGitHubIssuePage> ReadDiscoveryPageAsync(CentralProject project, GitHubIssueDiscoveryQuery query,
            CancellationToken cancellationToken = default)
        {
            Pages++;
            Cursors.Add(query.After);
            if (BeforePage is { } before) await before(cancellationToken);
            if (Failure is { } failure && project.Repository == FailureRepository) throw failure;
            return new((query.After is null ? new[] { 1, 2 } : [3]).Select(number => Issue(project, number)).ToArray(),
                query.After is null ? "later" : null);
        }
        public async Task<ManagedGitHubIssue?> GetIssueAsync(CentralProject project, int number, CancellationToken cancellationToken = default)
        {
            if (BeforeGet is { } before) await before(number, cancellationToken);
            return Missing.Contains(number) ? null : Issue(project, number);
        }
        private ManagedGitHubIssue Issue(CentralProject project, int number) => ManagedGitHubIssueEligibility.Evaluate(project, new(number, "Issue", "", "OPEN",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, $"https://github.com/{project.Repository}/issues/{number}",
            Blocked.Contains(number) ? ["ready", "blocked"] : Unready.Contains(number) ? [] : ["ready"],
            OpenDependencies.Contains(number) ? [new(99, "Prerequisite", "OPEN", $"https://github.com/{project.Repository}/issues/99")] : [],
            false, []));
        public Task<GitHubRepositoryAccess> CheckAccessAsync(CentralProject project, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedGitHubIssue>> ListIssuesAsync(CentralProject project, GitHubIssueQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubIssueRelationships?> GetIssueRelationshipsAsync(CentralProject project, int issueNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
