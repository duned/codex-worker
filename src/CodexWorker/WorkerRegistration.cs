namespace CodexWorker;

using CodexProvisioning;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using static CodexWorker.WorkerServerHttpTransport;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;

/// <summary>Versioned wire contract sent to Codex Server. Contains operational metadata only.</summary>
public sealed record WorkerRegistrationContract(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("workerVersion")] string WorkerVersion,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<WorkerCapabilityContract> Capabilities,
    IReadOnlyList<CapabilityState>? CapabilityInventory = null);

/// <summary>A runtime, tool, or service currently available to this worker.</summary>
public sealed record WorkerCapabilityContract(string Type, string Name, string? Version = null, string? Scope = null) : CodexProvisioning.ICapabilityDescriptor;

public static class WorkerAuthenticationCapabilities
{
    public static IReadOnlyList<WorkerCapabilityContract> ForRepository(string repository) =>
    [
        new("authentication", "github-api", Scope: repository),
        new("authentication", "git-repository", Scope: repository)
    ];
}

/// <summary>Agent providers whose configured authentication passed the Worker startup preflight.</summary>
public static class WorkerAgentCapabilities
{
    public static WorkerCapabilityContract AuthenticatedProvider(string provider) =>
        new("agent-provider", provider.Trim().ToLowerInvariant());
}

public sealed record WorkerHeartbeatContract(int ContractVersion, string WorkerId, string WorkerVersion,
    string LifecycleState, int ActiveExecutions, int MaximumCapacity, IReadOnlyList<WorkerCapabilityContract> Capabilities,
    IReadOnlyList<string> ActiveProjects, string? ConfigurationSynchronization = null, string? ConfigurationVersion = null,
    IReadOnlyList<CapabilityState>? CapabilityInventory = null, ManagedWorkerDiagnostics? ManagedDiagnostics = null);
public sealed record WorkerHeartbeatStatus(int ActiveExecutions, IReadOnlyList<string> Projects, string State);
public sealed record WorkerAssignmentRequestContract(string WorkerId, bool WorkerEnabled, int AvailableCapacity,
    IReadOnlyDictionary<string, int> ProjectCapacities, IReadOnlyList<IntegrationRecoveryCandidate>? IntegrationRecoveries = null);
public sealed record ServerProjectRequirementContract(string Type, string Name, string? Version = null, string? Scope = null) : CodexProvisioning.ICapabilityDescriptor;
public sealed record ServerProjectContract(string Id, string Name, string Repository, string DefaultBranch,
    string Description, IReadOnlyList<ServerProjectRequirementContract> Requirements, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool Enabled = true);
public sealed record ServerWorkReferenceContract(string Type, string Id, string? Url = null);
public sealed record ServerExecutionLeaseContract(string ExecutionId, string WorkerId, long Generation,
    DateTimeOffset AcquiredAtUtc, DateTimeOffset ExpiresAtUtc, string State, int RenewalIntervalSeconds = 60);
public sealed record WorkerAssignmentContract(string AssignmentId, string ServerExecutionId, ServerProjectContract Project,
    ServerWorkReferenceContract Work, string WorkerId, IReadOnlyDictionary<string, string> Metadata,
    ServerExecutionLeaseContract? Lease = null);
public sealed record WorkerAssignmentResponseContract(bool HasWork, WorkerAssignmentContract? Assignment,
    IReadOnlyDictionary<string, string>? IntegrationRecoveryRejections = null);
public sealed record ProvisioningActionContract(string Id, string Type, string Name, string? Version = null, string Operation = "ensure",
    string? CredentialId = null, string? Scope = null);
public sealed record ProvisioningPlanContract(string Id, string WorkerId, DateTimeOffset CreatedAtUtc, string State,
    IReadOnlyList<ProvisioningActionContract> Actions, string? CurrentActionId = null, DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null, string? Result = null, string? Failure = null);
public sealed record ProvisioningWorkerReportContract(string WorkerId, string State, string? CurrentActionId = null,
    string? Result = null, string? Failure = null);
public sealed record WorkerExecutionReportContract(string WorkerId, string AssignmentId, string WorkerExecutionId, string State,
    string? Stage = null, DateTimeOffset? StartedAtUtc = null, DateTimeOffset? CompletedAtUtc = null,
    long? DurationMilliseconds = null, string? ValidationResult = null, string? IntegrationResult = null,
    string? FailureClassification = null, bool Recoverable = false, string? Summary = null, long Generation = 0);
