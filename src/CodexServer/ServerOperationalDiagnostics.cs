namespace CodexServer;

using CodexProvisioning;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

/// <summary>Bounded control-plane events; never accepts payloads, summaries or exception messages.</summary>
internal static class ServerOperationalDiagnostics
{
    internal static void Write(ILogger logger, LogLevel level, string operation, string reasonCode,
        string? projectId = null, WorkReference? work = null, string? executionId = null,
        string? workerId = null, string? assignmentId = null, long? leaseGeneration = null,
        string? workerExecutionId = null, IReadOnlyList<string>? reasons = null)
    {
        if (!logger.IsEnabled(level)) return;
        logger.Log(level, new EventId(2201, "ServerOperationalDecision"),
            "Server {Operation}: {ReasonCode}; project {ProjectId}; work {WorkReference}; execution {ExecutionId}; worker {WorkerId}; assignment {AssignmentId}; lease generation {LeaseGeneration}; worker execution {WorkerExecutionId}; reasons {Reasons}",
            operation, reasonCode, Safe(projectId), Safe(work?.Url ?? (work is null ? null : work.Type + ":" + work.Id)), Safe(executionId), Safe(workerId),
            Safe(assignmentId), leaseGeneration, Safe(workerExecutionId),
            reasons is null ? null : string.Join("; ", reasons.Take(10).Select(Safe)));
    }

    // Defense in depth for identifiers from rejected requests and historical records. A URL is
    // useful only without userinfo/query/fragment; never retain authentication from an input URL.
    private static string? Safe(string? value)
    {
        if (value is null) return null;
        if (value.Length > 200 || value.Any(char.IsControl) ||
            Regex.IsMatch(value, "(?i)(\\bgh[pousr]_|\\bgithub_pat_|\\bsk-|-----BEGIN|\\bbearer\\s|(?:token|password|secret|credential|api[_-]?key)\\s*[:=])"))
            return "[redacted]";
        if (value.Contains("://", StringComparison.Ordinal) &&
            (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
             uri.Host != "github.com" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            return "[redacted]";
        return SecretSanitizer.Sanitize(value, 200);
    }

    internal static async Task<T> RunAsync<T>(ILogger logger, string operation, Func<Task<T>> action,
        CancellationToken cancellationToken, string? projectId = null, string? executionId = null,
        string? workerId = null, string? assignmentId = null, long? generation = null, string? workerExecutionId = null, WorkReference? work = null)
    {
        try { return await action(); }
        // Ownership and deduplication failures are logged where persisted correlation is available.
        catch (Exception exception) when (exception is not (ExecutionRequestOwnershipException or ExecutionRequestConflictException))
        {
            var reason = exception switch
            {
                OperationCanceledException when cancellationToken.IsCancellationRequested => "request-cancelled",
                ProjectDisabledException => "project-disabled",
                ManagedIssueIneligibleException => "issue-ineligible",
                GitHubIssueNotFoundException => "issue-not-found",
                KeyNotFoundException => "not-found",
                InvalidDataException => "invalid-request",
                ExecutionRequestCancellationException => "not-queued",
                ProjectRevisionConflictException => "revision-conflict",
                GitHubReadUnavailableException => "github-read-unavailable",
                _ => "infrastructure-failure"
            };
            Write(logger, reason == "request-cancelled" ? LogLevel.Debug :
                reason is "infrastructure-failure" or "github-read-unavailable" ? LogLevel.Error : LogLevel.Information,
                operation, reason, projectId, work, executionId: executionId, workerId: workerId,
                assignmentId: assignmentId, leaseGeneration: generation, workerExecutionId: workerExecutionId);
            // Exception objects and messages may contain credentials or remote process output.
            throw;
        }
    }
}
