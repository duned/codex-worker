namespace CodexWorker;

internal static class StandaloneConflictResolutionCli
{
    internal static async Task<int> RunAsync(WorkerCommandLine command, WorkerConsole output, TextWriter destination,
        string? databasePath, CancellationToken ct)
    {
        var args = command.Arguments;
        if (args.Count is < 2 or > 3 || !Guid.TryParse(args[1], out var id) || id == Guid.Empty ||
            args.Count == 3 && args[2] != "--confirm") throw new ArgumentException(ExecutionAdministrationCli.Help);
        var global = GlobalWorkerConfiguration.Load(command.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
        if (global.Server.Enabled || global.Projects.Ownership == "managed") throw new InvalidOperationException("Standalone recovery only; managed ownership requires Server reconciliation.");
        using var history = new ExecutionHistoryStore(databasePath);
        var source = await history.ReadExecutionAsync(id, ct) ?? throw new ArgumentException("Exact execution ID was not found.");
        var config = ProjectConfigurationDiscovery.LoadForWorker(global).Select(project => project.Configuration)
            .SingleOrDefault(config => config.Project.Name == source.Project && config.Project.Repository == source.Repository)
            ?? throw new InvalidOperationException("Execution project/repository is not configured.");
        var runner = new ProcessRunner();
        using var git = new GitRepository(runner, config.Project.Directory, config.Project.Repository, config.Git, config.Worker);
        await git.AcquireWorkerLockAsync(ct);
        await git.ValidateStartupReadOnlyAsync(ct);
        var codex = new CodexExecutor(runner, config.Codex, config.Environment.Variables);
        // Preserve global execution readiness before querying Issue state; never retry a service failure.
        await new CodexExecutor(runner, new CodexSettings { Model = null }).PreflightAsync(ct, global.Worker.PreflightTimeoutSeconds);
        using var telegram = new TelegramNotifier(global.Telegram.Enabled, output);
        var worker = new Worker(config, new GitHubClient(runner, config.Project.Repository, config.Worker.GitHubTimeoutSeconds),
            git, codex, new ValidationRunner(runner, config.Validation.TimeoutSeconds, config.Environment.Variables),
            telegram, output, history, shutdownToken: ct);
        destination.WriteLine($"Selected execution {id} · Issue #{source.IssueNumber} · {source.Repository} · {source.FeatureBranch} · preserved workspace {git.RecoveryWorkspacePath(source)}");
        var result = await worker.ResolveStandaloneConflictAsync(id, args.Contains("--confirm"), ct);
        destination.WriteLine(result is null ? $"Execution {id}: verified. Inspection only; repeat with --confirm to authorize conflict resolution." : result.Summary);
        return result is null || result.Kind == IssueOutcomeKind.Succeeded ? ProcessExitCodes.Success : ProcessExitCodes.StartupFailure;
    }
}
