namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Json;

public sealed partial class WorkerRegistrationClient
{
    internal async Task<bool> ExecuteMaintenanceCommandAsync(WorkerServerSettings settings, ExecutionHistoryStore history,
        WorkerRuntimeReadModel runtime, CancellationToken ct)
    {
        var worker = await LoadRegisteredIdentityAsync(settings, ct);
        var endpoint = WorkerAuthentication.GetConnection(settings).Endpoint;
        var receipt = await history.ReadPendingMaintenanceAsync(endpoint, ct);
        if (receipt is not null)
        {
            if (receipt.Command.Request.WorkerId != worker) throw new InvalidDataException("Retained maintenance belongs to another Worker.");
            // A started receipt without a terminal outcome means process interruption. Never replay mutations.
            receipt = receipt with { Report = receipt.Report ?? new("failed", "interrupted-inspect-before-retry", []) };
        }
        else
        {
            using var message = CreateAuthorizedRequest(HttpMethod.Post, settings, $"api/v1/workers/{worker}/maintenance/executions/request");
            using var response = await SendAsync(message, ct);
            // Old Servers have no protocol. They grant no maintenance authority.
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return false;
            response.EnsureSuccessStatusCode();
            var command = await response.Content.ReadFromJsonAsync<ExecutionMaintenanceCommand>(cancellationToken: ct)
                ?? throw new InvalidDataException("Empty maintenance command.");
            if (!ExecutionMaintenanceProtocol.Valid(command.Request) || command.Request.WorkerId != worker || command.Status is not ("running" or "uncertain") ||
                command.DeadlineUtc is null || command.DeadlineUtc > DateTimeOffset.UtcNow.AddSeconds(command.Request.TimeoutSeconds + 5))
                throw new InvalidDataException("Invalid maintenance identity or deadline.");
            var retained = await history.ReadMaintenanceReceiptAsync(endpoint, command.Request.OperationId, ct);
            if (retained is not null)
            {
                if (retained.Command.Request != command.Request) throw new InvalidDataException("Maintenance operation identity changed scope.");
                receipt = retained with { Report = retained.Report ?? new("failed", "interrupted-inspect-before-retry", []) };
            }
            else
            {
                receipt = new(command);
                await history.SaveMaintenanceReceiptAsync(endpoint, receipt, false, ct);
                if (command.Status == "uncertain" || command.DeadlineUtc <= DateTimeOffset.UtcNow)
                {
                    receipt = receipt with { Report = new("failed", command.Status == "uncertain"
                        ? "dispatch-unconfirmed-inspect-before-retry" : "timeout-inspect-before-retry", []) };
                }
                else
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var remaining = command.DeadlineUtc.Value - DateTimeOffset.UtcNow;
                    deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                    async Task<bool> AuthorizeAsync(CancellationToken token)
                    {
                        using var authorization = CreateAuthorizedRequest(HttpMethod.Post, settings,
                            $"api/v1/workers/{worker}/maintenance/executions/{command.Request.OperationId}/authorize");
                        using var confirmation = await SendAsync(authorization, token);
                        if (!confirmation.IsSuccessStatusCode) return false;
                        var current = await confirmation.Content.ReadFromJsonAsync<ExecutionMaintenanceCommand>(cancellationToken: token);
                        return current?.Request == command.Request && current.Status == "running" && current.DeadlineUtc > DateTimeOffset.UtcNow;
                    }
                    try
                    {
                        receipt = receipt with { Report = await new ManagedExecutionMaintenance(history, runtime).ExecuteAsync(command, AuthorizeAsync,
                            (entry, token) => ReportExecutionAsync(settings, entry, entry.State == "Completed" ? "Completed" : "Failed", null,
                                command.Request.Generation ?? 0, token), deadline.Token) };
                    }
                    catch (OperationCanceledException)
                    {
                        receipt = receipt with { Report = new("failed", ct.IsCancellationRequested ? "interrupted-inspect-before-retry" : "timeout-inspect-before-retry", []) };
                    }
                    catch (Exception ex) when (ex is WorkerInfrastructureException or HttpRequestException or InvalidOperationException or System.IO.IOException)
                    {
                        receipt = receipt with { Report = new("failed", "maintenance-failed-inspect-before-retry", []) };
                    }
                }
            }
        }
        // Durable recording is intentionally uncancellable after effects. A reconnect resends only this report.
        await history.SaveMaintenanceReceiptAsync(endpoint, receipt, false, CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        using var reportMessage = CreateAuthorizedRequest(HttpMethod.Post, settings,
            $"api/v1/workers/{worker}/maintenance/executions/{receipt.Command.Request.OperationId}/report");
        reportMessage.Content = JsonContent.Create(receipt.Report);
        using var acknowledgement = await SendAsync(reportMessage, ct);
        if (acknowledgement.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            // Surface a permanent disposition in the local audit, rather than retrying invisibly forever.
            receipt = receipt with { Report = new("refused", acknowledgement.StatusCode == HttpStatusCode.NotFound
                ? "maintenance-server-record-missing" : "maintenance-report-conflict", receipt.Report?.Observations ?? []) };
            await history.SaveMaintenanceReceiptAsync(endpoint, receipt, true, CancellationToken.None);
            runtime.Events.Publish("maintenance.report.rejected", $"Maintenance {receipt.Command.Request.OperationId} requires Server reconciliation.");
            return true;
        }
        acknowledgement.EnsureSuccessStatusCode();
        await history.SaveMaintenanceReceiptAsync(endpoint, receipt, true, CancellationToken.None);
        runtime.Events.Publish("maintenance.completed", $"Maintenance {receipt.Command.Request.OperationId}: {receipt.Report?.Reason}.");
        return true;
    }
}
