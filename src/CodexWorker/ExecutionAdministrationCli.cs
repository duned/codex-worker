using System.Text.Json;

namespace CodexWorker;

/// <summary>Offline, checkout-fenced administration. Never replays execution or completion effects.</summary>
internal static class ExecutionAdministrationCli
{
    internal const string Help = """
        Usage: codex-worker executions list [--config <path>]
               codex-worker executions show <execution-id> [--config <path>]
               codex-worker executions acknowledge <execution-id> [--confirm] [--prune] [--config <path>]
        Acknowledge defaults to dry-run. Stop the Worker first. Remote Issue proof is required.
        --prune removes only the retained Issue prompt, never history, provenance, branches,
        worktrees or session evidence. Managed ownership must be reconciled on the Server.
        """;

    internal static string? Ineligible(ExecutionHistoryEntry entry, IReadOnlyList<ExecutionHistoryEntry> entries)
    {
        if (entry.CompletedAtUtc is null || entry.State is not
            ("Completed" or "Failed" or "Blocked" or "InfrastructureFailure" or "Cancelled" or "IntegrationConflict"))
            return "Execution is active or has an unknown outcome; stop and reconcile it first.";
        if (entry.ServerExecutionId is not null || entry.AssignmentId is not null || entry.OwnershipGeneration is not null)
            return "Managed ownership requires Server lease reconciliation; local acknowledgment is refused.";
        if (entry.IntegrationRecoveryClaim is not null)
            return "An integration recovery claim remains; reconcile its owning attempt first.";
        if (entries.Any(other => other.ExecutionId != entry.ExecutionId && other.Repository == entry.Repository &&
            other.IssueNumber == entry.IssueNumber && other.CompletedAtUtc is null))
            return "Another execution for this Issue is active; reconcile its ownership first.";
        return null;
    }

    internal static bool IsResolved(GitHubIssueState remote, GitHubSettings labels) => !remote.IsOpen &&
        remote.Labels.Contains(labels.DoneLabel, StringComparer.OrdinalIgnoreCase) &&
        !new[] { labels.ReadyLabel, labels.WorkingLabel, labels.FailedLabel, labels.BlockedLabel,
            labels.IntegrationConflictLabel, labels.IntegrationRecoveryLabel }
            .Any(label => remote.Labels.Contains(label, StringComparer.OrdinalIgnoreCase));

    internal static async Task<string> VerifyResolutionAsync(ExecutionHistoryEntry entry, WorkerConfiguration config,
        GitHubIssueState remote, Func<ExecutionHistoryEntry, CancellationToken, Task<bool>> verifyIntegration, CancellationToken ct)
    {
        if (!IsResolved(remote, config.GitHub))
            throw new InvalidOperationException("Verify the authoritative Issue is closed with the configured done label and no ready, working, failure, blocked or recovery labels.");
        var proof = "Authoritative Issue closed with configured done label; conflicting labels absent; resources retained";
        if (entry.CommitSha is not null || entry.CompletionJson is not null || entry.ValidationOutcome == "passed")
        {
            if (entry.ValidationOutcome != "passed" || !await verifyIntegration(entry, ct))
                throw new InvalidOperationException("Exact validated integration commit must be present on the authoritative remote base; restore missing provenance before acknowledgment.");
            if (entry.CompletionJson is not null &&
                ExecutionCompletion.Read(entry).PolicyFingerprint != ExecutionCompletion.Policy(config))
                throw new InvalidOperationException("Completion policy changed; restore the original project policy before acknowledgment.");
            proof += $"; validated commit {entry.CommitSha} verified on origin/{entry.BaseBranch}";
        }
        else if (entry.State is "IntegrationConflict" or "InfrastructureFailure" or "Cancelled")
            throw new InvalidOperationException("Uncertain execution has no exact validated integration provenance; retain evidence and reconcile it first.");
        return proof;
    }

