namespace CodexWorker;

/// <summary>Local enrollment adapter. Installed configuration supplies defaults; explicit options override them.</summary>
public sealed class WorkerRegistrationCli(
    Func<WorkerServerSettings, int, string, CancellationToken, Task> bootstrap,
    WorkerConsole output, TextReader input, TextWriter writer,
    Func<CancellationToken, Task<string>>? interactiveSecretReader = null)
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
            var pair = false;
            var options = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Count;)
            {
                if (!options.Add(args[index])) throw new ArgumentException("Register options must be specified only once.");
                switch (args[index])
                {
                    case "--pair":
                        pair = true;
                        index++;
                        continue;
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
            if (pair && (readTokenFromStandardInput || json || operation is not ("enroll" or "associate")))
                throw new ArgumentException("--pair requires interactive enroll or associate, without --token-stdin or --json.");
            var settings = new WorkerServerSettings { Enabled = true, Url = uri.ToString().TrimEnd('/'), IdentityFile = identityFile };
            settings.Validate();
            if (!CodexProvisioning.WorkerEnrollmentProtocol.ValidOperation(operation)) throw new ArgumentException("Register operation must be enroll, rotate, recover, or associate.");
            settings.RegistrationOperation = operation;
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdown.CancelAfter(pair ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
            if (pair)
            {
                var publicRequest = new CodexProvisioning.WorkerPairingRequest(1, new string('0', 32), operation, settings.Url);
                if (!publicRequest.IsValid()) throw new ArgumentException("Pairing requires the final HTTPS Server origin, without a path.");
                var identityPath = Path.GetFullPath(identityFile ?? WorkerIdentity.DefaultPath);
                // Association must never create a replacement identity.
                var identity = operation == "associate" ? await WorkerIdentity.LoadAsync(identityPath, shutdown.Token) :
                    await WorkerIdentity.LoadOrCreateAsync(identityPath, shutdown.Token);
                var pending = WorkerPendingRegistration.Load(identityPath);
                if (pending is not null && (pending.WorkerId != identity || pending.Endpoint != settings.Url || pending.Operation != operation))
                    throw new WorkerStartupException("A different registration is pending. Resume its original Server and operation; preserve local state.");
                writer.WriteLine($"Local authorization: {operation} Worker at {settings.Url}.");
                writer.WriteLine("Copy this public pairing request into Workers > Add Worker:");
                writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(publicRequest with { WorkerId = identity }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                writer.WriteLine("Paste the one-use authorization below (hidden). Press Enter without a value to reconcile retained credentials. Do not paste the Server management token.");
                writer.Flush();
                token = interactiveSecretReader is null ?
                    await input.ReadLineAsync(shutdown.Token).AsTask().WaitAsync(shutdown.Token) :
                    await interactiveSecretReader(shutdown.Token).WaitAsync(shutdown.Token);
                token ??= string.Empty;
                writer.WriteLine();
                // Human pairing time is separate from the bounded registration exchange.
                shutdown.CancelAfter(TimeSpan.FromSeconds(30));
            }
            else
            {
                // Bound stdin even when the stream ignores cancellation.
                if (readTokenFromStandardInput)
                    token = await input.ReadLineAsync(shutdown.Token).AsTask().WaitAsync(shutdown.Token);
                if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Register requires a nonempty token from --token-stdin.");
            }
            await bootstrap(settings, capacity, token ?? string.Empty, shutdown.Token);
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
                    "Worker registration deadline expired; retain local state and resume the same Server and operation.");
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
