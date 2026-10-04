namespace CodexWorker;

using CodexProvisioning;

/// <summary>Global host with bounded execution concurrency across independently configured projects.</summary>
public sealed class WorkerHost
{
    private readonly GlobalWorkerConfiguration _global;
    private readonly IReadOnlyList<(string Path, WorkerConfiguration Configuration)> _projects;
    private readonly WorkerConsole _output;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _operationalLog;
    private readonly ProcessRunner _runner = new();
    private readonly WorkerRegistrationClient _registration;
    private readonly IAgentAuthenticationProvider? _agentAuthentication;

    public WorkerHost(GlobalWorkerConfiguration global,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects, WorkerConsole? output = null,
        TimeProvider? timeProvider = null, Action<string>? operationalLog = null,
        WorkerRegistrationClient? registrationClient = null, IAgentAuthenticationProvider? agentAuthentication = null)
    {
        _registration = registrationClient ?? new WorkerRegistrationClient();
        _agentAuthentication = agentAuthentication;
        _global = global;
        _projects = projects;
        _output = output ?? new WorkerConsole();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _operationalLog = operationalLog ?? (_ => { });
    }

    internal async Task RejectIncompatibleAssignmentAsync(WorkerAssignmentContract assignment,
        WorkerConfiguration configuration, ExecutionHistoryStore history, string reason, CancellationToken token)
    {
        if (assignment.Work.Type is not ("issue" or "github-issue") ||
            !int.TryParse(assignment.Work.Id, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var issueNumber) || issueNumber <= 0 ||
            assignment.Lease is not { State: "Active", Generation: > 0 } lease)
            throw new WorkerInfrastructureException("Incompatible assignment has no valid Issue or ownership lease.");
        var now = _timeProvider.GetUtcNow();
        var previous = (await history.ReadAllAsync(token)).Where(entry => entry.Project == configuration.Project.Name &&
            entry.Repository == configuration.Project.Repository && entry.IssueNumber == issueNumber).ToArray();
        var safeReason = FailureDiagnosticRedactor.Redact(reason, configuration.Environment.Variables.Values.ToArray());
        if (safeReason.Length > 1000) safeReason = safeReason[..1000];
        // No Issue claim or workspace exists. Record a terminal refusal so neither local
        // recovery nor Server lease expiry mistakes it for an interrupted execution.
        var entry = new ExecutionHistoryEntry(Guid.NewGuid(), configuration.Project.Name,
            configuration.Project.Repository, issueNumber, $"Issue #{issueNumber}", "", configuration.Git.BaseBranch,
            now, now, "Blocked", 0, null, null, 0, [], null, null, null, safeReason,
            RecoveryState: "preparation-failed", AttemptNumber: previous.Length == 0 ? 1 : previous.Max(item => item.AttemptNumber) + 1,
            ServerExecutionId: assignment.ServerExecutionId, AssignmentId: assignment.AssignmentId,
            OwnershipGeneration: lease.Generation);
        await history.CreateAsync(entry, token);
        await _registration.ReportExecutionAsync(_global.Server, entry, "Failed", null, lease.Generation, token);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var configuredProjects = _projects;
        ManagedConfigurationSynchronizer? managedConfiguration = null;
        if (_global.Server.Enabled && _global.Projects.Ownership == "managed")
        {
            var identityPath = _global.Server.IdentityFile ?? WorkerIdentity.DefaultPath;
            managedConfiguration = new ManagedConfigurationSynchronizer(identityPath + ".configuration.json");
        }
        _output.Startup(configuredProjects.Count);
        foreach (var item in configuredProjects) _output.ProjectLoaded(item.Configuration.Project.Name);
        using var telegram = new TelegramNotifier(_global.Telegram.Enabled, _output);
        ExecutionHistoryStore? history = null;
        WorkerRuntimeReadModel? runtimeReadModel = null;
        Microsoft.AspNetCore.Builder.WebApplication? managementApi = null;
        ProjectConfigurationWatcher? configurationWatcher = null;
        var runtimes = new List<ProjectRuntime>();
        var allRuntimes = new List<ProjectRuntime>();
        var repositoryGates = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        var discoveredCapabilities = (IReadOnlyList<WorkerCapabilityContract>)Array.Empty<WorkerCapabilityContract>();
        var validatedConfigurations = new HashSet<WorkerConfiguration>(ReferenceEqualityComparer.Instance);
        string? activeProject = null;
        var operational = false;
        var nextManagedConfigurationSync = DateTimeOffset.MinValue;
        var active = new Dictionary<Task<IssueProcessingResult?>, ProjectRuntime>();
        var leaseRenewals = new Dictionary<Task<IssueProcessingResult?>, (CancellationTokenSource Stop, Task Run)>();
        CancellationTokenSource? executionCancellation = null;
        WorkerHeartbeatStatus heartbeatStatus = new(0, Array.Empty<string>(), WorkerLifecycleStates.Starting);
        IReadOnlyList<WorkerCapabilityContract> heartbeatCapabilities = [];
        WorkerHeartbeatLoop? heartbeat = null;
        WorkerHeartbeatStatus CurrentHeartbeatStatus()
        {
            var status = Volatile.Read(ref heartbeatStatus);
            return runtimeReadModel?.HeartbeatStatus(status) ?? status;
        }
        async Task ReportProvisionedCapabilitiesAsync(IReadOnlyList<WorkerCapabilityContract> capabilities, CancellationToken token)
        {
            Volatile.Write(ref heartbeatCapabilities, capabilities);
            if (_global.Server.Enabled)
            {
                var status = CurrentHeartbeatStatus();
                await _registration.HeartbeatAsync(_global.Server, _global.Worker.MaxParallelTasks,
                    status.ActiveExecutions, status.Projects, status.State, token, capabilities, managedConfiguration?.Status);
            }
        }
        using var shutdownRegistration = ct.Register(() =>
        {
            var status = Volatile.Read(ref heartbeatStatus);
            Volatile.Write(ref heartbeatStatus, status with { State = "shutting-down" });
            if (runtimeReadModel is not null) runtimeReadModel.State = "shutting-down";
            _output.Warning("Worker shutdown requested");
        });
        try
        {
            discoveredCapabilities = await _registration.CapabilityDiscovery.GetCachedAsync(ct);
            heartbeatCapabilities = discoveredCapabilities;
            if (managedConfiguration?.HasCachedSnapshot == true)
            {
                try { _ = managedConfiguration.LoadLastValid(configuredProjects); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    managedConfiguration.RecordUnavailable(ex);
                }
            }
            if (_global.Server.Enabled)
            {
                var registration = _registration;
                var registered = false;
                try
                {
                    await registration.RegisterAsync(_global.Server, _global.Worker.MaxParallelTasks, ct);
                    registered = true;
                }
                catch (Exception ex) when ((ex is HttpRequestException or WorkerStartupException or TaskCanceledException) &&
                    (ex is not OperationCanceledException || !ct.IsCancellationRequested))
                {
                    if (_global.Projects.Ownership != "managed") throw;
                    configuredProjects = LoadCachedManagedConfiguration(managedConfiguration!, configuredProjects, ex);
                }
                if (managedConfiguration is not null && registered)
                {
                    try
                    {
                        var desired = await registration.GetManagedConfigurationAsync(_global.Server, ct);
                        configuredProjects = managedConfiguration.Apply(desired, _projects);
                    }
                    catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException) &&
                        (ex is not OperationCanceledException || !ct.IsCancellationRequested))
                    {
                        managedConfiguration.RecordUnavailable(ex);
                        configuredProjects = LoadCachedManagedConfiguration(managedConfiguration, configuredProjects, ex);
                    }
                }
            }
            if (_global.Server.Enabled)
            {
                heartbeat = new WorkerHeartbeatLoop(_global.Server, _global.Worker.MaxParallelTasks, CurrentHeartbeatStatus,
                    message => _output.Warning(message), () => Volatile.Read(ref heartbeatCapabilities),
                    () => managedConfiguration?.Status, _registration);
                heartbeat.Start();
            }
            history = new ExecutionHistoryStore();
            if (_global.Server.Enabled)
            {
                foreach (var entry in await history.ReadAllAsync(ct))
                {
                    if (entry.ServerExecutionId is null || entry.AssignmentId is null || entry.OwnershipGeneration is null ||
                        entry.State is not ("Completed" or "Blocked" or "Failed" or "IntegrationConflict" or "InfrastructureFailure" or "Cancelled")) continue;
                    // A shutdown interruption leaves the last Server stage/lease intact so
                    // expiry reconciliation can fence retries and retain uncertain integration.
                    if (IsShutdownInterruption(entry)) continue;
                    var serverState = entry.State == "Completed" ? "Completed" : "Failed";
                    try { await _registration.ReportExecutionAsync(_global.Server, entry, serverState, null, entry.OwnershipGeneration.Value, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    { _output.Warning($"Codex Server result reporting remains pending for execution {entry.ExecutionId}: {ex.Message}"); }
                }
            }
            var configurationProvider = new LocalYamlProjectConfigurationProvider(_global.Projects.Directory);
            var lifecycle = new WorkerLifecycle();
            runtimeReadModel = new WorkerRuntimeReadModel(_global, configuredProjects, history, managedConfiguration: managedConfiguration,
                lifecycle: lifecycle, repositoryGates: repositoryGates);
            var configurationService = _global.Projects.Ownership == "managed"
                ? null
                : new ProjectConfigurationService(configurationProvider, history, _global.Projects.Directory, runtimeReadModel.Registry);
            runtimeReadModel.Events.Publish("worker.starting", "Worker startup began.");
            managementApi = await ManagementApi.StartAsync(runtimeReadModel, _global.Api, ct, configurationService);
            foreach (var (path, config) in configuredProjects)
            {
                var project = CreateRuntime(path, config, telegram, history, repositoryGates, ct);
                runtimeReadModel.CompletedBranchMaintenance.Register(config, project.Git, project.RepositoryGate);
                runtimes.Add(project);
                allRuntimes.Add(project);
            }

            var managed = _global.Server.Enabled && _global.Projects.Ownership == "managed";
            if (_global.Server.Enabled)
            {
                var registration = _registration;
                var initialPlan = await registration.RequestProvisioningPlanAsync(_global.Server, ct);
                if (initialPlan is not null)
                {
                    runtimeReadModel.Events.Publish("provisioning.started", $"Provisioning plan {initialPlan.Id} started.");
                    var result = await CreateProvisioningExecutor(registration, runtimes, ReportProvisionedCapabilitiesAsync).ExecuteAsync(initialPlan,
                        initialPlan.WorkerId,
                        (report, token) => ReportProvisioningStateAsync(registration, initialPlan.Id, report, token), ct);
                    runtimeReadModel.Events.Publish("provisioning.finished", $"Provisioning plan {initialPlan.Id} finished.");
                    if (result.State != "Completed")
                    {
                        if (!managed) throw new WorkerInfrastructureException($"Provisioning plan {initialPlan.Id} failed before execution readiness.");
                        _output.Warning($"Provisioning plan {initialPlan.Id} did not complete; missing readiness remains unavailable.");
                    }
                }
            }

            async Task InitializeProjectsAsync(CancellationToken token)
            {
                var startupPlans = runtimes.Where(project => !validatedConfigurations.Contains(project.Configuration))
                    .Select(project => new ProjectStartupPlan(
                    project.Path,
                    project.Configuration.Project.Name,
                    async token => { activeProject = project.Configuration.Project.Name; await project.Git.ValidateStartupReadOnlyAsync(token); },
                    async token => { activeProject = project.Configuration.Project.Name; await project.GitHub.ValidateCapabilitiesAsync(token); },
                    async token =>
                    {
                        activeProject = project.Configuration.Project.Name;
                        return await project.GitHub.FindMissingLabelsAsync(project.Configuration.GitHub.RequiredLabels, token);
                    },
                    async (label, token) =>
                    {
                        activeProject = project.Configuration.Project.Name;
                        await project.GitHub.CreateLabelAsync(label, token);
                    },
                    async token => { activeProject = project.Configuration.Project.Name; await project.Worker.PrepareForHostAsync(token); }
                )).ToArray();
                var startupResult = await StartupCoordinator.RunIsolatedAsync(startupPlans, token);
                var createdLabels = startupResult.CreatedLabels;
                foreach (var failure in startupResult.UnavailableProjects)
                {
                    activeProject = failure.Name;
                    runtimeReadModel.Registry.MarkUnavailable(failure.Name, failure.Reason);
                    var message = $"Project '{failure.Name}' is unavailable after startup validation: {failure.Reason}";
                    _output.Warning(message);
                    _operationalLog($"Scheduler · {failure.Name} · unavailable · {failure.Reason}");
                }
                var unavailableNames = startupResult.UnavailableProjects.Select(failure => failure.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var healthyRuntimes = runtimes.Where(project => startupPlans.Any(plan => plan.Name == project.Configuration.Project.Name) &&
                    !unavailableNames.Contains(project.Configuration.Project.Name)).ToList();
                if (!managed && configuredProjects.Count > 0 && healthyRuntimes.Count == 0)
                {
                    var observations = await _registration.InventoryDiscovery.GetAsync(cancellationToken: token);
                    var missingProjectTool = !CapabilityCatalog.ExecutionReadiness(observations).Available;
                    // Missing execution prerequisites leave projects unavailable until their normal safety checks
                    // can run. An absent checkout is still a standalone configuration failure.
                    if (!missingProjectTool || configuredProjects.Any(project => !Directory.Exists(project.Configuration.Project.Directory)))
                    {
                        var failure = startupResult.UnavailableProjects[0];
                        var message = failure.Reason.StartsWith("read-only startup validation failed:", StringComparison.Ordinal)
                            ? $"Project configuration '{failure.Path}' failed read-only startup validation: {failure.Reason["read-only startup validation failed:".Length..].Trim()}"
                            : $"All configured projects are unavailable; '{failure.Name}' could not start: {failure.Reason}";
                        throw new WorkerInfrastructureException(message);
                    }
                }
                foreach (var project in healthyRuntimes)
                    if (runtimeReadModel.Registry.Status().Any(state => state.Name == project.Configuration.Project.Name &&
                        state.State == ProjectLifecycleState.Unavailable))
                        runtimeReadModel.Registry.Enable(project.Configuration.Project.Name);
                validatedConfigurations.UnionWith(healthyRuntimes
                    .Select(project => project.Configuration));
                Volatile.Write(ref heartbeatCapabilities, heartbeatCapabilities.Concat(healthyRuntimes.SelectMany(project =>
                    WorkerAuthenticationCapabilities.ForRepository(project.Configuration.Project.Repository)))
                    .Distinct().ToArray());
                if (_global.Server.Enabled)
                {
                    var status = CurrentHeartbeatStatus();
                    await _registration.HeartbeatAsync(_global.Server, _global.Worker.MaxParallelTasks,
                        status.ActiveExecutions, status.Projects, status.State, token, heartbeatCapabilities, managedConfiguration?.Status);
                }
                await ReconcileRecoveryAsync(healthyRuntimes, history, runtimeReadModel, token);
                if (healthyRuntimes.Count > 0)
                {
                    _output.GitHubCliReady();
                    _output.GitHubAuthenticationReady();
                    _output.GitRepositoryAuthenticationReady(healthyRuntimes.Count);
                    _output.GitHubLabelsReady(healthyRuntimes.Count, createdLabels);
                    _output.GitHubDependenciesReady();
                }

            }
            await InitializeProjectsAsync(ct);
            if (configurationService is not null)
                configurationWatcher = new ProjectConfigurationWatcher(_global.Projects.Directory, configurationService, runtimeReadModel.Registry);

            _output.GlobalPreflight();
            activeProject = null;
            IAgentAuthenticationProvider agentAuthentication = _agentAuthentication ?? new CodexAgentAuthenticationProvider(
                new CodexExecutor(_runner, new CodexSettings { Model = null }), _global.Worker.PreflightTimeoutSeconds);
            var readiness = new ManagedCodexReadiness(agentAuthentication);
            var agentReady = await readiness.EvaluateAsync(_registration.InventoryDiscovery, false, ct);
            var executionDependenciesReady = false;
            IReadOnlyList<string> executionReadinessBlockers = [];
            async Task PublishReadinessAsync(CancellationToken token)
            {
                heartbeatCapabilities = heartbeatCapabilities.Where(capability => capability.Type != "agent-provider").ToArray();
                if (agentReady)
                    heartbeatCapabilities = heartbeatCapabilities.Append(
                        WorkerAgentCapabilities.AuthenticatedProvider(agentAuthentication.Provider)).Distinct().ToArray();
                var localReadiness = CapabilityCatalog.ExecutionReadiness(
                    await _registration.InventoryDiscovery.GetAsync(cancellationToken: token));
                executionDependenciesReady = localReadiness.Available;
                executionReadinessBlockers = localReadiness.BlockingReasons;
                if (managedConfiguration is not null)
                {
                    var inventory = await _registration.InventoryDiscovery.GetAsync(cancellationToken: token);
                    foreach (var project in runtimes.Where(project => validatedConfigurations.Contains(project.Configuration)))
                    {
                        var definition = managedConfiguration.AppliedProjects.FirstOrDefault(item =>
                            MatchesServerProject(project.Configuration, item));
                        if (definition is null) continue;
                        var eligibility = CapabilityEligibility.Evaluate(CapabilityEligibility.ForProject(definition.Requirements, definition.Repository),
                            heartbeatCapabilities, inventory);
                        runtimeReadModel.Registry.ApplyExecutionEligibility(project.Configuration.Project.Name,
                            project.Configuration, eligibility);
                    }
                }
                var ready = agentReady && executionDependenciesReady && managedConfiguration?.Status.SynchronizationStatus != "error" &&
                    (runtimes.Count == 0 || runtimes.Any(project => validatedConfigurations.Contains(project.Configuration) &&
                        runtimeReadModel.Registry.Get(project.Configuration.Project.Name)?.State != ProjectLifecycleState.Unavailable));
                lifecycle.SetExecutionReadiness(ready, localReadiness.Available
                    ? readiness.DiagnosticCode ?? string.Join("; ", runtimeReadModel.Registry.Status()
                        .Where(project => project.UnavailableReason is not null).Select(project => $"{project.Name}: {project.UnavailableReason}"))
                    : string.Join(", ", localReadiness.BlockingReasons));
                runtimeReadModel.State = ready ? WorkerLifecycleStates.Running : WorkerLifecycleStates.NotReady;
                Volatile.Write(ref heartbeatStatus, heartbeatStatus with { State = runtimeReadModel.State });
                await ReportProvisionedCapabilitiesAsync(heartbeatCapabilities, token);
            }
            async Task RevokeReadinessAsync(CancellationToken token)
            {
                agentReady = false;
                validatedConfigurations.Clear();
                heartbeatCapabilities = heartbeatCapabilities.Where(capability => capability.Type != "authentication").ToArray();
                await PublishReadinessAsync(token);
            }
            async Task RefreshProvisionedReadinessAsync(CancellationToken token)
            {
                discoveredCapabilities = await _registration.CapabilityDiscovery.RefreshAsync(token);
                heartbeatCapabilities = discoveredCapabilities.Concat(heartbeatCapabilities.Where(
                    capability => capability.Type == "authentication")).Distinct().ToArray();
                await InitializeProjectsAsync(token);
                agentReady = await readiness.EvaluateAsync(_registration.InventoryDiscovery, true, token);
                await PublishReadinessAsync(token);
            }
            await PublishReadinessAsync(ct);
            await telegram.StartedAsync(runtimes.Count, ct);
            if (runtimes.Count == 0) _output.NoProjectsConfigured();
            _output.Started();
            runtimeReadModel.Events.Publish("worker.started", "Worker control loop is online.");
            operational = true;

            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var executionToken = executionCancellation.Token;
            var scheduler = new ProjectScheduler(runtimes.Count);
            var capacityLog = new SchedulerCapacityLog(_global.Worker.MaxParallelTasks, _operationalLog);
            var idleNoWorkNotification = new IdleNoWorkNotificationState();
            void ReportCapacity() => capacityLog.Report(active.Count, active.Values
                .GroupBy(project => project.Configuration.Project.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => (group.First().Configuration.Project.Name, group.Count(),
                    group.First().Configuration.Worker.MaxParallelTasks)));
            ReportCapacity();
            var idleHeartbeat = new IdleWorkerHeartbeat(_timeProvider.GetUtcNow());
            var projectObservations = (await _registration.InventoryDiscovery.GetAsync(cancellationToken: ct))
                .Select(state => state with { DetectedAtUtc = null }).ToArray();
            while (!ct.IsCancellationRequested)
            {
                var runtimeVersion = runtimeReadModel.Registry.Version;
                foreach (var completed in active.Keys.Where(task => task.IsCompleted).ToArray())
                {
                    var project = active[completed];
                    activeProject = project.Configuration.Project.Name;
                    if (leaseRenewals.Remove(completed, out var renewal))
                    {
                        renewal.Stop.Cancel();
                        try { await renewal.Run; } catch (OperationCanceledException) { }
                        renewal.Stop.Dispose();
                    }
                    try { await completed; }
                    catch (WorkerInfrastructureException ex) when (GitHubOperationException.Find(ex) is { } githubFailure)
                    {
                        PauseProjectForGitHubFailure(runtimeReadModel, project.Configuration.Project.Name, githubFailure);
                    }
                    catch (PreExecutionInfrastructureException ex)
                    {
                        var message = $"Project '{ex.Project}' is unavailable after a pre-execution infrastructure failure for Issue #{ex.IssueNumber} [{ExecutionFormatting.ShortId(ex.ExecutionId)}]: {ex.Message}";
                        runtimeReadModel.Registry.MarkUnavailable(ex.Project, ex.Message);
                        runtimeReadModel.Events.Publish("project.unavailable", message, ex.Project);
                        _output.Warning(message);
                        _operationalLog($"Scheduler · {message}");
                    }
                    active.Remove(completed);
                    runtimeReadModel.Registry.Release(project.Configuration.Project.Name);
                    ReportCapacity();
                    runtimeReadModel.Events.Publish("execution.finished", "Execution finished.", project.Configuration.Project.Name);
                    Volatile.Write(ref heartbeatStatus, new WorkerHeartbeatStatus(active.Count, active.Values.Select(value => value.Configuration.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), WorkerLifecycleStates.Running));
                }

                if (_global.Server.Enabled && _global.Projects.Ownership == "managed" && active.Count == 0 &&
                    _timeProvider.GetUtcNow() >= nextManagedConfigurationSync)
                {
                    nextManagedConfigurationSync = _timeProvider.GetUtcNow().AddSeconds(_global.Server.HeartbeatIntervalSeconds);
                    try
                    {
                        var registration = _registration;
                        var desired = await registration.GetManagedConfigurationAsync(_global.Server, executionToken);
                        var previousVersion = managedConfiguration!.Status.AppliedVersion;
                        var localProjects = ProjectConfigurationDiscovery.Load(_global.Projects.Directory, allowEmpty: true);
                        var replacement = managedConfiguration.Apply(desired, localProjects);
                        if (!string.Equals(previousVersion, desired.Version, StringComparison.Ordinal))
                        {
                            runtimeReadModel.Registry.ReplaceConfiguration(replacement);
                            runtimeReadModel.Events.Publish("configuration.synchronized", "Server-managed configuration was applied.");
                        }
                    }
                    catch (OperationCanceledException) when (executionToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        managedConfiguration!.RecordUnavailable(ex);
                        runtimeReadModel.Events.Publish("configuration.sync.failed", "Server-managed configuration could not be applied; the last valid configuration remains active.");
                        _output.Warning($"Server-managed configuration sync failed: {ex.Message}");
                    }
                }

                // Rebuild schedulable project runtimes from one atomically installed configuration snapshot.
                var currentConfigurations = runtimeReadModel.Registry.Snapshot();
                if (currentConfigurations.Count != runtimes.Count || currentConfigurations.Where((item, index) =>
                        !ReferenceEquals(item.Configuration, runtimes[index].Configuration)).Any())
                {
                    var currentByName = runtimes.ToDictionary(project => project.Configuration.Project.Name, StringComparer.OrdinalIgnoreCase);
                    var replacement = new List<ProjectRuntime>(currentConfigurations.Count);
                    foreach (var (path, configuration) in currentConfigurations)
                    {
                        if (currentByName.TryGetValue(configuration.Project.Name, out var existing) && ReferenceEquals(existing.Configuration, configuration))
                            replacement.Add(existing);
                        else
                        {
                            var project = CreateRuntime(path, configuration, telegram, history, repositoryGates, ct);
                            runtimeReadModel.CompletedBranchMaintenance.Register(configuration, project.Git, project.RepositoryGate);
                            replacement.Add(project);
                            allRuntimes.Add(project);
                        }
                    }
                    runtimes.Clear();
                    runtimes.AddRange(replacement);
                    Volatile.Write(ref heartbeatCapabilities, discoveredCapabilities.Concat(runtimes
                        .Where(project => validatedConfigurations.Contains(project.Configuration))
                        .SelectMany(project => WorkerAuthenticationCapabilities.ForRepository(project.Configuration.Project.Repository)))
                        .Distinct().ToArray());
                    if (managed && active.Count == 0) await InitializeProjectsAsync(executionToken);
                    if (agentReady) heartbeatCapabilities = heartbeatCapabilities.Append(
                        WorkerAgentCapabilities.AuthenticatedProvider(agentAuthentication.Provider)).Distinct().ToArray();
                    scheduler.Reconfigure(runtimes.Count);
                    capacityLog.Reconfigure(_global.Worker.MaxParallelTasks);
                    ReportCapacity();
                }

                var foundWork = false;
                if (_global.Projects.Ownership == "managed" && active.Count == 0 && !runtimeReadModel.Registry.WorkerDraining)
                {
                    var registration = _registration;
                    if (await registration.ExecuteProvisioningCommandAsync(_global.Server, _global.Worker.Provisioning,
                        executionToken, RevokeReadinessAsync))
                    {
                        await RefreshProvisionedReadinessAsync(executionToken);
                        continue;
                    }
                    var provisioningPlan = await registration.RequestProvisioningPlanAsync(_global.Server, executionToken);
                    if (provisioningPlan is not null)
                    {
                        await RevokeReadinessAsync(executionToken);
                        runtimeReadModel.Events.Publish("provisioning.started", $"Provisioning plan {provisioningPlan.Id} started.");
                        var provisioningResult = await CreateProvisioningExecutor(registration, runtimes, ReportProvisionedCapabilitiesAsync).ExecuteAsync(provisioningPlan, provisioningPlan.WorkerId,
                            (report, token) => ReportProvisioningStateAsync(registration, provisioningPlan.Id, report, token), executionToken);
                        runtimeReadModel.Events.Publish("provisioning.finished", $"Provisioning plan {provisioningPlan.Id} finished.");
                        if (provisioningResult.State != "Completed")
                            _output.Warning($"Provisioning plan {provisioningPlan.Id} did not complete; the Worker remains online.");
                        await RefreshProvisionedReadinessAsync(executionToken);
                        continue;
                    }
                }
                if (active.Count == 0)
                {
                    var observations = (await _registration.InventoryDiscovery.GetAsync(cancellationToken: executionToken))
                        .Select(state => state with { DetectedAtUtc = null }).ToArray();
                    if (!observations.SequenceEqual(projectObservations))
                    {
                        projectObservations = observations;
                        discoveredCapabilities = await _registration.CapabilityDiscovery.RefreshAsync(executionToken);
                        validatedConfigurations.Clear();
                        heartbeatCapabilities = discoveredCapabilities;
                        await InitializeProjectsAsync(executionToken);
                    }
                }
                agentReady = await readiness.EvaluateAsync(_registration.InventoryDiscovery, false, executionToken);
                await PublishReadinessAsync(executionToken);
                while (agentReady && executionDependenciesReady && managedConfiguration?.Status.SynchronizationStatus != "error" &&
                    !ct.IsCancellationRequested && active.Count < _global.Worker.MaxParallelTasks)
                {
                    if (_global.Projects.Ownership == "managed")
                    {
                        var projectLifecycles = runtimeReadModel.Registry.Status().ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
                        var projectCapacities = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var candidate in runtimes)
                        {
                            var name = candidate.Configuration.Project.Name;
                            var activeForProject = active.Values.Count(value => string.Equals(value.Configuration.Project.Name, name, StringComparison.OrdinalIgnoreCase));
                            if (validatedConfigurations.Contains(candidate.Configuration) &&
                                projectLifecycles.TryGetValue(name, out var state) && state.State == ProjectLifecycleState.Enabled &&
                                activeForProject < candidate.Configuration.Worker.MaxParallelTasks)
                            {
                                if (!projectCapacities.TryAdd(ServerProjectId(name), candidate.Configuration.Worker.MaxParallelTasks - activeForProject))
                                    throw new WorkerInfrastructureException($"Managed project names produce a duplicate Server project identity near '{name}'.");
                            }
                        }
                        if (projectCapacities.Count == 0) break;
                        activeProject = null;
                        // The Server may accept the assignment before a cancelled request returns.
                        // Treat interruption here as uncertain so shutdown preserves that state for inspection.

                        var integrationRecoveries = new List<CodexProvisioning.IntegrationRecoveryCandidate>();
                        foreach (var candidate in runtimes.Where(candidate => projectCapacities.ContainsKey(ServerProjectId(candidate.Configuration.Project.Name))))
                            integrationRecoveries.AddRange(await candidate.Worker.DiscoverManagedIntegrationRecoveriesAsync(
                                ServerProjectId(candidate.Configuration.Project.Name), executionToken));
                        var assignmentResponse = await _registration.RequestAssignmentAsync(_global.Server,
                            !runtimeReadModel.Registry.WorkerDraining, _global.Worker.MaxParallelTasks - active.Count,
                            projectCapacities, executionToken, integrationRecoveries.Take(128).ToArray());
                        foreach (var rejection in assignmentResponse.IntegrationRecoveryRejections ?? new Dictionary<string, string>())
                        {
                            if (!Guid.TryParse(rejection.Key, out var sourceId)) continue;
                            var candidate = integrationRecoveries.FirstOrDefault(item => item.WorkerExecutionId == rejection.Key);
                            var owner = runtimes.FirstOrDefault(item => candidate?.ProjectId == ServerProjectId(item.Configuration.Project.Name));
                            if (owner is not null) await owner.Worker.ReportIntegrationRecoveryRejectionAsync(sourceId, rejection.Value, executionToken);
                        }
                        if (!assignmentResponse.HasWork && assignmentResponse.Assignment is null) { break; }
                        if (!assignmentResponse.HasWork || assignmentResponse.Assignment is null)
                            throw new WorkerInfrastructureException("Codex Server returned an inconsistent assignment response; remote assignment state may be uncertain.");
                        var assignment = assignmentResponse.Assignment!;
                        var assignedProject = runtimes.FirstOrDefault(candidate => MatchesServerProject(candidate.Configuration, assignment.Project));
                        if (assignedProject is null)
                        {
                            runtimeReadModel.Events.Publish("assignment.rejected", $"Assignment {assignment.AssignmentId} references a project outside the configured Worker project registry.");
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} references a project that is not safely configured on this Worker; assignment remains owned by this Worker for inspection.");
                        }
                        if (assignment.Lease is not { State: "Active", Generation: > 0 } lease ||
                            lease.ExpiresAtUtc <= lease.AcquiredAtUtc || lease.RenewalIntervalSeconds is < 10 or > 3600 ||
                            lease.RenewalIntervalSeconds * 3 >= (lease.ExpiresAtUtc - lease.AcquiredAtUtc).TotalSeconds ||
                            lease.ExecutionId != assignment.ServerExecutionId || lease.WorkerId != assignment.WorkerId)
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} has no valid active ownership lease.");
                        var assignmentEligibility = CapabilityEligibility.Evaluate(
                            CapabilityEligibility.ForProject(assignment.Project.Requirements, assignment.Project.Repository), heartbeatCapabilities, await _registration.InventoryDiscovery.GetAsync(cancellationToken: executionToken));
                        if (!assignmentEligibility.IsEligible)
                        {
                            var reason = string.Join("; ", assignmentEligibility.MissingRequirements);
                            await RejectIncompatibleAssignmentAsync(assignment, assignedProject.Configuration, history,
                                reason, executionToken);
                            runtimeReadModel.Events.Publish("assignment.rejected", reason, assignedProject.Configuration.Project.Name);
                            continue;
                        }
                        if (!runtimeReadModel.Registry.TryReserve(assignedProject.Configuration.Project.Name, assignedProject.Configuration))
                        {
                            runtimeReadModel.Events.Publish("assignment.rejected", $"Assignment {assignment.AssignmentId} arrived after project '{assignment.Project.Name}' began draining.", assignedProject.Configuration.Project.Name);
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} arrived while project '{assignment.Project.Name}' was draining; assignment remains owned by this Worker for inspection.");
                        }
                        activeProject = assignedProject.Configuration.Project.Name;
                        Task<IssueProcessingResult?>? assignedExecution;
                        var leaseStop = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
                        // Start guarding the assignment before any GitHub label/comment work in
                        // ClaimAssignedAsync; that work can itself outlive a short lease.
                        var leaseRenewal = RenewLeaseWhileActiveAsync(lease, leaseStop);
                        try { assignedExecution = await assignedProject.Worker.ClaimAssignedAsync(assignment, leaseStop.Token); }
                        catch (WorkerInfrastructureException ex) when (GitHubOperationException.Find(ex) is { } githubFailure)
                        {
                            leaseStop.Cancel();
                            try { await leaseRenewal; } catch (OperationCanceledException) { }
                            finally
                            {
                                leaseStop.Dispose();
                                runtimeReadModel.Registry.Release(assignedProject.Configuration.Project.Name);
                            }
                            PauseProjectForGitHubFailure(runtimeReadModel, assignedProject.Configuration.Project.Name, githubFailure,
                                "The Server assignment remains leased until expiry reconciliation.");
                            continue;
                        }
                        catch
                        {
                            leaseStop.Cancel();
                            try { await leaseRenewal; } catch (OperationCanceledException) { }
                            finally
                            {
                                leaseStop.Dispose();
                                runtimeReadModel.Registry.Release(assignedProject.Configuration.Project.Name);
                            }
                            throw;
                        }
                        if (assignedExecution is null)
                        {
                            leaseStop.Cancel();
                            try { await leaseRenewal; } catch (OperationCanceledException) { }
                            finally
                            {
                                leaseStop.Dispose();
                                runtimeReadModel.Registry.Release(assignedProject.Configuration.Project.Name);
                            }
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} did not create an execution.");
                        }
                        active.Add(assignedExecution, assignedProject);
                        ReportCapacity();
                        leaseRenewals.Add(assignedExecution, (leaseStop, leaseRenewal));
                        Volatile.Write(ref heartbeatStatus, new WorkerHeartbeatStatus(active.Count, active.Values.Select(value => value.Configuration.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), WorkerLifecycleStates.Running));
                        runtimeReadModel.Events.Publish("execution.started", $"Server assignment {assignment.AssignmentId} started.", assignedProject.Configuration.Project.Name);
                        foundWork = true;
                        continue;
                    }
                    var selected = false;
                    foreach (var index in scheduler.ScanOrder())
                    {
                        var project = runtimes[index];
                        var projectActive = active.Values.Count(activeProject => string.Equals(activeProject.Configuration.Project.Name,
                            project.Configuration.Project.Name, StringComparison.OrdinalIgnoreCase));
                        if (projectActive >= project.Configuration.Worker.MaxParallelTasks) continue;
                        if (!runtimeReadModel.Registry.TryReserve(project.Configuration.Project.Name, project.Configuration)) continue;
                        activeProject = project.Configuration.Project.Name;
                        Task<IssueProcessingResult?>? execution;
                        try { execution = await project.Worker.ClaimNextAsync(executionToken); }
                        catch (WorkerInfrastructureException ex) when (GitHubOperationException.Find(ex) is { } githubFailure)
                        {
                            PauseProjectForGitHubFailure(runtimeReadModel, project.Configuration.Project.Name, githubFailure);
                            runtimeReadModel.Registry.Release(project.Configuration.Project.Name);
                            continue;
                        }
                        catch (PreExecutionInfrastructureException ex)
                        {
                            runtimeReadModel.Registry.MarkUnavailable(project.Configuration.Project.Name, ex.Message);
                            runtimeReadModel.Registry.Release(project.Configuration.Project.Name);
                            var message = $"Project '{ex.Project}' is unavailable after a pre-execution infrastructure failure for Issue #{ex.IssueNumber} [{ExecutionFormatting.ShortId(ex.ExecutionId)}]: {ex.Message}";
                            runtimeReadModel.Events.Publish("project.unavailable", message, ex.Project);
                            _output.Warning(message);
                            _operationalLog($"Scheduler · {message}");
                            continue;
                        }
                        catch { runtimeReadModel.Registry.Release(project.Configuration.Project.Name); throw; }
                        if (execution is null) { runtimeReadModel.Registry.Release(project.Configuration.Project.Name); continue; }
                        active.Add(execution, project);
                        ReportCapacity();
                        Volatile.Write(ref heartbeatStatus, new WorkerHeartbeatStatus(active.Count, active.Values.Select(value => value.Configuration.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), WorkerLifecycleStates.Running));
                        runtimeReadModel.Events.Publish("execution.started", "Execution claimed.", project.Configuration.Project.Name);
                        scheduler.Selected(index);
                        selected = true;
                        foundWork = true;
                        break;
                    }
                    if (!selected) break;
                }

                if (ct.IsCancellationRequested) break;
                if (idleNoWorkNotification.Observe(active.Count, foundWork))
                    await telegram.NoWorkAsync(ct);
                if (active.Count > 0)
                {
                    using var changeWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var activeFinished = Task.WhenAny(active.Keys);
                    var runtimeChanged = runtimeReadModel.Registry.WaitForChangeAsync(runtimeVersion, changeWait.Token);
                    await SchedulerPollWait.WaitForNextEventAsync(activeFinished, runtimeChanged,
                        TimeSpan.FromSeconds(_global.Worker.PollingSeconds),
                        active.Count < _global.Worker.MaxParallelTasks, ct);
                    changeWait.Cancel();
                    // Observe on the next pass so all task exceptions follow the common infrastructure path.
                    continue;
                }

                if (!executionDependenciesReady || !agentReady || managedConfiguration?.Status.SynchronizationStatus == "error")
                {
                    var blockers = executionReadinessBlockers.ToList();
                    if (!agentReady) blockers.Add(readiness.DiagnosticCode ?? "codex-execution-unavailable");
                    if (managedConfiguration?.Status.SynchronizationStatus == "error") blockers.Add("managed-configuration-unavailable");
                    await _output.WaitingForPrerequisitesAsync(blockers.Distinct(StringComparer.Ordinal).ToArray());
                }
                else
                    _output.Waiting();
                idleHeartbeat.EmitIfDue(_timeProvider.GetUtcNow(), _operationalLog);
                if (!foundWork)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(_global.Worker.PollingSeconds), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
            }
            Volatile.Write(ref heartbeatStatus, heartbeatStatus with { State = "draining" });
            // Cancellation has already reached active executions. Await them so no child process is orphaned.
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            if (active.Count > 0) await Task.WhenAll(active.Keys);
            await _output.StopWaitingAsync(finalizeLine: true);
            _output.Shutdown();
            runtimeReadModel.State = "stopped";
            runtimeReadModel.Events.Publish("worker.stopped", "Worker stopped.");
            await telegram.StoppedAsync(runtimes.Count, CancellationToken.None);
        }
        catch (Exception shutdownError) when (ct.IsCancellationRequested && WorkerShutdown.IsCancellation(shutdownError))
        {
            Volatile.Write(ref heartbeatStatus, heartbeatStatus with { State = "shutting-down" });
            if (runtimeReadModel is not null)
            {
                runtimeReadModel.State = "shutting-down";
                runtimeReadModel.Events.Publish("worker.shutting-down", "Worker shutdown requested.");
            }
            executionCancellation?.Cancel();
            // Await every execution, including those still validating or integrating. Only
            // controlled cancellations are expected; concurrent genuine failures still fail the host.
            await AwaitShutdownExecutionsAsync(active.Keys);
            await _output.StopWaitingAsync(finalizeLine: true);
            _output.Shutdown("Worker stopped.");
            if (runtimeReadModel is not null)
            {
                runtimeReadModel.State = "stopped";
                runtimeReadModel.Events.Publish("worker.stopped", "Worker stopped.");
            }
            await telegram.StoppedAsync(runtimes.Count, CancellationToken.None);
        }
        catch (Exception ex)
        {
            foreach (var renewal in leaseRenewals.Values) renewal.Stop.Cancel();
            executionCancellation?.Cancel();
            foreach (var renewal in leaseRenewals.Values)
            {
                try { await renewal.Run; } catch (OperationCanceledException) { } catch (Exception) { }
                renewal.Stop.Dispose();
            }
            leaseRenewals.Clear();
            if (active.Count > 0)
            {
                try { await Task.WhenAll(active.Keys); }
                catch { /* Preserve the first infrastructure failure after all child processes have stopped. */ }
            }
            await _output.StopWaitingAsync();
            var infrastructure = ex as WorkerInfrastructureException ??
                new WorkerInfrastructureException($"Worker startup or project processing failed: {ex.Message}", ex);
            if (runtimeReadModel is not null)
            {
                runtimeReadModel.State = "failed";
                runtimeReadModel.Events.Publish("worker.failed", "Worker stopped after an infrastructure failure.", activeProject);
            }
            _output.InfrastructureFailure($"Infrastructure failure: {infrastructure.Message}");
            await telegram.CriticalAsync(activeProject, infrastructure.Message, CancellationToken.None);
            if (!operational)
                throw new WorkerStartupException(infrastructure.Message, infrastructure);
            throw infrastructure;
        }
        finally
        {
            foreach (var renewal in leaseRenewals.Values) renewal.Stop.Cancel();
            foreach (var renewal in leaseRenewals.Values)
            {
                try { await renewal.Run; } catch (OperationCanceledException) { } catch (Exception) { }
                renewal.Stop.Dispose();
            }
            if (heartbeat is not null) await heartbeat.StopAsync();
            if (configurationWatcher is not null) await configurationWatcher.DisposeAsync();
            if (managementApi is not null) await managementApi.DisposeAsync();
            await _output.StopWaitingAsync();
            foreach (var project in allRuntimes) project.Git.Dispose();
            foreach (var gate in repositoryGates.Values) gate.Dispose();
            history?.Dispose();
            executionCancellation?.Dispose();
        }
    }

    internal static bool IsShutdownInterruption(ExecutionHistoryEntry entry) =>
        entry.State == "Cancelled" && entry.FailureReason?.StartsWith("Execution interrupted by Worker shutdown", StringComparison.Ordinal) == true;

    internal static bool RequiresManualGitHubReconciliation(GitHubIssueState issue, string readyLabel) =>
        issue.IsOpen && issue.Labels.Contains(readyLabel, StringComparer.OrdinalIgnoreCase);

    internal static async Task AwaitShutdownExecutionsAsync(IEnumerable<Task<IssueProcessingResult?>> executions)
    {
        var tasks = executions.Select(ObserveAsync).ToArray();
        try { await Task.WhenAll(tasks); }
        catch (Exception error)
        {
            throw new WorkerInfrastructureException("Execution failed while Worker shutdown was requested.", error);
        }

        static async Task ObserveAsync(Task<IssueProcessingResult?> task)
        {
            try { await task; }
            catch (WorkerShutdownException) { /* Explicitly recorded controlled interruption. */ }
            catch (WorkerInfrastructureException error) when (GitHubOperationException.Find(error) is not null)
            {
                // Shutdown already stopped scheduling. Preserve the recorded remote uncertainty
                // without turning a secondary Issue report failure into a process crash.
            }
        }
    }

    private IReadOnlyList<(string Path, WorkerConfiguration Configuration)> LoadCachedManagedConfiguration(
        ManagedConfigurationSynchronizer synchronizer,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> localProjects, Exception cause)
    {
        synchronizer.RecordUnavailable(cause);
        try
        {
            var cached = synchronizer.LoadLastValid(localProjects);
            _output.Warning($"Codex Server configuration is unavailable; continuing with applied version {synchronizer.Status.AppliedVersion}.");
            return cached;
        }
        catch (Exception cacheError) when (cacheError is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            throw new WorkerStartupException($"Managed Server configuration is unavailable and no valid cached configuration can be applied: {cacheError.Message}", cause);
        }
    }

    private async Task RenewLeaseWhileActiveAsync(ServerExecutionLeaseContract lease, CancellationTokenSource leaseStop)
    {
        var cancellationToken = leaseStop.Token;
        var client = _registration;
        var interval = TimeSpan.FromSeconds(Math.Clamp(lease.RenewalIntervalSeconds, 10, 3600));
        var expiresAtUtc = lease.ExpiresAtUtc;
        var renewalRetryDue = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = expiresAtUtc - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                _output.Warning($"Execution lease {lease.ExecutionId} expired before ownership could be renewed; cancelling work to prevent stale authoritative operations.");
                leaseStop.Cancel();
                return;
            }
            var wait = renewalRetryDue ? TimeSpan.Zero : remaining < interval ? remaining : interval;
            renewalRetryDue = false;
            try { await Task.Delay(wait, _timeProvider, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            try
            {
                var renewedExpiry = await client.RenewExecutionLeaseAsync(_global.Server, lease, cancellationToken);
                if (renewedExpiry is null)
                {
                    _output.Warning($"Execution lease {lease.ExecutionId} is no longer owned by this Worker; cancelling work before any further authoritative operations.");
                    leaseStop.Cancel();
                    return;
                }
                expiresAtUtc = renewedExpiry.Value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _output.Warning($"Execution lease renewal for {lease.ExecutionId} failed temporarily: {ex.Message}");
                var untilExpiry = expiresAtUtc - _timeProvider.GetUtcNow();
                if (untilExpiry <= TimeSpan.Zero)
                {
                    leaseStop.Cancel();
                    return;
                }
                try { await Task.Delay(untilExpiry < TimeSpan.FromSeconds(2) ? untilExpiry : TimeSpan.FromSeconds(2), _timeProvider, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                renewalRetryDue = true;
            }
        }
    }

    private static string ServerProjectId(string name)
    {
        var id = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (id.Length == 0) id = "project-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..12].ToLowerInvariant();
        return id.Length > 80 ? id[..80].TrimEnd('-') : id;
    }

    internal static bool MatchesServerProject(WorkerConfiguration configuration, ServerProjectContract project) =>
        string.Equals(ServerProjectId(configuration.Project.Name), project.Id, StringComparison.Ordinal) &&
        string.Equals(configuration.Project.Name, project.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(configuration.Project.Repository, project.Repository, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(configuration.Git.BaseBranch, project.DefaultBranch, StringComparison.Ordinal);

    private async Task ReconcileRecoveryAsync(IReadOnlyList<ProjectRuntime> runtimes, ExecutionHistoryStore history,
        WorkerRuntimeReadModel runtime, CancellationToken ct)
    {
        foreach (var project in runtimes)
        {
            await project.RepositoryGate.WaitAsync(ct);
            try
            {
                var entries = await history.ReadAllAsync(ct);
                foreach (var entry in entries.Where(item => item.Project == project.Configuration.Project.Name &&
                             item.Repository == project.Configuration.Project.Repository &&
                             item.RecoveryState == GitHubOperationException.ReconciliationRequiredState))
                {
                    GitHubIssueState remote;
                    try
                    {
                        remote = await project.GitHub.ReadIssueStateAsync(entry.IssueNumber, ct);
                    }
                    catch (WorkerInfrastructureException ex)
                    {
                        var safeDetail = FailureDiagnosticRedactor.Redact(ex.Message,
                            project.Configuration.Environment.Variables.Values.ToArray());
                        if (safeDetail.Length > 1200) safeDetail = safeDetail[..1180] + " … [truncated]";
                        var reason = $"GitHub reconciliation required: Issue #{entry.IssueNumber} remote state could not be verified for execution {entry.ExecutionId}: {safeDetail}";
                        runtime.Registry.MarkUnavailable(project.Configuration.Project.Name, reason);
                        runtime.Events.Publish("project.github-reconciliation-required", reason, project.Configuration.Project.Name);
                        _output.Warning(reason);
                        _operationalLog($"Scheduler · {project.Configuration.Project.Name} · Issue #{entry.IssueNumber} · execution {entry.ExecutionId} · startup GitHub reconciliation could not verify remote state · project scheduling paused.");
                        continue;
                    }
                    var readyRemains = RequiresManualGitHubReconciliation(remote, project.Configuration.GitHub.ReadyLabel);
                    if (readyRemains)
                    {
                        var reason = $"GitHub reconciliation required: Issue #{entry.IssueNumber} remains open and ready after execution {entry.ExecutionId} had an uncertain mutation. Inspect the Issue and history, then explicitly enable the project after reconciliation.";
                        runtime.Registry.MarkUnavailable(project.Configuration.Project.Name, reason);
                        runtime.Events.Publish("project.github-reconciliation-required", reason, project.Configuration.Project.Name);
                        _output.Warning(reason);
                        _operationalLog($"Scheduler · {project.Configuration.Project.Name} · Issue #{entry.IssueNumber} · execution {entry.ExecutionId} · remote state verified as open and ready · scheduling paused pending manual reconciliation.");
                        continue;
                    }
                    await history.UpdateRecoveryAsync(entry.ExecutionId, "github-reconciled", ct);
                    _operationalLog($"Scheduler · {project.Configuration.Project.Name} · Issue #{entry.IssueNumber} · execution {entry.ExecutionId} · GitHub state verified; Issue is not eligible for automatic replay.");
                }
                var retention = TimeSpan.FromDays(project.Configuration.Worker.RecoveryRetentionDays);
                foreach (var entry in entries.Where(item => item.Project == project.Configuration.Project.Name &&
                             item.Repository == project.Configuration.Project.Repository &&
                             (item.RecoveryState is "recoverable" or "integration-conflict" or "cleanup-pending" or "missing") &&
                             item.State != "IntegrationConflict" && item.IntegrationRecoveryClaim is null))
                {
                    var expired = RecoveryRetentionPolicy.IsExpired(entry, retention, DateTimeOffset.UtcNow);
                    var cleanupPending = entry.RecoveryState == "cleanup-pending";
                    if (!project.Git.RecoveryWorkspaceExists(entry) && !expired && !cleanupPending)
                    {
                        if (entry.RecoveryState != "missing")
                        {
                            await history.UpdateRecoveryAsync(entry.ExecutionId, "missing", ct);
                            runtime.Events.Publish("recovery.missing", $"Recovery workspace for execution {entry.ExecutionId} is missing.", entry.Project);
                        }
                        continue;
                    }
                    if (!expired && !cleanupPending)
                    {
                        try
                        {
                            await project.Git.ValidateRecoveryWorkspaceAsync(project.Git.RecoveryWorkspacePath(entry), entry, ct);
                        }
                        catch (WorkerInfrastructureException ex)
                        {
                            runtime.Events.Publish("recovery.reconciliation.skipped", $"Recovery state for execution {entry.ExecutionId} could not be verified: {ex.Message}", entry.Project);
                            _output.Warning($"Recovery state retained for execution {entry.ExecutionId}: {ex.Message}");
                        }
                        continue;
                    }

                    runtime.Events.Publish(expired ? "recovery.expired" : "recovery.cleanup.resuming",
                        expired ? $"Recovery workspace for execution {entry.ExecutionId} expired and is eligible for cleanup." :
                        $"Resuming interrupted recovery cleanup for execution {entry.ExecutionId}.", entry.Project);
                    await history.UpdateRecoveryAsync(entry.ExecutionId, "cleanup-pending", ct);
                    try
                    {
                        await project.Git.CleanupRecoveryWorkspaceAsync(entry with { RecoveryState = "cleanup-pending" }, ct);
                        await history.UpdateRecoveryAsync(entry.ExecutionId, "expired-cleaned", ct);
                        runtime.Events.Publish("recovery.cleanup.completed", $"Cleaned expired recovery resources for execution {entry.ExecutionId}.", entry.Project);
                    }
                    catch (WorkerInfrastructureException ex)
                    {
                        runtime.Events.Publish("recovery.cleanup.skipped", $"Cleanup skipped for execution {entry.ExecutionId}: {ex.Message}", entry.Project);
                        _output.Warning($"Recovery cleanup skipped for execution {entry.ExecutionId}: {ex.Message}");
                    }
                }
                runtime.Events.Publish("recovery.reconciled", "Recovery metadata reconciliation completed.", project.Configuration.Project.Name);
            }
            finally { project.RepositoryGate.Release(); }
        }
    }

    private ProjectRuntime CreateRuntime(string path, WorkerConfiguration config, TelegramNotifier telegram,
        ExecutionHistoryStore history, System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> repositoryGates, CancellationToken shutdownToken)
    {
        var github = new GitHubClient(_runner, config.Project.Repository, config.Worker.GitHubTimeoutSeconds);
        var git = new GitRepository(_runner, config.Project.Directory, config.Project.Repository, config.Git, config.Worker);
        var codex = new CodexExecutor(_runner, config.Codex, config.Environment.Variables);
        var validation = new ValidationRunner(_runner, config.Validation.TimeoutSeconds, config.Environment.Variables);
        var repositoryGate = repositoryGates.GetOrAdd(config.Project.Repository, _ => new SemaphoreSlim(1, 1));
        return new ProjectRuntime(path, config, git,
            new Worker(config, github, git, codex, validation, telegram, _output, history, repositoryGate, _global.Server, _operationalLog, shutdownToken), codex, github, repositoryGate);
    }

    private void PauseProjectForGitHubFailure(WorkerRuntimeReadModel runtime, string projectName,
        GitHubOperationException failure, string? additionalContext = null)
    {
        var issue = failure.IssueNumber is { } issueNumber ? $"Issue #{issueNumber}" : "queue eligibility";
        var correlation = failure.ExecutionId is { } executionId ? $" · execution {executionId}" : "";
        var remoteState = failure.RemoteState switch
        {
            GitHubRemoteState.Uncertain => "uncertain; manual reconciliation is required",
            GitHubRemoteState.NotChanged => "known unchanged",
            _ => "read-only state unavailable"
        };
        var reconciliationRequired = failure.IsMutation && failure.RemoteStateUncertain;
        var heading = reconciliationRequired ? "GitHub reconciliation required" : "GitHub operation failed";
        var reason = $"{heading}: {issue}{correlation} operation '{failure.Operation}' failed ({failure.FailureKind}); remote state is {remoteState}. {failure.Message}";
        var projectConfiguration = runtime.Registry.Snapshot()
            .FirstOrDefault(project => string.Equals(project.Configuration.Project.Name, projectName, StringComparison.OrdinalIgnoreCase))
            .Configuration;
        if (projectConfiguration is not null)
            reason = FailureDiagnosticRedactor.Redact(reason, projectConfiguration.Environment.Variables.Values.ToArray());
        if (reason.Length > 1600) reason = reason[..1580] + " … [truncated]";
        if (!string.IsNullOrWhiteSpace(additionalContext)) reason += $" {additionalContext}";
        if (reason.Length > 1800) reason = reason[..1780] + " … [truncated]";
        runtime.Registry.MarkUnavailable(projectName, reason);
        runtime.Events.Publish(reconciliationRequired ? "project.github-reconciliation-required" : "project.github-failure",
            reason, projectName);
        _output.Warning($"Project '{projectName}' paused after a GitHub failure; unrelated projects will continue. {reason}");
        var pauseReason = reconciliationRequired ? "uncertain mutation requires manual reconciliation" : "the GitHub operation must succeed before this project can schedule again";
        _operationalLog($"Scheduler · {projectName} · {issue}{correlation} · GitHub {failure.FailureKind} failure during {failure.Operation} · remote state {remoteState} · {pauseReason} · scheduling paused for this project.");
    }

    private async Task ReportProvisioningStateAsync(WorkerRegistrationClient registration, string planId,
        ProvisioningWorkerReportContract report, CancellationToken token)
    {
        if (report.State is "Completed" or "Failed")
            await _registration.InventoryDiscovery.GetAsync(refresh: true, cancellationToken: token);
        await registration.ReportProvisioningPlanAsync(_global.Server, planId, report, token);
    }

    private ProvisioningPlanExecutor CreateProvisioningExecutor(WorkerRegistrationClient registration,
        IReadOnlyList<ProjectRuntime> runtimes,
        Func<IReadOnlyList<WorkerCapabilityContract>, CancellationToken, Task> capabilitiesChanged) => new(_registration.CapabilityDiscovery,
        policy: _global.Worker.Provisioning,
        authenticationExecutor: new GitHubAuthenticationProvisioner(_runner,
            repository => runtimes.FirstOrDefault(project => project.Configuration.Project.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))?.GitHub,
            repository => runtimes.FirstOrDefault(project => project.Configuration.Project.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))?.Git,
            (credentialId, token) => registration.RetrieveCredentialAsync(_global.Server, credentialId, token)),
        capabilitiesChanged: async (capabilities, token) =>
        {
            await _registration.InventoryDiscovery.GetAsync(refresh: true, cancellationToken: token);
            await capabilitiesChanged(capabilities, token);
        });

    private sealed record ProjectRuntime(string Path, WorkerConfiguration Configuration, GitRepository Git, Worker Worker,
        CodexExecutor Codex, GitHubClient GitHub, SemaphoreSlim RepositoryGate);
}
