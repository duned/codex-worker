namespace CodexWorker;

using CodexProvisioning;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

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
public sealed record WorkerCapabilityContract(string Type, string Name, string? Version = null, string? Scope = null);

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
    IReadOnlyList<CapabilityState>? CapabilityInventory = null);
public sealed record WorkerHeartbeatStatus(int ActiveExecutions, IReadOnlyList<string> Projects, string State);
public sealed record WorkerAssignmentRequestContract(string WorkerId, bool WorkerEnabled, int AvailableCapacity,
    IReadOnlyDictionary<string, int> ProjectCapacities, IReadOnlyList<IntegrationRecoveryCandidate>? IntegrationRecoveries = null);
public sealed record ServerProjectRequirementContract(string Type, string Name, string? Version = null, string? Scope = null);
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
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-worker", "worker-id");

    /// <summary>Reads existing identity without creating or repairing local state.</summary>
    public static async Task<string> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var existing = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
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
            await File.WriteAllTextAsync(temp, identity + Environment.NewLine, cancellationToken);
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
        if (token.Length is < 32 or > 4096 || token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new InvalidDataException("Persisted Worker authentication material is invalid.");
        return token;
    }

    internal static async Task<string> LoadTokenAsync(string identityPath, CancellationToken cancellationToken) =>
        ValidateToken(await File.ReadAllTextAsync(TokenPath(identityPath), cancellationToken));
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
            await File.WriteAllTextAsync(temp, token, cancellationToken);
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

    public static string GetToken(WorkerServerSettings settings)
    {
        var identityPath = settings.IdentityFile ?? WorkerIdentity.DefaultPath;
        var path = TokenPath(identityPath);
        if (File.Exists(path)) return ValidateToken(File.ReadAllText(path));
        return Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN") ?? "";
    }
}

internal static partial class WorkerRegistrationFile
{
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
    internal WorkerCapabilityDiscovery CapabilityDiscovery => capabilityDiscovery ?? WorkerCapabilityDiscovery.Shared;
    internal NodeCapabilityDiscovery InventoryDiscovery => provisioningDiscovery ?? ProvisioningDiscovery;
    internal static readonly NodeCapabilityDiscovery ProvisioningDiscovery = new();

