namespace CodexServer;

using CodexProvisioning;

/// <summary>Operational, secret-free view derived from the existing worker registry and project models.</summary>
public sealed record WorkerProjectReadiness(string ProjectId, string ProjectName, bool IsEligible,
    IReadOnlyList<string> MissingRequirements, long? WorkerReportedRevision = null,
    string? MaterializationState = null, string? DiagnosticCode = null, string ObservationStatus = "not-reported");

public sealed record ProvisioningOperationSummary(string Source, string Id, string Status, string? Action,
    string Diagnostic, DateTimeOffset CreatedAtUtc);

public sealed record WorkerDiagnostics(string WorkerId, string WorkerVersion, string Availability,
    DateTimeOffset? LastHeartbeatAtUtc, string LifecycleState, int ActiveExecutions, int Capacity,
    int AvailableCapacity, string ConfigurationSynchronization, string ProvisioningState,
    bool GitHubReady, bool GitReady, bool AiAgentReady, IReadOnlyList<WorkerProjectReadiness> Projects,
    IReadOnlyList<string> Reasons, string? RecentOperationalError, ProvisioningOperationSummary? LatestProvisioningOperation = null,
    ManagedWorkerDiagnostics? WorkerReportedManagedDiagnostics = null,
    bool CanActivate = false, IReadOnlyList<string>? ActivationBlockingReasons = null, bool CapabilityObservationsCurrent = false);

/// <summary>Deterministically explains whether a registered worker can accept work.</summary>
public static class WorkerDiagnosticsDerivation
{
    public static WorkerDiagnostics Derive(WorkerRegistrationResponse worker, IReadOnlyList<CentralProject> projects,
        IReadOnlyList<ProvisioningPlan> provisioningPlans, string expectedConfigurationVersion,
        IReadOnlyList<ProvisioningCommand>? provisioningCommands = null, DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(provisioningPlans);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConfigurationVersion);
        var typedCommands = provisioningCommands ?? [];

