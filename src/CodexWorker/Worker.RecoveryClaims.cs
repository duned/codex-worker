using System.Collections.Concurrent;
using System.Diagnostics;

namespace CodexWorker;

public sealed partial class Worker
{
    private async Task<Task<IssueProcessingResult?>?> ClaimCodexRecoveryAsync(WorkerAssignmentContract? assignment, CancellationToken ct)
    {
        if (history is null) return null;
        var entries = await history.ReadAllAsync(ct);
        var candidates = entries.Where(entry => entry.Project == config.Project.Name && entry.Repository == config.Project.Repository &&
            entry.RecoveryState == "codex-interrupted" && !entries.Any(other => other.Project == entry.Project &&
                other.Repository == entry.Repository && other.IssueNumber == entry.IssueNumber &&
                (other.AttemptNumber > entry.AttemptNumber || other.State == "Completed")))
            .OrderBy(entry => entry.StartedAtUtc);
        foreach (var source in candidates)
        {
            if (source.CodexRecovery?.RetryAfterUtc > _clock.GetUtcNow() ||
                assignment is null && engineReady?.Invoke(source.Engine) == false) continue;
            if (assignment is not null && (!assignment.Metadata.TryGetValue("codexRecoveryExecutionId", out var sourceId) ||
                sourceId != source.ExecutionId.ToString() || !assignment.Metadata.TryGetValue("originalServerExecutionId", out var serverId) ||
                serverId != source.ServerExecutionId)) continue;
            if (assignment is null && source.ServerExecutionId is not null) continue; // Cached managed history grants no authority.
            if (!_activeIssues.TryAdd(source.IssueNumber, 0)) continue;
            var handedOff = false;
            try
            {
                var snapshot = source.CodexRecovery;
                var originalBody = source.OriginalIssueBody;
                var metadataError = CodexRecoveryCoordinator.ValidateMetadata(source, entries, config);
                if (metadataError is not null || snapshot is null || originalBody is null)
                {
                    await history.UpdateRecoveryAsync(source.ExecutionId, "codex-recovery-inspection-required", ct,
                        "Codex recovery requires inspection: " + (metadataError ?? "workspace snapshot missing"));
                    _output.Warning($"Codex recovery requires inspection · execution {source.ExecutionId} · {metadataError ?? "workspace snapshot missing"}.");
                    continue;
                }
                string? rejection;
                await _repositoryGate.WaitAsync(ct);
                try
                {
                    rejection = await git.ValidateCodexRecoveryAsync(source, ct);
                    if (rejection is null && assignment is null)
                    {
                        var inspection = await git.InspectInterruptedExecutionAsync(source, ct);
                        rejection = inspection.Blocker ?? (!inspection.NotIntegrated ? "remote-integration-ambiguous" : null);
                    }
                }
                finally { _repositoryGate.Release(); }
                if (rejection is not null)
                {
                    await history.UpdateRecoveryAsync(source.ExecutionId, "codex-recovery-inspection-required", ct,
                        "Codex recovery requires inspection: " + FailureDiagnosticRedactor.Redact(rejection, config.Environment.Variables.Values.ToArray()));
                    _output.Warning($"Codex recovery requires inspection · execution {source.ExecutionId} · {FailureDiagnosticRedactor.Redact(rejection, config.Environment.Variables.Values.ToArray())}");
                    continue;
                }
                GitHubIssue? issue;
                if (source.ValidationOutcome == "legacy: pending validation")
                {
                    try { issue = await github.GetIssueAsync(source.IssueNumber, ct); }
                    catch (WorkerInfrastructureException) when (!ct.IsCancellationRequested)
                    {
                        await history.RecordLegacyReviewAsync(source.ExecutionId,
                            "fresh Issue details are unavailable; legacy continuation cannot verify the original title and intent",
                            _clock.GetUtcNow(), ct);
                        continue;
                    }
                    if (issue is null || issue.Title != source.IssueTitle || issue.Body != originalBody)
                    {
                        await history.RecordLegacyReviewAsync(source.ExecutionId,
                            "original Issue is closed, unavailable, or its title/body changed", _clock.GetUtcNow(), ct);
                        continue;
                    }
                }
                else issue = new GitHubIssue(source.IssueNumber, source.IssueTitle, originalBody, source.StartedAtUtc,
                    ParentIssueNumber: source.ParentIssueNumber, ParentIssueCaptured: source.ParentIssueCaptured, ParentProvenance: source.ParentProvenance);
                if (!await github.IsIssueOpenAsync(source.IssueNumber, ct))
                {
                    if (source.ValidationOutcome == "legacy: pending validation")
                        await history.RecordLegacyReviewAsync(source.ExecutionId,
                            "original Issue is closed; legacy continuation requires an open Issue with unchanged intent",
                            _clock.GetUtcNow(), ct);
                    continue;
                }
                if (assignment is null)
                {
                    var live = await github.GetIssueAsync(source.IssueNumber, ct);
                    var state = await github.ReadIssueStateAsync(source.IssueNumber, ct);
                    bool Has(string label) => state.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);
                    if (live is null || live.Title != source.IssueTitle || live.Body != originalBody || !state.IsOpen ||
                        Has(config.GitHub.DoneLabel) || !Has(config.GitHub.ReadyLabel) &&
                        (!Has(config.GitHub.WorkingLabel) || Has(config.GitHub.FailedLabel) || Has(config.GitHub.BlockedLabel)))
                    {
                        await ReportStandaloneBlockerAsync(source, "issue-no-longer-eligible", ct,
                            "Implementation continuation requires the open original Issue with unchanged intent; explicitly mark it ready after resolving conflicting state.");
                        continue;
                    }
                    issue = live with { Labels = state.Labels };
                }
                var execution = WorkerExecution.Create(config.Project, config.Git, issue, retryOfExecutionId: source.ExecutionId,
                    attemptNumber: source.AttemptNumber + 1, resumed: true, featureBranchOverride: source.FeatureBranch,
                    codexSettings: config.Codex, settingsSource: source, serverExecutionId: assignment?.ServerExecutionId,
                    assignmentId: assignment?.AssignmentId, ownershipGeneration: assignment?.Lease?.Generation);
                execution.CodexThreadId = source.CodexThreadId;
                execution.CodexSessionOwner = source.CodexSessionOwner;
                execution.CodexRecovery = snapshot with { ResumeCount = snapshot.ResumeCount + 1 };
                if (!await history.TryClaimCodexRecoveryAsync(source, CreateInitialEntry(execution) with { RecoveryBaseCommit = source.RecoveryBaseCommit, RecoveryStatus = source.RecoveryStatus,
                    OriginalIssueBodyFingerprint = source.OriginalIssueBodyFingerprint }, ct)) continue;
                await TransitionAsync(execution, ExecutionState.Claimed, ct);
                _output.Warning($"Resuming execution {execution.ExecutionId} · interrupted execution {source.ExecutionId} · " +
                    (snapshot.SessionId is null || CodexFailure.RequiresFreshSession(source) ? "continuing preserved workspace with new Codex session" : $"attempting Codex session {snapshot.SessionId}"));
                // ProcessClaimedAsync now owns the in-memory Issue claim until its finally block.
                DisplayIssueStart(issue, execution);
                handedOff = true;
                return ProcessClaimedAsync(execution, issue, source, source.IssueNumber, ct);
            }
            finally
            {
                // Preserve the claim only while the independent execution task is active.
                if (!handedOff) _activeIssues.TryRemove(source.IssueNumber, out _);
            }
        }
        return null;
    }

    private async Task<Task<IssueProcessingResult?>?> ClaimIntegrationRecoveryAsync(GitHubIssue issue, CancellationToken ct, bool explicitRecovery = true,
        WorkerAssignmentContract? assignment = null, Guid? resolveExecutionId = null)
    {
        var issueKey = issue.Number;
        if (!_activeIssues.TryAdd(issueKey, 0)) return null;
        WorkerExecution? claimedExecution = null;
        try
        {
            IReadOnlyList<ExecutionHistoryEntry> allHistory = history is null ? [] : await history.ReadAllAsync(ct);
            ExecutionHistoryEntry[] prior = allHistory.Where(entry =>
                entry.Project == config.Project.Name && entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number &&
                entry.State == "IntegrationConflict" && entry.RecoveryState is (null or "integration-conflict") &&
                // A failed recovery attempt can itself be recorded as an integration conflict.
                // It aliases the original preserved workspace; only that workspace owner carries
                // the durable attempted-base marker used to suppress duplicate automatic claims.
                !allHistory.Any(parent => parent.ExecutionId == entry.RetryOfExecutionId &&
                    parent.State == "IntegrationConflict" && parent.FeatureBranch == entry.FeatureBranch))
                .OrderByDescending(entry => entry.StartedAtUtc).ToArray();
            var managedExplicit = !string.IsNullOrEmpty(assignment?.Metadata.GetValueOrDefault("explicitIntegrationRecovery"));
            if (managedExplicit && (assignment?.Metadata.GetValueOrDefault("explicitIntegrationRecovery") != assignment?.ServerExecutionId ||
                assignment?.ServerExecutionId == assignment?.Metadata.GetValueOrDefault("originalServerExecutionId")))
                throw new WorkerInfrastructureException("Explicit recovery requires a distinct Server execution authorization.");
            var source = resolveExecutionId is { } exactId ? prior.SingleOrDefault(entry => entry.ExecutionId == exactId) : prior.FirstOrDefault();
            // Recovery attempts use their original worktree, not a workspace named for the new history row.
            // A later implementation or completion supersedes older conflicts.
            var latest = allHistory.Where(entry => entry.Project == config.Project.Name &&
                entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number)
                .OrderByDescending(entry => entry.AttemptNumber).ThenByDescending(entry => entry.StartedAtUtc).FirstOrDefault();
            if (source is not null && latest is not null && latest.ExecutionId != source.ExecutionId &&
                latest.FeatureBranch != source.FeatureBranch) source = null;
            if (source is null)
            {
                if (resolveExecutionId is not null) throw new InvalidOperationException("Selected execution is stale or no longer recoverable.");
                const string missingHistoryReason = "no recoverable integration-conflict execution history";
                if (!explicitRecovery)
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, null, missingHistoryReason, ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · integration recovery rejected · {missingHistoryReason}.";
                _operationalLog(diagnostic);
                _output.Warning(diagnostic);
                await telegram.IntegrationRecoveryRejectedAsync(config.Project.Name, config.Project.Repository, issue,
                    missingHistoryReason, ct);
                if (explicitRecovery) await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel, ct);
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            var standaloneConflict = serverSettings?.Enabled != true && source.ServerExecutionId is null &&
                source.AssignmentId is null && source.OwnershipGeneration is null;
            // The engine resolves a preserved conflict only on an explicit request for that execution.
            // Automatic recovery retries integration of the preserved commit and never edits it.
            var resolveIntegrationConflict = resolveExecutionId is not null;
            var lineage = new HashSet<Guid> { source.ExecutionId };
            while (resolveExecutionId is null && source.RetryOfExecutionId is { } parentId)
            {
                var parent = allHistory.FirstOrDefault(entry => entry.ExecutionId == parentId);
                if (parent is null || parent.Project != source.Project || parent.Repository != source.Repository ||
                    parent.IssueNumber != source.IssueNumber || !lineage.Add(parent.ExecutionId))
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, source.ExecutionId,
                        "preserved recovery lineage is missing, cyclic or inconsistent", ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                // A normal implementation retry owns its own branch/worktree. Only integration
                // recovery attempts share their parent's preserved branch and workspace.
                if (parent.FeatureBranch != source.FeatureBranch) break;
                if (parent.State != "IntegrationConflict" || parent.RecoveryState is not (null or "integration-conflict"))
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, source.ExecutionId,
                        "the preserved workspace's original execution is no longer recoverable", ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                source = parent;
            }
            if (source.IntegrationRecoveryClaim is not null)
            {
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            if (assignment is not null && (assignment.Metadata["integrationRecoveryExecutionId"] != source.ExecutionId.ToString() ||
                !assignment.Metadata.TryGetValue("originalServerExecutionId", out var originalServerId) || originalServerId != source.ServerExecutionId))
                throw new WorkerInfrastructureException("Recovery assignment source differs from the preserved implementation.");
            issue = issue with { Title = source.IssueTitle, Body = source.OriginalIssueBody ?? issue.Body };
            string? integrationBase;
            string? rejectionReason;
            await _repositoryGate.WaitAsync(ct);
            try
            {
                rejectionReason = await IntegrationRecovery.ValidateSourceAsync(source, ct);
                integrationBase = rejectionReason is null ? await git.GetIntegrationBaseAsync(ct) : null;
            }
            finally { _repositoryGate.Release(); }
            if (!explicitRecovery && !managedExplicit && source.IntegrationRecoveryAttemptBase == integrationBase && integrationBase is not null)
            {
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            if (!explicitRecovery && integrationBase is null && rejectionReason is null)
                rejectionReason = "the authoritative integration base could not be identified";
            if (rejectionReason is not null)
            {
                if (!explicitRecovery)
                {
                    await IntegrationRecovery.ReportRejectedAsync(issue.Number, source.ExecutionId, rejectionReason, ct);
                    _activeIssues.TryRemove(issueKey, out _);
                    return null;
                }
                var diagnostic = $"Scheduler · {config.Project.Name} · Issue #{issue.Number} · execution {source.ExecutionId} · integration recovery rejected · {FailureDiagnosticRedactor.Redact(rejectionReason, config.Environment.Variables.Values.ToArray())}.";
                _operationalLog(diagnostic);
                _output.Warning(diagnostic);
                await telegram.IntegrationRecoveryRejectedAsync(config.Project.Name, config.Project.Repository, issue, rejectionReason, ct);
                if (explicitRecovery) await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.IntegrationConflictLabel, ct);
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            // An unchanged base is already durably exhausted. Skip it before strict workspace
            // checks because an unsuccessful resolver may have left an intentionally
            // inspection-required recovery row in the preserved workspace.
            // New attempts still verify the selected workspace owner before any claim or mutation.
            // Use the exact freshly verified Issue for resolver/completion fingerprints.
            // A historical redacted/LF-only prompt is evidence, not the current GitHub payload.
            if (resolveIntegrationConflict)
                issue = await VerifyConflictResolutionAsync(source, allHistory, ct, managed: managedExplicit);
            if (managedExplicit && assignment?.Metadata.GetValueOrDefault("parentProvenance") != issue.ParentProvenance)
                throw ConflictIssueIntent.Refuse("issue-parent-proof-changed");
            if (managedExplicit && (assignment?.Metadata.GetValueOrDefault("preservedCommit") != source.RecoveryBaseCommit ||
                assignment?.Metadata.GetValueOrDefault("integrationBase") != integrationBase))
                throw new InvalidOperationException("Preserved commit or integration base changed since preview; inspect again.");
            _operationalLog($"Integration recovery discovered · {config.Project.Name} · Issue #{issue.Number} · original execution {source.ExecutionId}");
            source = source with { RecoveryBaseCommit = source.RecoveryBaseCommit ?? source.CommitSha };
            issue = await PrepareCommentContextAsync(issue, ct);
            var execution = WorkerExecution.Create(config.Project, config.Git, issue,
                retryOfExecutionId: source.ExecutionId, attemptNumber: allHistory.Where(entry =>
                    entry.Project == config.Project.Name && entry.Repository == config.Project.Repository && entry.IssueNumber == issue.Number)
                    .Select(entry => entry.AttemptNumber).DefaultIfEmpty(0).Max() + 1,
                featureBranchOverride: source.FeatureBranch, codexSettings: config.Codex, settingsSource: source,
                resumed: true, serverExecutionId: assignment?.ServerExecutionId, assignmentId: assignment?.AssignmentId,
                ownershipGeneration: assignment?.Lease?.Generation);
            execution.CodexRecovery = source.CodexRecovery;
            if (managedExplicit)
            {
                execution.ConflictRecoveryLease = assignment?.Lease;
                if (!await IsAuthoritativeAsync(execution, ct))
                    throw new WorkerInfrastructureException("Explicit recovery lease is no longer current; workspace preserved.");
            }
            if (execution.ParentIssueNumber == source.ParentIssueNumber)
            {
                execution.CodexThreadId = source.CodexThreadId;
                execution.CodexSessionOwner = source.CodexSessionOwner;
            }
            if (history is null || !await history.TryClaimIntegrationRecoveryAsync(source,
                    CreateInitialEntry(execution) with
                    {
                        // Recovery uses the stored (redacted) prompt. Retain its original proof;
                        // hashing that prompt would invent evidence for different Issue content.
                        OriginalIssueBodyFingerprint = source.OriginalIssueBodyFingerprint,
                        RecoveryStatus = resolveExecutionId is not null
                            ? $"Operator authorized conflict resolution for execution {source.ExecutionId}."
                            : resolveIntegrationConflict ? $"Automatic standalone conflict resolution for execution {source.ExecutionId}." : null
                    }, integrationBase ?? "explicit", explicitRecovery || managedExplicit, ct))
            {
                _activeIssues.TryRemove(issueKey, out _);
                return null;
            }
            claimedExecution = execution;
            if (resolveIntegrationConflict) await VerifyConflictIntentAsync(source, ct, execution.ParentProvenance);
            await _output.StopWaitingAsync();
            await TransitionAsync(execution, ExecutionState.Claimed, ct);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationConflictLabel, config.GitHub.IntegrationRecoveryLabel, ct);
            if (resolveIntegrationConflict) await VerifyConflictIntentAsync(source, ct, execution.ParentProvenance);
            await github.ReplaceLabelAsync(issue.Number, config.GitHub.IntegrationRecoveryLabel, config.GitHub.WorkingLabel, ct);
            _operationalLog($"Integration recovery claimed · Issue #{issue.Number} · execution {execution.ExecutionId} · original execution {source.ExecutionId}");
            DisplayIssueStart(issue, execution);
            await telegram.StartingAsync(config.Project.Name, config.Project.Repository, issue, execution, ct);
            return ProcessClaimedAsync(execution, issue, source, issueKey, ct, integrationRecovery: true,
                resolveIntegrationConflict: resolveIntegrationConflict);
        }
        catch (Exception shutdownError) when (ct.IsCancellationRequested && shutdownToken.IsCancellationRequested && WorkerShutdown.IsCancellation(shutdownError))
        {
            _activeIssues.TryRemove(issueKey, out _);
            if (claimedExecution is not null) await RecordShutdownAsync(claimedExecution, ShutdownRecoveryState(shutdownError));
            throw new WorkerShutdownException(shutdownToken, shutdownError);
        }
        catch (Exception ex)
        {
            _activeIssues.TryRemove(issueKey, out _);
            if (claimedExecution is not null && !claimedExecution.IsTerminal)
                await RecordInfrastructureFailureAsync(claimedExecution, ex.Message,
                    GitHubOperationException.Find(ex) is { IsMutation: true } ? GitHubOperationException.ReconciliationRequiredState : "uncertain");
            if (claimedExecution is not null && ex is GitHubOperationException githubFailure)
                throw githubFailure.ForExecution(claimedExecution.ExecutionId);
            throw;
        }
    }

    public async Task<IReadOnlyList<CodexProvisioning.IntegrationRecoveryCandidate>> DiscoverManagedIntegrationRecoveriesAsync(
        string projectId, CancellationToken ct)
    {
        var candidates = (await IntegrationRecovery.DiscoverManagedAsync(projectId, ct)).Select(candidate => candidate with
        {
            RefusalReason = candidate.RefusalReason ?? (!config.Git.AutoMerge ? "Enable git.autoMerge before conflict resolution." :
                config.Validation.MaxFixAttempts < 1 ? "A positive validation repair budget is required." : null)
        }).ToList();
        if (history is null) return candidates;
        var entries = await history.ReadAllAsync(ct);
        candidates = candidates.Select(candidate => candidate with
        {
            RefusalReason = candidate.RefusalReason ?? (entries.FirstOrDefault(entry => entry.ExecutionId.ToString() == candidate.WorkerExecutionId) is { } source &&
                !(engines ?? new ImplementationEngineSelector(settings: config.Codex)).IsAvailable(source.Engine)
                    ? "The original implementation engine is unavailable." : null)
        }).ToList();
        foreach (var entry in entries.Where(entry => entry.Project == config.Project.Name && entry.Repository == config.Project.Repository &&
                     entry.RecoveryState == "codex-interrupted" && entry.ServerExecutionId is not null &&
                     !_activeIssues.ContainsKey(entry.IssueNumber) && !entries.Any(other => other.Project == entry.Project &&
                         other.Repository == entry.Repository && other.IssueNumber == entry.IssueNumber &&
                         (other.AttemptNumber > entry.AttemptNumber || other.State == "Completed"))))
        {
            var metadataError = CodexRecoveryCoordinator.ValidateMetadata(entry, entries, config);
            if (metadataError is not null)
            {
                await history.UpdateRecoveryAsync(entry.ExecutionId, "codex-recovery-inspection-required", ct,
                    "Codex recovery requires inspection: " + metadataError);
                _output.Warning($"Codex recovery requires inspection · execution {entry.ExecutionId} · {metadataError}.");
                continue;
            }
            if (entry.CodexRecovery?.RetryAfterUtc > _clock.GetUtcNow()) continue;
            string? rejection;
            await _repositoryGate.WaitAsync(ct);
            try { rejection = await git.ValidateCodexRecoveryAsync(entry, ct); }
            finally { _repositoryGate.Release(); }
            if (rejection is not null)
            {
                await history.UpdateRecoveryAsync(entry.ExecutionId, "codex-recovery-inspection-required", ct,
                    "Codex recovery requires inspection: " + FailureDiagnosticRedactor.Redact(rejection, config.Environment.Variables.Values.ToArray()));
                _output.Warning($"Codex recovery requires inspection · execution {entry.ExecutionId} · {FailureDiagnosticRedactor.Redact(rejection, config.Environment.Variables.Values.ToArray())}");
                continue;
            }
            // A per-attempt fingerprint makes Server queueing idempotent across readiness checks/restart.
            candidates.Add(new(projectId, entry.ServerExecutionId ?? "", entry.ExecutionId.ToString(),
                entry.RecoveryBaseCommit ?? "", "Codex"));
        }
        return candidates.Take(128).ToArray();
    }

    public async Task ReportIntegrationRecoveryRejectionAsync(Guid sourceId, string reason, CancellationToken ct)
    {
        if (history is null) return;
        var source = (await history.ReadAllAsync(ct)).FirstOrDefault(entry => entry.ExecutionId == sourceId &&
            entry.Project == config.Project.Name && entry.Repository == config.Project.Repository);
        if (source is not null) await IntegrationRecovery.ReportRejectedAsync(source.IssueNumber, source.ExecutionId, reason, ct);
    }
}
