namespace CodexWorker;

public sealed record ProjectStartupPlan(
    string Path,
    string Name,
    Func<CancellationToken, Task> ValidateReadOnlyAsync,
    Func<CancellationToken, Task> ValidateGitHubCapabilitiesAsync,
    Func<CancellationToken, Task<IReadOnlyList<RequiredGitHubLabel>>> FindMissingLabelsAsync,
    Func<RequiredGitHubLabel, CancellationToken, Task> CreateLabelAsync,
    Func<CancellationToken, Task> InitializeAsync);

public sealed record ProjectStartupFailure(string Path, string Name, string Reason);
public sealed record ProjectStartupResult(int CreatedLabels, IReadOnlyList<ProjectStartupFailure> UnavailableProjects);

/// <summary>Enforces the global startup barriers before Codex preflight or queue access.</summary>
public static class StartupCoordinator
{
    public static async Task<int> RunAsync(IReadOnlyList<ProjectStartupPlan> projects, CancellationToken ct)
    {
        foreach (var project in projects)
        {
            try { await project.ValidateReadOnlyAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new WorkerInfrastructureException($"Project configuration '{project.Path}' failed read-only startup validation: {ex.Message}", ex); }
        }
        foreach (var project in projects)
        {
            try { await project.ValidateGitHubCapabilitiesAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new WorkerInfrastructureException($"Project '{project.Name}' GitHub capability validation failed: {ex.Message}", ex); }
        }
        var missingLabels = new List<(ProjectStartupPlan Project, IReadOnlyList<RequiredGitHubLabel> Missing)>();
        foreach (var project in projects)
        {
            try { missingLabels.Add((project, await project.FindMissingLabelsAsync(ct))); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new WorkerInfrastructureException($"Project '{project.Name}' label discovery failed: {ex.Message}", ex); }
        }
        var created = 0;
        foreach (var (project, labels) in missingLabels)
        foreach (var label in labels)
        {
            try { await project.CreateLabelAsync(label, ct); created++; }
            catch (Exception ex) { throw new WorkerInfrastructureException($"Project '{project.Name}' could not initialize required GitHub label '{label.Name}': {ex.Message}", ex); }
        }
        foreach (var project in projects)
        {
            try { await project.InitializeAsync(ct); }
            catch (Exception ex) { throw new WorkerInfrastructureException($"Project configuration '{project.Path}' failed startup initialization: {ex.Message}", ex); }
        }
        return created;
    }

    public static async Task<ProjectStartupResult> RunIsolatedAsync(IReadOnlyList<ProjectStartupPlan> projects, CancellationToken ct)
    {
        var unavailable = new Dictionary<string, ProjectStartupFailure>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            try { await project.ValidateReadOnlyAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { MarkUnavailable(project, $"read-only startup validation failed: {ex.Message}"); }
        }

        foreach (var project in projects.Where(project => !unavailable.ContainsKey(project.Name)))
        {
            try { await project.ValidateGitHubCapabilitiesAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { MarkUnavailable(project, $"GitHub capability validation failed: {ex.Message}"); }
        }

        var missingLabels = new List<(ProjectStartupPlan Project, IReadOnlyList<RequiredGitHubLabel> Missing)>();
        foreach (var project in projects.Where(project => !unavailable.ContainsKey(project.Name)))
        {
            try { missingLabels.Add((project, await project.FindMissingLabelsAsync(ct))); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { MarkUnavailable(project, $"label discovery failed: {ex.Message}"); }
        }

        var created = 0;
        foreach (var (project, labels) in missingLabels)
        {
            foreach (var label in labels)
            {
                try { await project.CreateLabelAsync(label, ct); created++; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { MarkUnavailable(project, $"could not initialize required GitHub label '{label.Name}': {ex.Message}"); break; }
            }
        }

        foreach (var project in projects.Where(project => !unavailable.ContainsKey(project.Name)))
        {
            try { await project.InitializeAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { MarkUnavailable(project, $"startup initialization failed: {ex.Message}"); }
        }

        return new ProjectStartupResult(created, unavailable.Values.ToArray());

        void MarkUnavailable(ProjectStartupPlan project, string reason) =>
            unavailable.TryAdd(project.Name, new ProjectStartupFailure(project.Path, project.Name, reason));
    }
}
