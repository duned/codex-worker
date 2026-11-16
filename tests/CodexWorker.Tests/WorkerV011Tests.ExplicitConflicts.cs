using CodexWorker;
using CodexServer;

namespace CodexWorker.Tests;

public sealed partial class WorkerV011Tests
{
    [Theory]
    [InlineData("not-an-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("12345678-1234-1234-1234-123456789abc", "--apply")]
    [InlineData("12345678-1234-1234-1234-123456789abc", "--confirm", "--confirm")]
    [Trait("TestType", "Unit")]
    [Trait("Duration", "Fast")]
    public async Task ExplicitConflictCliRejectsInvalidOrAmbiguousAuthorization(params string[] args)
    {
        using var text = new StringWriter();
        var command = new WorkerCommandLine("executions", "/missing-config.yml", ["resolve-conflict", .. args]);
        Assert.Equal(ProcessExitCodes.StartupFailure,
            await ExecutionAdministrationCli.RunAsync(command, new WorkerConsole(text, false, text), CancellationToken.None, text));
        Assert.Contains("resolve-conflict", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ExplicitConflictSelectsOriginalAfterSettledConservativeRecovery()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        await history.CreateAsync(source);
        await history.CreateAsync(source with { ExecutionId = Guid.NewGuid(), RetryOfExecutionId = source.ExecutionId,
            AttemptNumber = 2, Resumed = true });
        h.Git.RequestConflictResolution = true;
        Assert.Equal(IssueOutcomeKind.Succeeded,
            (await h.Worker.ResolveStandaloneConflictAsync(source.ExecutionId, true, CancellationToken.None))?.Kind);
        Assert.Equal(source.ExecutionId, h.Git.LastRetryOf?.ExecutionId);
        Assert.Equal(1, h.Codex.ConflictResolutions);
    }

    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ExplicitConflictInspectionDoesNotClaimOrInvokeEngine()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        await history.CreateAsync(source);
        Assert.Null(await h.Worker.ResolveStandaloneConflictAsync(source.ExecutionId, false, CancellationToken.None));
        Assert.Single(await history.ReadAllAsync());
        Assert.Empty(h.GitHub.Labels);
        Assert.Equal(0, h.Git.RecoveryStarted);
        Assert.Equal(0, h.Codex.ConflictResolutions);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("assignment")]
    [InlineData("generation")]
    [InlineData("missing-intent")]
    [InlineData("claim")]
    [InlineData("base")]
    [InlineData("workspace")]
    [InlineData("process")]
    [InlineData("later")]
    [InlineData("active")]
    [InlineData("unknown-id")]
    [InlineData("changed-intent")]
    [InlineData("closed")]
    [InlineData("ready")]
    [InlineData("budget")]
    [InlineData("integration-disabled")]
    [InlineData("failed-label")]
    [InlineData("blocked-label")]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ExplicitConflictResolutionRefusesUnverifiedTargetsWithoutMutation(string refusal)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        source = refusal switch
        {
            "managed" => source with { ServerExecutionId = "server" },
            "assignment" => source with { AssignmentId = "assignment" },
            "generation" => source with { OwnershipGeneration = 1 },
            "missing-intent" => source with { OriginalIssueBody = null },
            "claim" => source with { IntegrationRecoveryClaim = Guid.NewGuid() },
            "base" => source with { BaseBranch = "other" },
            _ => source
        };
        if (refusal == "base") h.Git.RecoveryRejection = "base mismatch";
        if (refusal == "workspace") h.Git.RecoveryRejection = "workspace identity mismatch";
        if (refusal == "process") h.Git.ProcessBlocker = "workspace-process-active";
        if (refusal == "changed-intent") h.GitHub.Issue = h.GitHub.Issue with { Body = "changed" };
        if (refusal == "closed") h.GitHub.Closed = true;
        if (refusal == "ready") h.GitHub.CompletionLabels = ["ready"];
        if (refusal == "budget") h.Worker.Configuration.Validation.MaxFixAttempts = 0;
        if (refusal == "integration-disabled") h.Worker.Configuration.Git.AutoMerge = false;
        if (refusal == "failed-label") h.GitHub.CompletionLabels = ["working", "failed"];
        if (refusal == "blocked-label") h.GitHub.CompletionLabels = ["working", "blocked"];
        await history.CreateAsync(source);
        if (refusal is "later" or "active")
            await history.CreateAsync(source with { ExecutionId = Guid.NewGuid(), AttemptNumber = 2,
                CompletedAtUtc = refusal == "active" ? null : source.CompletedAtUtc });
        await Assert.ThrowsAnyAsync<Exception>(() => h.Worker.ResolveStandaloneConflictAsync(
            refusal == "unknown-id" ? Guid.NewGuid() : source.ExecutionId, true, CancellationToken.None));
        Assert.Empty(h.GitHub.Labels);
        Assert.Equal(0, h.Git.RecoveryStarted);
        Assert.Equal(0, h.Codex.ConflictResolutions);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(source),
            System.Text.Json.JsonSerializer.Serialize(await history.ReadExecutionAsync(source.ExecutionId)));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("ccc")]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ExplicitConflictResolutionUsesSelectedEngineAndExistingIntegrationLifecycle(string engine)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        var implementation = new ImplementationEngineTests.Engine();
        using var h = new Harness(history: history, engine: implementation);
        h.GitHub.Issue = h.GitHub.Issue with { Body = "## Worker\nengine: " + engine };
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body, Engine = engine };
        await history.CreateAsync(source);
        h.Git.RequestConflictResolution = true;
        var result = await h.Worker.ResolveStandaloneConflictAsync(source.ExecutionId, true, CancellationToken.None);
        Assert.Equal(IssueOutcomeKind.Succeeded, result?.Kind);
        Assert.Equal(1, h.Git.RecoveryStarted);
        Assert.Equal(source.ExecutionId, h.Git.LastRetryOf?.ExecutionId);
        Assert.Equal(0, h.Git.Started);
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(1, h.Validation.Calls);
        if (engine == "codex")
        {
            Assert.Equal(1, h.Codex.ConflictResolutions);
            Assert.Equal(h.Git.ExecutionDirectory, h.Codex.ConflictDirectory);
        }
        else Assert.Equal(h.Git.ExecutionDirectory, Assert.Single(implementation.Requests).ProjectDirectory);
        Assert.True(h.GitHub.Closed);
        var rows = await history.ReadAllAsync();
        Assert.Equal("integration-recovered", rows.Single(row => row.ExecutionId == source.ExecutionId).RecoveryState);
        var completed = rows.Single(row => row.State == "Completed");
        Assert.Equal(source.ExecutionId, completed.RetryOfExecutionId);
        Assert.Contains("Operator authorized conflict resolution", completed.RecoveryStatus, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task StandaloneRecoveryAutomaticallyResolvesVerifiedRebaseConflictInPreservedWorkspace()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var sharedThread = Guid.NewGuid().ToString("N");
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body, CodexThreadId = sharedThread };
        await history.CreateAsync(source);
        await history.CreateAsync(source with
        {
            ExecutionId = Guid.NewGuid(), IssueNumber = source.IssueNumber + 1,
            FeatureBranch = "feature/unrelated-thread-owner", State = "Completed",
            CodexThreadId = sharedThread
        });
        h.GitHub.ReturnConflictIssue = true;
        h.Git.RequestConflictResolution = true;

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.Succeeded, result?.Kind);
        Assert.Equal(source.ExecutionId, h.Git.LastRetryOf?.ExecutionId);
        Assert.Equal(1, h.Git.RecoveryStarted);
        Assert.Equal(1, h.Codex.ConflictResolutions);
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(1, h.Validation.Calls);
        var rows = await history.ReadAllAsync();
        Assert.Equal("integration-recovered", rows.Single(row => row.ExecutionId == source.ExecutionId).RecoveryState);
        Assert.Contains("Automatic standalone conflict resolution", rows.Single(row => row.RetryOfExecutionId == source.ExecutionId && row.State == "Completed").RecoveryStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task UnresolvedAutomaticStandaloneConflictIsPreservedAndNotRepeatedForSameBase()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        await history.CreateAsync(source);
        h.GitHub.ReturnConflictIssue = true;
        h.Git.RequestConflictResolution = true;
        h.Codex.ConflictAction = () => new CodexOutcome("failed", "cannot safely resolve", [], false, null);

        var result = await h.ProcessOneAsync();

        Assert.Equal(IssueOutcomeKind.IntegrationConflict, result?.Kind);
        Assert.False(h.GitHub.Closed);
        Assert.Equal(0, h.Git.Cleanups);
        Assert.Equal(1, h.Codex.ConflictResolutions);
        var retainedSource = await history.ReadExecutionAsync(source.ExecutionId);
        Assert.Equal("current-main", retainedSource?.IntegrationRecoveryAttemptBase);
        Assert.Null(retainedSource?.IntegrationRecoveryClaim);
        Assert.Equal("integration-conflict", retainedSource?.RecoveryState);
        // This Issue is only present in the recovery queue. The fake ready queue does not
        // enforce label filtering, so prevent it from returning the same conflict as new work.
        h.GitHub.ReturnIssueOnFirstQuery = false;
        Assert.Null(await h.Worker.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(1, h.Codex.ConflictResolutions);
        Assert.Equal(source.FeatureBranch, h.Git.LastRetryOf?.FeatureBranch);

        using var restarted = new Harness(history: history);
        restarted.GitHub.ReturnConflictIssue = true;
        restarted.GitHub.ReturnIssueOnFirstQuery = false;
        Assert.Null(await restarted.Worker.ProcessOneAsync(CancellationToken.None));
        Assert.Equal(0, restarted.Codex.ConflictResolutions);
        Assert.Equal(0, restarted.Git.RecoveryStarted);

        restarted.Git.IntegrationBase = "new-main";
        restarted.Git.RequestConflictResolution = true;
        Assert.Equal(IssueOutcomeKind.Succeeded, (await restarted.Worker.ProcessOneAsync(CancellationToken.None))?.Kind);
        Assert.Equal(1, restarted.Codex.ConflictResolutions);
        Assert.Equal(1, restarted.Git.RecoveryStarted);
        Assert.Equal("new-main", (await history.ReadExecutionAsync(source.ExecutionId))?.IntegrationRecoveryAttemptBase);
    }

    [Theory]
    [InlineData("integration-disabled")]
    [InlineData("engine-unavailable")]
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    public async Task AutomaticStandaloneConflictWithoutResolverPrerequisitesReportsAndDoesNotClaim(string missingPrerequisite)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        if (missingPrerequisite == "engine-unavailable") source = source with { Engine = "ccc" };
        else h.Worker.Configuration.Git.AutoMerge = false;
        await history.CreateAsync(source);
        h.GitHub.ReturnConflictIssue = true;
        h.GitHub.ReadyIssueCount = 0;
        h.GitHub.CancelWhenEmpty = false;

        var result = await h.ProcessOneAsync();

        Assert.Null(result);
        Assert.Equal(0, h.Git.RecoveryStarted);
        Assert.Equal(0, h.Codex.ConflictResolutions);
        Assert.Null((await history.ReadExecutionAsync(source.ExecutionId))?.IntegrationRecoveryClaim);
        Assert.Contains(h.GitHub.Comments, comment => comment.Contains("requires inspection", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("changed-intent")]
    [InlineData("validation")]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ExplicitConflictResolutionFailurePreservesSourceAndNeverCompletesIssue(string failure)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history, gracefulShutdown: true);
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        await history.CreateAsync(source);
        h.Git.RequestConflictResolution = true;
        h.Git.Recovery = new(source.FeatureBranch, source.RecoveryBaseCommit ?? "missing", "preserved");
        h.Codex.ConflictAction = () =>
        {
            if (failure == "cancelled") { h.Cancellation.Cancel(); h.Cancellation.Token.ThrowIfCancellationRequested(); }
            if (failure == "changed-intent") h.GitHub.Issue = h.GitHub.Issue with { Body = "changed" };
            return new CodexOutcome(failure == "failed" ? "failed" : "success", "resolution result", [], false, null);
        };
        if (failure is "validation" or "failed")
        {
            if (failure == "validation") h.Validation.Results.Enqueue(Failure("check", 1, "failed"));
            Assert.Equal(IssueOutcomeKind.IntegrationConflict,
                (await h.Worker.ResolveStandaloneConflictAsync(source.ExecutionId, true, h.Cancellation.Token))?.Kind);
        }
        else await Assert.ThrowsAnyAsync<Exception>(() => h.Worker.ResolveStandaloneConflictAsync(source.ExecutionId, true, h.Cancellation.Token));
        Assert.False(h.GitHub.Closed);
        Assert.DoesNotContain("working->done", h.GitHub.Labels);
        if (failure == "changed-intent")
        {
            Assert.DoesNotContain("working->blocked", h.GitHub.Labels);
            Assert.Empty(h.GitHub.Comments);
        }
        Assert.Equal(0, h.Git.Cleanups);
        var retained = await history.ReadExecutionAsync(source.ExecutionId);
        Assert.NotNull(retained);
        Assert.Equal("integration-conflict", retained.RecoveryState);
        Assert.Equal(source.FeatureBranch, retained.FeatureBranch);
        Assert.Equal(source.RecoveryBaseCommit, retained.RecoveryBaseCommit);
        Assert.Equal(2, (await history.ReadAllAsync()).Count);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("legacy-success")]
    [InlineData("legacy-stale-lease")]
    [InlineData("legacy-changed-proof")]
    [InlineData("historical-crlf")]
    [InlineData("fingerprinted-redaction")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("stale-lease")]
    [InlineData("changed-intent")]
    [InlineData("changed-commit")]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ExplicitManagedConflictUsesFreshLeaseAndPreservedIntentWithoutImplementationReplay(string scenario)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var legacy = scenario.StartsWith("legacy-", StringComparison.Ordinal);
        if (legacy) h.GitHub.Issue = h.GitHub.Issue with { ParentIssueNumber = 32 };
        if (scenario == "historical-crlf")
        {
            h.GitHub.Issue = h.GitHub.Issue with { Body = "Original task\r\nDetails\r\n", ParentIssueNumber = 32 };
            h.GitHub.CompletionLabels = [h.Worker.Configuration.GitHub.IntegrationConflictLabel];
        }
        if (scenario == "fingerprinted-redaction")
            h.GitHub.Issue = h.GitHub.Issue with { Body = "Task\r\npassword: example-placeholder\r\n" };
        var source = PreservedConflict() with { ServerExecutionId = Guid.NewGuid().ToString("N"),
            AssignmentId = Guid.NewGuid().ToString("N"), OwnershipGeneration = 1,
            OriginalIssueBody = FailureDiagnosticRedactor.Redact(h.GitHub.Issue.Body),
            OriginalIssueBodyFingerprint = scenario == "fingerprinted-redaction" ? ConflictIssueIntent.BodyFingerprint(h.GitHub.Issue.Body) : null,
            ParentIssueNumber = legacy ? null : h.GitHub.Issue.ParentIssueNumber, ParentIssueCaptured = !legacy, IntegrationRecoveryAttemptBase = "current-main" };
        if (legacy) h.GitHub.ParentProvenance = System.Text.Json.JsonSerializer.Serialize(new CodexProvisioning.IssueParentProvenance(
            source.Repository, source.IssueNumber, "issue-node", source.StartedAtUtc.AddDays(-1), source.StartedAtUtc, 32,
            [new("parent-added", source.StartedAtUtc.AddSeconds(-51), true, 32)]));
        await history.CreateAsync(source);
        var now = DateTimeOffset.UtcNow;
        var serverId = Guid.NewGuid().ToString("N");
        var identity = Path.Combine(Path.GetDirectoryName(database.Path) ?? "", "identity");
        var workerId = await WorkerIdentity.LoadOrCreateAsync(identity);
        await WorkerAuthentication.LoadOrCreateTokenAsync(identity);
        var settings = new WorkerServerSettings { Enabled = true, Url = "http://127.0.0.1:5090", IdentityFile = identity };
        var lease = new ServerExecutionLeaseContract(serverId, workerId, 3, now, now.AddMinutes(5), "Active");
        using var handler = new ConflictRecoveryHandler(lease) { RejectLease = scenario is "stale-lease" or "legacy-stale-lease" };
        using var client = new HttpClient(handler);
        var worker = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram,
            history: history, serverSettings: settings, registrationClient: new WorkerRegistrationClient(client));
        var candidates = await worker.DiscoverManagedIntegrationRecoveriesAsync("project", CancellationToken.None);
        var candidate = Assert.Single(candidates);
        Assert.True(candidate.AutomaticRetryExhausted);
        Assert.Null(candidate.RefusalReason);
        var assignment = new WorkerAssignmentContract(Guid.NewGuid().ToString("N"), serverId,
            new("project", source.Project, source.Repository, source.BaseBranch, "", [], 1, now, now),
            new("github-issue", "17"), workerId, new Dictionary<string, string>
            {
                ["integrationRecoveryExecutionId"] = source.ExecutionId.ToString(),
                ["originalServerExecutionId"] = source.ServerExecutionId,
                ["explicitIntegrationRecovery"] = serverId,
                ["preservedCommit"] = scenario == "changed-commit" ? "other-commit" : source.RecoveryBaseCommit ?? "",
                ["integrationBase"] = "current-main"
            }, lease);
        if (candidate.ParentProvenance is { } proof)
            assignment = assignment with { Metadata = assignment.Metadata.Append(new KeyValuePair<string, string>("parentProvenance", proof)).ToDictionary() };
        if (scenario == "legacy-changed-proof") h.GitHub.ParentProvenance = h.GitHub.ParentProvenance?.Replace("parent-added", "different-event", StringComparison.Ordinal);
        h.Git.RequestConflictResolution = true;
        h.Codex.ConflictAction = () =>
        {
            if (scenario == "cancelled") { h.Cancellation.Cancel(); h.Cancellation.Token.ThrowIfCancellationRequested(); }
            if (scenario == "changed-intent") h.GitHub.Issue = h.GitHub.Issue with { Body = "new intent" };
            return new CodexOutcome(scenario == "failed" ? "failed" : "success", "resolution", [], false, null);
        };
        async Task<IssueProcessingResult?> RunAsync()
        {
            var task = await worker.ClaimAssignedAsync(assignment, h.Cancellation.Token);
            return task is null ? null : await task;
        }
        if (scenario is "success" or "legacy-success" or "historical-crlf" or "fingerprinted-redaction" or "failed")
            Assert.Equal(scenario != "failed" ? IssueOutcomeKind.Succeeded : IssueOutcomeKind.IntegrationConflict, (await RunAsync())?.Kind);
        else await Assert.ThrowsAnyAsync<Exception>(RunAsync);
        Assert.Empty(h.Codex.Issues);
        Assert.Equal(scenario is "stale-lease" or "legacy-stale-lease" or "legacy-changed-proof" or "changed-commit" ? 0 : 1, h.Codex.ConflictResolutions);
        if (scenario is "success" or "legacy-success" or "historical-crlf" or "fingerprinted-redaction")
        {
            Assert.True(h.Validation.Calls > 0);
            Assert.Contains(handler.Reports, report => report.State == "Completed" && report.Generation == 3);
        }
        else
        {
            Assert.False(h.GitHub.Closed);
            Assert.DoesNotContain("working->done", h.GitHub.Labels);
        }
        if (scenario is "success" or "legacy-success" or "historical-crlf" or "fingerprinted-redaction" or "failed")
        {
            // A redelivered assignment after either outcome cannot re-run resolution, even on restart.
            Assert.Null(await worker.ClaimAssignedAsync(assignment, CancellationToken.None));
            var restarted = new Worker(h.Worker.Configuration, h.GitHub, h.Git, h.Codex, h.Validation, h.Telegram,
                history: history, serverSettings: settings, registrationClient: new WorkerRegistrationClient(client));
            Assert.Null(await restarted.ClaimAssignedAsync(assignment, CancellationToken.None));
            Assert.Equal(1, h.Codex.ConflictResolutions);
            var attempt = Assert.Single(await history.ReadAllAsync(), entry => entry.RetryOfExecutionId == source.ExecutionId);
            Assert.Equal(source.OriginalIssueBodyFingerprint, attempt.OriginalIssueBodyFingerprint);
            Assert.Equal(candidate.ParentProvenance, attempt.ParentProvenance);
            Assert.Equal(source.ParentIssueNumber, (await history.ReadExecutionAsync(source.ExecutionId))?.ParentIssueNumber);
            Assert.Equal(source.OriginalIssueBody, h.Codex.ConflictIssueBody);
        }
    }

    private sealed class ConflictRecoveryHandler(ServerExecutionLeaseContract lease) : HttpMessageHandler
    {
        internal bool RejectLease { get; init; }
        internal List<WorkerExecutionReportContract> Reports { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/lease/renew", StringComparison.Ordinal) == true)
                return new(RejectLease ? System.Net.HttpStatusCode.Conflict : System.Net.HttpStatusCode.OK)
                { Content = System.Net.Http.Json.JsonContent.Create(lease) };
            var report = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<WorkerExecutionReportContract>(
                request.Content ?? throw new InvalidOperationException("Missing report"), cancellationToken);
            Reports.Add(report ?? throw new InvalidOperationException("Empty report"));
            return new(System.Net.HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ManagedConflictIntentAcceptsPersistedCrLfBodyAndParentWithOnlyConflictLabel(bool fingerprinted)
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        var body = "Original task\r\n\r\nKeep whitespace.  \r\n";
        h.GitHub.Issue = h.GitHub.Issue with { Body = body, ParentIssueNumber = 32 };
        h.GitHub.CompletionLabels = [h.Worker.Configuration.GitHub.IntegrationConflictLabel.ToUpperInvariant()];
        var source = PreservedConflict() with { ServerExecutionId = Guid.NewGuid().ToString("N"),
            AssignmentId = Guid.NewGuid().ToString("N"), OwnershipGeneration = 1, ParentIssueNumber = 32,
            OriginalIssueBody = FailureDiagnosticRedactor.Redact(body),
            OriginalIssueBodyFingerprint = fingerprinted ? ConflictIssueIntent.BodyFingerprint(body) : null };
        await history.CreateAsync(source);
        var candidate = Assert.Single(await h.Worker.DiscoverManagedIntegrationRecoveriesAsync("project", CancellationToken.None));
        Assert.Null(candidate.RefusalReason);
        Assert.Null(candidate.RefusalCode);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(source),
            System.Text.Json.JsonSerializer.Serialize(await history.ReadExecutionAsync(source.ExecutionId)));
        Assert.Empty(h.GitHub.Labels);
        Assert.Equal(0, h.Codex.ConflictResolutions);
    }

    [Theory]
    [InlineData("missing", "issue-missing")]
    [InlineData("closed", "issue-closed")]
    [InlineData("title", "issue-title-changed")]
    [InlineData("body", "issue-body-changed")]
    [InlineData("parent", "issue-original-parent-absent")]
    [InlineData("label", "issue-recovery-label-missing")]
    [InlineData("conflicting-label", "issue-conflicting-lifecycle-label")]
    [InlineData("redacted-history", "issue-original-metadata-missing")]
    [InlineData("missing-history", "issue-original-metadata-missing")]
    [InlineData("query", "issue-query-unavailable")]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task ConflictIntentRefusalsAreSpecificAndNeverExposeIssueContent(string scenario, string code)
    {
        using var h = new Harness();
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body };
        if (scenario == "missing") source = source with { IssueNumber = source.IssueNumber + 1 };
        if (scenario == "closed") h.GitHub.Closed = true;
        if (scenario == "title") h.GitHub.Issue = h.GitHub.Issue with { Title = "private changed title" };
        if (scenario == "body") h.GitHub.Issue = h.GitHub.Issue with { Body = "private changed body" };
        if (scenario == "parent") h.GitHub.Issue = h.GitHub.Issue with { ParentIssueNumber = 32 };
        if (scenario == "label") h.GitHub.CompletionLabels = ["unrelated"];
        if (scenario == "conflicting-label") h.GitHub.CompletionLabels = ["working", "ready"];
        if (scenario == "redacted-history") source = source with { OriginalIssueBody = "password: [redacted]" };
        if (scenario == "missing-history") source = source with { OriginalIssueBody = null };
        if (scenario == "query") h.GitHub.StateReadFailure = new WorkerInfrastructureException("private provider response");
        var error = await Assert.ThrowsAsync<ConflictIntentException>(() =>
            StandaloneConflictResolution.VerifyIntentAsync(h.Worker.Configuration, h.GitHub, source, CancellationToken.None));
        Assert.Equal(code, error.ReasonCode);
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        Assert.Empty(h.GitHub.Labels);
    }

    [Fact]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task InitialCapturePersistsFingerprintBeforeLossyRedactionAndRejectsChangesBehindRedaction()
    {
        using var database = new TempHistoryDatabase();
        using var history = new ExecutionHistoryStore(database.Path);
        using var h = new Harness(history: history);
        // Synthetic prose triggers the sanitizer without containing authentication material.
        const string body = "Task\r\npassword: example-placeholder\r\n";
        h.GitHub.Issue = h.GitHub.Issue with { Body = body, ParentIssueNumber = 32 };
        await h.ProcessOneAsync();
        var source = Assert.Single(await history.ReadAllAsync());
        Assert.Equal(32, source.ParentIssueNumber);
        Assert.Equal(FailureDiagnosticRedactor.Redact(body), source.OriginalIssueBody);
        Assert.Equal(ConflictIssueIntent.BodyFingerprint(body), source.OriginalIssueBodyFingerprint);
        using var reopened = new ExecutionHistoryStore(database.Path);
        source = Assert.IsType<ExecutionHistoryEntry>(await reopened.ReadExecutionAsync(source.ExecutionId));
        h.GitHub.Closed = false;
        h.GitHub.CompletionLabels = ["working"];
        await StandaloneConflictResolution.VerifyIntentAsync(h.Worker.Configuration, h.GitHub, source, CancellationToken.None);
        h.GitHub.Issue = h.GitHub.Issue with { Body = body.Replace("example-placeholder", "changed-placeholder", StringComparison.Ordinal) };
        var error = await Assert.ThrowsAsync<ConflictIntentException>(() =>
            StandaloneConflictResolution.VerifyIntentAsync(h.Worker.Configuration, h.GitHub, source, CancellationToken.None));
        Assert.Equal("issue-body-changed", error.ReasonCode);
    }

    [Theory]
    [InlineData(true, null, "issue-original-parent-absent")]
    [InlineData(false, null, "issue-parent-history-unavailable")]
    [InlineData(false, 2, "issue-parent-changed")]
    [Trait("TestType", "Component")]
    [Trait("Duration", "Fast")]
    public async Task CapturedAbsentParentAndMissingLegacyParentHaveDifferentDiagnostics(bool captured, int? recordedParent, string expected)
    {
        using var h = new Harness();
        h.GitHub.Issue = h.GitHub.Issue with { ParentIssueNumber = 32 };
        var source = PreservedConflict() with { OriginalIssueBody = h.GitHub.Issue.Body,
            ServerExecutionId = Guid.NewGuid().ToString("N"), ParentIssueNumber = recordedParent, ParentIssueCaptured = captured };
        var error = await Assert.ThrowsAsync<ConflictIntentException>(() =>
            StandaloneConflictResolution.VerifyIntentAsync(h.Worker.Configuration, h.GitHub, source, CancellationToken.None));
        Assert.Equal(expected, error.ReasonCode);
        Assert.Empty(h.GitHub.Labels);
    }

    private static ExecutionHistoryEntry PreservedConflict() => new(Guid.NewGuid(), "Test Project", "owner/repo", 17,
        "Example task", "feature/example-task-17", "main", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
        "IntegrationConflict", 1000, "Original implementation intent", "passed", 0, [], "preserved-head", "main", null,
        "integration conflict", "integration-conflict", "preserved-head", "preserved implementation", EffectiveEffort: "high", ParentIssueCaptured: true);
}
