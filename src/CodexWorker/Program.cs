namespace CodexWorker;

using System.Runtime.InteropServices;
using System.Diagnostics;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var output = new WorkerConsole();
        if (args.Length > 0 && args[0] == "register") return await RegisterAsync(args, output);
        if (args.Length != 1 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("Usage: codex-worker <worker.yml> | register --server <url> --token <registration-token> [--identity-file <path>]");
            return args.Length == 1 ? ProcessExitCodes.Success : ProcessExitCodes.StartupFailure;
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
            return ProcessExitCodes.StartupFailure;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        try { await new WorkerHost(global, projects, output, operationalLog: message => Trace.WriteLine(message)).RunAsync(shutdown.Token); return ProcessExitCodes.Success; }
        catch (Exception ex) { return ExitCodeFor(ex); }
    }

    private static async Task<int> RegisterAsync(string[] args, WorkerConsole output)
    {
        try
        {
            string? server = null, token = null, identityFile = null;
            for (var index = 1; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length) throw new ArgumentException("Register requires a value for every option.");
                switch (args[index])
                {
                    case "--server": server = args[index + 1]; break;
                    case "--token": token = args[index + 1]; break;
                    case "--identity-file": identityFile = args[index + 1]; break;
                    default: throw new ArgumentException($"Unknown register option '{args[index]}'.");
                }
            }
            if (string.IsNullOrWhiteSpace(token) || !Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Usage: codex-worker register --server <url> --token <registration-token> [--identity-file <path>]");
            var settings = new WorkerServerSettings { Enabled = true, Url = uri.ToString().TrimEnd('/'), IdentityFile = identityFile };
            settings.Validate();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new WorkerRegistrationClient().BootstrapAsync(settings, 1, token, shutdown.Token);
            Console.WriteLine("Worker registered. Start or restart the Codex Worker service to begin managed operation.");
            return ProcessExitCodes.Success;
        }
        catch (Exception ex)
        {
            output.InfrastructureFailure($"Worker registration failed: {ex.Message}");
            return ProcessExitCodes.StartupFailure;
        }
    }

    internal static int ExitCodeFor(Exception? failure) => failure switch
    {
        null => ProcessExitCodes.Success,
        WorkerStartupException => ProcessExitCodes.StartupFailure,
        _ => ProcessExitCodes.RuntimeFailure
    };
}
