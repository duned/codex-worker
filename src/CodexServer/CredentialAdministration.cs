namespace CodexServer;

using CodexProvisioning;
using System.Text;
using System.Text.Json;

public interface IServerCredentialAdministrationService
{
    Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> GetAsync(string credentialId, CancellationToken cancellationToken = default);
    Task<CredentialMetadata> CreateAsync(string provider, string type, CredentialSecretInput secret,
        CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> AssignAsync(string credentialId, string workerId, CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> ReplaceSecretAsync(string credentialId, CredentialSecretInput secret,
        CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> RevokeAsync(string credentialId, CancellationToken cancellationToken = default);
}

public interface IServerCredentialAdministrationServiceFactory
{
    IServerCredentialAdministrationService Create(ServerConfiguration configuration);
}

public sealed class LocalServerCredentialAdministrationServiceFactory : IServerCredentialAdministrationServiceFactory
{
    public IServerCredentialAdministrationService Create(ServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var database = configuration.ResolveDatabasePath();
        return new LocalServerCredentialAdministrationService(database,
            new SqliteRegistryStore(database, configuration.WorkerStaleAfterSeconds,
                leaseDurationSeconds: configuration.ExecutionLeaseDurationSeconds,
                leaseRenewalIntervalSeconds: configuration.ExecutionLeaseRenewalIntervalSeconds),
            new SqliteCredentialStore(database, Environment.GetEnvironmentVariable("CODEX_SERVER_CREDENTIAL_ENCRYPTION_KEY")));
    }
}

/// <summary>Local CLI access to the existing encrypted credential store and Worker registry.</summary>
public sealed class LocalServerCredentialAdministrationService(string databasePath, IRegistryStore registry,
    ICredentialStore credentials) : IServerCredentialAdministrationService
{
    public async Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await credentials.ListAsync(cancellationToken);
    }

    public async Task<CredentialMetadata?> GetAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await credentials.GetAsync(credentialId, cancellationToken);
    }

    public async Task<CredentialMetadata> CreateAsync(string provider, string type, CredentialSecretInput secret,
        CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await credentials.CreateAsync(new(provider, type, secret), cancellationToken);
    }

    public async Task<CredentialMetadata?> AssignAsync(string credentialId, string workerId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        if (!Guid.TryParseExact(workerId, "N", out _)) throw new InvalidDataException("Worker identity is invalid.");
        if (await registry.GetWorkerAsync(workerId, cancellationToken) is null)
            throw new KeyNotFoundException("Worker was not found.");
        return await credentials.AssignAsync(credentialId, workerId, cancellationToken);
    }

    public async Task<CredentialMetadata?> ReplaceSecretAsync(string credentialId, CredentialSecretInput secret,
        CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await credentials.ReplaceSecretAsync(credentialId, secret, cancellationToken);
    }

    public async Task<CredentialMetadata?> RevokeAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken);
        return await credentials.RevokeAsync(credentialId, cancellationToken);
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            throw new InvalidOperationException("The configured Server database is unavailable or not initialized.");
        // Credential administration runs against the Server's existing registry. Re-running
        // registry migrations from this CLI could race with the Server process that owns it.
        await credentials.InitializeAsync(cancellationToken);
    }
}

