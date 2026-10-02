namespace CodexServer;

using CodexProvisioning;
using System.Text.Json;

public interface IProvisioningCommandAdministrationService
{
    Task<IReadOnlyList<ProvisioningCommand>> ListAsync(CancellationToken cancellationToken = default, int limit = 100, int offset = 0);
    Task<ProvisioningCommand?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<ProvisioningCommand> CreateAsync(ProvisioningCommandRequest request, CancellationToken cancellationToken = default);
    Task<ProvisioningCommand?> CancelAsync(string id, CancellationToken cancellationToken = default);
    Task<ProvisioningCommand?> ReconcileAsync(string id, bool nodeQuiescent, CancellationToken cancellationToken = default);
}

public interface IProvisioningCommandAdministrationServiceFactory
{
    IProvisioningCommandAdministrationService Create(ServerConfiguration configuration);
}

public sealed class LocalProvisioningCommandAdministrationServiceFactory : IProvisioningCommandAdministrationServiceFactory
{
    public IProvisioningCommandAdministrationService Create(ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var database = configuration.ResolveDatabasePath();
        var registry = new SqliteRegistryStore(database, configuration.WorkerStaleAfterSeconds,
            leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
            leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds);
        return new LocalProvisioningCommandAdministrationService(database, registry,
            new ProvisioningCommandStore(database));
    }
}

/// <summary>Local CLI access to the Server's existing durable typed-command contracts.</summary>
public sealed class LocalProvisioningCommandAdministrationService(string databasePath, IRegistryStore registry,
    ProvisioningCommandStore commands) : IProvisioningCommandAdministrationService
{
    public async Task<IReadOnlyList<ProvisioningCommand>> ListAsync(CancellationToken cancellationToken = default,
        int limit = 100, int offset = 0)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await commands.ListAsync(cancellationToken, limit, offset);
    }

    public async Task<ProvisioningCommand?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await commands.GetAsync(id, cancellationToken);
    }

    public async Task<ProvisioningCommand> CreateAsync(ProvisioningCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        if (!ProvisioningCommandProtocol.Valid(request) || !ProvisioningCommandProtocol.Supported(request))
            throw new InvalidDataException("Unsupported or invalid provisioning action.");
        if (request.NodeId != "server")
        {
            var worker = await registry.GetWorkerAsync(request.NodeId, cancellationToken);
            if (worker is null) throw new KeyNotFoundException("Worker was not found.");
            if (worker.Availability == "stale") throw new InvalidOperationException("Worker is offline.");
        }
        return await commands.CreateAsync(request, cancellationToken);
    }

    public async Task<ProvisioningCommand?> CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await commands.CancelAsync(id, cancellationToken);
    }

    public async Task<ProvisioningCommand?> ReconcileAsync(string id, bool nodeQuiescent,
        CancellationToken cancellationToken = default)
    {
        if (!nodeQuiescent)
            throw new InvalidOperationException("Verify that the node operation has stopped before reconciliation.");
        await EnsureDatabaseAsync(cancellationToken);
        return await commands.ReconcileAsync(id, cancellationToken);
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("The configured Server database is unavailable or not initialized.");
        await commands.InitializeAsync(cancellationToken);
    }
}

