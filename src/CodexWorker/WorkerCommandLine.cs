namespace CodexWorker;

/// <summary>Parsed local command and its optional worker configuration path.</summary>
public sealed record WorkerCommandLine(string Command, string? ConfigurationPath, IReadOnlyList<string> Arguments)
{
    public const string LinuxDefaultConfigurationPath = "/etc/codex-worker/worker.yml";

    public static string DefaultConfigurationPath => OperatingSystem.IsLinux()
        ? LinuxDefaultConfigurationPath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-worker", "worker.yml");

    public static WorkerCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0) return new("run", null, []);

        var command = args[0] switch
        {
            "run" or "status" or "executions" or "diagnostics" or "config" or "capabilities" or "provision" or "credential" or "register" => args[0],
            _ when !args[0].StartsWith("-", StringComparison.Ordinal) => "run",
            _ => "run"
        };
        var start = command == "run" && args[0] != "run" ? 0 : 1;
        string? config = null;
        var remaining = new List<string>();
        for (var index = start; index < args.Count; index++)
        {
            if (args[index] == "--config")
            {
                if (config is not null) throw new ArgumentException("Specify --config only once.");
                if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException("Option --config requires a path.");
                config = args[++index];
            }
            else remaining.Add(args[index]);
        }

        return new(command, config, remaining);
    }
}
