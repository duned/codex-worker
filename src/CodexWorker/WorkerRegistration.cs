namespace CodexWorker;

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
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

public sealed record WorkerHeartbeatContract(int ContractVersion, string WorkerId, string WorkerVersion,
    string LifecycleState, int ActiveExecutions, int MaximumCapacity, IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> ActiveProjects);
public sealed record WorkerHeartbeatStatus(int ActiveExecutions, IReadOnlyList<string> Projects, string State);
public sealed record WorkerAssignmentRequestContract(string WorkerId, bool WorkerEnabled, int AvailableCapacity,
    IReadOnlyDictionary<string, int> ProjectCapacities);
public sealed record ServerProjectContract(string Id, string Name, string Repository, string DefaultBranch,
    string Description, IReadOnlyList<string> Requirements, long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ServerWorkReferenceContract(string Type, string Id, string? Url = null);
public sealed record WorkerAssignmentContract(string AssignmentId, string ServerExecutionId, ServerProjectContract Project,
    ServerWorkReferenceContract Work, string WorkerId, IReadOnlyDictionary<string, string> Metadata);
public sealed record WorkerAssignmentResponseContract(bool HasWork, WorkerAssignmentContract? Assignment);
public sealed record WorkerExecutionReportContract(string WorkerId, string AssignmentId, string WorkerExecutionId, string State,
    string? Stage = null, DateTimeOffset? StartedAtUtc = null, DateTimeOffset? CompletedAtUtc = null,
    long? DurationMilliseconds = null, string? ValidationResult = null, string? IntegrationResult = null,
    string? FailureClassification = null, bool Recoverable = false, string? Summary = null);

/// <summary>Loads or creates a stable, random worker identifier stored with restrictive permissions.</summary>
public static class WorkerIdentity
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-worker", "worker-id");

    public static async Task<string> LoadOrCreateAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Worker identity path must include a directory.");
        Directory.CreateDirectory(directory);
        try
        {
            var existing = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            if (Guid.TryParseExact(existing, "N", out _)) return existing;
            throw new InvalidDataException($"Worker identity file '{path}' does not contain a valid identity.");
        }
        catch (FileNotFoundException) { }

        var identity = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, identity + Environment.NewLine, cancellationToken);
            RestrictFile(temp);
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                var winner = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
                if (!Guid.TryParseExact(winner, "N", out _)) throw new InvalidDataException($"Worker identity file '{path}' does not contain a valid identity.");
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

public sealed class WorkerRegistrationClient(HttpClient? httpClient = null)
{
    public async Task RegisterAsync(WorkerServerSettings settings, int capacity, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return;
        var token = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new WorkerStartupException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN.");
        var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(settings.Url.TrimEnd('/') + "/"), $"api/v1/workers/{identity}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new WorkerRegistrationContract(1, identity,
                Environment.GetEnvironmentVariable("CODEX_WORKER_DISPLAY_NAME") is { Length: > 0 } name ? name : Environment.MachineName,
                ApplicationVersion.Display, $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}", capacity,
                ["codex-cli", "github-issues", "git"]));
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new WorkerStartupException($"Codex Server registration failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        catch (WorkerStartupException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            throw new WorkerStartupException($"Codex Server registration failed: {ex.Message}", ex);
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task HeartbeatAsync(WorkerServerSettings settings, int capacity, int activeExecutions,
        IReadOnlyList<string> activeProjects, string lifecycleState, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return;
        var token = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN.");
        var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.Url.TrimEnd('/') + "/"), $"api/v1/workers/{identity}/heartbeat"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new WorkerHeartbeatContract(1, identity, ApplicationVersion.Display,
                lifecycleState, activeExecutions, capacity, ["codex-cli", "github-issues", "git"], activeProjects));
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server heartbeat failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task<WorkerAssignmentResponseContract> RequestAssignmentAsync(WorkerServerSettings settings, bool workerEnabled, int availableCapacity,
        IReadOnlyDictionary<string, int> projectCapacities, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new InvalidOperationException("Assignment requests require managed Server mode.");
        if (availableCapacity is < 0 or > 8 || projectCapacities is null || projectCapacities.Any(p => p.Value is < 0 or > 8))
            throw new ArgumentOutOfRangeException(nameof(availableCapacity), "Assignment capacity must be between zero and eight.");
        if (!workerEnabled || availableCapacity == 0 || projectCapacities.Count == 0 || projectCapacities.All(p => p.Value == 0))
            return new(false, null);
        var token = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new WorkerStartupException("Managed mode requires CODEX_SERVER_REGISTRATION_TOKEN.");
        var identity = await WorkerIdentity.LoadOrCreateAsync(settings.IdentityFile ?? WorkerIdentity.DefaultPath, cancellationToken);
        var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(settings.Url.TrimEnd('/') + "/"), $"api/v1/workers/{identity}/assignments/request"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new WorkerAssignmentRequestContract(identity, workerEnabled, availableCapacity, projectCapacities));
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server assignment request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            return await response.Content.ReadFromJsonAsync<WorkerAssignmentResponseContract>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Codex Server returned an empty assignment response.");
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    public async Task ReportExecutionAsync(WorkerServerSettings settings, ExecutionHistoryEntry entry, string state,
        string? stage, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || entry.ServerExecutionId is null || entry.AssignmentId is null) return;
        var token = Environment.GetEnvironmentVariable("CODEX_SERVER_REGISTRATION_TOKEN");
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
                state == "Failed" ? Bound(entry.State, 100) : null, entry.RecoveryState == "recoverable",
                Bound(state == "Completed" ? entry.ImplementationSummary : entry.FailureReason ?? entry.ImplementationSummary, 1000));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.Url.TrimEnd('/') + "/"),
                $"api/v1/workers/{workerId}/executions/{entry.ServerExecutionId}/report"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(report);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Codex Server execution report failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        finally { if (httpClient is null) client.Dispose(); }
    }

    private static string? Bound(string? value, int limit) => value is { Length: > 0 } ? value[..Math.Min(value.Length, limit)] : null;
}

/// <summary>Best-effort runtime reporting. Connectivity loss is reported once per degraded period and never stops execution.</summary>
public sealed class WorkerHeartbeatLoop(WorkerServerSettings settings, int capacity,
    Func<WorkerHeartbeatStatus> snapshot, Action<string>? report = null)
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private readonly WorkerRegistrationClient _client = new();
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
                await _client.HeartbeatAsync(settings, capacity, current.ActiveExecutions, current.Projects, current.State, _stop.Token);
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
        try { await _client.HeartbeatAsync(settings, capacity, 0, Array.Empty<string>(), "stopped", CancellationToken.None); }
        catch (Exception ex) { if (!_degraded) report?.Invoke($"Codex Server final heartbeat failed: {ex.Message}"); }
        _stop.Dispose();
    }
}
