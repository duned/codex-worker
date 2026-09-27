namespace CodexWorker;

public sealed record ProjectStartupPlan(
    string Path,
    string Name,
    Func<CancellationToken, Task> ValidateReadOnlyAsync,
    Func<CancellationToken, Task<IReadOnlyList<RequiredGitHubLabel>>> FindMissingLabelsAsync,
    Func<RequiredGitHubLabel, CancellationToken, Task> CreateLabelAsync,
    Func<CancellationToken, Task> InitializeAsync);

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
}