        var capabilities = worker.Capabilities;
        var githubReady = projects.Count == 0
            ? Has(capabilities, "authentication", "github-api")
            : projects.All(project => Has(capabilities, "authentication", "github-api", project.Repository));
        var gitReady = projects.Count == 0
            ? Has(capabilities, "authentication", "git-repository")
            : projects.All(project => Has(capabilities, "authentication", "git-repository", project.Repository));
        var aiReady = Has(capabilities, "agent-provider", "codex");
        if (worker.CapabilityInventory is { } localInventory)
        {
            githubReady &= CapabilityCatalog.ProvidesTool(ProvidedToolKind.GitHubCli, localInventory);
            gitReady &= CapabilityCatalog.ProvidesTool(ProvidedToolKind.Git, localInventory);
            aiReady &= CapabilityCatalog.ProvidesTool(ProvidedToolKind.CodexCli, localInventory);
        }
        var readiness = projects.Select(project =>
        {
            var result = WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(project), capabilities, worker.CapabilityInventory);
            var observation = worker.ManagedDiagnostics?.Projects.FirstOrDefault(item => item.ProjectId == project.Id);
            return new WorkerProjectReadiness(project.Id, project.Name, result.IsEligible, result.MissingRequirements,
                observation?.Revision, observation?.State, observation?.DiagnosticCode, observation is null ? "not-reported" :
                    worker.Availability is "stale" or "offline" ? "stale-heartbeat" :
                    worker.ManagedDiagnostics?.Source == "cached" ? "cached-worker-observation" :
                    observation.Revision == project.Revision ? "worker-reported-current-revision" : "stale-revision");
        }).ToArray();

        var reasons = new List<string>();
        if (worker.CapabilityInventory is { } inventory)
            reasons.AddRange(CapabilityCatalog.ExecutionReadiness(inventory).BlockingReasons);
        if (worker.SchedulingPolicy == WorkerSchedulingPolicy.Disabled) reasons.Add("Scheduling disabled by Server operator");
        if (worker.SchedulingPolicy == WorkerSchedulingPolicy.Draining)
            reasons.Add(worker.ActiveAssignments == 0 ? "Drain complete; scheduling remains paused" :
                $"Draining; {worker.ActiveAssignments} active assignments retain their leases");
        if (worker.Availability is "stale" or "offline") reasons.Add("Offline");
        if (worker.Availability == "draining" || worker.Availability == "online" && worker.LifecycleState == "draining") reasons.Add("Draining");
        if (worker.Availability == "online" && worker.AvailableCapacity == 0 && worker.LifecycleState == "running") reasons.Add("At capacity");
        if (readiness.Length > 0 && !readiness.Any(project => project.IsEligible))
        {
            if (!githubReady) reasons.Add("GitHub unavailable");
            if (!gitReady) reasons.Add("Git unavailable");
            if (!aiReady) reasons.Add("AI agent unavailable");
            foreach (var requirement in readiness.SelectMany(project => project.MissingRequirements).Distinct(StringComparer.Ordinal))
                reasons.Add("Missing requirement: " + requirement);
        }

        if (worker.ManagedDiagnostics is { } managed)
        {
            if (managed.DiagnosticCode is { } code) reasons.Add($"Worker managed configuration: {managed.FailureStage} ({code})");
            foreach (var observation in managed.Projects.Where(item => item.State is "blocked" or "failed" or "materializing" or "not-materialized"))
                reasons.Add($"Worker-observed project {observation.ProjectId}, revision {observation.Revision}: {observation.State}" +
                    (observation.DiagnosticCode is null ? "" : $" ({observation.DiagnosticCode})"));
        }

        var latestPlan = provisioningPlans.Where(plan => plan.WorkerId == worker.WorkerId)
            .OrderByDescending(plan => plan.CreatedAtUtc).FirstOrDefault();
        var latestCommand = typedCommands.Where(command => command.Request.NodeId == worker.WorkerId)
            .OrderByDescending(command => command.CreatedAtUtc).FirstOrDefault();
        var planSummary = latestPlan is null ? null : new ProvisioningOperationSummary("legacy-plan", latestPlan.Id,
            latestPlan.State, latestPlan.CurrentActionId, latestPlan.State == "Failed" ? "operation-failed" : latestPlan.State.ToLowerInvariant(),
            latestPlan.CreatedAtUtc);
        var commandSummary = latestCommand is null ? null : new ProvisioningOperationSummary("typed-command", latestCommand.Id,
            latestCommand.Status.ToString(), latestCommand.Request.Action.ToString(),
            latestCommand.Diagnostic.ToString().ToLowerInvariant(), latestCommand.CreatedAtUtc);
        ProvisioningOperationSummary? latestProvisioning;
        if (planSummary is null) latestProvisioning = commandSummary;
        else if (commandSummary is null || planSummary.CreatedAtUtc > commandSummary.CreatedAtUtc) latestProvisioning = planSummary;
        else latestProvisioning = commandSummary;
        var provisioningState = latestProvisioning?.Status ?? "not-required";
        string? recentError = null;
        if (latestProvisioning?.Source == "legacy-plan" && latestPlan is { State: "Failed" } failedPlan)
            recentError = failedPlan.Failure;
        else if (latestProvisioning?.Source == "typed-command" && latestCommand is
            { Status: ProvisioningCommandStatus.Failed or ProvisioningCommandStatus.TimedOut } failedCommand)
            recentError = $"Typed provisioning {failedCommand.Request.Action.ToString().ToLowerInvariant()} for {failedCommand.Request.CapabilityId} ended with {failedCommand.Diagnostic.ToString().ToLowerInvariant()}.";
        var configurationSynchronization = worker.ConfigurationSynchronization switch
        {
            "error" => "error",
            "not-synchronized" => "not-synchronized",
            "synchronized" when string.Equals(worker.ConfigurationVersion, expectedConfigurationVersion, StringComparison.Ordinal) => "synchronized",
            "synchronized" => "out-of-sync",
            "cached" when string.Equals(worker.ConfigurationVersion, expectedConfigurationVersion, StringComparison.Ordinal) => "cached",
            "cached" => "out-of-sync",
            "unavailable" => "unavailable",
            _ => "unknown"
        };
        if (configurationSynchronization is "error" or "not-synchronized" or "out-of-sync" or "unavailable" or "cached")
            reasons.Add(configurationSynchronization == "error" ? "Configuration synchronization failed" :
                configurationSynchronization is "unavailable" or "cached" ? "Configuration synchronization unavailable" :
                "Configuration synchronization required");

        // A checkout can remain lazy until assignment; Server readability never proves Worker access.
        var activation = new List<string>();
        if (worker.Availability != "online" || worker.LifecycleState != "running")
            activation.Add("A current running Worker heartbeat is required");
        if (worker.AuthenticationCredentialStatus != "active") activation.Add("Worker API authentication is not active");
        if (configurationSynchronization != "synchronized") activation.Add("Current managed configuration synchronization is required");
        if (worker.ManagedDiagnostics is not { Source: "server-retrieved", Retrieval: "retrieved", Synchronization: "synchronized" })
            activation.Add("Live Server-managed ownership evidence is required");
        if (!aiReady) activation.Add("Successful Worker Codex execution preflight is required");
        var now = observedAtUtc ?? TimeProvider.System.GetUtcNow();
        var observationsCurrent = worker.Availability == "online" && worker.CapabilityInventory is { } observations &&
            CapabilityCatalog.Definitions.Where(definition => definition.RequiredForExecution).All(definition =>
                observations.FirstOrDefault(state => state.Id == definition.Id)?.DetectedAtUtc is { } detected &&
                detected <= now && now - detected <= TimeSpan.FromMinutes(6));
        if (worker.CapabilityInventory is not { } activationInventory)
            activation.Add("Current tool and authentication observations are required");
        else
        {
            activation.AddRange(CapabilityCatalog.ExecutionReadiness(activationInventory).BlockingReasons);
            if (!observationsCurrent)
                activation.Add("Execution capability observations are stale or unavailable; re-detect on the Worker");
        }
        if (!readiness.Any(project => project.IsEligible && projects.Any(central => central.Id == project.ProjectId && central.Enabled) &&
                project.ObservationStatus == "worker-reported-current-revision" &&
                project.MaterializationState is "ready" or "not-materialized"))
            activation.Add("An enabled central project with current Worker revision and scoped access evidence is required");
        if (typedCommands.Any(command => command.Request.NodeId == worker.WorkerId && !ProvisioningCommandProtocol.Terminal(command.Status)) ||
            provisioningPlans.Any(plan => plan.WorkerId == worker.WorkerId && plan.State is "Pending" or "Accepted" or "Running"))
            activation.Add("Reconcile pending provisioning operations before activation");

        return new WorkerDiagnostics(worker.WorkerId, worker.WorkerVersion, worker.Availability, worker.LastHeartbeatAtUtc,
            worker.LifecycleState, worker.ActiveExecutions, worker.MaximumCapacity, worker.AvailableCapacity,
            configurationSynchronization, provisioningState, githubReady, gitReady, aiReady, readiness, reasons.Distinct(StringComparer.Ordinal).ToArray(),
            recentError, latestProvisioning, worker.ManagedDiagnostics, activation.Count == 0,
            activation.Distinct(StringComparer.Ordinal).ToArray(), observationsCurrent);
    }

    private static bool Has(IReadOnlyList<WorkerCapability> capabilities, string type, string name, string? scope = null) =>
        capabilities.Any(capability => string.Equals(capability.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(capability.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (scope is null || string.Equals(capability.Scope, scope, StringComparison.OrdinalIgnoreCase)));
}
