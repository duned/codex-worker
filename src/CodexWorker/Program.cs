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
            Console.WriteLine("Usage: codex-worker <worker.yml> | register --server <url> (--token <registration-token> | --token-stdin) [--capacity 1..8] [--identity-file <path>]");
            return args.Length == 1 ? ProcessExitCodes.Success : ProcessExitCodes.StartupFailure;
        }

        GlobalWorkerConfiguration? global = null;
        IReadOnlyList<(string Path, WorkerConfiguration Configuration)> projects = [];
        try
        {
            global = GlobalWorkerConfiguration.Load(args[0]);
            TelegramNotifier.ValidateConfiguration(global.Telegram.Enabled,
                Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"), Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
            projects = ProjectConfigurationDiscovery.LoadForWorker(global);
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
            var capacity = 1;
            var readTokenFromStandardInput = false;
            for (var index = 1; index < args.Length;)
            {
                switch (args[index])
                {
                    case "--token-stdin":
                        if (readTokenFromStandardInput) throw new ArgumentException("Specify only one bootstrap token source.");
                        readTokenFromStandardInput = true;
                        index++;
                        continue;
                    case "--server":
                    case "--token":
                    case "--identity-file":
                    case "--capacity":
                        if (index + 1 >= args.Length) throw new ArgumentException("Register requires a value for every option.");
                        if (args[index] == "--server") server = args[index + 1];
                        else if (args[index] == "--token") token = args[index + 1];
                        else if (args[index] == "--identity-file") identityFile = args[index + 1];
                        else if (!int.TryParse(args[index + 1], out capacity) || capacity is < 1 or > 8)
                            throw new ArgumentException("Register capacity must be an integer from 1 to 8.");
                        index += 2;
                        continue;
                    default: throw new ArgumentException($"Unknown register option '{args[index]}'.");
                }
            }
            if (readTokenFromStandardInput && token is not null) throw new ArgumentException("Specify only one bootstrap token source.");
            if (readTokenFromStandardInput) token = await Console.In.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(token) || !Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Usage: codex-worker register --server <url> (--token <registration-token> | --token-stdin) [--capacity 1..8] [--identity-file <path>]");
            var settings = new WorkerServerSettings { Enabled = true, Url = uri.ToString().TrimEnd('/'), IdentityFile = identityFile };
            settings.Validate();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new WorkerRegistrationClient().BootstrapAsync(settings, capacity, token, shutdown.Token);
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
