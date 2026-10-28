namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Json;

public sealed partial class WorkerRegistrationClient
{
    internal async Task<bool> ExecuteTelegramTestAsync(WorkerServerSettings settings,
        ManagedConfigurationSynchronizer synchronizer, TelegramNotifier notifier, CancellationToken ct)
    {
        var workerId = await LoadRegisteredIdentityAsync(settings, ct);
        var path = $"api/v1/workers/{workerId}/telegram/test";
        using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, path + "/request");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return false;
        response.EnsureSuccessStatusCode();
        var operation = await response.Content.ReadFromJsonAsync<TelegramTestOperation>(cancellationToken: ct)
            ?? throw new InvalidDataException("Missing Telegram test operation.");
        if (!Guid.TryParseExact(operation.Id, "N", out _) || operation.Status != "running" ||
            operation.Version is not { Length: > 0 and <= 128 } || operation.Version.Any(char.IsControl) ||
            operation.DeadlineUtc == default || operation.DeadlineUtc > DateTimeOffset.UtcNow.AddSeconds(60))
            throw new InvalidDataException("Invalid Telegram test operation.");
        var diagnostic = "configuration-changed";
        var remaining = operation.DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(remaining);
            try
            {
                var current = await GetManagedConfigurationAsync(settings, deadline.Token);
                if (synchronizer.Status.SynchronizationStatus == "synchronized" &&
                    synchronizer.Status.AppliedVersion == operation.Version && current.Version == operation.Version &&
                    synchronizer.AppliedTelegram is { Enabled: true })
                    diagnostic = await notifier.TestAsync(deadline.Token);
            }
            catch (Exception error) when (!ct.IsCancellationRequested && (error is HttpRequestException or OperationCanceledException or InvalidDataException))
            {
                diagnostic = "delivery-unavailable";
            }
        }
        using var report = CreateAuthorizedRequest(HttpMethod.Post, settings, path + "/report");
        report.Content = JsonContent.Create(new TelegramTestReport(operation.Id, diagnostic));
        using var acknowledgement = await SendAsync(report, ct);
        if (acknowledgement.StatusCode != HttpStatusCode.Conflict) acknowledgement.EnsureSuccessStatusCode();
        return true;
    }
}
