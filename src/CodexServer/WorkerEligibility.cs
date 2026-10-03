namespace CodexServer;

using CodexProvisioning;

public static class WorkerAuthenticationRequirements
{
    public static IReadOnlyList<ProjectRequirement> ForRepository(string repository) =>
    [
        new("authentication", "github-api", Scope: repository),
        new("authentication", "git-repository", Scope: repository)
    ];

    public static IReadOnlyList<ProjectRequirement> ForProject(CentralProject project) =>
        CapabilityEligibility.ForProject(project.Requirements, project.Repository)
            .Select(requirement => new ProjectRequirement(requirement.Type, requirement.Name, requirement.Version, requirement.Scope)).ToArray();
}

/// <summary>Deterministic matching for centrally declared project requirements.</summary>
public static class WorkerEligibility
{
    public static WorkerEligibilityResult Evaluate(IEnumerable<ProjectRequirement>? requirements,
        IEnumerable<WorkerCapability>? capabilities, IReadOnlyList<CapabilityState>? inventory = null)
    {
        var result = CapabilityEligibility.Evaluate(requirements, capabilities, inventory);
        return new(result.IsEligible, result.MissingRequirements);
    }
}

public sealed record WorkerEligibilityResult(bool IsEligible, IReadOnlyList<string> MissingRequirements);
