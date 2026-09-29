namespace CodexWorker;

/// <summary>Global host with bounded execution concurrency across independently configured projects.</summary>
public sealed class WorkerHost
{
    private readonly GlobalWorkerConfiguration _global;
    private readonly IReadOnlyList<(string Path, WorkerConfiguration Configuration)> _projects;
    private readonly WorkerConsole _output;
    private readonly ProcessRunner _runner = new();

    public WorkerHost(GlobalWorkerConfiguration global,
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects, WorkerConsole? output = null)
    {
        _global = global;
        _projects = projects;
        _output = output ?? new WorkerConsole();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _output.Startup(_projects.Count);
        foreach (var item in _projects) _output.ProjectLoaded(item.Configuration.Project.Name);
        using var telegram = new TelegramNotifier(_global.Telegram.Enabled, _output);
        ExecutionHistoryStore? history = null;
        WorkerRuntimeReadModel? runtimeReadModel = null;
        Microsoft.AspNetCore.Builder.WebApplication? managementApi = null;
        ProjectConfigurationWatcher? configurationWatcher = null;
        var runtimes = new List<ProjectRuntime>();
        var allRuntimes = new List<ProjectRuntime>();
        var repositoryGates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        string? activeProject = null;
        var safeToStop = true;
        var operational = false;
        var active = new Dictionary<Task<IssueProcessingResult?>, ProjectRuntime>();
        CancellationTokenSource? executionCancellation = null;
        WorkerHeartbeatStatus heartbeatStatus = new(0, Array.Empty<string>(), "starting");
        WorkerHeartbeatLoop? heartbeat = null;
        try
        {
            if (_projects.Count == 0) throw new InvalidDataException("At least one project must be configured.");
            await new WorkerRegistrationClient().RegisterAsync(_global.Server, _global.Worker.MaxParallelTasks, ct);
            if (_global.Server.Enabled)
            {
                heartbeat = new WorkerHeartbeatLoop(_global.Server, _global.Worker.MaxParallelTasks, () => Volatile.Read(ref heartbeatStatus),
                    message => _output.Warning(message));
                heartbeat.Start();
            }
            history = new ExecutionHistoryStore();
            if (_global.Server.Enabled)
            {
                foreach (var entry in await history.ReadAllAsync(ct))
                {
                    if (entry.ServerExecutionId is null || entry.AssignmentId is null ||
                        entry.State is not ("Completed" or "Blocked" or "Failed" or "InfrastructureFailure" or "Cancelled")) continue;
                    var serverState = entry.State == "Completed" ? "Completed" : "Failed";
                    try { await new WorkerRegistrationClient().ReportExecutionAsync(_global.Server, entry, serverState, null, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    { _output.Warning($"Codex Server result reporting remains pending for execution {entry.ExecutionId}: {ex.Message}"); }
                }
            }
            var configurationProvider = new LocalYamlProjectConfigurationProvider(_global.Projects.Directory);
            runtimeReadModel = new WorkerRuntimeReadModel(_global, _projects, history);
            var configurationService = new ProjectConfigurationService(configurationProvider, history, _global.Projects.Directory, runtimeReadModel.Registry);
            runtimeReadModel.Events.Publish("worker.starting", "Worker startup began.");
            managementApi = await ManagementApi.StartAsync(runtimeReadModel, _global.Api, ct, configurationService);
            foreach (var (path, config) in _projects)
            {
                var project = CreateRuntime(path, config, telegram, history, repositoryGates);
                runtimes.Add(project);
                allRuntimes.Add(project);
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
            await ReconcileRecoveryAsync(runtimes, history, runtimeReadModel, ct);
            configurationWatcher = new ProjectConfigurationWatcher(_global.Projects.Directory, configurationService, runtimeReadModel.Registry);
            _output.GitHubCliReady();
            _output.GitHubAuthenticationReady();
            _output.GitHubLabelsReady(runtimes.Count, createdLabels);
            _output.GitHubDependenciesReady();

            _output.GlobalPreflight();
            activeProject = null;
            var globalPreflight = new CodexExecutor(_runner, new CodexSettings { Model = null });
            await _output.RunProgressAsync("Codex preflight", async () =>
            {
                await globalPreflight.PreflightAsync(ct, _global.Worker.PreflightTimeoutSeconds);
                return true;
            }, ct: ct);
            await telegram.StartedAsync(runtimes.Count, ct);
            _output.Started();
            runtimeReadModel.State = "running";
            Volatile.Write(ref heartbeatStatus, heartbeatStatus with { State = "running" });
            runtimeReadModel.Events.Publish("worker.started", "Worker is ready.");
            operational = true;

            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var executionToken = executionCancellation.Token;
            var scheduler = new ProjectScheduler(runtimes.Count);
            while (!ct.IsCancellationRequested)
            {
                var runtimeVersion = runtimeReadModel.Registry.Version;
                foreach (var completed in active.Keys.Where(task => task.IsCompleted).ToArray())
                {
                    var project = active[completed];
                    activeProject = project.Configuration.Project.Name;
                    await completed;
                    active.Remove(completed);
                    runtimeReadModel.Registry.Release(project.Configuration.Project.Name);
                    runtimeReadModel.Events.Publish("execution.finished", "Execution finished.", project.Configuration.Project.Name);
                    Volatile.Write(ref heartbeatStatus, new WorkerHeartbeatStatus(active.Count, active.Values.Select(value => value.Configuration.Project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "running"));
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
                    scheduler.Reconfigure(runtimes.Count);
                }

                var foundWork = false;
                while (!ct.IsCancellationRequested && active.Count < _global.Worker.MaxParallelTasks)
                {
                    if (_global.Projects.Ownership == "managed")
                    {
                        var lifecycle = runtimeReadModel.Registry.Status().ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
                        var projectCapacities = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var candidate in runtimes)
                        {
                            var name = candidate.Configuration.Project.Name;
                            var activeForProject = active.Values.Count(value => string.Equals(value.Configuration.Project.Name, name, StringComparison.OrdinalIgnoreCase));
                            if (lifecycle.TryGetValue(name, out var state) && state.State == ProjectLifecycleState.Enabled &&
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
                        try { assignedExecution = await assignedProject.Worker.ClaimAssignedAsync(assignment, executionToken); }
                        catch { runtimeReadModel.Registry.Release(assignedProject.Configuration.Project.Name); throw; }
                        if (assignedExecution is null)
                        {
                            runtimeReadModel.Registry.Release(assignedProject.Configuration.Project.Name);
                            throw new WorkerInfrastructureException($"Server assignment {assignment.AssignmentId} did not create an execution.");
                        }
                        active.Add(assignedExecution, assignedProject);
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
            executionCancellation?.Cancel();
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
            new Worker(config, github, git, codex, validation, telegram, _output, history, repositoryGate, _global.Server), codex, github, repositoryGate);
    }

    private sealed record ProjectRuntime(string Path, WorkerConfiguration Configuration, GitRepository Git, Worker Worker,
        CodexExecutor Codex, GitHubClient GitHub, SemaphoreSlim RepositoryGate);
}