/// <summary>Local, secret-safe CLI for Server-managed credential metadata and assignment.</summary>
public sealed class ServerCredentialAdministrationCli(IServerConfigurationAdministrationService configurationService,
    IServerCredentialAdministrationServiceFactory serviceFactory, TextReader? input = null, TextWriter? output = null,
    TextWriter? error = null, Func<CancellationToken, Task<string>>? interactiveSecretReader = null)
{
    private const int MaximumSecretLength = 16_384;
    private readonly TextReader _input = input ?? Console.In;
    private readonly TextWriter _output = output ?? Console.Out;
    private readonly TextWriter _error = error ?? Console.Error;
    private readonly Func<CancellationToken, Task<string>> _interactiveSecretReader = interactiveSecretReader ?? ReadInteractiveSecretAsync;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string Usage = "Usage: codex-server credential <list|show|create|assign|replace|revoke> [arguments] [--json] [--secret-stdin] [Server configuration options]";

    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var helpArguments = arguments.Count == 0
            ? (IReadOnlyList<string>)["credential", "--help"]
            : ["credential", .. arguments];
        if (ServerCommandHelp.TryWrite(helpArguments, _output)) return ServerAdministrationExitCodes.Success;
        var result = await RunCoreAsync(arguments, cancellationToken);
        if (result == ServerAdministrationExitCodes.InvalidArguments)
            _error.WriteLine(ServerCommandHelp.UsageHint(helpArguments));
        return result;
    }

    private async Task<int> RunCoreAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            return InvalidArguments("Missing credential operation.");

        var operation = arguments[0];
        if (!TryParseOptions(arguments, out var positionals, out var json, out var secretFromStandardInput,
            out var configurationArguments))
            return InvalidArguments("Invalid credential administration options. Use --json, --secret-stdin, and valid --Server:<setting>=<value> options.");

        var expectedPositionals = operation switch
        {
            "list" => 0,
            "show" or "revoke" or "replace" => 1,
            "create" or "assign" => 2,
            _ => -1
        };
        var needsSecret = operation is "create" or "replace";
        if (expectedPositionals < 0 || positionals.Count != expectedPositionals || secretFromStandardInput && !needsSecret)
            return InvalidArguments(Usage);

        var inspected = configurationService.Inspect(configurationArguments);
        if (!inspected.Document.IsValid || inspected.Configuration is null)
        {
            if (json) _output.WriteLine(JsonSerializer.Serialize(inspected.Document, JsonOptions));
            else _error.WriteLine("Server configuration is invalid. Run 'codex-server config validate' for details.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }

        try
        {
            var service = serviceFactory.Create(inspected.Configuration);
            switch (operation)
            {
                case "list":
                    WriteList(await service.ListAsync(cancellationToken), json);
                    break;
                case "show":
                    var found = await service.GetAsync(positionals[0], cancellationToken);
                    if (found is null) return NotFound();
                    WriteMetadata(found, json);
                    break;
                case "create":
                    var created = await service.CreateAsync(positionals[0], positionals[1],
                        new CredentialSecretInput(await ReadSecretAsync(secretFromStandardInput, cancellationToken)), cancellationToken);
                    WriteMetadata(created, json);
                    break;
                case "assign":
                    var assigned = await service.AssignAsync(positionals[0], positionals[1], cancellationToken);
                    if (assigned is null) return NotFound();
                    WriteMetadata(assigned, json);
                    break;
                case "replace":
                    var replaced = await service.ReplaceSecretAsync(positionals[0],
                        new CredentialSecretInput(await ReadSecretAsync(secretFromStandardInput, cancellationToken)), cancellationToken);
                    if (replaced is null) return NotFound();
                    WriteMetadata(replaced, json);
                    break;
                case "revoke":
                    var revoked = await service.RevokeAsync(positionals[0], cancellationToken);
                    if (revoked is null) return NotFound();
                    WriteMetadata(revoked, json);
                    break;
            }
            return ServerAdministrationExitCodes.Success;
        }
        catch (OperationCanceledException) { return ServerAdministrationExitCodes.Canceled; }
        catch (InvalidDataException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.InvalidArguments;
        }
        catch (KeyNotFoundException)
        {
            _error.WriteLine("Worker was not found.");
            return ServerAdministrationExitCodes.NotFound;
        }
        catch (InvalidOperationException exception)
        {
            _error.WriteLine(exception.Message);
            return ServerAdministrationExitCodes.OperationalFailure;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            _error.WriteLine("Local credential administration failed. Check the configured Server database, encryption key, and file permissions.");
            return ServerAdministrationExitCodes.OperationalFailure;
        }
    }

    private async Task<string> ReadSecretAsync(bool secretFromStandardInput, CancellationToken cancellationToken)
    {
        if (!secretFromStandardInput)
        {
            _error.Write("Secret: ");
            var secret = await _interactiveSecretReader(cancellationToken);
            _error.WriteLine();
            if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaximumSecretLength)
                throw new InvalidDataException($"Credential secret must contain 1 to {MaximumSecretLength} characters.");
            return secret;
        }
        var builder = new StringBuilder();
        var buffer = new char[1024];
        string value;
        try
        {
            while (true)
            {
                var read = await _input.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0) break;
                if (builder.Length + read > MaximumSecretLength + 2)
                    throw new InvalidDataException($"Credential secret must contain 1 to {MaximumSecretLength} characters.");
                builder.Append(buffer, 0, read);
            }
            value = builder.ToString();
        }
        finally
        {
            builder.Clear();
            Array.Clear(buffer, 0, buffer.Length);
        }
        if (value.EndsWith("\r\n", StringComparison.Ordinal)) value = value[..^2];
        else if (value.EndsWith('\r') || value.EndsWith('\n')) value = value[..^1];
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumSecretLength)
            throw new InvalidDataException($"Credential secret must contain 1 to {MaximumSecretLength} characters.");
        return value;
    }

    internal static Task<string> ReadInteractiveSecretAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Interactive secret entry requires a terminal; use --secret-stdin for piped input.");

        var secret = new StringBuilder();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (secret.Length > 0) secret.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar) && secret.Length < MaximumSecretLength) secret.Append(key.KeyChar);
            }
            return Task.FromResult(secret.ToString());
        }
        finally { secret.Clear(); }
    }

    private void WriteList(IReadOnlyList<CredentialMetadata> credentials, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(credentials, JsonOptions));
            return;
        }
        if (credentials.Count == 0)
        {
            _output.WriteLine("No Server-managed credentials are registered.");
            return;
        }
        foreach (var credential in credentials)
            _output.WriteLine($"{credential.Id}\t{credential.Provider}/{credential.Type}\t{credential.Status}\t" +
                $"worker={credential.AssignedWorkerId ?? "unassigned"}\tversion={credential.Version}");
    }

    private void WriteMetadata(CredentialMetadata credential, bool json)
    {
        if (json)
        {
            _output.WriteLine(JsonSerializer.Serialize(credential, JsonOptions));
            return;
        }
        _output.WriteLine($"Credential: {credential.Id}");
        _output.WriteLine($"Provider / type: {credential.Provider} / {credential.Type}");
        _output.WriteLine($"Status: {credential.Status}");
        _output.WriteLine($"Assignment: {credential.AssignedWorkerId ?? "unassigned"}");
        _output.WriteLine($"Version: {credential.Version}");
        _output.WriteLine($"Created: {credential.CreatedAtUtc:O}");
        _output.WriteLine($"Updated: {credential.UpdatedAtUtc:O}");
        if (credential.RevokedAtUtc is { } revokedAt) _output.WriteLine($"Revoked: {revokedAt:O}");
        _output.WriteLine("Secret payload: not displayed");
    }

    private int InvalidArguments(string message)
    {
        _error.WriteLine(message);
        return ServerAdministrationExitCodes.InvalidArguments;
    }

    private int NotFound()
    {
        _error.WriteLine("Credential was not found.");
        return ServerAdministrationExitCodes.NotFound;
    }

    private static bool TryParseOptions(IReadOnlyList<string> arguments, out IReadOnlyList<string> positionals,
        out bool json, out bool secretFromStandardInput, out IReadOnlyList<string> configurationArguments)
    {
        var values = new List<string>();
        var configuration = new List<string>();
        json = false;
        secretFromStandardInput = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--json" && !json) { json = true; continue; }
            if (argument == "--secret-stdin" && !secretFromStandardInput) { secretFromStandardInput = true; continue; }
            if (argument.StartsWith("--Server:", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--Logging:", StringComparison.OrdinalIgnoreCase))
            {
                configuration.Add(argument);
                if (!argument.Contains('=') && index + 1 < arguments.Count &&
                    !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    configuration.Add(arguments[++index]);
                else if (!argument.Contains('='))
                    return Fail(out positionals, out configurationArguments);
                continue;
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                return Fail(out positionals, out configurationArguments);
            values.Add(argument);
        }
        positionals = values;
        configurationArguments = configuration;
        return true;
    }

    private static bool Fail(out IReadOnlyList<string> positionals, out IReadOnlyList<string> configurationArguments)
    {
        positionals = [];
        configurationArguments = [];
        return false;
    }
}
