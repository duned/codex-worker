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
        var runtimes = new List<ProjectRuntime>();
        string? activeProject = null;
        var safeToStop = true;
        var operational = false;
        var active = new Dictionary<Task<IssueProcessingResult?>, ProjectRuntime>();
        CancellationTokenSource? executionCancellation = null;
        try
        {
            if (_projects.Count == 0) throw new InvalidDataException("At least one project must be configured.");
            history = new ExecutionHistoryStore();
            var repositoryGates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, config) in _projects)
            {
                var github = new GitHubClient(_runner, config.Project.Repository, config.Worker.GitHubTimeoutSeconds);
                var git = new GitRepository(_runner, config.Project.Directory, config.Project.Repository, config.Git, config.Worker);
                var codex = new CodexExecutor(_runner, config.Codex, config.Environment.Variables);
                var validation = new ValidationRunner(_runner, config.Validation.TimeoutSeconds, config.Environment.Variables);
                if (!repositoryGates.TryGetValue(config.Project.Repository, out var repositoryGate))
                    repositoryGates.Add(config.Project.Repository, repositoryGate = new SemaphoreSlim(1, 1));
                runtimes.Add(new ProjectRuntime(path, config, git,
                    new Worker(config, github, git, codex, validation, telegram, _output, history, repositoryGate), codex, github, repositoryGate));
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
            operational = true;

            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var executionToken = executionCancellation.Token;
            var scheduler = new ProjectScheduler(runtimes.Count);
            while (!ct.IsCancellationRequested)
            {
                foreach (var completed in active.Keys.Where(task => task.IsCompleted).ToArray())
                {
                    var project = active[completed];
                    activeProject = project.Configuration.Project.Name;
                    await completed;
                    active.Remove(completed);
                }

                var foundWork = false;
                while (!ct.IsCancellationRequested && active.Count < _global.Worker.MaxParallelTasks)
                {
                    var selected = false;
                    foreach (var index in scheduler.ScanOrder())
                    {
                        var project = runtimes[index];
                        var projectActive = active.Values.Count(activeProject => ReferenceEquals(activeProject, project));
                        if (projectActive >= project.Configuration.Worker.MaxParallelTasks) continue;
                        activeProject = project.Configuration.Project.Name;
                        safeToStop = true; // no Issue has been claimed while queue lookup is in progress
                        var execution = await project.Worker.ClaimNextAsync(executionToken);
                        safeToStop = true;
                        if (execution is null) continue;
                        active.Add(execution, project);
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
                    await Task.WhenAny(active.Keys);
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
            await telegram.StoppedAsync(runtimes.Count, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && safeToStop && active.Count == 0)
        {
            await _output.StopWaitingAsync(finalizeLine: true);
            _output.Shutdown("Worker stopped.");
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
            _output.InfrastructureFailure($"Infrastructure failure: {infrastructure.Message}");
            await telegram.CriticalAsync(activeProject, infrastructure.Message, CancellationToken.None);
            if (!operational)
                throw new WorkerStartupException(infrastructure.Message, infrastructure);
            throw infrastructure;
        }
        finally
        {
            await _output.StopWaitingAsync();
            foreach (var project in runtimes) project.Git.Dispose();
            foreach (var gate in runtimes.Select(project => project.RepositoryGate).Distinct()) gate.Dispose();
            history?.Dispose();
            executionCancellation?.Dispose();
        }
    }

    private sealed record ProjectRuntime(string Path, WorkerConfiguration Configuration, GitRepository Git, Worker Worker,
        CodexExecutor Codex, GitHubClient GitHub, SemaphoreSlim RepositoryGate);
}
