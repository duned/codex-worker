namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

public sealed partial class WorkerRegistrationClient
{
    private sealed record LocalDeliveryAuthorization(string Endpoint, string OperationId, string Token)
    {
        public override string ToString() => "[protected delivery authorization]";
    }

    private static async Task<LocalDeliveryAuthorization?> LoadDeliveryAsync(string identityPath, CancellationToken ct)
    {
        var path = identityPath + ".delivery";
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("Delivery authorization exceeds supported bounds.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var local = JsonSerializer.Deserialize<LocalDeliveryAuthorization>(await File.ReadAllTextAsync(path, ct));
        if (local is null || !Guid.TryParseExact(local.OperationId, "N", out _) ||
            !WorkerEnrollmentProtocol.ValidToken(local.Token) || local.Token.Length > 512)
            throw new InvalidDataException("Local delivery authorization is invalid. Repair it from the Worker dashboard.");
        return local;
    }

    private static async Task<string?> LoadDeliveryTokenAsync(string identityPath, string endpoint, CancellationToken ct)
    {
        var local = await LoadDeliveryAsync(identityPath, ct);
        return local?.Endpoint == endpoint.TrimEnd('/') ? local.Token : null;
    }

    internal async Task<bool> SynchronizeDeliveryAuthorizationAsync(string identityPath, string workerId,
        string endpoint, string apiToken, CancellationToken ct)
    {
        try
        {
            // Serialize publication and confirmation across local processes. Never rotate on an HTTP retry.
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var gate = new FileStream(identityPath + ".delivery-lock", options);
            var baseUrl = endpoint.TrimEnd('/') + $"/api/v1/workers/{workerId}/credential-access/";
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "request");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            using var response = await SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NoContent) return false;
            if (!response.IsSuccessStatusCode) return false;
            var operation = await response.Content.ReadFromJsonAsync<WorkerDeliveryOperation>(cancellationToken: ct);
            if (operation is null || !Guid.TryParseExact(operation.Id, "N", out _)) return false;
            LocalDeliveryAuthorization? local;
            try { local = await LoadDeliveryAsync(identityPath, ct); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { local = null; }
            if (local?.Endpoint != endpoint.TrimEnd('/') || local.OperationId != operation.Id)
            {
                local = new(endpoint.TrimEnd('/'), operation.Id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                await WorkerRegistrationFile.ReplaceAsync(identityPath + ".delivery", JsonSerializer.Serialize(local), ct);
            }
            // Read the published file before acknowledgement; errors never activate the Server verifier.
            var persisted = await LoadDeliveryAsync(identityPath, ct);
            if (persisted != local) return false;
            using var confirm = new HttpRequestMessage(HttpMethod.Post, baseUrl + "confirm");
            confirm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            confirm.Content = JsonContent.Create(new WorkerDeliveryConfirmation(operation.Id, new(local.Token)));
            using var confirmed = await SendAsync(confirm, ct);
            return confirmed.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException)
        {
            // Best effort control exchange: retain the published candidate and retry on the next managed poll.
            // Server status expires to failed; no external response body or local secret enters diagnostics.
            return false;
        }
    }
}