public sealed record ExecutionLeaseRenewalContract(string WorkerId, long Generation);

/// <summary>Loads or creates a stable, random worker identifier stored with restrictive permissions.</summary>
public static class WorkerIdentity
{
    public static string DisplayName =>
        Environment.GetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME") is { Length: > 0 } name
            ? name : Environment.MachineName;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-worker", "worker-id");

    /// <summary>Reads existing identity without creating or repairing local state.</summary>
    public static async Task<string> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > 128) throw new InvalidDataException("Worker identity exceeds supported bounds.");
        var existing = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        RestrictFile(path);
        if (!Guid.TryParseExact(existing, "N", out _))
            throw new InvalidDataException("Worker identity file does not contain a valid identity.");
        return existing;
    }

    public static async Task<string> LoadOrCreateAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Worker identity path must include a directory.");
        Directory.CreateDirectory(directory);
        try
        {
            var existing = await LoadAsync(path, cancellationToken);
            RestrictFile(path);
            return existing;
        }
        catch (FileNotFoundException) { }

        var identity = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WorkerRegistrationFile.WritePrivateAsync(temp, identity + Environment.NewLine, cancellationToken);
            RestrictFile(temp);
            try { WorkerRegistrationFile.Publish(temp, path); }
            catch (IOException) when (File.Exists(path))
            {
                var winner = await LoadAsync(path, cancellationToken);
                RestrictFile(path);
                return winner;
            }
            RestrictFile(path);
            return identity;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public static class WorkerAuthentication
{
    public static string TokenPath(string identityPath) => Path.GetFullPath(identityPath) + ".token";
    internal static string ValidateToken(string token)
    {
        token = token.Trim();
        if (!WorkerEnrollmentProtocol.ValidToken(token))
            throw new InvalidDataException("Persisted Worker authentication material is invalid. Restore the enrolled credential from protected storage; do not replace the Worker identity.");
        return token;
    }

    internal static async Task<string> LoadTokenAsync(string identityPath, CancellationToken cancellationToken)
    {
        var path = TokenPath(identityPath);
        if (new FileInfo(path).Length > 4098) throw new InvalidDataException("Worker credential exceeds supported bounds.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return ValidateToken(await File.ReadAllTextAsync(path, cancellationToken));
    }
    public static async Task<string> LoadOrCreateTokenAsync(string identityPath, CancellationToken cancellationToken = default)
    {
        var path = TokenPath(identityPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var existing = await LoadTokenAsync(identityPath, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return existing;
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WorkerRegistrationFile.WritePrivateAsync(temp, token, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { WorkerRegistrationFile.Publish(temp, path); }
            catch (IOException) when (File.Exists(path))
            {
                var existing = await LoadTokenAsync(identityPath, cancellationToken);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return existing;
            }
            return token;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static (string Token, string Endpoint) GetConnection(WorkerServerSettings settings)
    {
        var identityPath = Path.GetFullPath(settings.IdentityFile ?? WorkerIdentity.DefaultPath);
        // Capture the endpoint and credential together under the same cross-process gate
        // used by enrollment. An in-flight request retains its original endpoint.
        if (!Directory.Exists(Path.GetDirectoryName(identityPath))) return (GetToken(settings), settings.EffectiveUrl);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using var registrationLock = new FileStream(identityPath + ".registration-lock", options);
            return (GetToken(settings), settings.EffectiveUrl);
        }
        catch (IOException)
        {
            throw new WorkerStartupException("Local registration is busy or unreadable. Complete registration before managed work resumes.");
        }
    }

    public static string GetToken(WorkerServerSettings settings)
    {
        var identityPath = settings.IdentityFile ?? WorkerIdentity.DefaultPath;
        var path = TokenPath(identityPath);
        WorkerPendingRegistration.CheckPublication(identityPath);
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 4098) throw new InvalidDataException("Worker credential exceeds supported bounds.");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return ValidateToken(File.ReadAllText(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkerStartupException("Persisted Worker API credential is unreadable. Check service-account access to the enrolled credential; do not replace the Worker identity.");
        }
        return "";
    }
}

internal sealed record WorkerPendingRegistration(string WorkerId, string Endpoint, string Operation, string Token, bool Verified)
{
    internal static WorkerPendingRegistration? Load(string identityPath)
    {
        var path = Path.GetFullPath(identityPath) + ".pending";
        if (!File.Exists(path)) return null;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Pending registration exceeds supported bounds.");
        WorkerPendingRegistration? pending;
        try { pending = JsonSerializer.Deserialize<WorkerPendingRegistration>(File.ReadAllText(path)); }
        catch (JsonException) { throw new InvalidDataException("Pending registration is invalid; retain it for operator reconciliation."); }
        if (pending is null || !Guid.TryParseExact(pending.WorkerId, "N", out _) ||
            !WorkerEnrollmentProtocol.ValidOperation(pending.Operation) || !WorkerEnrollmentProtocol.ValidToken(pending.Token) ||
            string.IsNullOrWhiteSpace(pending.Endpoint))
            throw new InvalidDataException("Pending registration is invalid; retain it for operator reconciliation.");
        WorkerServerSettings.ValidateUrl(pending.Endpoint);
        return pending;
    }

    internal static void CheckPublication(string identityPath)
    {
        if (Load(identityPath) is { Verified: true })
            throw new WorkerStartupException("Registration publication is pending. Retry register with the original Server and operation before starting managed work.");
    }
}

internal static partial class WorkerRegistrationFile
{
    internal static async Task WritePrivateAsync(string path, string content, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
        // The completed secret must reach disk before its name is published.
        stream.Flush(flushToDisk: true);
    }

    internal static async Task ReplaceAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WritePrivateAsync(temp, content, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void Publish(string temporaryPath, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(temporaryPath, path, overwrite: false);
            return;
        }

        // Unix File.Move can race between its destination check and rename. Linking
        // the completed file publishes it atomically without replacing another writer.
        // The caller removes the temporary name in its finally block.
        if (Link(temporaryPath, path) != 0)
            throw new IOException("Could not publish Worker registration state.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);
}

public sealed class WorkerRegistrationClient(HttpClient? httpClient = null, NodeCapabilityDiscovery? provisioningDiscovery = null,
    WorkerCapabilityDiscovery? capabilityDiscovery = null)
{
    // One process-owned connection pool. Injected clients are caller-owned test seams.
    private static readonly HttpClient SharedClient = new(WorkerServerHttpTransport.CreateHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken,
        TimeSpan? timeout = null) => WorkerServerHttpTransport.SendAsync(httpClient ?? SharedClient, request,
            timeout ?? TimeSpan.FromSeconds(20), cancellationToken);

    internal WorkerCapabilityDiscovery CapabilityDiscovery => capabilityDiscovery ?? WorkerCapabilityDiscovery.Shared;
    internal NodeCapabilityDiscovery InventoryDiscovery => provisioningDiscovery ?? ProvisioningDiscovery;
    internal static readonly NodeCapabilityDiscovery ProvisioningDiscovery = new();

    public async Task RegisterAsync(WorkerServerSettings settings, int capacity, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return;
        var identity = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        var connection = WorkerAuthentication.GetConnection(settings);
        var token = connection.Token;
        if (string.IsNullOrWhiteSpace(token))
            throw new WorkerStartupException("Managed mode requires a durable per-Worker API credential. Use codex-worker register for explicit enrollment; restore existing enrolled credentials when available.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(connection.Endpoint.TrimEnd('/') + "/"), $"api/v1/workers/{identity}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(await RegistrationAsync(identity, capacity, cancellationToken));
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadSafeServerErrorAsync(response, cancellationToken, token);
                throw new WorkerStartupException($"Codex Server registration failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{detail}",
                    new HttpRequestException("Server registration request was rejected.", null, response.StatusCode));
            }
            await ValidateAcknowledgementAsync(response, identity, enrollment: false, cancellationToken);
        }
        catch (WorkerStartupException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException)
        {
            throw new WorkerStartupException($"Codex Server registration failed: {ex.Message}", ex);
        }
    }

    public Task BootstrapAsync(WorkerServerSettings settings, int capacity, string bootstrapToken, CancellationToken cancellationToken) =>
        BootstrapAsync(settings, capacity, bootstrapToken, settings.RegistrationOperation, cancellationToken);

    public async Task BootstrapAsync(WorkerServerSettings settings, int capacity, string bootstrapToken, string operation, CancellationToken cancellationToken)
    {
        if (bootstrapToken.Length > 0 && string.IsNullOrWhiteSpace(bootstrapToken))
            throw new WorkerStartupException("Bootstrap token must be a single nonempty value. Copy only the token, without its description.");
        bootstrapToken = bootstrapToken.Trim();
        if (bootstrapToken.Length > 4096 || bootstrapToken.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new WorkerStartupException("Bootstrap token must be a single nonempty value. Copy only the token, without its description.");
        WorkerServerSettings.ValidateUrl(settings.Url);
        if (!WorkerEnrollmentProtocol.ValidOperation(operation) || capacity is < 1 or > 8)
            throw new InvalidDataException("Invalid registration operation or capacity.");
        var identityPath = Path.GetFullPath(settings.IdentityFile ?? WorkerIdentity.DefaultPath);
        var identity = await WorkerIdentity.LoadOrCreateAsync(identityPath, cancellationToken);
        // A process-shared exclusive handle prevents two operators from replacing pending state.
        var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var registrationLock = new FileStream(identityPath + ".registration-lock", lockOptions);
        var endpoint = new Uri(settings.Url).AbsoluteUri.TrimEnd('/');
        var urlPath = identityPath + ".server";
        var activeEndpoint = File.Exists(urlPath) ? settings.EffectiveUrl : null;
        if (activeEndpoint is not null) WorkerServerSettings.ValidateUrl(activeEndpoint);
        var changingServer = activeEndpoint is not null && new Uri(activeEndpoint).AbsoluteUri.TrimEnd('/') != endpoint;
        if (changingServer && operation != "associate")
            throw new WorkerStartupException("Changing Server requires explicit register --operation associate authorization from the destination Server.");
        var pendingPath = identityPath + ".pending";
        var pending = WorkerPendingRegistration.Load(identityPath);
        if (pending is not null && (pending.WorkerId != identity || pending.Endpoint != endpoint || pending.Operation != operation))
            throw new WorkerStartupException("A different registration is pending. Reconcile it using its original Server and operation before starting another operation.");
        if (pending is null && (operation == "enroll" || bootstrapToken.Length == 0 && !changingServer) && File.Exists(WorkerAuthentication.TokenPath(identityPath)))
        {
            // An ordinary retry never rotates or consumes another enrollment authorization.
            if (activeEndpoint is null)
                throw new WorkerStartupException("Existing credential has no Server association. Use explicit recover or associate authorization; it cannot be sent to an unverified endpoint.");
            var activeToken = await WorkerAuthentication.LoadTokenAsync(identityPath, cancellationToken);
            await VerifyCredentialAsync(endpoint, identity, activeToken, capacity, cancellationToken);
            await WorkerRegistrationFile.ReplaceAsync(urlPath, endpoint, cancellationToken);
            return;
        }
        if (pending is null && bootstrapToken.Length == 0)
            throw new WorkerStartupException("No retained registration to reconcile. Request a fresh authorization in Add Worker for this public pairing request.");
        var retainedPending = pending is not null;
        if (pending is null)
        {
            pending = new(identity, endpoint, operation, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), false);
            await WorkerRegistrationFile.ReplaceAsync(pendingPath, JsonSerializer.Serialize(pending), cancellationToken);
        }
        try
        {
            if (retainedPending)
            {
                // Probe the retained material first; a lost response must not consume a new authorization.
                if (await TryVerifyCredentialAsync(endpoint, identity, pending.Token, capacity, cancellationToken))
                {
                    await PublishAsync(identityPath, pending, cancellationToken);
                    return;
                }
                if (pending.Verified) throw new WorkerStartupException("Previously verified pending credential is no longer accepted. Preserve local state and reconcile with the Server operator.");
            }
            if (bootstrapToken.Length == 0)
                throw new WorkerStartupException("Retained credential is not acknowledged by this Server. Request a fresh authorization for the same identity, Server and operation; do not delete pending state.");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint + "/api/v1/workers/register"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bootstrapToken);
            request.Headers.Add("X-Codex-Worker-Token", pending.Token);
            request.Headers.Add("X-Codex-Worker-Operation", operation);
            request.Content = JsonContent.Create(await RegistrationAsync(identity, capacity, cancellationToken));
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                    await TryVerifyCredentialAsync(endpoint, identity, pending.Token, capacity, cancellationToken))
                {
                    await PublishAsync(identityPath, pending, cancellationToken);
                    return;
                }
                throw new WorkerStartupException($"Codex Server bootstrap failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). Authorization may be invalid, expired, used, or bound to another Worker/operation. Pending material was retained.{await ReadSafeServerErrorAsync(response, cancellationToken, bootstrapToken, pending.Token)}");
            }
            await ValidateAcknowledgementAsync(response, identity, enrollment: true, cancellationToken);
            await VerifyCredentialAsync(endpoint, identity, pending.Token, capacity, cancellationToken);
            await PublishAsync(identityPath, pending, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is { } status && (int)status is >= 300 and < 400)
        {
            throw new WorkerStartupException($"Codex Server returned HTTP {(int)status}. Redirects are disabled; configure the final Server endpoint. Pending material was retained.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            throw new WorkerStartupException("Server registration could not be verified. Pending identity and material were retained; retry the same Server and operation.");
        }
    }

    private async Task<WorkerRegistrationContract> RegistrationAsync(string identity, int capacity, CancellationToken cancellationToken) =>
        new(2, identity, WorkerIdentity.DisplayName, ApplicationVersion.Display,
            $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}", capacity,
            await CapabilityDiscovery.GetCachedAsync(cancellationToken), await InventoryDiscovery.GetAsync(cancellationToken: cancellationToken));

    private async Task<bool> TryVerifyCredentialAsync(string endpoint, string identity, string token, int capacity, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(endpoint + $"/api/v1/workers/{identity}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(await RegistrationAsync(identity, capacity, cancellationToken));
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return false;
        if (!response.IsSuccessStatusCode) throw new WorkerStartupException($"Credential verification failed with HTTP {(int)response.StatusCode}.");
        await ValidateAcknowledgementAsync(response, identity, enrollment: false, cancellationToken);
        return true;
    }

    private async Task VerifyCredentialAsync(string endpoint, string identity, string token, int capacity, CancellationToken cancellationToken)
    {
        if (!await TryVerifyCredentialAsync(endpoint, identity, token, capacity, cancellationToken))
            throw new WorkerStartupException("Server did not accept the retained Worker credential. Local registration material was preserved.");
    }

    private static async Task ValidateAcknowledgementAsync(HttpResponseMessage response, string identity, bool enrollment, CancellationToken cancellationToken)
    {
        const int maximumBytes = 64 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        try
        {
            if (count > maximumBytes) throw new InvalidDataException("Registration acknowledgement exceeds the supported bound.");
            var acknowledgement = JsonSerializer.Deserialize<WorkerEnrollmentAcknowledgement>(bytes.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (acknowledgement is null || acknowledgement.WorkerId != identity ||
                (enrollment ? acknowledgement.ContractVersion != WorkerEnrollmentProtocol.AcknowledgementVersion : acknowledgement.ContractVersion is not (1 or 2)))
                throw new InvalidDataException("Registration acknowledgement has an unsupported contract or mismatched Worker identity.");
        }
        catch (JsonException) { throw new InvalidDataException("Server returned an invalid registration acknowledgement. Pending material was retained."); }
    }

    private static async Task PublishAsync(string identityPath, WorkerPendingRegistration pending, CancellationToken cancellationToken)
    {
        // Journal verification before publishing either file. Runtime authentication refuses partial publication.
        pending = pending with { Verified = true };
        await WorkerRegistrationFile.ReplaceAsync(identityPath + ".pending", JsonSerializer.Serialize(pending), cancellationToken);
        await WorkerRegistrationFile.ReplaceAsync(WorkerAuthentication.TokenPath(identityPath), pending.Token, cancellationToken);
        await WorkerRegistrationFile.ReplaceAsync(identityPath + ".server", pending.Endpoint, cancellationToken);
        File.Delete(identityPath + ".pending");
    }

    private static async Task<string> ReadSafeServerErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken,
        params string[] secrets)
    {
        var context = SafeRequestContext(response, secrets);
        try
        {
            const int maximumErrorBodyBytes = 16 * 1024;
            if (response.Content.Headers.ContentLength is > maximumErrorBodyBytes) return context;
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var boundedBody = new MemoryStream();
            var buffer = new byte[maximumErrorBodyBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await responseStream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                if (read == 0) break;
                total += read;
            }
            if (total == buffer.Length) return context;
            await boundedBody.WriteAsync(buffer.AsMemory(0, total), cancellationToken);
            boundedBody.Position = 0;
            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                boundedBody, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var message = error.GetString();
                if (!string.IsNullOrWhiteSpace(message) && message.Length <= 300)
                {
                    var safeMessage = message;
                    foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
                        safeMessage = safeMessage.Replace(secret, "[redacted]", StringComparison.Ordinal);
                    safeMessage = FailureDiagnosticRedactor.Redact(safeMessage);
                    safeMessage = string.Concat(safeMessage.Where(character => !char.IsControl(character)));
                    if (safeMessage.Length <= 300) return " " + safeMessage + context;
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // Do not surface unstructured server response bodies in worker diagnostics.
        }
        return context;
    }

    public async Task HeartbeatAsync(WorkerServerSettings settings, int capacity, int activeExecutions,
        IReadOnlyList<string> activeProjects, string lifecycleState, CancellationToken cancellationToken,
        IReadOnlyList<WorkerCapabilityContract>? capabilities = null,
        WorkerConfigurationSyncStatus? configurationSync = null)
    {
        if (!settings.Enabled) return;
        var connection = WorkerAuthentication.GetConnection(settings);
        var token = connection.Token;
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires a durable per-Worker API credential. Use codex-worker register for explicit enrollment; restore existing enrolled credentials when available.");
        var identity = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(connection.Endpoint.TrimEnd('/') + "/"), $"api/v1/workers/{identity}/heartbeat"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new WorkerHeartbeatContract(2, identity, ApplicationVersion.Display,
            lifecycleState, activeExecutions, capacity, capabilities ?? await CapabilityDiscovery.GetCachedAsync(cancellationToken), activeProjects,
            configurationSync?.SynchronizationStatus, configurationSync?.AppliedVersion,
            await InventoryDiscovery.GetAsync(cancellationToken: cancellationToken), configurationSync?.Diagnostics));
        using var response = await SendAsync(request, cancellationToken, TimeSpan.FromSeconds(10));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Codex Server heartbeat failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}", null, response.StatusCode);
    }

    public async Task<WorkerAssignmentResponseContract> RequestAssignmentAsync(WorkerServerSettings settings, bool workerEnabled, int availableCapacity,
        IReadOnlyDictionary<string, int> projectCapacities, CancellationToken cancellationToken,
        IReadOnlyList<IntegrationRecoveryCandidate>? integrationRecoveries = null)
    {
        if (!settings.Enabled) throw new InvalidOperationException("Assignment requests require managed Server mode.");
        if (availableCapacity is < 0 or > 8 || projectCapacities is null || projectCapacities.Any(p => p.Value is < 0 or > 8))
            throw new ArgumentOutOfRangeException(nameof(availableCapacity), "Assignment capacity must be between zero and eight.");
        if (!workerEnabled || availableCapacity == 0 || projectCapacities.Count == 0 || projectCapacities.All(p => p.Value == 0))
            return new(false, null);
        var connection = WorkerAuthentication.GetConnection(settings);
        var token = connection.Token;
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires a durable per-Worker API credential. Use codex-worker register for explicit enrollment; restore existing enrolled credentials when available.");
        var identity = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(connection.Endpoint.TrimEnd('/') + "/"), $"api/v1/workers/{identity}/assignments/request"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new WorkerAssignmentRequestContract(identity, workerEnabled, availableCapacity, projectCapacities, integrationRecoveries));
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Codex Server assignment request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
        return await response.Content.ReadFromJsonAsync<WorkerAssignmentResponseContract>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Codex Server returned an empty assignment response.");
    }

    public async Task<ServerManagedConfigurationContract> GetManagedConfigurationAsync(WorkerServerSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("Managed configuration requires Server mode.");
        var identity = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        using var request = CreateAuthorizedRequest(HttpMethod.Get, settings, $"api/v1/workers/{identity}/configuration");
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Codex Server configuration request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}", null, response.StatusCode);
        try
        {
            return await response.Content.ReadFromJsonAsync<ServerManagedConfigurationContract>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Codex Server returned an empty managed configuration.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidDataException("Codex Server configuration was retrieved but its JSON contract is invalid.", ex);
        }
    }

    public async Task<bool> ExecuteProvisioningCommandAsync(WorkerServerSettings settings, ProvisioningPolicy policy,
        CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCapabilityMutation = null)
    {
        if (!settings.Enabled) return false;
        var workerId = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{workerId}/provisioning/commands/request");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return false;
        response.EnsureSuccessStatusCode();
        var command = await response.Content.ReadFromJsonAsync<ProvisioningCommand>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Empty provisioning command.");
        if (!ProvisioningCommandProtocol.Valid(command.Request) || command.Request.NodeId != workerId ||
            !Guid.TryParseExact(command.Id, "N", out _) || command.Status != ProvisioningCommandStatus.Running ||
            command.StartedAtUtc is null || command.DeadlineUtc is null ||
            command.DeadlineUtc > command.StartedAtUtc.Value.AddSeconds(command.Request.TimeoutSeconds))
            throw new InvalidDataException("Invalid provisioning command identity or deadline.");
        var action = command.Request.Action;
        var permitted = WorkerProvisioning.Permitted(command.Request, policy);
        if (permitted && action is not (ProvisioningCommandAction.Detect or ProvisioningCommandAction.CheckAuthentication or
                ProvisioningCommandAction.CheckConfiguration or ProvisioningCommandAction.InspectSshKey or ProvisioningCommandAction.VerifyRepositoryAccess) &&
            beforeCapabilityMutation is not null)
            await beforeCapabilityMutation(cancellationToken);
        async Task ReportAsync(ProvisioningCommandReport report, CancellationToken token)
        {
            using var message = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{workerId}/provisioning/commands/{command.Id}/report");
            // Failure detail is retained by the local Server executor. Keep Worker reports
            // on the established wire contract for mixed-version Server installations.
            message.Content = JsonContent.Create(report with { FailureDetail = null });
            using var acknowledgement = await SendAsync(message, token);
            acknowledgement.EnsureSuccessStatusCode();
        }
        var result = await new NodeProvisioningCommandExecutor(InventoryDiscovery)
            .ExecuteAsync(command, permitted, cancellationToken, ReportAsync);
        // A terminal acknowledgement is safe to resend; execution itself is never retried.
        await ReportAsync(result, cancellationToken);
        return true;
    }

    public async Task<ProvisioningPlanContract?> RequestProvisioningPlanAsync(WorkerServerSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return null;
        var workerId = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{workerId}/provisioning/request");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Codex Server provisioning request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
        var plan = await response.Content.ReadFromJsonAsync<ProvisioningPlanContract>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Codex Server returned an empty provisioning plan.");
        if (plan.WorkerId != workerId || plan.State != "Accepted" || !Guid.TryParseExact(plan.Id, "N", out _))
            throw new InvalidDataException("Codex Server returned a provisioning plan with an invalid identity or lifecycle state.");
        return plan;
    }

    public async Task ReportProvisioningPlanAsync(WorkerServerSettings settings, string planId, ProvisioningWorkerReportContract report,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{report.WorkerId}/provisioning/{planId}/report");
        request.Content = JsonContent.Create(report);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Codex Server provisioning report failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
    }

    public async Task<CredentialDeliveryResponse?> RetrieveCredentialAsync(WorkerServerSettings settings, string credentialId,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("Credential delivery requires managed Server mode.");
        var connection = WorkerAuthentication.GetConnection(settings);
        var token = Environment.GetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Credential delivery requires CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN.");
        var workerId = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(connection.Endpoint.TrimEnd('/') + "/"), $"api/v1/workers/{workerId}/credentials/{Uri.EscapeDataString(credentialId)}"));
        request.Headers.Add("X-Worker-Credential-Token", token);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Codex Server credential retrieval failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
        var credential = await response.Content.ReadFromJsonAsync<CredentialDeliveryResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Codex Server returned an empty credential response.");
        if (credential.Id != credentialId || credential.Version < 1 || string.IsNullOrEmpty(credential.Secret))
            throw new InvalidDataException("Codex Server returned an invalid credential response.");
        return credential;
    }

    private static async Task<string> LoadRegisteredIdentityAsync(WorkerServerSettings settings, CancellationToken cancellationToken)
    {
        try { return await WorkerIdentity.LoadAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new WorkerStartupException("Managed Worker identity is missing, invalid, or unreadable. Restore the enrolled identity from protected storage; use codex-worker register only for explicit enrollment.");
        }
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, WorkerServerSettings settings, string path)
    {
        var connection = WorkerAuthentication.GetConnection(settings);
        var token = connection.Token;
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires a durable per-Worker API credential. Use codex-worker register for explicit enrollment; restore existing enrolled credentials when available.");
        var request = new HttpRequestMessage(method, new Uri(new Uri(connection.Endpoint.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task ReportExecutionAsync(WorkerServerSettings settings, ExecutionHistoryEntry entry, string state,
        string? stage, long generation, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || entry.ServerExecutionId is null || entry.AssignmentId is null) return;
        var connection = WorkerAuthentication.GetConnection(settings);
        var token = connection.Token;
        if (string.IsNullOrWhiteSpace(token)) throw new HttpRequestException("Managed mode requires a durable per-Worker API credential. Use codex-worker register for explicit enrollment; restore existing enrolled credentials when available before reporting execution state.");
        var workerId = await LoadRegisteredIdentityAsync(settings, cancellationToken);
        var final = state is "Completed" or "Failed";
        var report = new WorkerExecutionReportContract(workerId, entry.AssignmentId, entry.ExecutionId.ToString(), state,
            stage, entry.StartedAtUtc, final ? entry.CompletedAtUtc ?? DateTimeOffset.UtcNow : null,
            entry.DurationMilliseconds, Bound(entry.ValidationOutcome, 1000),
            state == "Completed" ? "passed" : null,
            state == "Failed" ? entry.RecoveryState == "codex-interrupted" ? "CodexInterruption" : Bound(entry.State, 100) : null, entry.RecoveryState is "recoverable" or "integration-conflict" or "codex-interrupted",
            Bound(state == "Completed" ? entry.ImplementationSummary : entry.FailureReason ?? entry.ImplementationSummary, 1000), generation);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(connection.Endpoint.TrimEnd('/') + "/"),
            $"api/v1/workers/{workerId}/executions/{entry.ServerExecutionId}/report"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(report);
        using var response = await SendAsync(request, cancellationToken, TimeSpan.FromSeconds(10));
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Codex Server execution report failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}", null, response.StatusCode);
    }

    public async Task<DateTimeOffset?> RenewExecutionLeaseAsync(WorkerServerSettings settings, ServerExecutionLeaseContract lease,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return null;
        var connection = WorkerAuthentication.GetConnection(settings);
        var token = connection.Token;
        if (string.IsNullOrWhiteSpace(token)) throw new HttpRequestException("Managed mode requires a durable per-Worker API credential. Use codex-worker register for explicit enrollment; restore existing enrolled credentials when available before renewing an execution lease.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(connection.Endpoint.TrimEnd('/') + "/"),
            $"api/v1/workers/{lease.WorkerId}/executions/{lease.ExecutionId}/lease/renew"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new ExecutionLeaseRenewalContract(lease.WorkerId, lease.Generation));
        using var response = await SendAsync(request, cancellationToken, TimeSpan.FromSeconds(10));
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Codex Server execution lease renewal failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
        var renewed = await response.Content.ReadFromJsonAsync<ServerExecutionLeaseContract>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Codex Server returned an empty lease renewal response.");
        if (renewed.Generation != lease.Generation || renewed.WorkerId != lease.WorkerId || renewed.ExecutionId != lease.ExecutionId || renewed.State != "Active")
            throw new InvalidDataException("Codex Server returned a lease renewal for a different ownership generation.");
        return renewed.ExpiresAtUtc;
    }

    private static string? Bound(string? value, int limit) => value is { Length: > 0 } ? value[..Math.Min(value.Length, limit)] : null;
}

/// <summary>Best-effort runtime reporting. Connectivity loss is reported once per degraded period and never stops execution.</summary>
public sealed class WorkerHeartbeatLoop(WorkerServerSettings settings, int capacity,
    Func<WorkerHeartbeatStatus> snapshot, Action<string>? report = null,
    Func<IReadOnlyList<WorkerCapabilityContract>>? capabilities = null,
    Func<WorkerConfigurationSyncStatus?>? configurationSync = null, WorkerRegistrationClient? client = null)
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private readonly WorkerRegistrationClient _client = client ?? new();
    private bool _degraded;

    public void Start() => _run ??= RunAsync();

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.HeartbeatIntervalSeconds));
        do
        {
            try
            {
                var current = snapshot();
                await _client.HeartbeatAsync(settings, capacity, current.ActiveExecutions, current.Projects, current.State,
                    _stop.Token, capabilities?.Invoke(), configurationSync?.Invoke());
                if (_degraded) report?.Invoke("Codex Server heartbeat connectivity recovered.");
                _degraded = false;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!_degraded) report?.Invoke($"Codex Server heartbeat connectivity degraded ({FailureCategory(ex)}); check Server connectivity and enrolled Worker API authorization.");
                _degraded = true;
            }
        } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
    }

    // Exception messages can include remote diagnostics or authentication material.
    private static string FailureCategory(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        OperationCanceledException => "cancelled or timed out",
        IOException or UnauthorizedAccessException => "local enrollment state unavailable",
        _ => "request failed"
    };

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_run is not null) try { await _run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await _client.HeartbeatAsync(settings, capacity, 0, Array.Empty<string>(), WorkerLifecycleStates.Stopped, CancellationToken.None,
            capabilities?.Invoke(), configurationSync?.Invoke()); }
        catch (Exception ex) { if (!_degraded) report?.Invoke($"Codex Server final heartbeat failed ({FailureCategory(ex)}); check Server connectivity and enrolled Worker API authorization."); }
        _stop.Dispose();
    }
}