/// <summary>Local Server CLI adapter for typed provisioning command history and lifecycle actions.</summary>
public sealed class ServerProvisioningCommandCli(IServerConfigurationAdministrationService configurationService,
    IProvisioningCommandAdministrationServiceFactory serviceFactory, TextWriter? output = null, TextWriter? error = null)
{
    private readonly TextWriter _output = output ?? Console.Out;
    private readonly TextWriter _error = error ?? Console.Error;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParse(arguments, out var request, out var parseError))
            return InvalidArguments(parseError);
        if (request is null)
            return InvalidArguments("Invalid provisioning administration arguments. Run 'codex-server provision --help' for usage.");
        if (request.Help)
        {
            _output.WriteLine(Usage);
            return ServerAdministrationExitCodes.Success;
        }

        var inspected = configurationService.Inspect(request.ConfigurationArguments);
        if (!inspected.Document.IsValid || inspected.Configuration is null)
        {
            _error.WriteLine("Server configuration is invalid. Run 'codex-server config validate' for details.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }

        try
        {
            var service = serviceFactory.Create(inspected.Configuration);
            switch (request.Verb)
            {
                case "list":
                    WriteCommands(await service.ListAsync(cancellationToken, request.Limit, request.Offset), request.Json);
                    break;
                case "show":
                    if (request.Id is not { } showId) return InvalidArguments("A provisioning command ID is required.");
                    var shown = await service.GetAsync(showId, cancellationToken);
                    if (shown is null) return NotFound("Provisioning command was not found.");
                    WriteCommand(shown, request.Json);
                    break;
                case "create":
                    if (request.Command is not { } createRequest) return InvalidArguments("A typed provisioning command request is required.");
                    var created = await service.CreateAsync(createRequest, cancellationToken);
                    WriteCommand(created, request.Json);
                    break;
                case "cancel":
                    if (request.Id is not { } cancelId) return InvalidArguments("A provisioning command ID is required.");
                    var cancelled = await service.CancelAsync(cancelId, cancellationToken);
                    if (cancelled is null) return NotFound("Provisioning command was not found.");
                    WriteCommand(cancelled, request.Json);
                    break;
                case "reconcile":
                    if (request.Id is not { } reconcileId) return InvalidArguments("A provisioning command ID is required.");
                    var reconciled = await service.ReconcileAsync(reconcileId, request.NodeQuiescent, cancellationToken);
                    if (reconciled is null) return NotFound("Provisioning command was not found.");
                    WriteCommand(reconciled, request.Json);
                    break;
                default:
                    return InvalidArguments("Unknown provisioning command. Run 'codex-server provision --help' for usage.");
            }
            return ServerAdministrationExitCodes.Success;
        }
        catch (OperationCanceledException) { return ServerAdministrationExitCodes.Canceled; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error.WriteLine("Local provisioning administration failed. Check Server state, Worker availability, and command lifecycle rules.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
    }

    private void WriteCommands(IReadOnlyList<ProvisioningCommand> commands, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(commands, JsonOptions));
            return;
        }
        if (commands.Count == 0)
        {
            _output.WriteLine("No typed provisioning commands have been created.");
            return;
        }
        foreach (var command in commands) WriteCommand(command, json: false);
    }

    private void WriteCommand(ProvisioningCommand command, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(command, JsonOptions));
            return;
        }
        _output.WriteLine($"{command.Id} · {command.Request.NodeId} · {command.Request.CapabilityId} · {command.Request.Action} · {command.Status} · {command.Diagnostic}");
        _output.WriteLine($"Created: {command.CreatedAtUtc:O}");
        if (command.StartedAtUtc is { } started) _output.WriteLine($"Started: {started:O}");
        if (command.DeadlineUtc is { } deadline) _output.WriteLine($"Deadline: {deadline:O}");
        if (command.LoginInstructions is { } login)
        {
            _output.WriteLine($"Device login: {login.VerificationUri}");
            _output.WriteLine($"One-time code: {login.UserCode}");
        }
        if (command.CompletedAtUtc is { } completed) _output.WriteLine($"Completed: {completed:O}");
        if (command.FailureDetail is { } failure)
        {
            _output.WriteLine($"Failure: {failure.Description}");
            if (failure.ProcessExitCode is { } exitCode) _output.WriteLine($"Process exit code: {exitCode}");
        }
    }

    private int NotFound(string message)
    {
        _error.WriteLine(message);
        return ServerAdministrationExitCodes.OperationalFailure;
    }

    private int InvalidArguments(string message)
    {
        _error.WriteLine(message);
        return ServerAdministrationExitCodes.InvalidArguments;
    }

    private static bool TryParse(IReadOnlyList<string> arguments, out CliRequest? request, out string error)
    {
        request = null;
        error = "Invalid provisioning administration arguments. Run 'codex-server provision --help' for usage.";
        if (arguments.Count == 1 && arguments[0] == "--help")
        {
            request = new("help", Help: true);
            return true;
        }
        if (arguments.Count == 0 || arguments[0] is not ("list" or "show" or "create" or "cancel" or "reconcile")) return false;

        var verb = arguments[0];
        var positionals = new List<string>();
        var configuration = new List<string>();
        var json = false;
        var allowElevation = false;
        var nodeQuiescent = false;
        var timeoutSeconds = 120;
        var limit = 100;
        var offset = 0;
        string? repository = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (IsConfigurationOption(argument))
            {
                if (!seen.Add("config:" + argument.Split('=', 2)[0])) return false;
                if (argument.Contains('='))
                {
                    if (argument.EndsWith('=') || !IsConfigurationOptionValue(argument[(argument.IndexOf('=') + 1)..])) return false;
                    configuration.Add(argument);
                }
                else
                {
                    if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)) return false;
                    configuration.Add(argument);
                    configuration.Add(arguments[++index]);
                }
                continue;
            }
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }
            var name = argument.Split('=', 2)[0];
            if (!seen.Add(name)) return false;
            switch (name)
            {
                case "--json" when argument == name: json = true; break;
                case "--allow-elevation" when argument == name: allowElevation = true; break;
                case "--node-quiescent" when argument == name: nodeQuiescent = true; break;
                case "--timeout-seconds":
                    if (!TryReadOptionValue(arguments, ref index, argument, out var timeoutText) ||
                        !int.TryParse(timeoutText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out timeoutSeconds)) return false;
                    break;
                case "--limit":
                    if (!TryReadOptionValue(arguments, ref index, argument, out var limitText) ||
                        !int.TryParse(limitText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit)) return false;
                    break;
                case "--offset":
                    if (!TryReadOptionValue(arguments, ref index, argument, out var offsetText) ||
                        !int.TryParse(offsetText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset)) return false;
                    break;
                case "--repository":
                    if (!TryReadOptionValue(arguments, ref index, argument, out repository)) return false;
                    break;
                default: return false;
            }
        }

        string? id = null;
        ProvisioningCommandRequest? command = null;
        switch (verb)
        {
            case "list" when positionals.Count == 0 && !allowElevation && !nodeQuiescent && repository is null && timeoutSeconds == 120 &&
                limit is >= 1 and <= 100 && offset is >= 0 and <= 10_000:
                break;
            case "show" or "cancel" or "reconcile" when positionals.Count == 1 && Guid.TryParseExact(positionals[0], "N", out _) &&
                !allowElevation && repository is null && timeoutSeconds == 120 && limit == 100 && offset == 0 &&
                (verb == "reconcile" || !nodeQuiescent):
                id = positionals[0];
                if (verb == "reconcile" && !nodeQuiescent) return false;
                break;
            case "create" when positionals.Count == 3 && !nodeQuiescent && limit == 100 && offset == 0:
                if (!TryAction(positionals[2], out var action)) return false;
                command = new(positionals[0], positionals[1], action, timeoutSeconds, allowElevation, repository);
                if (!ProvisioningCommandProtocol.Valid(command) || !ProvisioningCommandProtocol.Supported(command)) return false;
                break;
            default: return false;
        }
        request = new(verb, id, command, json, nodeQuiescent, configuration, Limit: limit, Offset: offset);
        error = string.Empty;
        return true;
    }

    private static bool TryAction(string value, out ProvisioningCommandAction action)
    {
        var name = Enum.GetNames<ProvisioningCommandAction>().FirstOrDefault(candidate =>
            string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            action = default;
            return false;
        }
        return Enum.TryParse(name, ignoreCase: true, out action);
    }

    private static bool TryReadOptionValue(IReadOnlyList<string> arguments, ref int index, string argument, out string? value)
    {
        var separator = argument.IndexOf('=');
        if (separator >= 0)
        {
            value = argument[(separator + 1)..];
            return value.Length > 0;
        }
        if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = arguments[++index];
            return true;
        }
        value = null;
        return false;
    }

    private static bool IsConfigurationOption(string argument)
    {
        var key = argument.Split('=', 2)[0];
        return key.StartsWith("--Server:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("--Logging:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfigurationOptionValue(string value) => value.Length > 0;

    private sealed record CliRequest(string Verb, string? Id = null, ProvisioningCommandRequest? Command = null,
        bool Json = false, bool NodeQuiescent = false, IReadOnlyList<string>? Configuration = null, bool Help = false,
        int Limit = 100, int Offset = 0)
    {
        public IReadOnlyList<string> ConfigurationArguments => Configuration ?? [];
    }

    private const string Usage = """
        Usage: codex-server provision <list|show|create|cancel|reconcile> [arguments] [options]
               provision list [--limit 1..100] [--offset 0..10000] [--json]
               provision show <command-id> [--json]
               provision create <node-id|server> <capability-id> <typed-action> [--timeout-seconds 120] [--allow-elevation] [--repository owner/repository] [--json]
               provision cancel <command-id> [--json]
               provision reconcile <command-id> --node-quiescent [--json]

        Commands inspect the configured local Server database and do not contact the running Server.
        Cancellation applies only to queued commands. Running operations stop at their deadline;
        reconcile only after verifying the target node is quiescent. Worker-local policy remains final authority.
        """;
}
