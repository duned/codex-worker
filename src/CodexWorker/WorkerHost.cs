namespace CodexWorker;

/// <summary>Global, single-threaded host for independently configured project workers.</summary>
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
        if (projects.Count == 0) throw new InvalidDataException("At least one project must be configured.");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _output.Startup(_projects.Count);
        foreach (var item in _projects) _output.ProjectLoaded(item.Configuration.Project.Name);
        using var telegram = new TelegramNotifier(_global.Telegram.Enabled, _output);
        var runtimes = new List<ProjectRuntime>();
        string? activeProject = null;
        var safeToStop = true;
        try
        {
            foreach (var (path, config) in _projects)
            {
                var github = new GitHubClient(_runner, config.Project.Repository, config.Worker.GitHubTimeoutSeconds);
                var git = new GitRepository(_runner, config.Project.Directory, config.Project.Repository, config.Git, config.Worker);
                var codex = new CodexExecutor(_runner, config.Codex, config.Environment.Variables);
                var validation = new ValidationRunner(_runner, config.Validation.TimeoutSeconds, config.Environment.Variables);
                runtimes.Add(new ProjectRuntime(path, config, git,
                    new Worker(config, github, git, codex, validation, telegram, _output), codex, github));
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

            var scheduler = new ProjectScheduler(runtimes.Count);
            while (!ct.IsCancellationRequested)
            {
                _output.Waiting();
                var selected = await scheduler.ScanAsync(async index =>
                {
                    var project = runtimes[index];
                    activeProject = project.Configuration.Project.Name;
                    safeToStop = true; // only a read-only queue lookup is initially in flight
                    var task = project.Worker.ProcessOneAsync(ct);
                    // Once ProcessOneAsync has found an Issue, it claims immediately. From this point cancellation is conservative.
                    safeToStop = false;
                    var processed = await task;
                    safeToStop = true;
                    if (!processed) return false;
                    safeToStop = true;
                    return true;
                });
                if (selected is null && !ct.IsCancellationRequested)
                {
                    safeToStop = true;
                    try { await Task.Delay(TimeSpan.FromSeconds(_global.Worker.PollingSeconds), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
            }
            await _output.StopWaitingAsync(finalizeLine: true);
            _output.Shutdown();
            await telegram.StoppedAsync(runtimes.Count, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && safeToStop)
        {
            await _output.StopWaitingAsync(finalizeLine: true);
            _output.Shutdown("Worker stopped.");
            await telegram.StoppedAsync(runtimes.Count, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _output.StopWaitingAsync();
            var infrastructure = ex as WorkerInfrastructureException ??
                new WorkerInfrastructureException($"Worker startup or project processing failed: {ex.Message}", ex);
            _output.InfrastructureFailure($"Infrastructure failure: {infrastructure.Message}");
            await telegram.CriticalAsync(activeProject, infrastructure.Message, CancellationToken.None);
            throw infrastructure;
        }
        finally
        {
            await _output.StopWaitingAsync();
            foreach (var project in runtimes) project.Git.Dispose();
        }
    }

    private sealed record ProjectRuntime(string Path, WorkerConfiguration Configuration, GitRepository Git, Worker Worker,
        CodexExecutor Codex, GitHubClient GitHub);
}
