namespace CodexServer;

/// <summary>Operational, secret-free view derived from the existing worker registry and project models.</summary>
public sealed record WorkerProjectReadiness(string ProjectId, string ProjectName, bool IsEligible,
    IReadOnlyList<string> MissingRequirements);

public sealed record WorkerDiagnostics(string WorkerId, string WorkerVersion, string Availability,
    DateTimeOffset? LastHeartbeatAtUtc, string LifecycleState, int ActiveExecutions, int Capacity,
    int AvailableCapacity, string ConfigurationSynchronization, string ProvisioningState,
    bool GitHubReady, bool GitReady, bool AiAgentReady, IReadOnlyList<WorkerProjectReadiness> Projects,
    IReadOnlyList<string> Reasons, string? RecentOperationalError);

/// <summary>Deterministically explains whether a registered worker can accept work.</summary>
public static class WorkerDiagnosticsDerivation
{
    public static WorkerDiagnostics Derive(WorkerRegistrationResponse worker, IReadOnlyList<CentralProject> projects,
        IReadOnlyList<ProvisioningPlan> provisioningPlans, string expectedConfigurationVersion)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(provisioningPlans);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConfigurationVersion);

        var capabilities = worker.Capabilities;
        var githubReady = projects.Count == 0
            ? Has(capabilities, "authentication", "github-api")
            : projects.All(project => Has(capabilities, "authentication", "github-api", project.Repository));
        var gitReady = projects.Count == 0
            ? Has(capabilities, "authentication", "git-repository")
            : projects.All(project => Has(capabilities, "authentication", "git-repository", project.Repository));
        var aiReady = Has(capabilities, "agent-provider", "codex");
        var readiness = projects.Select(project =>
        {
            var result = WorkerEligibility.Evaluate(WorkerAuthenticationRequirements.ForProject(project), capabilities);
            return new WorkerProjectReadiness(project.Id, project.Name, result.IsEligible, result.MissingRequirements);
        }).ToArray();

        var reasons = new List<string>();
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

        var latestProvisioning = provisioningPlans.Where(plan => plan.WorkerId == worker.WorkerId)
            .OrderByDescending(plan => plan.CreatedAtUtc).FirstOrDefault();
        var provisioningState = latestProvisioning?.State ?? "not-required";
        if (latestProvisioning?.State is "Pending" or "Accepted" or "Running") reasons.Add("Provisioning required");
        if (latestProvisioning?.State == "Failed") reasons.Add("Provisioning failed");
        var recentError = latestProvisioning?.Failure;
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

        return new WorkerDiagnostics(worker.WorkerId, worker.WorkerVersion, worker.Availability, worker.LastHeartbeatAtUtc,
            worker.LifecycleState, worker.ActiveExecutions, worker.MaximumCapacity, worker.AvailableCapacity,
            configurationSynchronization, provisioningState, githubReady, gitReady, aiReady, readiness, reasons.Distinct(StringComparer.Ordinal).ToArray(),
            recentError);
    }

    private static bool Has(IReadOnlyList<WorkerCapability> capabilities, string type, string name, string? scope = null) =>
        capabilities.Any(capability => string.Equals(capability.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(capability.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (scope is null || string.Equals(capability.Scope, scope, StringComparison.OrdinalIgnoreCase)));
}