    public async Task RegisterAsync(WorkerServerSettings settings, int capacity, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return;
        var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var token = WorkerAuthentication.GetToken(settings);
        if (string.IsNullOrWhiteSpace(token))
            throw new WorkerStartupException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN.");
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"), $"api/v1/workers/{identity}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var capabilities = await CapabilityDiscovery.GetCachedAsync(cancellationToken);
            request.Content = JsonContent.Create(new WorkerRegistrationContract(2, identity,
                Environment.GetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME") is { Length: > 0 } name ? name : Environment.MachineName,
                ApplicationVersion.Display, $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}", capacity,
                capabilities, await InventoryDiscovery.GetAsync(cancellationToken: cancellationToken)));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadSafeServerErrorAsync(response, cancellationToken, token);
                throw new WorkerStartupException($"Codex Server registration failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{detail}");
            }
        }
        catch (WorkerStartupException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            throw new WorkerStartupException($"Codex Server registration failed: {ex.Message}", ex);
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task BootstrapAsync(WorkerServerSettings settings, int capacity, string bootstrapToken, CancellationToken cancellationToken)
    {
        // Copy/paste and CRLF secret files can include surrounding whitespace.
        // Keep internal characters intact: they must still fail authentication.
        bootstrapToken = bootstrapToken.Trim();
        if (bootstrapToken.Length == 0 || bootstrapToken.Any(char.IsWhiteSpace))
            throw new WorkerStartupException("Bootstrap token must be a single nonempty value. Copy only the token, without its description.");
        WorkerServerSettings.ValidateUrl(settings.Url);
        var identityPath = settings.IdentityFile ?? WorkerIdentity.DefaultPath;
        var identity = await WorkerIdentity.LoadOrCreateAsync(identityPath, cancellationToken);
        var workerToken = await WorkerAuthentication.LoadOrCreateTokenAsync(identityPath, cancellationToken);
        var urlPath = Path.GetFullPath(identityPath) + ".server";
        await File.WriteAllTextAsync(urlPath, settings.Url.TrimEnd('/') + Environment.NewLine, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(urlPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            var capabilities = await CapabilityDiscovery.GetCachedAsync(cancellationToken);
            var registration = new WorkerRegistrationContract(2, identity,
                Environment.GetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME") is { Length: > 0 } name ? name : Environment.MachineName,
                ApplicationVersion.Display, $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}", capacity, capabilities, await InventoryDiscovery.GetAsync(cancellationToken: cancellationToken));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"), "api/v1/workers/register"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bootstrapToken);
            request.Headers.Add("X-Codex-Worker-Token", workerToken);
            request.Content = JsonContent.Create(registration);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // A prior request may have committed registration before its response was lost.
                    try
                    {
                        await RegisterAsync(settings, capacity, cancellationToken);
                        return;
                    }
                    catch (WorkerStartupException)
                    {
                        throw new WorkerStartupException($"Codex Server bootstrap failed with HTTP 401 (Unauthorized). The bootstrap token is invalid, expired, or already used. Create a fresh registration token and retry. Local worker credentials were retained so a completed registration can be recovered safely.{SafeRequestContext(response, [bootstrapToken, workerToken])}");
                    }
                }
                var detail = await ReadSafeServerErrorAsync(response, cancellationToken, bootstrapToken, workerToken);
                throw new WorkerStartupException($"Codex Server bootstrap failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{detail}");
            }
        }
        catch (WorkerStartupException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            throw new WorkerStartupException($"Codex Server bootstrap failed: {ex.Message}", ex);
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    private static string[] RequestSecrets(HttpRequestMessage request) =>
        new[] { request.Headers.Authorization?.Parameter ?? string.Empty }
            .Concat(request.Headers.Where(header => header.Key.Contains("Token", StringComparison.OrdinalIgnoreCase))
                .SelectMany(header => header.Value)).ToArray();

    private static string SafeRequestContext(HttpResponseMessage response, string[] secrets)
    {
        var context = string.Empty;
        if (response.Headers.TryGetValues("X-Codex-Request-Id", out var identifiers))
        {
            var requestId = identifiers.FirstOrDefault();
            if (requestId is { Length: > 0 and <= 100 } && requestId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.'))
                context = $" Request ID: {FailureDiagnosticRedactor.Redact(requestId, secrets)}.";
        }
        return context;
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
        var token = WorkerAuthentication.GetToken(settings);
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN.");
        var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"), $"api/v1/workers/{identity}/heartbeat"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new WorkerHeartbeatContract(2, identity, ApplicationVersion.Display,
                lifecycleState, activeExecutions, capacity, capabilities ?? await CapabilityDiscovery.GetCachedAsync(cancellationToken), activeProjects,
                configurationSync?.SynchronizationStatus, configurationSync?.AppliedVersion,
                await InventoryDiscovery.GetAsync(cancellationToken: cancellationToken)));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server heartbeat failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
        }
        finally { if (httpClient is null) client.Dispose(); }
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
        var token = WorkerAuthentication.GetToken(settings);
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN.");
        var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"), $"api/v1/workers/{identity}/assignments/request"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new WorkerAssignmentRequestContract(identity, workerEnabled, availableCapacity, projectCapacities, integrationRecoveries));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server assignment request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
            return await response.Content.ReadFromJsonAsync<WorkerAssignmentResponseContract>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Codex Server returned an empty assignment response.");
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task<ServerManagedConfigurationContract> GetManagedConfigurationAsync(WorkerServerSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("Managed configuration requires Server mode.");
        var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, settings, $"api/v1/workers/{identity}/configuration");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server configuration request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
            return await response.Content.ReadFromJsonAsync<ServerManagedConfigurationContract>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Codex Server returned an empty managed configuration.");
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task<bool> ExecuteProvisioningCommandAsync(WorkerServerSettings settings, ProvisioningPolicy policy,
        CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCapabilityMutation = null)
    {
        if (!settings.Enabled) return false;
        var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = CreateClient();
        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{workerId}/provisioning/commands/request");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                using var acknowledgement = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                acknowledgement.EnsureSuccessStatusCode();
            }
            var result = await new NodeProvisioningCommandExecutor(InventoryDiscovery)
                .ExecuteAsync(command, permitted, cancellationToken, ReportAsync);
            // A terminal acknowledgement is safe to resend; execution itself is never retried.
            await ReportAsync(result, cancellationToken);
            return true;
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task<ProvisioningPlanContract?> RequestProvisioningPlanAsync(WorkerServerSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return null;
        var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = CreateClient();
        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{workerId}/provisioning/request");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Codex Server provisioning request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
            var plan = await response.Content.ReadFromJsonAsync<ProvisioningPlanContract>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Codex Server returned an empty provisioning plan.");
            if (plan.WorkerId != workerId || plan.State != "Accepted" || !Guid.TryParseExact(plan.Id, "N", out _))
                throw new InvalidDataException("Codex Server returned a provisioning plan with an invalid identity or lifecycle state.");
            return plan;
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task ReportProvisioningPlanAsync(WorkerServerSettings settings, string planId, ProvisioningWorkerReportContract report,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        try
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{report.WorkerId}/provisioning/{planId}/report");
            request.Content = JsonContent.Create(report);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Codex Server provisioning report failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task<CredentialDeliveryResponse?> RetrieveCredentialAsync(WorkerServerSettings settings, string credentialId,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("Credential delivery requires managed Server mode.");
        var token = Environment.GetEnvironmentVariable("CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Credential delivery requires CODEX_WORKER_CREDENTIAL_DELIVERY_TOKEN.");
        var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = CreateClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"), $"api/v1/workers/{workerId}/credentials/{Uri.EscapeDataString(credentialId)}"));
            request.Headers.Add("X-Worker-Credential-Token", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Codex Server credential retrieval failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
            var credential = await response.Content.ReadFromJsonAsync<CredentialDeliveryResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Codex Server returned an empty credential response.");
            if (credential.Id != credentialId || credential.Version < 1 || string.IsNullOrEmpty(credential.Secret))
                throw new InvalidDataException("Codex Server returned an invalid credential response.");
            return credential;
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    private HttpClient CreateClient() => httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, WorkerServerSettings settings, string path)
    {
        var token = WorkerAuthentication.GetToken(settings);
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN.");
        var request = new HttpRequestMessage(method, new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task ReportExecutionAsync(WorkerServerSettings settings, ExecutionHistoryEntry entry, string state,
        string? stage, long generation, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || entry.ServerExecutionId is null || entry.AssignmentId is null) return;
        var token = WorkerAuthentication.GetToken(settings);
        if (string.IsNullOrWhiteSpace(token)) throw new HttpRequestException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN to report execution state.");
        var workerId = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var final = state is "Completed" or "Failed";
            var report = new WorkerExecutionReportContract(workerId, entry.AssignmentId, entry.ExecutionId.ToString(), state,
                stage, entry.StartedAtUtc, final ? entry.CompletedAtUtc ?? DateTimeOffset.UtcNow : null,
                entry.DurationMilliseconds, Bound(entry.ValidationOutcome, 1000),
                state == "Completed" ? "passed" : null,
                state == "Failed" ? Bound(entry.State, 100) : null, entry.RecoveryState is "recoverable" or "integration-conflict",
                Bound(state == "Completed" ? entry.ImplementationSummary : entry.FailureReason ?? entry.ImplementationSummary, 1000), generation);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"),
                $"api/v1/workers/{workerId}/executions/{entry.ServerExecutionId}/report"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(report);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server execution report failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task<DateTimeOffset?> RenewExecutionLeaseAsync(WorkerServerSettings settings, ServerExecutionLeaseContract lease,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return null;
        var token = WorkerAuthentication.GetToken(settings);
        if (string.IsNullOrWhiteSpace(token)) throw new HttpRequestException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN to renew an execution lease.");
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.EffectiveUrl.TrimEnd('/') + "/"),
                $"api/v1/workers/{lease.WorkerId}/executions/{lease.ExecutionId}/lease/renew"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new ExecutionLeaseRenewalContract(lease.WorkerId, lease.Generation));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return null;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server execution lease renewal failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).{await ReadSafeServerErrorAsync(response, cancellationToken, RequestSecrets(request))}");
            var renewed = await response.Content.ReadFromJsonAsync<ServerExecutionLeaseContract>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Codex Server returned an empty lease renewal response.");
            if (renewed.Generation != lease.Generation || renewed.WorkerId != lease.WorkerId || renewed.ExecutionId != lease.ExecutionId || renewed.State != "Active")
                throw new InvalidDataException("Codex Server returned a lease renewal for a different ownership generation.");
            return renewed.ExpiresAtUtc;
        }
        finally { if (httpClient is null) client.Dispose(); }
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
                if (!_degraded) report?.Invoke($"Codex Server heartbeat connectivity degraded: {ex.Message}");
                _degraded = true;
            }
        } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_run is not null) try { await _run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await _client.HeartbeatAsync(settings, capacity, 0, Array.Empty<string>(), WorkerLifecycleStates.Stopped, CancellationToken.None,
            capabilities?.Invoke(), configurationSync?.Invoke()); }
        catch (Exception ex) { if (!_degraded) report?.Invoke($"Codex Server final heartbeat failed: {ex.Message}"); }
        _stop.Dispose();
    }
}
