namespace CodexWorker;

using System.Runtime.InteropServices;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var output = new WorkerConsole();
        if (args.Length != 1 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("Usage: CodexWorker <worker.yml>");
            return args.Length == 1 ? 0 : 2;
        }

        GlobalWorkerConfiguration? global = null;
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects = [];
        try
        {
            global = GlobalWorkerConfiguration.Load(args[0]);
            TelegramNotifier.ValidateConfiguration(global.Telegram.Enabled,
                Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"), Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
            projects = ProjectConfigurationDiscovery.Load(global.Projects.Directory);
        }
        catch (Exception ex)
        {
            output.InfrastructureFailure($"Configuration error: {ex.Message}");
            if (global?.Telegram.Enabled == true &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")) &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")))
            {
                using var startupTelegram = new TelegramNotifier(true, output);
                await startupTelegram.CriticalAsync(null, ex.Message, CancellationToken.None);
            }
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        try { await new WorkerHost(global, projects, output).RunAsync(shutdown.Token); return 0; }
        catch (Exception) { return 1; }
    }
}
