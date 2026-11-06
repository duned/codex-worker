namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

internal sealed class StandaloneReportingClient : IDisposable
{
    private readonly HttpClient client;
    private readonly bool ownsClient;
    internal StandaloneReportingClient(HttpClient? client = null)
    {
        ownsClient = client is null;
        this.client = client ?? new HttpClient(WorkerServerHttpTransport.CreateHandler());
    }
    internal async Task<ObserverRegistration> PairAsync(StandaloneReportingCredential local, string authorization, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(authorization.Length == 0 ? HttpMethod.Get : HttpMethod.Post,
            local.Server + (authorization.Length == 0 ? $"/api/v1/observers/{local.ObserverId}/registration" : "/api/v1/observers/pairing/redeem"));
        request.Headers.Authorization = new("Bearer", authorization.Length == 0 ? local.Credential : authorization);
        if (authorization.Length != 0)
        {
            request.Headers.Add("X-Codex-Observer-Token", local.Credential);
            request.Content = JsonContent.Create(new ObserverPairingRequest(1, ObserverProtocol.Mode, local.ObserverId, local.Server));
        }
        using var response = await WorkerServerHttpTransport.SendAsync(client, request, TimeSpan.FromSeconds(5), ct);
        if (!response.IsSuccessStatusCode) throw new WorkerStartupException("Observer authorization rejected. Retain pending state; use the same Server and empty hidden input to recover an uncertain redemption. Revoked identities require disconnect and a new pairing.");
        var accepted = await response.Content.ReadFromJsonAsync<ObserverRegistration>(cancellationToken: ct);
        if (accepted is null || accepted.ContractVersion != 1 || accepted.ObserverId != local.ObserverId || accepted.Server != local.Server ||
            accepted.Mode != ObserverProtocol.Mode || accepted.PairingState != "accepted" || accepted.State is not ("awaiting-first-report" or "reporting") ||
            !ObserverProtocol.Timestamp(accepted.AcceptedAtUtc))
            throw new WorkerStartupException("Invalid observer acknowledgement. Retain pending state and recover against the same Server.");
        return accepted;
    }
    internal async Task<string> ReportAsync(StandaloneReportingCredential local, ObserverStatusSnapshot snapshot, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, local.Server + $"/api/v1/observers/{local.ObserverId}/snapshots");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", local.Credential);
        request.Content = JsonContent.Create(snapshot);
        using var response = await WorkerServerHttpTransport.SendAsync(client, request, TimeSpan.FromSeconds(5), ct);
        return await ReportingResultAsync(response, ct);
    }
    public void Dispose() { if (ownsClient) client.Dispose(); }

    internal async Task<string> ReportEventAsync(StandaloneReportingCredential local, ObserverExecutionEvent report, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, local.Server + $"/api/v1/observers/{local.ObserverId}/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", local.Credential);
        request.Content = JsonContent.Create(report);
        using var response = await WorkerServerHttpTransport.SendAsync(client, request, TimeSpan.FromSeconds(5), ct);
        return await ReportingResultAsync(response, ct);
    }

    internal Task<bool> ExecuteBranchMaintenanceAsync(StandaloneReportingCredential local, WorkerRuntimeReadModel runtime, CancellationToken ct) =>
        WorkerRegistrationClient.ExecuteMaintenanceExchangeAsync(local.ObserverId, local.Server + "/observers/" + local.ObserverId,
            runtime.ObserverHistory, runtime, suffix =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    local.Server + $"/api/v1/workers/{local.ObserverId}/maintenance/executions/{suffix}");
                request.Headers.Authorization = new("Bearer", local.Credential);
                return request;
            }, (request, token) => WorkerServerHttpTransport.SendAsync(client, request, TimeSpan.FromSeconds(5), token),
            (command, authorize, token) => runtime.CompletedBranchMaintenance.RunManagedAsync(command.Request, authorize, token, standalone: true), ct);

    private static async Task<string> ReportingResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "unauthorized",
                HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge => "refused",
                _ => "unavailable"
            };
        // Do not consume unbounded or unrecognized acknowledgements, or mark an uncertain delivery complete.
        var bytes = new byte[1025];
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (count == 0) break;
            length += count;
        }
        if (length > 1024) return "rejected";
        using var acknowledgement = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0, length));
        return acknowledgement.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
            acknowledgement.RootElement.TryGetProperty("state", out var state) && state.ValueKind == System.Text.Json.JsonValueKind.String &&
            state.GetString() is "accepted" or "duplicate" ? "reporting" : "rejected";
    }
}
