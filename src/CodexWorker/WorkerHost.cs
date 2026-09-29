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
        try
        {
            if (_projects.Count == 0) throw new InvalidDataException("At least one project must be configured.");
            await new WorkerRegistrationClient().RegisterAsync(_global.Server, _global.Worker.MaxParallelTasks, ct);
            history = new ExecutionHistoryStore();
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
            if (configurationWatcher is not null) await configurationWatcher.DisposeAsync();
            if (managementApi is not null) await managementApi.DisposeAsync();
            await _output.StopWaitingAsync();
            foreach (var project in allRuntimes) project.Git.Dispose();
            foreach (var gate in repositoryGates.Values) gate.Dispose();
            history?.Dispose();
            executionCancellation?.Dispose();
        }
    }

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
            new Worker(config, github, git, codex, validation, telegram, _output, history, repositoryGate), codex, github, repositoryGate);
    }

    private sealed record ProjectRuntime(string Path, WorkerConfiguration Configuration, GitRepository Git, Worker Worker,
        CodexExecutor Codex, GitHubClient GitHub, SemaphoreSlim RepositoryGate);
}
