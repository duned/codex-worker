namespace CodexWorker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var output = new WorkerConsole();
        if (args.Length != 1 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("Usage: CodexWorker <project.yml>");
            return args.Length == 1 ? 0 : 2;
        }

        WorkerConfiguration config;
        try { config = WorkerConfiguration.Load(args[0]); }
        catch (Exception ex)
        {
            output.InfrastructureFailure($"Configuration error: {ex.Message}");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        var runner = new ProcessRunner();
        TelegramNotifier telegram;
        try { telegram = new TelegramNotifier(config.Telegram.Enabled, output); }
        catch (InvalidDataException ex)
        {
            output.InfrastructureFailure($"Configuration error: {ex.Message}");
            return 2;
        }
        using (telegram)
        {
            var github = new GitHubClient(runner, config.Project.Repository, config.Worker.GitHubTimeoutSeconds);
            using var git = new GitRepository(runner, config.Project.Directory, config.Project.Repository, config.Git, config.Worker);
            var codex = new CodexExecutor(runner, config.Codex);
            var validation = new ValidationRunner(runner, config.Validation.TimeoutSeconds);
            var worker = new Worker(config, github, git, codex, validation, telegram, output);
            try { await worker.RunAsync(shutdown.Token); return 0; }
            catch (Exception)
            {
                return 1;
            }
        }
    }
}
