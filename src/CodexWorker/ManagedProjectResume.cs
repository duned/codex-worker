namespace CodexWorker;

using CodexProvisioning;
using System.Net;
using System.Net.Http.Json;

public sealed partial class WorkerRegistrationClient
{
    /// <summary>Claims one pending operator request to lift a project pause and reports the local outcome once.</summary>
    internal async Task<bool> ExecuteProjectResumeAsync(WorkerServerSettings settings,
        Func<string, string> resume, CancellationToken ct)
    {
        var workerId = await LoadRegisteredIdentityAsync(settings, ct);
        var path = $"api/v1/workers/{workerId}/projects/resume";
        using var request = CreateAuthorizedRequest(HttpMethod.Post, settings, path + "/request");
        using var response = await SendAsync(request, ct);
        // Older Servers have no such protocol and grant no resume authority.
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        var operation = await response.Content.ReadFromJsonAsync<ProjectResumeOperation>(cancellationToken: ct)
            ?? throw new InvalidDataException("Missing project resume operation.");
        if (!Guid.TryParseExact(operation.Id, "N", out _) || operation.Status != "running" ||
            !ProjectResumeProtocol.ValidProjectId(operation.ProjectId) || operation.DeadlineUtc == default ||
            operation.DeadlineUtc > DateTimeOffset.UtcNow.AddSeconds(ProjectResumeProtocol.TimeoutSeconds + 5).Add(ManagedClock.Tolerance))
            throw new InvalidDataException("Invalid project resume operation.");
        var diagnostic = operation.DeadlineUtc > DateTimeOffset.UtcNow ? resume(operation.ProjectId) : "delivery-unavailable";
        using var report = CreateAuthorizedRequest(HttpMethod.Post, settings, path + "/report");
        report.Content = JsonContent.Create(new ProjectResumeReport(operation.Id, diagnostic));
        using var acknowledgement = await SendAsync(report, ct);
        if (acknowledgement.StatusCode != HttpStatusCode.Conflict) acknowledgement.EnsureSuccessStatusCode();
        return true;
    }
}
