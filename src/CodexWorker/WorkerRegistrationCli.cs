namespace CodexWorker;

/// <summary>Local enrollment adapter. Installed configuration supplies defaults; explicit options override them.</summary>
public sealed class WorkerRegistrationCli(
    Func<WorkerServerSettings, int, string, CancellationToken, Task> bootstrap,
    WorkerConsole output, TextReader input, TextWriter writer)
{
    public async Task<int> ExecuteAsync(WorkerCommandLine commandLine, CancellationToken cancellationToken = default)
    {
        string? token = null;
        var args = commandLine.Arguments;
        try
        {
            // Reject before configuration, stdin, identity publication or network access.
            if (args.Any(argument => argument == "--token" || argument.StartsWith("--token=", StringComparison.Ordinal)))
                throw new ArgumentException("--token is no longer supported because command-line values can appear in process listings and shell history. Use --token-stdin with a protected secret source; disable shell tracing.");
            string? server = null, identityFile = null;
            int? requestedCapacity = null;
            var json = false;
            var operation = "enroll";
            var readTokenFromStandardInput = false;
            var options = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Count;)
            {
                if (!options.Add(args[index])) throw new ArgumentException("Register options must be specified only once.");
                switch (args[index])
                {
                    case "--json":
                        json = true;
                        index++;
                        continue;
                    case "--token-stdin":
                        if (readTokenFromStandardInput) throw new ArgumentException("Specify only one bootstrap token source.");
                        readTokenFromStandardInput = true;
                        index++;
                        continue;
                    case "--operation":
                    case "--server":
                    case "--identity-file":
                    case "--capacity":
                        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal) ||
                            string.IsNullOrWhiteSpace(args[index + 1]))
                            throw new ArgumentException($"Register option '{args[index]}' requires a value.");
                        if (args[index] == "--operation") operation = args[index + 1];
                        else if (args[index] == "--server") server = args[index + 1];
                        else if (args[index] == "--identity-file") identityFile = args[index + 1];
                        else
                        {
                            if (!int.TryParse(args[index + 1], out var capacityValue) || capacityValue is < 1 or > 8)
                                throw new ArgumentException("Register capacity must be an integer from 1 to 8.");
                            requestedCapacity = capacityValue;
                        }
                        index += 2;
                        continue;
                    default: throw new ArgumentException("Unknown register option. Use the documented options below.");
                }
            }
            GlobalWorkerConfiguration? configuration = null;
            var path = commandLine.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath;
            if (commandLine.ConfigurationPath is not null || File.Exists(path))
                configuration = GlobalWorkerConfiguration.Load(path);
            server ??= configuration?.Server.Url;
            identityFile ??= configuration?.Server.IdentityFile;
            var capacity = requestedCapacity ?? configuration?.Worker.MaxParallelTasks ?? 1;
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Register requires a valid --server URL without credentials, query, or fragment.");
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdown.CancelAfter(TimeSpan.FromSeconds(30));
            // Standard-input streams can ignore cancellation after an OS read starts. Bound
            // the wait as well: a late input result has no enrollment or lifecycle side effects.
            if (readTokenFromStandardInput)
                token = await input.ReadLineAsync(shutdown.Token).AsTask().WaitAsync(shutdown.Token);
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Register requires a nonempty token from --token-stdin.");
            var settings = new WorkerServerSettings { Enabled = true, Url = uri.ToString().TrimEnd('/'), IdentityFile = identityFile };
            settings.Validate();
            if (!CodexProvisioning.WorkerEnrollmentProtocol.ValidOperation(operation)) throw new ArgumentException("Register operation must be enroll, rotate, recover, or associate.");
            settings.RegistrationOperation = operation;
            await bootstrap(settings, capacity, token, shutdown.Token);
            if (json)
                writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    contractVersion = 1,
                    status = "registered",
                    executionReadiness = "not-checked"
                }));
            else
                writer.WriteLine("Worker registered. Registration does not imply execution readiness. Start or restart the service to check external execution capabilities.");
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            WorkerCliOutput.Failure(commandLine, output, writer,
                cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out",
                cancellationToken.IsCancellationRequested ? "Worker registration cancelled; local identity and credentials are retained." :
                    "Worker registration exceeded its 30-second deadline; inspect Server registration before retrying.");
            return WorkerCliOutput.Cancelled;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or WorkerStartupException or HttpRequestException)
        {
            WorkerCliOutput.Failure(commandLine, output, writer, "registration-failed",
                $"Worker registration failed: {ex.Message} Use 'codex-worker register --help' for usage.", [token ?? string.Empty, token?.Trim() ?? string.Empty]);
            if (ex is ArgumentException && !args.Contains("--json", StringComparer.Ordinal)) WorkerCommandHelp.Write("register", writer);
            return ProcessExitCodes.StartupFailure;
        }
    }
}
