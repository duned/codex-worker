namespace CodexWorker;

/// <summary>Global host with bounded execution concurrency across independently configured projects.</summary>
public sealed class WorkerHost
{
    private readonly GlobalWorkerConfiguration _global;
    private readonly IReadOnlyList<(string Path, WorkerConfiguration Configuration)> _projects;
    private readonly WorkerConsole _output;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _operationalLog;
    private readonly ProcessRunner _runner = new();

    public WorkerHost(GlobalWorkerConfiguration global,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects, WorkerConsole? output = null,
        TimeProvider? timeProvider = null, Action<string>? operationalLog = null)
    {
        _global = global;
        _projects = projects;
        _output = output ?? new WorkerConsole();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _operationalLog = operationalLog ?? (_ => { });
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
        var repositoryGates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        var discoveredCapabilities = (IReadOnlyList<WorkerCapabilityContract>)Array.Empty<WorkerCapabilityContract>();
        var validatedConfigurations = new HashSet<WorkerConfiguration>(ReferenceEqualityComparer.Instance);
        string? activeProject = null;
        var safeToStop = true;
        var operational = false;
        var nextManagedConfigurationSync = DateTimeOffset.MinValue;
        var active = new Dictionary<Task<IssueProcessingResult?>, ProjectRuntime>();
        var leaseRenewals = new Dictionary<Task<IssueProcessingResult?>, (CancellationTokenSource Stop, Task Run)>();
        CancellationTokenSource? executionCancellation = null;
        WorkerHeartbeatStatus heartbeatStatus = new(0, Array.Empty<string>(), "starting");
        IReadOnlyList<WorkerCapabilityContract> heartbeatCapabilities = [];
        WorkerHeartbeatLoop? heartbeat = null;
        async Task ReportProvisionedCapabilitiesAsync(IReadOnlyList<WorkerCapabilityContract> capabilities, CancellationToken token)
        {
            Volatile.Write(ref heartbeatCapabilities, capabilities);
            if (_global.Server.Enabled)
            {
                var status = Volatile.Read(ref heartbeatStatus);
                await new WorkerRegistrationClient().HeartbeatAsync(_global.Server, _global.Worker.MaxParallelTasks,
                    status.ActiveExecutions, status.Projects, status.State, token, capabilities, managedConfiguration?.Status);
            }
        }
        try
        {
            if (configuredProjects.Count == 0) throw new InvalidDataException("At least one project must be configured.");
            discoveredCapabilities = await WorkerCapabilityDiscovery.Shared.GetCachedAsync(ct);
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
                var registration = new WorkerRegistrationClient();
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
                heartbeat = new WorkerHeartbeatLoop(_global.Server, _global.Worker.MaxParallelTasks, () => Volatile.Read(ref heartbeatStatus),
                    message => _output.Warning(message), () => Volatile.Read(ref heartbeatCapabilities),
                    () => managedConfiguration?.Status);
                heartbeat.Start();
            }
            history = new ExecutionHistoryStore();
            if (_global.Server.Enabled)
            {
                foreach (var entry in await history.ReadAllAsync(ct))
                {
                    if (entry.ServerExecutionId is null || entry.AssignmentId is null || entry.OwnershipGeneration is null ||
                        entry.State is not ("Completed" or "Blocked" or "Failed" or "InfrastructureFailure" or "Cancelled")) continue;
                    var serverState = entry.State == "Completed" ? "Completed" : "Failed";
                    try { await new WorkerRegistrationClient().ReportExecutionAsync(_global.Server, entry, serverState, null, entry.OwnershipGeneration.Value, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    { _output.Warning($"Codex Server result reporting remains pending for execution {entry.ExecutionId}: {ex.Message}"); }
                }
            }
            var configurationProvider = new LocalYamlProjectConfigurationProvider(_global.Projects.Directory);
            var lifecycle = new WorkerLifecycle();
            runtimeReadModel = new WorkerRuntimeReadModel(_global, configuredProjects, history, managedConfiguration: managedConfiguration,
                lifecycle: lifecycle);
            var configurationService = _global.Projects.Ownership == "managed"
                ? null
                : new ProjectConfigurationService(configurationProvider, history, _global.Projects.Directory, runtimeReadModel.Registry);
            runtimeReadModel.Events.Publish("worker.starting", "Worker startup began.");
            managementApi = await ManagementApi.StartAsync(runtimeReadModel, _global.Api, ct, configurationService);
            foreach (var (path, config) in configuredProjects)
            {
                var project = CreateRuntime(path, config, telegram, history, repositoryGates);
                runtimes.Add(project);
                allRuntimes.Add(project);
            }

            if (_global.Server.Enabled)
            {
                var registration = new WorkerRegistrationClient();
                var initialPlan = await registration.RequestProvisioningPlanAsync(_global.Server, ct);
                if (initialPlan is not null)
                {
                    runtimeReadModel.Events.Publish("provisioning.started", $"Provisioning plan {initialPlan.Id} started.");
                    var result = await CreateProvisioningExecutor(registration, runtimes, ReportProvisionedCapabilitiesAsync).ExecuteAsync(initialPlan,
                        initialPlan.WorkerId,
                        (report, token) => registration.ReportProvisioningPlanAsync(_global.Server, initialPlan.Id, report, token), ct);
                    runtimeReadModel.Events.Publish("provisioning.finished", $"Provisioning plan {initialPlan.Id} finished.");
                    if (result.State != "Completed")
                        throw new WorkerInfrastructureException($"Provisioning plan {initialPlan.Id} failed at action '{result.CurrentActionId ?? "unknown"}'; the Worker will stop before claiming execution work.");
                }
            }

            var startupPlans = runtimes.Select(project => new ProjectStartupPlan(
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
            var createdLabels = await StartupCoordinator.RunAsync(startupPlans, ct);
            validatedConfigurations.UnionWith(runtimes.Select(project => project.Configuration));
            Volatile.Write(ref heartbeatCapabilities, heartbeatCapabilities.Concat(runtimes.SelectMany(project =>
                WorkerAuthenticationCapabilities.ForRepository(project.Configuration.Project.Repository)))
                .Distinct().ToArray());
            if (_global.Server.Enabled)
                await new WorkerRegistrationClient().HeartbeatAsync(_global.Server, _global.Worker.MaxParallelTasks, 0,
                    Array.Empty<string>(), "starting", ct, heartbeatCapabilities, managedConfiguration?.Status);
            await ReconcileRecoveryAsync(runtimes, history, runtimeReadModel, ct);
            if (configurationService is not null)
                configurationWatcher = new ProjectConfigurationWatcher(_global.Projects.Directory, configurationService, runtimeReadModel.Registry);
            _output.GitHubCliReady();
            _output.GitHubAuthenticationReady();
            _output.GitRepositoryAuthenticationReady(runtimes.Count);
            _output.GitHubLabelsReady(runtimes.Count, createdLabels);
            _output.GitHubDependenciesReady();

            _output.GlobalPreflight();
            activeProject = null;
            IAgentAuthenticationProvider agentAuthentication = new CodexAgentAuthenticationProvider(
                new CodexExecutor(_runner, new CodexSettings { Model = null }), _global.Worker.PreflightTimeoutSeconds);
            await _output.RunProgressAsync("Codex preflight", async () =>
            {
                await agentAuthentication.ValidateAsync(ct);
                return true;
            }, ct: ct);
            heartbeatCapabilities = heartbeatCapabilities
                .Append(WorkerAgentCapabilities.AuthenticatedProvider(agentAuthentication.Provider)).Distinct().ToArray();
            await ReportProvisionedCapabilitiesAsync(heartbeatCapabilities, ct);
            lifecycle.BeginReconnect();
            var synchronization = managedConfiguration?.Status;
            var configurationCompatible = synchronization?.SynchronizationStatus != "error";
            if (!lifecycle.CompleteReadiness(discoveredCapabilities, heartbeatCapabilities, configurationCompatible,
                    synchronization?.Error ?? "Server-managed configuration is incompatible."))
                throw new WorkerInfrastructureException($"Worker readiness failed: {lifecycle.Snapshot.ReconnectReadinessResult}");
            await telegram.StartedAsync(runtimes.Count, ct);
            _output.Started();
            runtimeReadModel.State = "running";
            Volatile.Write(ref heartbeatStatus, heartbeatStatus with { State = "running" });
            runtimeReadModel.Events.Publish("worker.started", "Worker is ready.");
            operational = true;

            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var executionToken = executionCancellation.Token;
            var scheduler = new ProjectScheduler(runtimes.Count);
            var capacityLog = new SchedulerCapacityLog(_global.Worker.MaxParallelTasks, _operationalLog);
            void ReportCapacity() => capacityLog.Report(active.Count, runtimes.Select(project =>
                (project.Configuration.Project.Name,
                    active.Values.Count(value => string.Equals(value.Configuration.Project.Name,
                        project.Configuration.Project.Name, StringComparison.OrdinalIgnoreCase)),
                    project.Configuration.Worker.MaxParallelTasks)));
            ReportCapacity();
            var idleHeartbeat = new IdleWorkerHeartbeat(_timeProvider.GetUtcNow());
            while (!ct.IsCancellationRequested)
            {
                var runtimeVersion = runtimeReadModel.Registry.Version;
                if (runtimeReadModel.Registry.WorkerDraining)
                {
                    lifecycle.RequestDrain();
                    lifecycle.SetActiveExecutions(active.Count);
                    var drainState = active.Count == 0 ? "drained" : "draining";
                    Volatile.Write(ref heartbeatStatus, heartbeatStatus with { State = drainState });
                }
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
                    await completed;
                    active.Remove(completed);
                    runtimeReadModel.Registry.Release(project.Configuration.Project.Name);
                    ReportCapacity();
                    runtimeReadModel.Events.Publish("execution.finished", "Execution finished.", project.Configuration.Project.Name);
                    Volatile.Write(ref heartbeatStatus, new WorkerHeartbeatStatus(active.Count, active.Values.Select(value => value.Configuration.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "running"));
                }

                if (_global.Server.Enabled && _global.Projects.Ownership == "managed" && active.Count == 0 &&
                    _timeProvider.GetUtcNow() >= nextManagedConfigurationSync)
                {
                    nextManagedConfigurationSync = _timeProvider.GetUtcNow().AddSeconds(_global.Server.HeartbeatIntervalSeconds);
                    try
                    {
                        var registration = new WorkerRegistrationClient();
                        var desired = await registration.GetManagedConfigurationAsync(_global.Server, executionToken);
                        var previousVersion = managedConfiguration!.Status.AppliedVersion;
                        var replacement = managedConfiguration.Apply(desired, _projects);
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
                            var project = CreateRuntime(path, configuration, telegram, history, repositoryGates);
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
                    scheduler.Reconfigure(runtimes.Count);
                    capacityLog.Reconfigure(_global.Worker.MaxParallelTasks);
                    ReportCapacity();
                }

                var foundWork = false;
                if (_global.Projects.Ownership == "managed" && active.Count == 0 && !runtimeReadModel.Registry.WorkerDraining)
                {
                    safeToStop = false;
                    var registration = new WorkerRegistrationClient();
                    var provisioningPlan = await registration.RequestProvisioningPlanAsync(_global.Server, executionToken);
                    if (provisioningPlan is not null)
                    {
                        runtimeReadModel.Events.Publish("provisioning.started", $"Provisioning plan {provisioningPlan.Id} started.");
                        var provisioningResult = await CreateProvisioningExecutor(registration, runtimes, ReportProvisionedCapabilitiesAsync).ExecuteAsync(provisioningPlan, provisioningPlan.WorkerId,
                            (report, token) => registration.ReportProvisioningPlanAsync(_global.Server, provisioningPlan.Id, report, token), executionToken);
                        runtimeReadModel.Events.Publish("provisioning.finished", $"Provisioning plan {provisioningPlan.Id} finished.");
                        if (provisioningResult.State != "Completed")
                            throw new WorkerInfrastructureException($"Provisioning plan {provisioningPlan.Id} failed at action '{provisioningResult.CurrentActionId ?? "unknown"}'; the Worker will stop before claiming execution work.");
                        safeToStop = true;
                        continue;
                    }
                    safeToStop = true;
                }
                while (!ct.IsCancellationRequested && active.Count < _global.Worker.MaxParallelTasks)
                {
                    if (_global.Projects.Ownership == "managed")
                    {
                        var projectLifecycles = runtimeReadModel.Registry.Status().ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
                        var projectCapacities = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var candidate in runtimes)
                        {
                            var name = candidate.Configuration.Project.Name;
                            var activeForProject = active.Values.Count(value => string.Equals(value.Configuration.Project.Name, name, StringComparison.OrdinalIgnoreCase));
                            if (projectLifecycles.TryGetValue(name, out var state) && state.State == ProjectLifecycleState.Enabled &&
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
                        safeToStop = false;
                        var assignmentResponse = await new WorkerRegistrationClient().RequestAssignmentAsync(_global.Server,
                            !runtimeReadModel.Registry.WorkerDraining, _global.Worker.MaxParallelTasks - active.Count,
                            projectCapacities, executionToken);
                        if (!assignmentResponse.HasWork && assignmentResponse.Assignment is null) { safeToStop = true; break; }
                        if (!assignmentResponse.HasWork || assignmentResponse.Assignment is null)
                            throw new WorkerInfrastructureException("Codex Server returned an inconsistent assignment response; remote assignment state may be uncertain.");
                        var assignment = assignmentResponse.Assignment!;
                        var assignedProject = runtimes.FirstOrDefault(candidate => MatchesServerProject(candidate.Configuration, assignment.Project));
                        if (assignedProject is null)
                        {
                            runtimeReadModel.Events.Publish("assignment.rejected", $"Assignment {assignment.AssignmentId} references a project outside the configured Worker project registry.");
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} references a project that is not safely configured on this Worker; assignment remains owned by this Worker for inspection.");
                        }
                        if (!runtimeReadModel.Registry.TryReserve(assignedProject.Configuration.Project.Name, assignedProject.Configuration))
                        {
                            runtimeReadModel.Events.Publish("assignment.rejected", $"Assignment {assignment.AssignmentId} arrived after project '{assignment.Project.Name}' began draining.", assignedProject.Configuration.Project.Name);
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} arrived while project '{assignment.Project.Name}' was draining; assignment remains owned by this Worker for inspection.");
                        }
                        activeProject = assignedProject.Configuration.Project.Name;
                        Task<IssueProcessingResult?>? assignedExecution;
                        if (assignment.Lease is not { State: "Active", Generation: > 0 } lease ||
                            lease.ExpiresAtUtc <= lease.AcquiredAtUtc || lease.RenewalIntervalSeconds is < 10 or > 3600 ||
                            lease.RenewalIntervalSeconds * 3 >= (lease.ExpiresAtUtc - lease.AcquiredAtUtc).TotalSeconds ||
                            lease.ExecutionId != assignment.ServerExecutionId || lease.WorkerId != assignment.WorkerId)
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} has no valid active ownership lease.");
                        var leaseStop = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
                        // Start guarding the assignment before any GitHub label/comment work in
                        // ClaimAssignedAsync; that work can itself outlive a short lease.
                        var leaseRenewal = RenewLeaseWhileActiveAsync(lease, leaseStop);
                        try { assignedExecution = await assignedProject.Worker.ClaimAssignedAsync(assignment, leaseStop.Token); }
                        catch
                        {
                            leaseStop.Cancel();
                            try { await leaseRenewal; } catch (OperationCanceledException) { }
                            leaseStop.Dispose();
                            runtimeReadModel.Registry.Release(assignedProject.Configuration.Project.Name);
                            throw;
                        }
                        if (assignedExecution is null)
                        {
                            leaseStop.Cancel();
                            try { await leaseRenewal; } catch (OperationCanceledException) { }
                            leaseStop.Dispose();
                            runtimeReadModel.Registry.Release(assignedProject.Configuration.Project.Name);
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} did not create an execution.");
                        }
                        active.Add(assignedExecution, assignedProject);
                        ReportCapacity();
                        leaseRenewals.Add(assignedExecution, (leaseStop, leaseRenewal));
                        Volatile.Write(ref heartbeatStatus, new WorkerHeartbeatStatus(active.Count, active.Values.Select(value => value.Configuration.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "running"));
                        runtimeReadModel.Events.Publish("execution.started", $"Server assignment {assignment.AssignmentId} started.", assignedProject.Configuration.Project.Name);
                        foundWork = true;
                        safeToStop = false;
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
                        safeToStop = true; // no Issue has been claimed while queue lookup is in progress
                        Task<IssueProcessingResult?>? execution;
                        try { execution = await project.Worker.ClaimNextAsync(executionToken); }
                        catch { runtimeReadModel.Registry.Release(project.Configuration.Project.Name); throw; }
                        safeToStop = true;
                        if (execution is null) { runtimeReadModel.Registry.Release(project.Configuration.Project.Name); continue; }
                        active.Add(execution, project);
                        ReportCapacity();
                        Volatile.Write(ref heartbeatStatus, new WorkerHeartbeatStatus(active.Count, active.Values.Select(value => value.Configuration.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "running"));
                        runtimeReadModel.Events.Publish("execution.started", "Execution claimed.", project.Configuration.Project.Name);
                        scheduler.Selected(index);
                        selected = true;
                        foundWork = true;
                        safeToStop = false;
                        break;
                    }
                    if (!selected) break;
                }

                if (ct.IsCancellationRequested) break;
                if (active.Count > 0)
                {
                    safeToStop = false;
                    using var changeWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var activeFinished = Task.WhenAny(active.Keys);
                    var runtimeChanged = runtimeReadModel.Registry.WaitForChangeAsync(runtimeVersion, changeWait.Token);
                    await Task.WhenAny(activeFinished, runtimeChanged);
                    changeWait.Cancel();
                    // Observe on the next pass so all task exceptions follow the common infrastructure path.
                    continue;
                }
                safeToStop = true;
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
            if (active.Count > 0) await Task.WhenAll(active.Keys);
            await _output.StopWaitingAsync(finalizeLine: true);
            _output.Shutdown();
            runtimeReadModel.State = "stopped";
            runtimeReadModel.Events.Publish("worker.stopped", "Worker stopped.");
            await telegram.StoppedAsync(runtimes.Count, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && safeToStop && active.Count == 0)
        {
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
        var client = new WorkerRegistrationClient();
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
        var entries = await history.ReadAllAsync(ct);
        foreach (var project in runtimes)
        {
            var retention = TimeSpan.FromDays(project.Configuration.Worker.RecoveryRetentionDays);
            foreach (var entry in entries.Where(item => item.Project == project.Configuration.Project.Name &&
                         item.Repository == project.Configuration.Project.Repository &&
                         (item.RecoveryState is "recoverable" or "cleanup-pending" or "missing")))
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
    }

    private ProjectRuntime CreateRuntime(string path, WorkerConfiguration config, TelegramNotifier telegram,
        ExecutionHistoryStore history, IDictionary<string, SemaphoreSlim> repositoryGates)
    {
        var github = new GitHubClient(_runner, config.Project.Repository, config.Worker.GitHubTimeoutSeconds);
        var git = new GitRepository(_runner, config.Project.Directory, config.Project.Repository, config.Git, config.Worker);
        var codex = new CodexExecutor(_runner, config.Codex, config.Environment.Variables);
        var validation = new ValidationRunner(_runner, config.Validation.TimeoutSeconds, config.Environment.Variables);
        if (!repositoryGates.TryGetValue(config.Project.Repository, out var repositoryGate))
            repositoryGates.Add(config.Project.Repository, repositoryGate = new SemaphoreSlim(1, 1));
        return new ProjectRuntime(path, config, git,
            new Worker(config, github, git, codex, validation, telegram, _output, history, repositoryGate, _global.Server, _operationalLog), codex, github, repositoryGate);
    }

    private ProvisioningPlanExecutor CreateProvisioningExecutor(WorkerRegistrationClient registration,
        IReadOnlyList<ProjectRuntime> runtimes,
        Func<IReadOnlyList<WorkerCapabilityContract>, CancellationToken, Task> capabilitiesChanged) => new(WorkerCapabilityDiscovery.Shared,
        policy: _global.Worker.Provisioning,
        authenticationExecutor: new GitHubAuthenticationProvisioner(_runner,
            repository => runtimes.FirstOrDefault(project => project.Configuration.Project.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))?.GitHub,
            repository => runtimes.FirstOrDefault(project => project.Configuration.Project.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))?.Git,
            (credentialId, token) => registration.RetrieveCredentialAsync(_global.Server, credentialId, token)),
        capabilitiesChanged: capabilitiesChanged);

    private sealed record ProjectRuntime(string Path, WorkerConfiguration Configuration, GitRepository Git, Worker Worker,
        CodexExecutor Codex, GitHubClient GitHub, SemaphoreSlim RepositoryGate);
}
