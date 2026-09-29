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
}