    internal static async Task<int> RunAsync(WorkerCommandLine command, WorkerConsole output, CancellationToken ct, TextWriter? writer = null, string? databasePath = null)
    {
        var destination = writer ?? Console.Out;
        try
        {
            var args = command.Arguments;
            var action = args.FirstOrDefault();
            var list = action == "list" && args.Count == 1;
            var show = action == "show" && args.Count == 2;
            var acknowledge = action == "acknowledge" && args.Count >= 2 &&
                args.Skip(2).All(arg => arg is "--confirm" or "--prune") && args.Skip(2).Distinct().Count() == args.Count - 2;
            if (!list && !show && !acknowledge) throw new ArgumentException(Help);
            Guid id = default;
            if (!list && (!Guid.TryParse(args[1], out id) || id == Guid.Empty))
                throw new ArgumentException("Specify one valid execution ID.");
            using var history = new ExecutionHistoryStore(databasePath);
            var entries = await history.ReadAllAsync(ct);
            if (list || show)
            {
                var selected = list ? entries : entries.Where(entry => entry.ExecutionId == id).ToArray();
                if (!list && selected.Count == 0) throw new ArgumentException("Execution ID was not found.");
                foreach (var entry in selected)
                    destination.WriteLine(JsonSerializer.Serialize(new
                    {
                        entry.ExecutionId, entry.Project, entry.Repository, entry.IssueNumber, entry.State,
                        entry.CompletedAtUtc, entry.RecoveryState, entry.ValidationOutcome, entry.CommitSha,
                        entry.IntegrationBranch,
                        acknowledgment = await history.ReadAcknowledgementAsync(entry.ExecutionId, ct),
                        legacyReview = await history.ReadLegacyReviewAsync(entry.ExecutionId, ct),
                        eligibility = Ineligible(entry, entries) ?? "Remote verification and exclusive checkout lock required",
                        cleanup = entry.CodexRecovery is null && entry.RecoveryBaseCommit is null && entry.RecoveryStatus is null ? "Prompt only; all history and resources retained" : "Recovery evidence retained; pruning refused"
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return ProcessExitCodes.Success;
            }
            var global = GlobalWorkerConfiguration.Load(command.ConfigurationPath ?? WorkerCommandLine.DefaultConfigurationPath);
            if (global.Server.Enabled || global.Projects.Ownership == "managed")
                throw new InvalidOperationException("Managed mode requires Server ownership reconciliation; use the Server administration surface.");
            var entryToResolve = entries.SingleOrDefault(entry => entry.ExecutionId == id)
                ?? throw new ArgumentException("Execution ID was not found.");
            var config = ProjectConfigurationDiscovery.LoadForWorker(global).Select(project => project.Configuration)
                .SingleOrDefault(config => config.Project.Name == entryToResolve.Project && config.Project.Repository == entryToResolve.Repository)
                ?? throw new InvalidOperationException("Execution's project/repository is not configured; restore its configuration before verification.");
            var runner = new ProcessRunner();
            using var git = new GitRepository(runner, config.Project.Directory, config.Project.Repository, config.Git, config.Worker);
            await git.AcquireWorkerLockAsync(ct);
            // Refresh after acquiring the same exclusive lock held by the running Worker.
            entries = await history.ReadAllAsync(ct);
            entryToResolve = entries.Single(entry => entry.ExecutionId == id);
            if (Ineligible(entryToResolve, entries) is { } reason) throw new InvalidOperationException(reason);
            var prune = args.Contains("--prune");
            if (prune && (entryToResolve.CodexRecovery is not null || entryToResolve.RecoveryBaseCommit is not null || entryToResolve.RecoveryStatus is not null))
                throw new InvalidOperationException("Recovery evidence remains; acknowledge without --prune to retain it.");
            var remote = await new GitHubClient(runner, config.Project.Repository, config.Worker.GitHubTimeoutSeconds)
                .ReadIssueStateAsync(entryToResolve.IssueNumber, ct);
            var proof = await VerifyResolutionAsync(entryToResolve, config, remote, git.VerifyRemoteIntegrationAsync, ct);
            destination.WriteLine($"Execution {id}: {proof}. Outcome, audit, lineage, completion provenance, worktrees and sessions retained. " +
                (prune ? "Retained Issue prompt will be removed." : "No persisted artifacts removed."));
            if (!args.Contains("--confirm")) destination.WriteLine("Dry-run: no changes. Repeat with --confirm to acknowledge.");
            else
            {
                await history.AcknowledgeAsync(entryToResolve, proof + $"; original outcome={entryToResolve.State}, recovery={entryToResolve.RecoveryState ?? "none"}", prune, ct);
                destination.WriteLine("Acknowledged. Restart the Worker to resume startup reconciliation.");
            }
            return ProcessExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or WorkerInfrastructureException)
        {
            WorkerCliOutput.Failure(command, output, destination, "execution-administration-rejected",
                FailureDiagnosticRedactor.Redact(ex.Message));
            return ProcessExitCodes.StartupFailure;
        }
    }
}
