namespace CodexServer;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed record AutomaticIssueDiscovery(bool Enabled = false, int IntervalSeconds = 300,
    int PageSize = 25, int DeadlineSeconds = 120);

public static class AutomaticIssueDiscoveryValidation
{
    public static string? Error(AutomaticIssueDiscovery value) =>
        value.IntervalSeconds is < 30 or > 86400 || value.PageSize is < 1 or > 100 ||
        value.DeadlineSeconds is < 10 or > 120
            ? "automaticDiscovery requires intervalSeconds 30 to 86400, pageSize 1 to 100, and deadlineSeconds 10 to 120."
            : null;
}

/// <summary>Server-owned polling; discovery and authoritative enqueue retain their existing ownership.</summary>
public sealed class AutomaticIssueDiscoveryService(IRegistryStore registry, ServerGitHubAdministrationService github,
    ILogger<AutomaticIssueDiscoveryService> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly Dictionary<string, Progress> _progress = new(StringComparer.Ordinal);
    private sealed record Progress(long Revision, DateTimeOffset ProjectCreatedAtUtc, DateTimeOffset NextAtUtc, string? Cursor);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _clock);
        do
        {
            try { await RunDueCyclesAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Never log provider exception messages or payloads.
                ServerOperationalDiagnostics.Write(logger, LogLevel.Error, "automatic-discovery", "registry-unavailable");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Runs at most one bounded page per due project. Concurrent invocations do no work.</summary>
    public async Task RunDueCyclesAsync(CancellationToken cancellationToken = default)
    {
        if (!await _cycleGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var projects = await registry.GetProjectsAsync(cancellationToken);
            var enabled = projects.Where(project => project.Enabled && project.AutomaticDiscovery?.Enabled == true).ToArray();
            foreach (var id in _progress.Keys.Except(enabled.Select(project => project.Id)).ToArray()) _progress.Remove(id);
            foreach (var project in enabled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var settings = project.AutomaticDiscovery;
                if (settings is null) continue;
                _progress.TryGetValue(project.Id, out var progress);
                var sameProject = progress is not null && progress.Revision == project.Revision && progress.ProjectCreatedAtUtc == project.CreatedAtUtc;
                if (sameProject && progress is not null && progress.NextAtUtc > _clock.GetUtcNow()) continue;
                var cursor = sameProject ? progress?.Cursor : null;
                await RunProjectCycleAsync(project, settings, cursor, cancellationToken);
            }
        }
        finally { _cycleGate.Release(); }
    }

    private async Task RunProjectCycleAsync(CentralProject project, AutomaticIssueDiscovery settings,
        string? cursor, CancellationToken cancellationToken)
    {
        var cycleId = Guid.NewGuid().ToString("N");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CycleId"] = cycleId, ["ProjectId"] = project.Id });
        var started = _clock.GetTimestamp();
        var discovered = 0;
        var enqueued = 0;
        var skipped = 0;
        var duplicates = 0;
        var outcome = "complete";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(settings.DeadlineSeconds), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var page = await github.DiscoverIssuesAsync(project.Id, new(settings.PageSize, cursor), linked.Token);
            if (page is null) { outcome = "project-removed"; return; }
            discovered = page.Candidates.Count;
            cursor = page.NextCursor;
            foreach (var candidate in page.Candidates)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (candidate.Classification != "eligible")
                {
                    skipped++;
                    ServerOperationalDiagnostics.Write(logger, LogLevel.Debug, "automatic-discovery", "issue-ineligible",
                        project.Id, candidate.WorkReference, reasons: candidate.Reasons);
                    continue;
                }
                try
                {
                    var created = await github.EnqueueIssueAsync(project, candidate.WorkReference, linked.Token);
                    enqueued++;
                    ServerOperationalDiagnostics.Write(logger, LogLevel.Information, "automatic-discovery", "enqueued",
                        project.Id, candidate.WorkReference, created.Id);
                }
                catch (ExecutionRequestConflictException)
                {
                    duplicates++;
                    ServerOperationalDiagnostics.Write(logger, LogLevel.Debug, "automatic-discovery", "previously-enqueued",
                        project.Id, candidate.WorkReference);
                }
                catch (Exception exception) when (exception is ManagedIssueIneligibleException or GitHubIssueNotFoundException)
                {
                    skipped++;
                    ServerOperationalDiagnostics.Write(logger, LogLevel.Debug, "automatic-discovery", "eligibility-changed",
                        project.Id, candidate.WorkReference, reasons: exception is ManagedIssueIneligibleException ineligible
                            ? ineligible.Issue.EligibilityReasons : ["Issue is missing or no longer readable."]);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "shutdown";
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { outcome = "deadline-exceeded"; }
        catch (GitHubReadUnavailableException exception)
        {
            // Reset invalid cursors on the next interval, never retry immediately.
            if (exception.Code == "invalid-response") cursor = null;
            outcome = "github-read-unavailable";
            ServerOperationalDiagnostics.Write(logger, LogLevel.Error, "automatic-discovery",
                exception.Code is "authentication-failed" or "rate-limited" or "invalid-response" or "query-timeout" ? exception.Code : outcome,
                project.Id);
        }
        catch (Exception exception) when (exception is ProjectDisabledException or ProjectRevisionConflictException or KeyNotFoundException)
        { outcome = "project-changed"; cursor = null; }
        catch (Exception)
        {
            outcome = "infrastructure-failure";
            ServerOperationalDiagnostics.Write(logger, LogLevel.Error, "automatic-discovery", outcome, project.Id);
        }
        finally
        {
            _progress[project.Id] = new(project.Revision, project.CreatedAtUtc, _clock.GetUtcNow().AddSeconds(settings.IntervalSeconds), cursor);
            logger.Log(enqueued > 0 || outcome != "complete" ? LogLevel.Information : LogLevel.Debug,
                new EventId(2203, "AutomaticDiscoveryCycle"),
                "Server automatic discovery cycle {CycleId}; project {ProjectId}; revision {Revision}; discovered {Discovered}; enqueued {Enqueued}; skipped {Skipped}; duplicates {Duplicates}; duration ms {DurationMs}; outcome {Outcome}",
                cycleId, project.Id, project.Revision, discovered, enqueued, skipped, duplicates,
                _clock.GetElapsedTime(started).TotalMilliseconds, outcome);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _cycleGate.Dispose();
    }
}
